using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  DWG Export Command
    //
    //  Exports floor-plan view(s) as DWG file(s) tuned for Ekahau import:
    //    • Millimetre output unit  (Ekahau auto-scales when Unit = mm)
    //    • AutoCAD 2018 file format (Ekahau supports 2013+)
    //    • Optional "Clean" mode duplicates the view and hides everything
    //      except WiFi-relevant categories (walls, doors, windows, columns,
    //      floors, stairs, rooms, room separation lines).
    //    • A `<view>.ekahau-cal.json` sidecar is written next to each .dwg
    //      so ESX Read can round-trip AP coordinates back to Revit feet
    //      even when the .esx itself has no revitAnchor field (DWG-import
    //      projects in Ekahau Pro).
    // ═══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class DwgExportCommand : IExternalCommand
    {
        private const double FeetToMm = 304.8;

        // Categories to keep in "Clean" mode — WiFi-relevant only
        private static readonly HashSet<BuiltInCategory> CleanModeKeep =
            new HashSet<BuiltInCategory>
            {
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Stairs,
                BuiltInCategory.OST_Rooms,
                BuiltInCategory.OST_RoomSeparationLines,
                BuiltInCategory.OST_Grids,    // optional reference
            };

        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // ── 1. Collect floor-plan views ──────────────────────────
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => !v.IsTemplate &&
                            v.ViewType == ViewType.FloorPlan &&
                            v.CropBoxActive)
                .OrderBy(v => v.Name)
                .ToList();

            if (views.Count == 0)
            {
                TaskDialog.Show("DWG Export",
                    "No floor-plan views with an active crop box were found.\n\n" +
                    "Enable the crop box on at least one floor-plan view and try again.");
                return Result.Failed;
            }

            // ── 2. View selection (reuse the ESX Export view picker) ─
            var picker = new EsxViewSelectorDialog(views.Select(v => v.Name).ToList());
            if (picker.ShowDialog() != true || picker.SelectedIndices.Count == 0)
                return Result.Cancelled;

            var selectedViews = picker.SelectedIndices.Select(i => views[i]).ToList();

            // ── 3. Clean vs Full mode ────────────────────────────────
            bool cleanMode;
            var modeDlg = new TaskDialog("DWG Export Mode")
            {
                MainInstruction = "Choose export mode",
                MainContent =
                    "Clean mode hides categories that aren't useful for WiFi planning " +
                    "(furniture, MEP, annotations, etc.) so Ekahau gets a clean floor plan.",
            };
            modeDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Clean (recommended for Ekahau)",
                "Walls, doors, windows, columns, floors, stairs, rooms, grids only.");
            modeDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Full (export view as-is)",
                "Includes everything currently visible in the view.");
            modeDlg.CommonButtons = TaskDialogCommonButtons.Cancel;
            modeDlg.DefaultButton = TaskDialogResult.CommandLink1;

            var modeResp = modeDlg.Show();
            if (modeResp == TaskDialogResult.Cancel || modeResp == TaskDialogResult.Close)
                return Result.Cancelled;
            cleanMode = (modeResp == TaskDialogResult.CommandLink1);

            // ── 4. Output folder picker ──────────────────────────────
            string outputFolder;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
                   {
                       Description = "Select output folder for DWG files",
                       ShowNewFolderButton = true,
                   })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;
                outputFolder = dlg.SelectedPath;
            }

            if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder))
            {
                TaskDialog.Show("DWG Export", "Output folder is not valid.");
                return Result.Failed;
            }

            // ── 5. Per-view export ───────────────────────────────────
            var exported   = new List<string>();
            var skipped    = new List<string>();
            var dwgOptions = BuildDwgOptions();

            foreach (var srcView in selectedViews)
            {
                ViewPlan exportView = srcView;
                ElementId duplicatedViewId = null;

                try
                {
                    if (cleanMode)
                    {
                        duplicatedViewId = DuplicateAndCleanView(doc, srcView);
                        if (duplicatedViewId != null)
                            exportView = doc.GetElement(duplicatedViewId) as ViewPlan ?? srcView;
                    }

                    string baseName = SanitizeFileName(srcView.Name);
                    string dwgPath  = Path.Combine(outputFolder, baseName + ".dwg");

                    bool ok;
                    try
                    {
                        ok = doc.Export(outputFolder, baseName,
                            new List<ElementId> { exportView.Id }, dwgOptions);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"{srcView.Name}: export failed — {ex.Message}");
                        continue;
                    }

                    if (!ok || !File.Exists(dwgPath))
                    {
                        skipped.Add($"{srcView.Name}: DWG file not produced");
                        continue;
                    }

                    // Calibration sidecar — always uses the SOURCE view's CropBox
                    // (the duplicated view has the same CropBox; we record the
                    // user-recognisable view name + ID).
                    try
                    {
                        WriteCalibrationFile(doc, srcView, dwgPath, cleanMode);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"{srcView.Name}: DWG OK but calibration file failed — {ex.Message}");
                        // Still count as exported — the DWG is usable
                    }

                    exported.Add(dwgPath);
                }
                finally
                {
                    if (duplicatedViewId != null)
                        TryDeleteView(doc, duplicatedViewId);
                }
            }

            // ── 6. Summary dialog ────────────────────────────────────
            ShowSummary(exported, skipped, outputFolder);

            return Result.Succeeded;
        }

        // ══════════════════════════════════════════════════════════════
        //  DWG export options — tuned for Ekahau
        // ══════════════════════════════════════════════════════════════

        private static DWGExportOptions BuildDwgOptions()
        {
            var opts = new DWGExportOptions
            {
                TargetUnit       = ExportUnit.Millimeter,
                FileVersion      = ACADVersion.R2018,
                Colors           = ExportColorMode.TrueColor,
                ExportOfSolids   = SolidGeometry.ACIS,
                PropOverrides    = PropOverrideMode.ByEntity,
                SharedCoords     = true,
                LayerMapping     = "AIA",        // standard AIA layer naming
                MergedViews      = false,
                HideUnreferenceViewTags = true,
                HideReferencePlane      = true,
                HideScopeBox            = true,
            };
            return opts;
        }

        // ══════════════════════════════════════════════════════════════
        //  Clean-mode view duplication
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Duplicate a view and hide every category not in CleanModeKeep.
        /// Returns the duplicate's ElementId, or null on failure.
        /// Caller is responsible for deleting the duplicate after export.
        /// </summary>
        private static ElementId DuplicateAndCleanView(Document doc, ViewPlan src)
        {
            try
            {
                using var tx = new Transaction(doc, "DWG Export — clean view");
                tx.Start();

                var dupId = src.Duplicate(ViewDuplicateOption.WithDetailing);
                if (dupId == null || dupId == ElementId.InvalidElementId)
                {
                    tx.RollBack();
                    return null;
                }

                if (!(doc.GetElement(dupId) is ViewPlan dup))
                {
                    tx.RollBack();
                    return null;
                }

                // Drop the view template so our overrides take effect
                try { dup.ViewTemplateId = ElementId.InvalidElementId; } catch { }

                // Hide every category not in the keep set
                foreach (Category cat in doc.Settings.Categories)
                {
                    try
                    {
                        bool keep;
                        try { keep = CleanModeKeep.Contains((BuiltInCategory)VersionCompat.GetIdValue(cat.Id)); }
                        catch { keep = false; }

                        if (cat.CategoryType == CategoryType.Annotation)
                        {
                            // Hide ALL annotations except grids — Ekahau doesn't need them
                            bool isGrid = false;
                            try { isGrid = (BuiltInCategory)VersionCompat.GetIdValue(cat.Id) == BuiltInCategory.OST_Grids; }
                            catch { }
                            if (!isGrid && dup.CanCategoryBeHidden(cat.Id))
                                dup.SetCategoryHidden(cat.Id, true);
                        }
                        else if (cat.CategoryType == CategoryType.Model)
                        {
                            if (!keep && dup.CanCategoryBeHidden(cat.Id))
                                dup.SetCategoryHidden(cat.Id, true);
                        }
                    }
                    catch { /* per-category errors are non-fatal */ }
                }

                tx.Commit();
                return dupId;
            }
            catch
            {
                return null;
            }
        }

        private static void TryDeleteView(Document doc, ElementId viewId)
        {
            try
            {
                using var tx = new Transaction(doc, "DWG Export — delete temp view");
                tx.Start();
                try { doc.Delete(viewId); } catch { }
                tx.Commit();
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════
        //  Calibration sidecar (.ekahau-cal.json)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Write a JSON file alongside the .dwg storing the CropBox in Revit
        /// world feet, the DWG export unit, and the CropBox transform.
        /// ESX Read uses this when it can't find revitAnchor inside the .esx.
        /// </summary>
        private static void WriteCalibrationFile(
            Document doc, ViewPlan view, string dwgPath, bool cleanMode)
        {
            var cb = view.CropBox;
            var t  = cb.Transform;

            // World-space CropBox corners (handle rotated views)
            var corners = new[]
            {
                t.OfPoint(new XYZ(cb.Min.X, cb.Min.Y, 0)),
                t.OfPoint(new XYZ(cb.Max.X, cb.Min.Y, 0)),
                t.OfPoint(new XYZ(cb.Max.X, cb.Max.Y, 0)),
                t.OfPoint(new XYZ(cb.Min.X, cb.Max.Y, 0)),
            };
            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);

            var calibration = new Dictionary<string, object>
            {
                ["version"]    = "1.0",
                ["source"]     = "EkahauRevitPlugin_DWGExport",
                ["exportDate"] = DateTime.Now.ToString("o"),
                ["mode"]       = cleanMode ? "clean" : "full",

                ["revitProject"]    = doc.Title ?? "",
                ["revitProjectHash"] = RevitHelpers.GetProjectPathHash(doc),
                ["revitViewName"]    = view.Name,
                ["revitViewId"]      = VersionCompat.GetIdValue(view.Id),

                ["cropBox"] = new Dictionary<string, object>
                {
                    ["minX_ft"]   = minX,
                    ["minY_ft"]   = minY,
                    ["maxX_ft"]   = maxX,
                    ["maxY_ft"]   = maxY,
                    ["width_ft"]  = maxX - minX,
                    ["height_ft"] = maxY - minY,
                    ["width_m"]   = (maxX - minX) * 0.3048,
                    ["height_m"] = (maxY - minY) * 0.3048,
                },

                ["dwgExport"] = new Dictionary<string, object>
                {
                    ["unit"]       = "millimeter",
                    ["originX_mm"] = 0.0,
                    ["originY_mm"] = 0.0,
                    ["feetToMm"]   = FeetToMm,
                    ["fileVersion"] = "AutoCAD R2018",
                    ["layerMapping"] = "AIA",
                },

                ["transform"] = new Dictionary<string, object>
                {
                    ["originX_ft"] = t.Origin.X,
                    ["originY_ft"] = t.Origin.Y,
                    ["basisXx"]    = t.BasisX.X,
                    ["basisXy"]    = t.BasisX.Y,
                    ["basisYx"]    = t.BasisY.X,
                    ["basisYy"]    = t.BasisY.Y,
                },

                ["ekahauInstructions"] = new Dictionary<string, object>
                {
                    ["importFormat"] = "DWG/DXF",
                    ["scaleUnit"]    = "Millimeters",
                    ["note"] =
                        "Import this DWG into Ekahau Pro.  Set the unit to " +
                        "Millimeters when prompted; scale should resolve " +
                        "automatically from the DWG metadata.",
                },
            };

            string calPath = Path.ChangeExtension(dwgPath, ".ekahau-cal.json");
            File.WriteAllText(calPath, JsonSerializer.Serialize(calibration, JsonOpts));
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new ObjectAsRuntimeTypeConverter() },
        };

        /// <summary>
        /// Same idea as the polymorphic converter in EsxExportCommand —
        /// makes nested Dictionary&lt;string, object&gt; serialise via the
        /// runtime type instead of the declared <see cref="object"/>.
        /// </summary>
        private class ObjectAsRuntimeTypeConverter : JsonConverter<object>
        {
            public override bool CanConvert(Type t) => t == typeof(object);
            public override object Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
            {
                using var d = JsonDocument.ParseValue(ref r);
                return d.RootElement.Clone();
            }
            public override void Write(Utf8JsonWriter w, object v, JsonSerializerOptions o)
            {
                if (v == null) { w.WriteNullValue(); return; }
                var rt = v.GetType();
                if (rt == typeof(object)) { w.WriteStartObject(); w.WriteEndObject(); return; }
                JsonSerializer.Serialize(w, v, rt, o);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Summary dialog
        // ══════════════════════════════════════════════════════════════

        private static void ShowSummary(
            List<string> exported, List<string> skipped, string outputFolder)
        {
            int nOk    = exported.Count;
            int nSkip  = skipped.Count;

            var dlg = new TaskDialog("DWG Export Complete")
            {
                MainInstruction = nOk > 0
                    ? $"Exported {nOk} DWG file{(nOk == 1 ? "" : "s")}"
                    : "No DWG files were exported",
            };

            string body =
                $"Output folder:\n  {outputFolder}\n\n" +
                "For each view two files are written:\n" +
                "  \u2022 <view>.dwg               — geometry for Ekahau\n" +
                "  \u2022 <view>.ekahau-cal.json   — calibration for ESX Read round-trip\n\n" +
                "═══ Ekahau import ═══\n" +
                "  1. Open Ekahau AI Pro\n" +
                "  2. File → New Project (or open existing)\n" +
                "  3. Add Floor Plan → Import from File\n" +
                "  4. Select the .dwg file\n" +
                "  5. Set unit to \"Millimeters\" when prompted\n" +
                "  6. Scale should resolve automatically\n\n" +
                "After designing APs in Ekahau, save as .esx and run ESX Read in Revit. " +
                "If the .esx has no built-in revitAnchor (DWG-import workflow), " +
                "ESX Read will look for a matching .ekahau-cal.json next to the .esx.";

            if (nSkip > 0)
            {
                body += "\n\n═══ Skipped ═══\n  \u2022 " +
                        string.Join("\n  \u2022 ", skipped.Take(10));
                if (skipped.Count > 10)
                    body += $"\n  (… {skipped.Count - 10} more)";
            }

            dlg.MainContent = body;
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open output folder");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Close");
            dlg.DefaultButton = TaskDialogResult.CommandLink2;

            if (dlg.Show() == TaskDialogResult.CommandLink1)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{outputFolder}\"") { UseShellExecute = true }); }
                catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "view";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }
    }
}
