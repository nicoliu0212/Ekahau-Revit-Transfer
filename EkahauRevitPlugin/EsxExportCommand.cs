using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ESX Export Command  —  Feature 2
    //  Version 2.0 — matching original _build_esx_v2 format exactly
    // ═══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class EsxExportCommand : IExternalCommand
    {
        private const string EsxVersion = "2.0";
        private const double FeetToMetres = 0.3048;

        // ── Entry point ───────────────────────────────────────────────────

        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // 1. Collect floor plan views with active crop box
            var floorPlanViews = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => !v.IsTemplate &&
                             v.ViewType == ViewType.FloorPlan &&
                             v.CropBoxActive)
                .OrderBy(v => v.Name)
                .ToList();

            if (floorPlanViews.Count == 0)
            {
                TaskDialog.Show("ESX Export",
                    "No floor-plan views with an active crop box were found.\n\n" +
                    "Enable the crop box on at least one floor-plan view and try again.");
                return Result.Failed;
            }

            // 2. View selector (Req 10)
            var viewSel = new EsxViewSelectorDialog(floorPlanViews.Select(v => v.Name).ToList());
            if (viewSel.ShowDialog() != true) return Result.Cancelled;
            if (viewSel.SelectedIndices.Count == 0)
            {
                TaskDialog.Show("ESX Export", "No views selected.");
                return Result.Cancelled;
            }
            var selectedViews = viewSel.SelectedIndices.Select(i => floorPlanViews[i]).ToList();
            ExportMode exportMode = viewSel.ExportMode;

            // 3. Resolution selector (Req 14)
            var resDlg = new EsxResolutionDialog();
            if (resDlg.ShowDialog() != true) return Result.Cancelled;
            int resolution = resDlg.SelectedResolution;

            // 4. Output file path
            var saveDlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = exportMode == ExportMode.MergeAll
                             ? "Save Merged ESX File"
                             : "Save ESX Files — choose base name",
                Filter     = "Ekahau Site Survey (*.esx)|*.esx",
                FileName   = doc.Title + ".esx",
                DefaultExt = ".esx",
            };
            if (saveDlg.ShowDialog() != true) return Result.Cancelled;
            string outputPath = saveDlg.FileName;
            string outputDir  = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();

            // 5. Linked model config from ExtensibleStorage (Req 2)
            var linkConfig = ReadLinkedModelConfig(doc);

            // Req 2: Check whether the configured linked model is actually loaded.
            if (linkConfig.TypeUniqueIdToPreset.Count > 0)
            {
                bool configuredLinkLoaded = false;
                foreach (var linkInst in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    if (linkInst.GetLinkDocument() == null) continue;
                    // Match by specific link, or accept any if no specific link was stored
                    if (string.IsNullOrEmpty(linkConfig.SelectedLinkUniqueId) ||
                        linkInst.UniqueId == linkConfig.SelectedLinkUniqueId)
                    {
                        configuredLinkLoaded = true;
                        break;
                    }
                }
                if (!configuredLinkLoaded)
                {
                    TaskDialog.Show("ESX Export — Linked Model Warning",
                        "A linked model wall-type mapping was configured in Param Config,\n" +
                        "but no linked models are currently loaded.\n\n" +
                        "The export will proceed with host model only.\n" +
                        "Re-run Param Config to update linked model settings if needed.");
                    linkConfig = new LinkWallMapping(); // clear mappings
                }
            }

            // 6. Project info (Req 21)
            var projInfo   = doc.ProjectInformation;
            string projName    = !string.IsNullOrWhiteSpace(projInfo?.Name)       ? projInfo.Name       : doc.Title;
            string clientName  = projInfo?.ClientName ?? "";
            string projAddress = projInfo?.Address    ?? "";

            // 7. Progress window (Req 12)
            var progress = new EsxProgressWindow();
            progress.Show();
            DoEvents();

            // Shared wall-type cache (presetKey → wallType dict) so that
            // all floors in a merged export reference the same Ekahau type IDs.
            var globalTypeCache = new Dictionary<string, Dictionary<string, object>>();
            var globalWallTypes = new List<Dictionary<string, object>>();

            var allViewData = new List<PerViewData>();
            bool cancelAll  = false;
            var  debugLog   = new StringBuilder();
            debugLog.AppendLine($"ESX Export  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            debugLog.AppendLine($"Project: {projName}");
            debugLog.AppendLine($"Resolution: {resolution} px");

            // 8. Process each view
            foreach (var view in selectedViews)
            {
                if (cancelAll) break;

                progress.Update($"Processing: {view.Name}", "Collecting type information…");
                DoEvents();

                // 8a. Collect unique type entries for the mapping review
                var typeEntries = CollectTypeEntries(doc, view, linkConfig);

                // 8b. Mapping review dialog (Req 11)
                progress.Hide();
                var reviewDlg = new EsxMappingReviewDialog(view.Name, typeEntries);
                if (reviewDlg.ShowDialog() != true)
                {
                    progress.Close();
                    return Result.Cancelled;
                }
                var reviewResult = reviewDlg.Result;
                progress.Show();
                DoEvents();

                if (reviewResult.Action == MappingReviewAction.CancelAll)
                {
                    cancelAll = true;
                    break;
                }
                if (reviewResult.Action == MappingReviewAction.SkipView)
                    continue;

                var overrides = reviewResult.Overrides; // TypeUniqueId → preset override

                // 8c. Prepare duplicate view for export (Req 15)
                progress.Update($"Processing: {view.Name}", "Preparing export view…");
                DoEvents();

                var (dupView, visOk, visErr) = PrepareEkahauView(doc, view);

                if (dupView == null)
                {
                    TaskDialog.Show("ESX Export",
                        $"Could not duplicate view '{view.Name}':\n{visErr}\nSkipping.");
                    continue;
                }

                if (!visOk)
                {
                    // Req 15: ask whether to continue with partial visibility
                    var td = new TaskDialog("ESX Export — Visibility Warning")
                    {
                        MainContent =
                            $"Failed to apply visibility settings to '{view.Name}':\n{visErr}\n\n" +
                            "Continue export with current view settings?",
                    };
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Continue anyway");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Skip this view");
                    if (td.Show() == TaskDialogResult.CommandLink2)
                    {
                        DeleteView(doc, dupView.Id);
                        continue;
                    }
                }

                // Req 16: always delete dup view in finally
                try
                {
                    // 8d. Export PNG (Req 7)
                    progress.Update($"Processing: {view.Name}", "Exporting floor plan image…");
                    DoEvents();

                    var (pngBytes, imgW, imgH, exportErr) = ExportViewAsPng(doc, dupView, resolution);
                    if (pngBytes == null)
                    {
                        TaskDialog.Show("ESX Export",
                            $"PNG export failed for '{view.Name}':\n{exportErr}\nSkipping.");
                        continue;
                    }

                    // 8e. Crop / coordinate geometry
                    var cropBox       = view.CropBox;          // view-local coordinates
                    var viewTransform = cropBox.Transform;     // view-local → world
                    var worldToLocal  = viewTransform.Inverse; // world → view-local

                    // Compute world-space bounds (same as Python get_crop_box_world_bounds)
                    var corners = new[]
                    {
                        viewTransform.OfPoint(new XYZ(cropBox.Min.X, cropBox.Min.Y, 0)),
                        viewTransform.OfPoint(new XYZ(cropBox.Max.X, cropBox.Min.Y, 0)),
                        viewTransform.OfPoint(new XYZ(cropBox.Max.X, cropBox.Max.Y, 0)),
                        viewTransform.OfPoint(new XYZ(cropBox.Min.X, cropBox.Max.Y, 0)),
                    };
                    double cwMinX = corners.Min(p => p.X);
                    double cwMinY = corners.Min(p => p.Y);
                    double cwMaxX = corners.Max(p => p.X);
                    double cwMaxY = corners.Max(p => p.Y);

                    double rotDeg = Math.Atan2(
                        viewTransform.BasisX.Y, viewTransform.BasisX.X) * 180.0 / Math.PI;

                    // Req 8 / Req 9: compute + validate MPU
                    double localCropW = cropBox.Max.X - cropBox.Min.X;
                    double localCropH = cropBox.Max.Y - cropBox.Min.Y;
                    var (mpuX, mpuY) = ComputeMetersPerUnit(cropBox, imgW, imgH, view.Name, debugLog);

                    // Req 9: padding-aware pixel region
                    var (offPxX, offPxY, cPxW, cPxH) =
                        ComputeCropPixelRegion(localCropW, localCropH, imgW, imgH);

                    // Build coordinate transform closure (Req 9)
                    var xform = BuildRevitToEkahauXform(cropBox, worldToLocal, imgW, imgH);

                    // 8f. Collect geometry with linked models (Req 2, 3)
                    progress.Update($"Processing: {view.Name}", "Collecting wall geometry…");
                    DoEvents();

                    var walls    = WallCollector.CollectWalls(doc, view, linkConfig, overrides);
                    var openings = WallCollector.CollectOpenings(doc, view, linkConfig, overrides);

                    // 8g. Split walls at openings, fix overlapping merge (Req 4)
                    var finalSegs = WallSplitter.SplitWallsWithOpenings(walls, openings);

                    // 8g2. Snap nearby endpoints to close corner gaps (Bug Fix #6)
                    EndpointSnapper.Snap(finalSegs);

                    // 8h. Build wall JSON (Req 17 debug logging)
                    progress.Update($"Processing: {view.Name}", "Building wall data…");
                    DoEvents();

                    string floorPlanId = Guid.NewGuid().ToString();
                    string imageId     = Guid.NewGuid().ToString();

                    debugLog.AppendLine($"\n=== View: {view.Name} ({finalSegs.Count} segments) ===");
                    var (wallSegments, wallPoints) = EkahauJsonBuilder.BuildWallJson(
                        finalSegs, floorPlanId, xform,
                        globalTypeCache, globalWallTypes, debugLog);

                    // 8i. Collect APs (Req 20)
                    progress.Update($"Processing: {view.Name}", "Looking for access points…");
                    DoEvents();

                    var apCandidates = CollectApInstances(doc, view);
                    if (apCandidates.Count > 0)
                    {
                        progress.Hide();
                        var apDlg = new EsxApConfirmDialog(apCandidates);
                        apDlg.ShowDialog();
                        if (apDlg.SkipAps)
                            apCandidates.Clear();
                        else
                            apCandidates.RemoveAll(ap => !ap.Include);
                        progress.Show();
                        DoEvents();
                    }

                    // 8j. Assemble per-view data
                    var viewData = new PerViewData
                    {
                        FloorPlanId     = floorPlanId,
                        ImageId         = imageId,
                        ViewId          = view.Id,
                        ViewName        = view.Name,
                        // View-local CropBox bounds
                        CropMinX        = cropBox.Min.X,
                        CropMinY        = cropBox.Min.Y,
                        CropMaxX        = cropBox.Max.X,
                        CropMaxY        = cropBox.Max.Y,
                        // World-space CropBox bounds (for revitAnchor)
                        CropWorldMinX   = cwMinX,
                        CropWorldMinY   = cwMinY,
                        CropWorldMaxX   = cwMaxX,
                        CropWorldMaxY   = cwMaxY,
                        // CropBox Transform (for revitAnchor rotated-view support)
                        XformOriginX    = viewTransform.Origin.X,
                        XformOriginY    = viewTransform.Origin.Y,
                        XformBasisXx    = viewTransform.BasisX.X,
                        XformBasisXy    = viewTransform.BasisX.Y,
                        XformBasisYx    = viewTransform.BasisY.X,
                        XformBasisYy    = viewTransform.BasisY.Y,
                        // Padding-aware pixel region
                        CropPixelOffsetX = offPxX,
                        CropPixelOffsetY = offPxY,
                        CropPixelWidth   = cPxW,
                        CropPixelHeight  = cPxH,
                        // Legacy anchor
                        AnchorWorldX    = cwMinX,
                        AnchorWorldY    = cwMaxY,
                        ViewRotationDeg = rotDeg,
                        // Image
                        ImageWidth      = imgW,
                        ImageHeight     = imgH,
                        MpuX            = mpuX,
                        MpuY            = mpuY,
                        PngBytes        = pngBytes,
                        WallSegments    = wallSegments,
                        WallPoints      = wallPoints,
                        AccessPoints    = apCandidates,
                        WorldToEkahau   = xform,
                    };
                    allViewData.Add(viewData);
                }
                finally
                {
                    // Req 16: guaranteed cleanup of dup view
                    DeleteView(doc, dupView.Id);
                }
            }

            progress.Close();

            if (cancelAll && allViewData.Count == 0)
                return Result.Cancelled;

            if (allViewData.Count == 0)
            {
                TaskDialog.Show("ESX Export", "No floors were successfully processed.");
                return Result.Failed;
            }

            // 9. Build ESX ZIP(s)
            progress = new EsxProgressWindow();
            progress.Update("Writing ESX file(s)…", "");
            progress.Show();
            DoEvents();

            string debugLogPath = null;
            if (debugLog.Length > 0)
            {
                debugLogPath = Path.Combine(outputDir,
                    Path.GetFileNameWithoutExtension(outputPath) + "_debug.txt");
                try { File.WriteAllText(debugLogPath, debugLog.ToString(), Encoding.UTF8); }
                catch { debugLogPath = null; }
            }

            var esxFiles = new List<string>();
            try
            {
                if (exportMode == ExportMode.MergeAll)
                {
                    BuildEsx(allViewData, globalWallTypes, outputPath,
                             projName, clientName, projAddress);
                    esxFiles.Add(outputPath);
                }
                else
                {
                    // Separate mode: one .esx per floor, only include the wall types
                    // that are actually referenced by that floor's segments.
                    string stem = Path.GetFileNameWithoutExtension(outputPath);
                    foreach (var vd in allViewData)
                    {
                        string safe = SanitizeFileName(vd.ViewName);
                        string path = Path.Combine(outputDir, $"{stem}_{safe}.esx");

                        // Collect wall type IDs referenced by this view's segments
                        var usedTypeIds = new HashSet<string>();
                        foreach (var seg in vd.WallSegments)
                        {
                            if (seg.TryGetValue("wallTypeId", out object id))
                                usedTypeIds.Add(id?.ToString() ?? "");
                        }

                        // Filter global types to only those used by this floor
                        var viewTypes = globalWallTypes
                            .Where(wt => usedTypeIds.Contains(wt.GetValueOrDefault("id")?.ToString() ?? ""))
                            .ToList();

                        BuildEsx(new List<PerViewData> { vd }, viewTypes, path,
                                 projName, clientName, projAddress);
                        esxFiles.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
                progress.Close();
                TaskDialog.Show("ESX Export — Error",
                    $"Failed to write ESX file:\n{ex.Message}");
                return Result.Failed;
            }

            progress.Close();

            // 10. Summary (Req 13)
            int totalSegs = allViewData.Sum(v => v.WallSegments.Count);
            int totalAps  = allViewData.Sum(v => v.AccessPoints.Count(a => a.Include));

            var summaryDlg = new EsxSummaryDialog(
                allViewData.Count, totalSegs, totalAps,
                esxFiles.Count == 1 ? esxFiles[0] : outputDir,
                debugLogPath != null, debugLogPath ?? "");
            summaryDlg.ShowDialog();

            return Result.Succeeded;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Req 2 — Read ExtensibleStorage linked-model config
        // ══════════════════════════════════════════════════════════════════

        private static LinkWallMapping ReadLinkedModelConfig(Document doc)
        {
            var result = new LinkWallMapping();
            try
            {
                var schemaGuid = new Guid("E7B8C9D0-F1A2-3456-BCDE-F0123456789A");
                var schema = Schema.Lookup(schemaGuid);
                if (schema == null) return result;

                foreach (var ds in new FilteredElementCollector(doc)
                    .OfClass(typeof(DataStorage)).ToElements())
                {
                    try
                    {
                        var entity = ds.GetEntity(schema);
                        if (!entity.IsValid()) continue;
                        string json = entity.Get<string>("MappingJson");
                        if (string.IsNullOrWhiteSpace(json)) continue;

                        // Param Config stores JSON as:
                        // {"selectedLinkName":"...", "selectedLinkUniqueId":"...", "mappings":{...}}
                        using var jsonDoc = JsonDocument.Parse(json);
                        var root = jsonDoc.RootElement;

                        if (root.TryGetProperty("selectedLinkUniqueId", out var linkIdProp))
                            result.SelectedLinkUniqueId = linkIdProp.GetString() ?? "";

                        if (root.TryGetProperty("mappings", out var mappingsProp) &&
                            mappingsProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var kv in mappingsProp.EnumerateObject())
                                result.TypeUniqueIdToPreset[kv.Name] = kv.Value.GetString() ?? "";
                        }
                        else
                        {
                            // Fallback: legacy flat dict format (no wrapper)
                            var dict = JsonSerializer
                                .Deserialize<Dictionary<string, string>>(json);
                            if (dict != null)
                            {
                                dict.Remove("selectedLinkName");
                                dict.Remove("selectedLinkUniqueId");
                                foreach (var kv in dict)
                                    result.TypeUniqueIdToPreset[kv.Key] = kv.Value;
                            }
                        }
                        break;
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Collect unique type entries for Mapping Review dialog (Req 11)
        // ══════════════════════════════════════════════════════════════════

        private static List<MappingEntry> CollectTypeEntries(
            Document doc, ViewPlan view, LinkWallMapping linkConfig)
        {
            var entries = new List<MappingEntry>();
            var seen    = new HashSet<string>();

            void AddEntry(Element elemType, string category, bool isLinked)
            {
                if (elemType == null) return;
                string uid = elemType.UniqueId ?? "";
                if (!seen.Add(uid)) return;

                var (preset, source) = EkahauTypeResolver.ResolveEkahauType(
                    doc, elemType, category, linkConfig, null);

                entries.Add(new MappingEntry
                {
                    TypeUniqueId  = uid,
                    TypeName      = RevitHelpers.SafeName(elemType),
                    InitialPreset = preset,
                    Source        = source,
                    Category      = category,
                    IsLinked      = isLinked,
                });
            }

            // ── Host walls ────────────────────────────────────────────────
            foreach (var wall in new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType().ToElements())
            {
                var wt = wall is Wall w ? w.WallType
                       : doc.GetElement(wall.GetTypeId()) as WallType;
                AddEntry(wt, "wall", false);
            }

            // ── Host doors/windows ────────────────────────────────────────
            foreach (var (bic, cat) in new[] {
                (BuiltInCategory.OST_Doors,   "door"),
                (BuiltInCategory.OST_Windows, "window") })
            {
                foreach (var elem in new FilteredElementCollector(doc, view.Id)
                    .OfCategory(bic).WhereElementIsNotElementType().ToElements())
                    AddEntry(doc.GetElement(elem.GetTypeId()), cat, false);
            }

            // ── Linked model walls (Req 2) — only from configured link ────
            if (linkConfig != null && linkConfig.TypeUniqueIdToPreset.Count > 0)
            {
                foreach (var linkInst in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>())
                {
                    if (!string.IsNullOrEmpty(linkConfig.SelectedLinkUniqueId) &&
                        linkInst.UniqueId != linkConfig.SelectedLinkUniqueId)
                        continue;

                    var linkDoc = linkInst.GetLinkDocument();
                    if (linkDoc == null) continue;

                    foreach (var wall in new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType().ToElements())
                    {
                        var wt = wall is Wall w ? w.WallType
                               : linkDoc.GetElement(wall.GetTypeId()) as WallType;
                        AddEntry(wt, "wall", true);
                    }
                }
            }

            return entries;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Req 15 — Prepare duplicate view for export
        // ══════════════════════════════════════════════════════════════════

        private static (ViewPlan DupView, bool VisOk, string VisErr)
            PrepareEkahauView(Document doc, ViewPlan view)
        {
            ElementId dupId = ElementId.InvalidElementId;
            try
            {
                using (var tx = new Transaction(doc, "Duplicate view for ESX export"))
                {
                    tx.Start();
                    dupId = view.Duplicate(ViewDuplicateOption.WithDetailing);
                    var dup = (ViewPlan)doc.GetElement(dupId);
                    dup.Name = view.Name + "_EkExport_" +
                               Guid.NewGuid().ToString("N").Substring(0, 6);
                    dup.CropBoxVisible = false;
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                return (null, false, ex.Message);
            }

            // Apply visibility overrides in a separate transaction
            bool visOk    = true;
            string visErr = null;

            var annotCats = new[]
            {
                BuiltInCategory.OST_Grids,
                BuiltInCategory.OST_Dimensions,
                BuiltInCategory.OST_TextNotes,
                BuiltInCategory.OST_GenericAnnotation,
                BuiltInCategory.OST_RoomSeparationLines,
                BuiltInCategory.OST_RoomTags,
            };

            using (var tx = new Transaction(doc, "ESX export view visibility"))
            {
                try
                {
                    tx.Start();
                    var dup = (ViewPlan)doc.GetElement(dupId);
                    foreach (var cat in annotCats)
                    {
                        try
                        {
                            var catObj = Category.GetCategory(doc, cat);
                            if (catObj != null && dup.CanCategoryBeHidden(catObj.Id))
                                dup.SetCategoryHidden(catObj.Id, true);
                        }
                        catch { }
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    visOk  = false;
                    visErr = ex.Message;
                }
            }

            return ((ViewPlan)doc.GetElement(dupId), visOk, visErr);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Req 7 — Export view to PNG; robust file detection
        // ══════════════════════════════════════════════════════════════════

        private static (byte[] PngBytes, int Width, int Height, string Error)
            ExportViewAsPng(Document doc, View view, int resolution)
        {
            string tempDir = Path.Combine(
                Path.GetTempPath(), "EkahauExport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // Auto-pick FitDirection based on the view's CropBox aspect:
                // a tall/narrow view should be fit Vertical so PixelSize is
                // applied to the LONGER dimension (giving the most pixels
                // along the longest edge of the floor plan).
                var fit = FitDirectionType.Horizontal;
                try
                {
                    var cb = view.CropBox;
                    double cw = cb.Max.X - cb.Min.X;
                    double ch = cb.Max.Y - cb.Min.Y;
                    if (ch > cw * 1.2) fit = FitDirectionType.Vertical;
                }
                catch { }

                var opts = new ImageExportOptions
                {
                    ZoomType           = ZoomFitType.FitToPage,
                    PixelSize          = resolution,
                    ImageResolution    = ImageResolution.DPI_300,
                    FitDirection       = fit,
                    ExportRange        = ExportRange.SetOfViews,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType   = ImageFileType.PNG,
                    FilePath           = Path.Combine(tempDir, "export"),
                };
                opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(opts);

                // Req 7: Try direct path first, then directory scan
                string pngPath = null;

                // Direct attempt: Revit names the file "export - <ViewName>.png" (varies)
                string direct = Path.Combine(tempDir, "export.png");
                if (File.Exists(direct))
                {
                    pngPath = direct;
                }
                else
                {
                    // Fallback: scan directory for newest PNG
                    var pngs = Directory.GetFiles(tempDir, "*.png");
                    if (pngs.Length == 0)
                        return (null, 0, 0, "No PNG produced by ExportImage.");
                    pngPath = pngs.OrderByDescending(f => new FileInfo(f).CreationTimeUtc).First();
                }

                byte[] bytes = File.ReadAllBytes(pngPath);
                var (w, h) = ReadPngDimensions(bytes);
                if (w == 0 || h == 0)
                    return (null, 0, 0, "Could not read PNG dimensions.");

                return (bytes, w, h, null);
            }
            catch (Exception ex)
            {
                return (null, 0, 0, ex.Message);
            }
            finally
            {
                // Req 16: always clean up temp dir
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Req 9 — ComputeCropPixelRegion (shared helper)
        //  Calculate CropBox pixel region within the exported PNG.
        //  When the PNG aspect ratio differs from the CropBox aspect ratio
        //  (e.g. due to annotation overflow), the CropBox content is centered
        //  within the PNG with padding on one axis.
        //  Used by ComputeMetersPerUnit AND BuildRevitToEkahauXform.
        // ══════════════════════════════════════════════════════════════════

        private static (double OffsetX, double OffsetY, double ContentW, double ContentH)
            ComputeCropPixelRegion(double cropW, double cropH, int imgW, int imgH)
        {
            double cropAR = cropH > 0 ? cropW / cropH : 1.0;
            double pngAR  = imgH  > 0 ? (double)imgW / imgH : cropAR;

            double contentW, contentH;
            if (cropAR >= pngAR)
            {
                // Crop is wider-than-or-equal — fits to width, padding on Y
                contentW = imgW;
                contentH = imgW / cropAR;
            }
            else
            {
                // Crop is taller — fits to height, padding on X
                contentH = imgH;
                contentW = imgH * cropAR;
            }

            double offsetX = (imgW - contentW) / 2.0;
            double offsetY = (imgH - contentH) / 2.0;

            return (offsetX, offsetY, contentW, contentH);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Req 8 — Compute MPU and warn if X/Y differ by >1 %
        // ══════════════════════════════════════════════════════════════════

        private static (double MpuX, double MpuY) ComputeMetersPerUnit(
            BoundingBoxXYZ cropBox, int imgW, int imgH,
            string viewName, StringBuilder debugLog)
        {
            double cropW = cropBox.Max.X - cropBox.Min.X;
            double cropH = cropBox.Max.Y - cropBox.Min.Y;

            var (_, _, cW, cH) = ComputeCropPixelRegion(cropW, cropH, imgW, imgH);

            double mpuX = cropW * FeetToMetres / cW;
            double mpuY = cropH * FeetToMetres / cH;

            double avg = (mpuX + mpuY) / 2.0;
            if (avg > 1e-9 && Math.Abs(mpuX - mpuY) / avg > 0.01)
            {
                string warn = $"[MPU WARNING] View '{viewName}': " +
                              $"mpuX={mpuX:F6} mpuY={mpuY:F6} — differ by " +
                              $"{Math.Abs(mpuX - mpuY) / avg:P1}";
                debugLog?.AppendLine(warn);
                System.Diagnostics.Debug.WriteLine(warn);
            }

            return (mpuX, mpuY);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Req 9 — Coordinate transform closure
        // ══════════════════════════════════════════════════════════════════

        private static Func<double, double, (double Ex, double Ey)>
            BuildRevitToEkahauXform(
                BoundingBoxXYZ cropBox,
                Transform worldToViewLocal,
                int imgW, int imgH)
        {
            double minX  = cropBox.Min.X;
            double maxY  = cropBox.Max.Y;
            double cropW = cropBox.Max.X - cropBox.Min.X;
            double cropH = cropBox.Max.Y - cropBox.Min.Y;

            // Req 9: shared padding-aware pixel region calculation
            var (offX, offY, cW, cH) = ComputeCropPixelRegion(cropW, cropH, imgW, imgH);

            return (wx, wy) =>
            {
                // Transform world → view-local
                XYZ localPt = worldToViewLocal.OfPoint(new XYZ(wx, wy, 0));

                // Map into the content area (accounting for any padding offset)
                double ex = offX + (localPt.X - minX) / cropW * cW;
                double ey = offY + (maxY - localPt.Y) / cropH * cH;
                return (ex, ey);
            };
        }

        // ══════════════════════════════════════════════════════════════════
        //  Req 20 — Collect AP family instances
        // ══════════════════════════════════════════════════════════════════

        private static List<ApCandidate> CollectApInstances(Document doc, View view)
        {
            var result = new List<ApCandidate>();
            var apKw   = new[] { "ap", "access point", "wap", "wireless ap", "wifi", "wi-fi" };
            // Prefix patterns for the Mark parameter (Req 20)
            var apMarkPrefixes = new[] { "ap-", "ap ", "wap-", "wap " };

            var searchCats = new[]
            {
                BuiltInCategory.OST_CommunicationDevices,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_ElectricalEquipment,
            };

            var seenIds = new HashSet<long>();

            foreach (var cat in searchCats)
            {
                try
                {
                    foreach (var elem in new FilteredElementCollector(doc, view.Id)
                        .OfCategory(cat)
                        .WhereElementIsNotElementType().ToElements())
                    {
                        if (!seenIds.Add(VersionCompat.GetIdValue(elem.Id))) continue;
                        try
                        {
                            string typeName = RevitHelpers.SafeName(doc.GetElement(elem.GetTypeId()));
                            string instName = elem.Name ?? "";

                            // Req 20: Check Mark parameter for AP-xxx / WAP-xxx patterns
                            string mark = "";
                            try
                            {
                                var markP = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                                if (markP != null && markP.HasValue)
                                    mark = (markP.AsString() ?? "").Trim();
                            }
                            catch { }

                            string markLow = mark.ToLowerInvariant();
                            // Match 1: Mark starts with AP-/WAP- prefix
                            bool isApByMark = !string.IsNullOrEmpty(mark) &&
                                apMarkPrefixes.Any(pf => markLow.StartsWith(pf));
                            // Match 2: Non-empty Mark AND family/type name contains AP keyword
                            bool isApByMarkAndName = !string.IsNullOrEmpty(mark) &&
                                apKw.Any(kw => (instName + " " + typeName).ToLowerInvariant().Contains(kw));
                            // Match 3: Type/instance name alone contains AP keywords
                            string combined = (instName + " " + typeName).ToLowerInvariant();
                            bool isApByName = apKw.Any(kw => combined.Contains(kw));

                            if (!isApByMark && !isApByMarkAndName && !isApByName) continue;

                            if (!(elem.Location is LocationPoint lp)) continue;
                            XYZ loc = lp.Point;

                            // Req 20: Compute mounting height relative to level
                            double mountingHeightM;
                            try
                            {
                                var levelParam = elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                                if (levelParam != null && levelParam.HasValue)
                                {
                                    var level = doc.GetElement(levelParam.AsElementId()) as Level;
                                    if (level != null)
                                    {
                                        mountingHeightM = (loc.Z - level.Elevation) * FeetToMetres;
                                    }
                                    else
                                    {
                                        mountingHeightM = loc.Z * FeetToMetres;
                                    }
                                }
                                else
                                {
                                    mountingHeightM = loc.Z * FeetToMetres;
                                }
                            }
                            catch
                            {
                                mountingHeightM = 2.7; // default fallback
                            }
                            if (mountingHeightM <= 0) mountingHeightM = 2.7;

                            // Prefer Mark value as AP name, then instance name, then type name
                            string apName = !string.IsNullOrWhiteSpace(mark) ? mark
                                          : !string.IsNullOrWhiteSpace(instName) ? instName
                                          : typeName;

                            result.Add(new ApCandidate
                            {
                                ElementId    = VersionCompat.GetIdValue(elem.Id),
                                Name         = apName,
                                WorldX       = loc.X,
                                WorldY       = loc.Y,
                                HeightMeters = mountingHeightM,
                                Include      = true,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  ESX ZIP builder
        // ══════════════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════════════
        //  BuildEsx — EXACT replica of _build_esx_v2 ZIP structure.
        //
        //  ZIP entries (in order):
        //    "version"                     ← plain text, content = "2.0"
        //    "project.json"                ← {"project": {...}}
        //    "projectConfiguration.json"   ← {"projectConfiguration": {...}}
        //    "floorPlans.json"             ← {"floorPlans": [...]}
        //    "images.json"                 ← {"images": [...]}
        //    "wallTypes.json"              ← {"wallTypes": [...]}
        //    "wallSegments.json"           ← {"wallSegments": [...]}
        //    "wallPoints.json"             ← {"wallPoints": [...]}
        //    12 stub files (accessPoints, simulatedRadios, antennaTypes, …)
        //    "image-{imgId}"               ← raw PNG bytes per floor
        //
        //  All text entries are UTF-8 **without BOM**.
        //  JSON is indented 2-space (matching Python json.dumps indent=2).
        //  Compression is Optimal for everything.
        // ══════════════════════════════════════════════════════════════════

        /// <summary>UTF-8 encoding without BOM, matching the original Python output.</summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,      // 2-space indent matches Python json.dumps(indent=2)
            Converters =
            {
                // Order matters: the object converter must come first so it
                // dispatches to runtime-typed Dictionary/List handlers, which
                // in turn pick up DoubleWithDecimalConverter for their values.
                new PolymorphicObjectConverter(),
                new DoubleWithDecimalConverter(),
            },
        };

        /// <summary>
        /// Bug Fix #14 — Polymorphic serialization of <see cref="object"/>-typed
        /// values.  Without this, System.Text.Json (.NET 6+) writes nested
        /// Dictionary&lt;string, object&gt; values as empty {} because every
        /// value in such a dictionary is statically typed as object and the
        /// serializer uses the declared type instead of the runtime type.
        /// This caused revitAnchor (and any other nested dict/list) to be
        /// dropped from floorPlans.json, breaking the wall/AP coordinate
        /// transform between Revit and Ekahau.
        /// </summary>
        private class PolymorphicObjectConverter : System.Text.Json.Serialization.JsonConverter<object>
        {
            public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(object);

            public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
                return doc.RootElement.Clone();
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
            {
                if (value == null) { writer.WriteNullValue(); return; }
                var rt = value.GetType();
                if (rt == typeof(object))
                {
                    // Plain System.Object — emit empty object so callers don't break.
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                    return;
                }
                JsonSerializer.Serialize(writer, value, rt, options);
            }
        }

        /// <summary>
        /// Ensures doubles are always written with a decimal point in JSON
        /// (e.g. 2000.0 not 2000), which Ekahau's Gson parser requires.
        /// </summary>
        private class DoubleWithDecimalConverter : System.Text.Json.Serialization.JsonConverter<double>
        {
            public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => reader.GetDouble();

            public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            {
                // If the value is a whole number, write with .0 suffix
                if (value == Math.Floor(value) && !double.IsInfinity(value) && !double.IsNaN(value))
                    writer.WriteRawValue(value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                else
                    writer.WriteNumberValue(value);
            }
        }

        private static void BuildEsx(
            List<PerViewData> views,
            List<Dictionary<string, object>> wallTypes,
            string outputPath,
            string projectName, string clientName, string address)
        {
            using var stream = File.Create(outputPath);
            using var zip    = new ZipArchive(stream, ZipArchiveMode.Create, false);

            // Helpers ──────────────────────────────────────────────────────

            void AddText(string entryName, string text)
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var s = entry.Open();
                byte[] raw = Utf8NoBom.GetBytes(text);
                s.Write(raw, 0, raw.Length);
            }

            void AddJson(string entryName, object obj)
            {
                AddText(entryName, JsonSerializer.Serialize(obj, JsonOpts));
            }

            void AddBytes(string entryName, byte[] bytes)
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes, 0, bytes.Length);
            }

            // ── 1. version  (plain text, not JSON) ────────────────────────
            AddText("version", EsxVersion);

            // ── 2. project.json  (Req 21 — original wrapper format) ───────
            AddJson("project.json", new Dictionary<string, object>
            {
                ["project"] = new Dictionary<string, object>
                {
                    ["id"]             = Guid.NewGuid().ToString(),
                    ["schemaVersion"]  = "1.9.0",
                    ["projectVersion"] = 1,
                    ["name"]           = projectName,
                    ["title"]          = projectName,
                    ["customer"]       = clientName,
                    ["location"]       = address,
                    ["noteIds"]        = Array.Empty<object>(),
                }
            });

            // ── 3. projectConfiguration.json ──────────────────────────────
            AddJson("projectConfiguration.json", new Dictionary<string, object>
            {
                ["projectConfiguration"] = new Dictionary<string, object>
                {
                    ["enabledGroupingLogics"] = Array.Empty<object>(),
                    ["displayOptions"]        = Array.Empty<object>(),
                }
            });

            // ── 4. floorPlans.json ────────────────────────────────────────
            AddJson("floorPlans.json", new Dictionary<string, object>
            {
                ["floorPlans"] = views.Select(BuildFloorPlanEntry).ToList(),
            });

            // ── 5. images.json ────────────────────────────────────────────
            AddJson("images.json", new Dictionary<string, object>
            {
                ["images"] = views.Select(v => new Dictionary<string, object>
                {
                    ["id"]               = v.ImageId,
                    ["imageFormat"]      = "PNG",
                    ["resolutionWidth"]  = (double)v.ImageWidth,
                    ["resolutionHeight"] = (double)v.ImageHeight,
                    ["status"]           = "CREATED",
                }).ToList(),
            });

            // ── 6. wallTypes.json (de-duplicated by presetKey) ────────────
            var seenKeys    = new HashSet<string>();
            var uniqueTypes = new List<Dictionary<string, object>>();
            foreach (var wt in wallTypes)
            {
                if (wt.TryGetValue("key", out object k) && seenKeys.Add(k?.ToString() ?? ""))
                    uniqueTypes.Add(wt);
            }
            AddJson("wallTypes.json", new Dictionary<string, object>
            {
                ["wallTypes"] = uniqueTypes,
            });

            // ── 7. wallSegments.json ──────────────────────────────────────
            AddJson("wallSegments.json", new Dictionary<string, object>
            {
                ["wallSegments"] = views.SelectMany(v => v.WallSegments).ToList(),
            });

            // ── 8. wallPoints.json ────────────────────────────────────────
            AddJson("wallPoints.json", new Dictionary<string, object>
            {
                ["wallPoints"] = views.SelectMany(v => v.WallPoints).ToList(),
            });

            // ── 9. Stub files — EXACT 12 entries from original ────────────
            //    accessPoints includes Req 20 AP data when available.
            var allAps = views.SelectMany(v =>
                v.AccessPoints.Where(ap => ap.Include)
                 .Select(ap => BuildApEntry(ap, v.WorldToEkahau, v.FloorPlanId)))
                .ToList();

            AddJson("accessPoints.json", new Dictionary<string, object>
                { ["accessPoints"] = allAps });
            AddJson("simulatedRadios.json", new Dictionary<string, object>
                { ["simulatedRadios"] = Array.Empty<object>() });
            AddJson("antennaTypes.json", new Dictionary<string, object>
                { ["antennaTypes"] = Array.Empty<object>() });
            AddJson("floorTypes.json", new Dictionary<string, object>
                { ["floorTypes"] = Array.Empty<object>() });
            AddJson("attenuationAreaTypes.json", new Dictionary<string, object>
                { ["attenuationAreaTypes"] = Array.Empty<object>() });
            AddJson("attenuationAreas.json", new Dictionary<string, object>
                { ["attenuationAreas"] = Array.Empty<object>() });
            AddJson("applicationProfiles.json", new Dictionary<string, object>
                { ["applicationProfiles"] = Array.Empty<object>() });
            AddJson("deviceProfiles.json", new Dictionary<string, object>
                { ["deviceProfiles"] = Array.Empty<object>() });
            AddJson("usageProfiles.json", new Dictionary<string, object>
                { ["usageProfiles"] = Array.Empty<object>() });
            AddJson("networkCapacitySettings.json", new Dictionary<string, object>
                { ["networkCapacitySettings"] = Array.Empty<object>() });
            AddJson("requirements.json", new Dictionary<string, object>
                { ["requirements"] = Array.Empty<object>() });
            AddJson("exclusionAreas.json", new Dictionary<string, object>
                { ["exclusionAreas"] = Array.Empty<object>() });

            // ── 10. PNG images (binary, no file extension) ────────────────
            foreach (var v in views)
                if (v.PngBytes?.Length > 0)
                    AddBytes($"image-{v.ImageId}", v.PngBytes);
        }

        // ── floorPlan entry builder (matches original fp_data_v2) ──────────

        private static Dictionary<string, object> BuildFloorPlanEntry(PerViewData v)
        {
            var fp = new Dictionary<string, object>
            {
                ["id"]                = v.FloorPlanId,
                ["name"]              = v.ViewName,
                ["width"]             = (double)v.ImageWidth,
                ["height"]            = (double)v.ImageHeight,
                ["metersPerUnit"]     = v.MpuX,
                ["imageId"]           = v.ImageId,
                ["legacyId"]          = 0,
                ["cropMinX"]          = 0.0,
                ["cropMinY"]          = 0.0,
                ["cropMaxX"]          = (double)v.ImageWidth,
                ["cropMaxY"]          = (double)v.ImageHeight,
                ["floorPlanType"]     = "FSPL",
                ["rotateUpDirection"] = "UP",
                ["tags"]              = Array.Empty<object>(),
                ["gpsReferencePoints"]= Array.Empty<object>(),
                ["status"]            = "CREATED",
            };

            // revitAnchor — EXACT field names from original _compute_revit_anchor
            var anchor = new Dictionary<string, object>
            {
                ["cropWorldMinX_ft"] = v.CropWorldMinX,
                ["cropWorldMinY_ft"] = v.CropWorldMinY,
                ["cropWorldMaxX_ft"] = v.CropWorldMaxX,
                ["cropWorldMaxY_ft"] = v.CropWorldMaxY,
                ["metersPerUnit"]    = v.MpuX,
                ["imageWidth"]       = v.ImageWidth,
                ["imageHeight"]      = v.ImageHeight,
                ["cropPixelOffsetX"] = v.CropPixelOffsetX,
                ["cropPixelOffsetY"] = v.CropPixelOffsetY,
                ["cropPixelWidth"]   = v.CropPixelWidth,
                ["cropPixelHeight"]  = v.CropPixelHeight,
                // CropBox Transform for rotated-view support
                ["xformOriginX_ft"]  = v.XformOriginX,
                ["xformOriginY_ft"]  = v.XformOriginY,
                ["xformBasisXx"]     = v.XformBasisXx,
                ["xformBasisXy"]     = v.XformBasisXy,
                ["xformBasisYx"]     = v.XformBasisYx,
                ["xformBasisYy"]     = v.XformBasisYy,
                ["localMinX"]        = v.CropMinX,
                ["localMinY"]        = v.CropMinY,
                ["localMaxX"]        = v.CropMaxX,
                ["localMaxY"]        = v.CropMaxY,
            };
            fp["revitAnchor"] = anchor;

            return fp;
        }

        // ── AP entry builder ───────────────────────────────────────────────

        private static Dictionary<string, object> BuildApEntry(
            ApCandidate ap,
            Func<double, double, (double Ex, double Ey)> xform,
            string floorPlanId)
        {
            var (ex, ey) = xform(ap.WorldX, ap.WorldY);
            return new Dictionary<string, object>
            {
                ["id"]   = Guid.NewGuid().ToString(),
                ["name"] = ap.Name,
                ["location"] = new Dictionary<string, object>
                {
                    ["floorPlanId"] = floorPlanId,
                    ["coord"]       = new Dictionary<string, object>
                    {
                        ["x"] = Math.Round(ex, 6),
                        ["y"] = Math.Round(ey, 6),
                    },
                },
                ["mountingHeight"] = Math.Round(ap.HeightMeters, 2),
                ["status"] = "CREATED",
            };
        }

        // ── Utilities ──────────────────────────────────────────────────────

        private static (int Width, int Height) ReadPngDimensions(byte[] bytes)
        {
            // PNG: 8-byte signature, then IHDR chunk: 4-len 4-"IHDR" 4-W 4-H
            if (bytes.Length < 24) return (0, 0);
            int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return (w, h);
        }

        private static void DeleteView(Document doc, ElementId id)
        {
            if (id == ElementId.InvalidElementId) return;
            try
            {
                using var tx = new Transaction(doc, "Delete ESX export view");
                tx.Start();
                doc.Delete(id);
                tx.Commit();
            }
            catch { }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        /// <summary>Allow WPF to process pending events (updates progress window).</summary>
        private static void DoEvents()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 1 — Type resolver (v1 dead code removed; 2-value return)
    //  Req 2 — Checks ExtensibleStorage linked-model config as Layer 0
    // ═══════════════════════════════════════════════════════════════════════

    public static class EkahauTypeResolver
    {
        /// <summary>
        /// Resolve the Ekahau preset for a Revit element type.
        /// Resolution order:
        ///   0. User overrides from the mapping review dialog
        ///   1. ExtensibleStorage linked-model config
        ///   2. Shared parameter "Ekahau_WallType"
        ///   3. Keyword match (curtain wall / type name / material names)
        ///   4. Category fallback
        /// Returns (presetKey, source) — Req 1: NO custom_attn.
        /// </summary>
        public static (string PresetKey, string Source) ResolveEkahauType(
            Document doc, Element elemType,
            string categoryHint               = "wall",
            LinkWallMapping linkConfig        = null,
            Dictionary<string, string> overrides = null)
        {
            if (elemType == null)
                return ("Generic", "Fallback");

            // Layer 0: mapping-review user overrides
            if (overrides != null &&
                overrides.TryGetValue(elemType.UniqueId, out string ov) &&
                EkahauPresets.All.ContainsKey(ov))
                return (ov, "Parameter");

            // Layer 1a: ExtensibleStorage linked-model config
            if (linkConfig != null &&
                linkConfig.TypeUniqueIdToPreset.TryGetValue(
                    elemType.UniqueId, out string linked) &&
                EkahauPresets.All.ContainsKey(linked))
                return (linked, "Parameter");

            // Layer 1b: Shared parameter on the type element
            try
            {
                var p = elemType.LookupParameter("Ekahau_WallType");
                if (p != null && p.HasValue)
                {
                    string val = (p.AsString() ?? "").Trim();
                    if (!string.IsNullOrEmpty(val) && EkahauPresets.All.ContainsKey(val))
                        return (val, "Parameter");
                }
            }
            catch { }

            // Layer 2a: Curtain wall
            if (elemType is WallType wt)
            {
                try
                {
                    if (wt.Kind == WallKind.Curtain)
                        return ("CurtainWall", "Keyword");
                }
                catch { }
            }

            // Layer 2b: Keyword match on type name
            var nameMatch = KeywordMatcher.MatchWithKeyword(RevitHelpers.SafeName(elemType));
            if (nameMatch.HasValue)
                return (nameMatch.Value.PresetKey, "Keyword");

            // Layer 2c: Keyword match on compound material names
            if (elemType is WallType wallType)
            {
                foreach (string matName in RevitHelpers.GetCompoundMaterialNames(doc, wallType))
                {
                    var matMatch = KeywordMatcher.MatchWithKeyword(matName);
                    if (matMatch.HasValue)
                        return (matMatch.Value.PresetKey, "Keyword");
                }
            }

            // Layer 3: Category fallback
            return categoryHint switch
            {
                "door"   => ("WoodDoor", "Fallback"),
                "window" => ("Window",   "Fallback"),
                _        => ("Generic",  "Fallback"),
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Data structures (Req 1: custom_attn removed everywhere)
    // ═══════════════════════════════════════════════════════════════════════

    public class WallData
    {
        public long   WallId          { get; set; }
        public string TypeName        { get; set; }
        public string TypeUniqueId    { get; set; }
        public List<((double X, double Y) Start, (double X, double Y) End)> Segments { get; set; }
        public string PresetKey       { get; set; }
        public string Source          { get; set; }
        public double ThicknessMeters { get; set; }
    }

    public class OpeningData
    {
        public long            HostWallId { get; set; }
        public (double X, double Y) Center { get; set; }
        public double          WidthFeet  { get; set; }
        public string          PresetKey  { get; set; }
        public string          Source     { get; set; }
        public string          Category   { get; set; }
        public string          TypeName   { get; set; }
    }

    public class FinalSegment
    {
        public (double X, double Y) Start { get; set; }
        public (double X, double Y) End   { get; set; }
        public string PresetKey           { get; set; }
        public string Source              { get; set; }
        public double ThicknessMeters     { get; set; }
        public string TypeName            { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 2, 3 — Wall & opening collector
    //  Req 2: also collects linked-model elements with spatial filter
    //  Req 3: arc walls use curve.Evaluate() at ≤0.5 m step, min 8 points
    // ═══════════════════════════════════════════════════════════════════════

    public static class WallCollector
    {
        private const double FeetToMetres = 0.3048;

        // ── Host + linked walls ────────────────────────────────────────────

        public static List<WallData> CollectWalls(
            Document doc, ViewPlan view,
            LinkWallMapping linkConfig = null,
            Dictionary<string, string> overrides = null)
        {
            var result = new List<WallData>();

            // Host model walls (visible in view — Revit's view range handles level filtering)
            foreach (var elem in new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType().ToElements())
            {
                if (!ShouldCollectWall(elem)) continue;
                AddWall(doc, elem, null, Transform.Identity, linkConfig, overrides, result);
            }

            // Req 2: linked model walls — only when user configured mappings
            if (linkConfig != null && linkConfig.TypeUniqueIdToPreset.Count > 0)
            {
                var cropBox      = view.CropBox;
                var worldToLocal = cropBox.Transform.Inverse;

                foreach (var linkInst in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    // Only collect from the specific linked model configured in Param Config
                    if (!string.IsNullOrEmpty(linkConfig.SelectedLinkUniqueId) &&
                        linkInst.UniqueId != linkConfig.SelectedLinkUniqueId)
                        continue;

                    var linkDoc = linkInst.GetLinkDocument();
                    if (linkDoc == null) continue;

                    Transform linkXform = linkInst.GetTotalTransform();

                    foreach (var wall in new FilteredElementCollector(linkDoc)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType().ToElements())
                    {
                        if (!ShouldCollectWall(wall)) continue;

                        // Spatial filter: at least one endpoint in crop bounds
                        if (!(wall.Location is LocationCurve lc2)) continue;
                        var p0w = linkXform.OfPoint(lc2.Curve.GetEndPoint(0));
                        var p1w = linkXform.OfPoint(lc2.Curve.GetEndPoint(1));
                        if (!AnyPointInCrop(cropBox, worldToLocal, p0w, p1w)) continue;

                        AddWall(linkDoc, wall, linkDoc, linkXform, linkConfig, overrides, result);
                    }
                }
            }

            // Deduplicate overlapping segments from wall joins
            DeduplicateOverlaps(result);

            return result;
        }

        // ── Host + linked openings ────────────────────────────────────────

        public static List<OpeningData> CollectOpenings(
            Document doc, ViewPlan view,
            LinkWallMapping linkConfig = null,
            Dictionary<string, string> overrides = null)
        {
            var result = new List<OpeningData>();

            // Host openings (Revit's view range handles level filtering)
            foreach (var (bic, cat) in new[]
            {
                (BuiltInCategory.OST_Doors,   "door"),
                (BuiltInCategory.OST_Windows, "window"),
            })
            {
                foreach (var elem in new FilteredElementCollector(doc, view.Id)
                    .OfCategory(bic).WhereElementIsNotElementType().ToElements())
                {
                    AddOpening(doc, elem, cat, null, Transform.Identity, linkConfig, overrides, result);
                }
            }

            // Req 2: linked openings — only when user configured mappings
            if (linkConfig != null && linkConfig.TypeUniqueIdToPreset.Count > 0)
            {
                var cropBox      = view.CropBox;
                var worldToLocal = cropBox.Transform.Inverse;

                foreach (var linkInst in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    // Only collect from the specific linked model configured in Param Config
                    if (!string.IsNullOrEmpty(linkConfig.SelectedLinkUniqueId) &&
                        linkInst.UniqueId != linkConfig.SelectedLinkUniqueId)
                        continue;

                    var linkDoc    = linkInst.GetLinkDocument();
                    if (linkDoc == null) continue;
                    Transform lx = linkInst.GetTotalTransform();

                    foreach (var (bic, cat) in new[]
                    {
                        (BuiltInCategory.OST_Doors,   "door"),
                        (BuiltInCategory.OST_Windows, "window"),
                    })
                    {
                        foreach (var elem in new FilteredElementCollector(linkDoc)
                            .OfCategory(bic).WhereElementIsNotElementType().ToElements())
                        {
                            try
                            {
                                XYZ wp;
                                if (elem.Location is LocationPoint llp)
                                    wp = lx.OfPoint(llp.Point);
                                else continue;

                                if (!AnyPointInCrop(cropBox, worldToLocal, wp, wp)) continue;

                                AddOpening(linkDoc, elem, cat, linkDoc, lx, linkConfig, overrides, result);
                            }
                            catch { }
                        }
                    }
                }
            }
            return result;
        }

        // ── Private helpers ───────────────────────────────────────────────

        private static void AddWall(
            Document hostDoc, Element wall,
            Document typeDoc, Transform xform,
            LinkWallMapping linkConfig,
            Dictionary<string, string> overrides,
            List<WallData> result)
        {
            try
            {
                if (!(wall.Location is LocationCurve locCurve)) return;

                var doc = typeDoc ?? hostDoc;
                var wt  = wall is Wall w ? w.WallType
                         : doc.GetElement(wall.GetTypeId()) as WallType;

                // Req 3: arc-aware tessellation
                var localPts = CurveToPoints(locCurve.Curve);
                if (localPts.Count < 2) return;

                // Transform local points to host world
                var worldPts = localPts
                    .Select(p => xform.OfPoint(new XYZ(p.X, p.Y, 0)))
                    .Select(p => (p.X, p.Y))
                    .ToList();

                var (presetKey, source) = EkahauTypeResolver.ResolveEkahauType(
                    doc, wt, "wall", linkConfig, overrides);

                double thicknessM;
                try
                {
                    thicknessM = (wt?.Width ?? 0) * FeetToMetres;
                    if (thicknessM <= 0)
                        thicknessM = EkahauPresets.All.GetValueOrDefault(presetKey)
                            ?.DefaultThicknessMeters ?? 0.15;
                }
                catch
                {
                    thicknessM = EkahauPresets.All.GetValueOrDefault(presetKey)
                        ?.DefaultThicknessMeters ?? 0.15;
                }

                var segs = new List<((double, double), (double, double))>();
                for (int i = 0; i < worldPts.Count - 1; i++)
                    segs.Add((worldPts[i], worldPts[i + 1]));

                // Bug Fix #13 — diagnostic logging for "phantom wall" hunts.
                // Visible in DebugView/IDE Output Window when an unexpected
                // wall slips through the view filter.  Compare the wall IDs
                // listed here against the walls actually visible in the
                // Revit floor-plan view to spot impostors.
                try
                {
                    string wkind = "?";
                    try { wkind = wt?.Kind.ToString() ?? "?"; } catch { }
                    System.Diagnostics.Debug.WriteLine(
                        $"[ESX Export] Wall {VersionCompat.GetIdValue(wall.Id)} " +
                        $"Type='{(wt != null ? RevitHelpers.SafeName(wt) : "Unknown")}' " +
                        $"Kind={wkind} " +
                        $"Preset={presetKey} " +
                        $"Source={(typeDoc != null ? "LINK" : "HOST")} " +
                        $"World=({worldPts[0].X:F2},{worldPts[0].Y:F2})→" +
                        $"({worldPts[worldPts.Count - 1].X:F2},{worldPts[worldPts.Count - 1].Y:F2})");
                }
                catch { }

                result.Add(new WallData
                {
                    WallId         = VersionCompat.GetIdValue(wall.Id),
                    TypeName       = wt != null ? RevitHelpers.SafeName(wt) : "Unknown",
                    TypeUniqueId   = wt?.UniqueId ?? "",
                    Segments       = segs,
                    PresetKey      = presetKey,
                    Source         = source,
                    ThicknessMeters = thicknessM,
                });
            }
            catch { }
        }

        private static void AddOpening(
            Document elemDoc, Element elem, string category,
            Document typeDoc, Transform xform,
            LinkWallMapping linkConfig,
            Dictionary<string, string> overrides,
            List<OpeningData> result)
        {
            try
            {
                if (!(elem is FamilyInstance fi)) return;

                XYZ localPt;
                if (elem.Location is LocationPoint lp)
                    localPt = lp.Point;
                else if (elem.Location is LocationCurve lc)
                    localPt = lc.Curve.Evaluate(0.5, true);
                else return;

                XYZ worldPt = xform.OfPoint(localPt);

                var doc = typeDoc ?? elemDoc;
                var et  = doc.GetElement(elem.GetTypeId());
                double w = GetOpeningWidthFeet(elem, et);

                // Host wall ID (may be in linked doc — store as negative to avoid collision)
                long hostId;
                try
                {
                    hostId = fi.Host != null ? VersionCompat.GetIdValue(fi.Host.Id) : -1;
                    if (typeDoc != null) hostId = -hostId; // mark as linked
                }
                catch { hostId = -1; }

                var (presetKey, source) = EkahauTypeResolver.ResolveEkahauType(
                    doc, et, category, linkConfig, overrides);

                result.Add(new OpeningData
                {
                    HostWallId = hostId,
                    Center     = (worldPt.X, worldPt.Y),
                    WidthFeet  = w,
                    PresetKey  = presetKey,
                    Source     = source,
                    Category   = category,
                    TypeName   = et != null ? RevitHelpers.SafeName(et) : "Unknown",
                });
            }
            catch { }
        }

        /// <summary>
        /// Req 3: Arc/spline precision — 0.5 m step, minimum 8 points.
        /// For plain Line: just two endpoints.
        /// </summary>
        private static List<(double X, double Y)> CurveToPoints(Curve curve)
        {
            try
            {
                if (curve is Line)
                {
                    return new List<(double, double)>
                    {
                        (curve.GetEndPoint(0).X, curve.GetEndPoint(0).Y),
                        (curve.GetEndPoint(1).X, curve.GetEndPoint(1).Y),
                    };
                }

                // Non-line: sample at fixed intervals
                double lengthFt = curve.Length;
                double stepFt   = 0.5 / FeetToMetres; // 0.5 m expressed in feet
                int    nPts     = Math.Max(8, (int)Math.Ceiling(lengthFt / stepFt) + 1);

                var pts = new List<(double, double)>(nPts);
                for (int i = 0; i < nPts; i++)
                {
                    double t  = (double)i / (nPts - 1);
                    var    pt = curve.Evaluate(t, true); // normalised parameter
                    pts.Add((pt.X, pt.Y));
                }
                return pts;
            }
            catch
            {
                try
                {
                    // Last-resort: tessellate
                    return curve.Tessellate().Select(p => (p.X, p.Y)).ToList();
                }
                catch { return new List<(double, double)>(); }
            }
        }

        private static double GetOpeningWidthFeet(Element instance, Element elemType)
        {
            var bps = new[]
            {
                BuiltInParameter.DOOR_WIDTH,
                BuiltInParameter.WINDOW_WIDTH,
                BuiltInParameter.FAMILY_WIDTH_PARAM,
            };
            foreach (var target in new[] { instance, elemType })
            {
                if (target == null) continue;
                foreach (var bp in bps)
                {
                    try
                    {
                        var p = target.get_Parameter(bp);
                        if (p != null && p.HasValue) { double v = p.AsDouble(); if (v > 0) return v; }
                    }
                    catch { }
                }
                foreach (var pname in new[] { "Width", "width", "\u5bbd\u5ea6" })
                {
                    try
                    {
                        var p = target.LookupParameter(pname);
                        if (p != null && p.HasValue) { double v = p.AsDouble(); if (v > 0) return v; }
                    }
                    catch { }
                }
            }
            return 3.0; // ~0.9 m fallback
        }

        /// <summary>
        /// Decide whether a wall element should be collected.
        /// Collects ALL walls with a LocationCurve — DeduplicateOverlaps
        /// handles any overlapping segments safely after collection.
        /// </summary>
        private static bool ShouldCollectWall(Element elem)
        {
            return elem is Wall;
        }

        /// <summary>
        /// Remove duplicate wall segments caused by wall joins.
        /// Two segments from different walls that overlap significantly
        /// (same direction, close position, >80% overlap) → keep the thicker one.
        /// </summary>
        private static void DeduplicateOverlaps(List<WallData> walls)
        {
            if (walls.Count < 2) return;

            // Build a flat list of (wallIndex, segIndex) for comparison
            var toRemove = new HashSet<(int Wi, int Si)>();

            for (int i = 0; i < walls.Count; i++)
            {
                for (int j = i + 1; j < walls.Count; j++)
                {
                    foreach (int si in Enumerable.Range(0, walls[i].Segments.Count))
                    {
                        var segA = walls[i].Segments[si];
                        foreach (int sj in Enumerable.Range(0, walls[j].Segments.Count))
                        {
                            if (toRemove.Contains((j, sj))) continue;
                            var segB = walls[j].Segments[sj];

                            if (SegmentsOverlap(segA, segB, 0.8))
                            {
                                // Keep the thicker wall, remove the thinner
                                if (walls[i].ThicknessMeters >= walls[j].ThicknessMeters)
                                    toRemove.Add((j, sj));
                                else
                                    toRemove.Add((i, si));
                            }
                        }
                    }
                }
            }

            if (toRemove.Count == 0) return;

            // Remove flagged segments (process in reverse order per wall)
            foreach (var group in toRemove.GroupBy(r => r.Wi).OrderByDescending(g => g.Key))
            {
                var indices = group.Select(g => g.Si).OrderByDescending(x => x).ToList();
                foreach (int si in indices)
                {
                    if (si < walls[group.Key].Segments.Count)
                        walls[group.Key].Segments.RemoveAt(si);
                }
            }

            // Remove walls with no remaining segments
            walls.RemoveAll(w => w.Segments.Count == 0);
        }

        /// <summary>
        /// Check if two segments are overlapping: nearly parallel, close together,
        /// and sharing >overlapRatio of their length.
        /// </summary>
        private static bool SegmentsOverlap(
            ((double X, double Y) Start, (double X, double Y) End) a,
            ((double X, double Y) Start, (double X, double Y) End) b,
            double overlapRatio)
        {
            double ax = a.End.X - a.Start.X, ay = a.End.Y - a.Start.Y;
            double bx = b.End.X - b.Start.X, by = b.End.Y - b.Start.Y;
            double lenA = Math.Sqrt(ax * ax + ay * ay);
            double lenB = Math.Sqrt(bx * bx + by * by);
            if (lenA < 1e-6 || lenB < 1e-6) return false;

            // Check parallelism: |cross product| / (lenA * lenB) < threshold
            double cross = Math.Abs(ax * by - ay * bx);
            if (cross / (lenA * lenB) > 0.1) return false; // >~6° angle — not parallel

            // Check proximity: distance from midpoint of A to line of B
            double midAx = (a.Start.X + a.End.X) / 2, midAy = (a.Start.Y + a.End.Y) / 2;
            double distToLine = Math.Abs((midAx - b.Start.X) * by - (midAy - b.Start.Y) * bx) / lenB;
            if (distToLine > 1.5) return false; // >1.5 ft apart — not overlapping

            // Check overlap extent: project both onto the longer segment's direction
            double shorter = Math.Min(lenA, lenB);
            // Project A's endpoints onto B's line
            double dotS = ((a.Start.X - b.Start.X) * bx + (a.Start.Y - b.Start.Y) * by) / lenB;
            double dotE = ((a.End.X   - b.Start.X) * bx + (a.End.Y   - b.Start.Y) * by) / lenB;
            double projMin = Math.Min(dotS, dotE);
            double projMax = Math.Max(dotS, dotE);

            double overlapStart = Math.Max(0, projMin);
            double overlapEnd   = Math.Min(lenB, projMax);
            double overlapLen   = Math.Max(0, overlapEnd - overlapStart);

            return overlapLen / shorter > overlapRatio;
        }

        private static bool AnyPointInCrop(
            BoundingBoxXYZ cropBox, Transform worldToLocal, XYZ p0, XYZ p1)
        {
            bool InBounds(XYZ wp)
            {
                XYZ lp = worldToLocal.OfPoint(wp);
                // Add 10% padding to catch walls that clip the crop edge
                double padX = (cropBox.Max.X - cropBox.Min.X) * 0.1;
                double padY = (cropBox.Max.Y - cropBox.Min.Y) * 0.1;
                return lp.X >= cropBox.Min.X - padX && lp.X <= cropBox.Max.X + padX &&
                       lp.Y >= cropBox.Min.Y - padY && lp.Y <= cropBox.Max.Y + padY;
            }
            // Also check midpoint
            var mid = new XYZ((p0.X + p1.X) / 2, (p0.Y + p1.Y) / 2, 0);
            return InBounds(p0) || InBounds(p1) || InBounds(mid);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 4 — Wall splitter: overlapping opening merge keeps higher 5 GHz
    // ═══════════════════════════════════════════════════════════════════════

    public static class WallSplitter
    {
        public static List<FinalSegment> SplitWallsWithOpenings(
            List<WallData> walls, List<OpeningData> openings)
        {
            // Group openings by host wall ID
            var byWall = new Dictionary<long, List<OpeningData>>();
            foreach (var op in openings)
            {
                if (!byWall.TryGetValue(op.HostWallId, out var lst))
                    byWall[op.HostWallId] = lst = new List<OpeningData>();
                lst.Add(op);
            }

            var final = new List<FinalSegment>();

            foreach (var wall in walls)
            {
                byWall.TryGetValue(wall.WallId, out var wallOps);

                foreach (var (segStart, segEnd) in wall.Segments)
                {
                    double segLen = Dist(segStart, segEnd);
                    if (segLen < 1e-6) continue;

                    if (wallOps == null || wallOps.Count == 0)
                    {
                        final.Add(WallSeg(segStart, segEnd, wall));
                        continue;
                    }

                    // Project openings onto segment parameter [0,1]
                    var intervals = new List<(double T0, double T1, OpeningData Op)>();
                    foreach (var op in wallOps)
                    {
                        double tC   = ProjectT(segStart, segEnd, op.Center);
                        double half = (op.WidthFeet / 2.0) / segLen;
                        double t0   = Math.Max(0.0, tC - half);
                        double t1   = Math.Min(1.0, tC + half);
                        if (t1 - t0 < 1e-6) continue;
                        intervals.Add((t0, t1, op));
                    }
                    intervals.Sort((a, b) => a.T0.CompareTo(b.T0));

                    // Req 4: merge overlapping intervals — keep higher 5 GHz attenuation
                    var merged = new List<(double T0, double T1, OpeningData Op)>();
                    foreach (var iv in intervals)
                    {
                        if (merged.Count > 0)
                        {
                            var last = merged[merged.Count - 1];
                            if (iv.T0 < last.T1 + 1e-6)
                            {
                                // Overlapping — keep the type with higher 5 GHz attenuation
                                var lastPrs = EkahauPresets.All.GetValueOrDefault(last.Op.PresetKey);
                                var ivPrs   = EkahauPresets.All.GetValueOrDefault(iv.Op.PresetKey);
                                double lastA = lastPrs?.AttenuationFiveGHz ?? 0;
                                double ivA   = ivPrs?.AttenuationFiveGHz   ?? 0;
                                var winner = ivA > lastA ? iv.Op : last.Op;
                                double newT1 = Math.Max(last.T1, iv.T1);
                                merged[merged.Count - 1] = (last.T0, newT1, winner);
                                continue;
                            }
                        }
                        merged.Add(iv);
                    }

                    double cursor = 0.0;
                    foreach (var iv in merged)
                    {
                        if (iv.T0 > cursor + 1e-6)
                            final.Add(WallSeg(Along(segStart, segEnd, cursor),
                                              Along(segStart, segEnd, iv.T0), wall));

                        var opPreset = EkahauPresets.All.GetValueOrDefault(iv.Op.PresetKey);
                        final.Add(new FinalSegment
                        {
                            Start          = Along(segStart, segEnd, iv.T0),
                            End            = Along(segStart, segEnd, iv.T1),
                            PresetKey      = iv.Op.PresetKey,
                            Source         = iv.Op.Source,
                            ThicknessMeters= opPreset?.DefaultThicknessMeters ?? 0.04,
                            TypeName       = iv.Op.TypeName,
                        });
                        cursor = iv.T1;
                    }

                    if (cursor < 1.0 - 1e-6)
                        final.Add(WallSeg(Along(segStart, segEnd, cursor),
                                          Along(segStart, segEnd, 1.0), wall));
                }
            }
            return final;
        }

        private static FinalSegment WallSeg(
            (double X, double Y) s, (double X, double Y) e, WallData w)
            => new FinalSegment
            {
                Start           = s,
                End             = e,
                PresetKey       = w.PresetKey,
                Source          = w.Source,
                ThicknessMeters = w.ThicknessMeters,
                TypeName        = w.TypeName,
            };

        private static double ProjectT(
            (double X, double Y) s, (double X, double Y) e, (double X, double Y) p)
        {
            double dx = e.X - s.X, dy = e.Y - s.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12) return 0;
            double t = ((p.X - s.X) * dx + (p.Y - s.Y) * dy) / len2;
            return Math.Max(0, Math.Min(1, t));
        }

        private static (double X, double Y) Along(
            (double X, double Y) s, (double X, double Y) e, double t)
            => (s.X + (e.X - s.X) * t, s.Y + (e.Y - s.Y) * t);

        private static double Dist((double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Bug Fix #6 — Endpoint snapping: close gaps at wall corners
    //  Runs in Revit feet BEFORE coordinate transform to Ekahau pixels.
    //  1. Endpoint-to-endpoint snapping (nearby ends → midpoint)
    //  2. L-corner line-line intersection (angled walls → exact corner)
    // ═══════════════════════════════════════════════════════════════════════

    public static class EndpointSnapper
    {
        /// <summary>
        /// Maximum angle change (degrees) allowed by a snap.  Bug Fix #13:
        /// at typical residential floor-plan scales, the old 2.0 ft snap
        /// threshold was pulling unrelated walls together and tilting
        /// orthogonal walls 1-7°.  Reject any snap that would tilt either
        /// segment by more than this.
        /// </summary>
        private const double MaxAngleChangeDeg = 2.0;

        /// <summary>
        /// Snap nearby segment endpoints together to close corner gaps.
        /// <paramref name="snapFt"/> is the threshold in feet.
        /// Default 0.5 ft (~6 inch / 15 cm) — enough to close genuine wall
        /// joins without reaching across to unrelated walls (Bug Fix #13).
        /// Mutates the segments in-place.
        /// </summary>
        public static void Snap(List<FinalSegment> segs, double snapFt = 0.5)
        {
            if (segs == null || segs.Count < 2) return;

            double thresh2 = snapFt * snapFt;

            // ── Pass 1: L-corner line-line intersection ──────────────────
            // For each pair of segments whose endpoints are near each other,
            // compute the intersection of their infinite lines.  If the
            // intersection is within threshold of both original endpoints,
            // move both to the intersection — this handles angled corners.
            for (int i = 0; i < segs.Count; i++)
            {
                for (int j = i + 1; j < segs.Count; j++)
                {
                    TrySnapCorner(segs, i, j, snapFt, thresh2);
                }
            }

            // ── Pass 2: endpoint-to-endpoint midpoint snap ───────────────
            // Catches any remaining close pairs that aren't L-corners
            // (e.g. collinear walls with small gaps).
            for (int i = 0; i < segs.Count; i++)
            {
                for (int j = i + 1; j < segs.Count; j++)
                {
                    TrySnapEndpoints(segs, i, j, thresh2);
                }
            }
        }

        // ── L-corner: intersect two line-extended segments ───────────────

        private static void TrySnapCorner(
            List<FinalSegment> segs, int i, int j,
            double snapFt, double thresh2)
        {
            var si = segs[i]; var sj = segs[j];

            // Check all 4 endpoint pairs to find the closest pair
            var pairs = new[]
            {
                (ei: 's', ej: 's', pi: si.Start, pj: sj.Start),
                (ei: 's', ej: 'e', pi: si.Start, pj: sj.End),
                (ei: 'e', ej: 's', pi: si.End,   pj: sj.Start),
                (ei: 'e', ej: 'e', pi: si.End,   pj: sj.End),
            };

            foreach (var (ei, ej, pi, pj) in pairs)
            {
                double d2 = Dist2(pi, pj);
                if (d2 > thresh2 || d2 < 1e-12) continue; // already coincident or too far

                // Compute line-line intersection of the two segments' infinite lines
                var ix = LineLineIntersect(si.Start, si.End, sj.Start, sj.End);
                if (ix == null) continue; // parallel lines

                var pt = ix.Value;

                // Verify intersection is close to BOTH original endpoints
                if (Dist2(pt, pi) > thresh2 || Dist2(pt, pj) > thresh2)
                    continue;

                // Bug Fix #13 — reject snaps that would tilt either segment
                // more than MaxAngleChangeDeg.  This prevents the snapper
                // from pulling an orthogonal wall sideways to meet a stray
                // endpoint of an unrelated wall.
                var newSiStart = ei == 's' ? pt : si.Start;
                var newSiEnd   = ei == 'e' ? pt : si.End;
                var newSjStart = ej == 's' ? pt : sj.Start;
                var newSjEnd   = ej == 'e' ? pt : sj.End;
                if (!IsAngleChangeAcceptable(si.Start, si.End, newSiStart, newSiEnd) ||
                    !IsAngleChangeAcceptable(sj.Start, sj.End, newSjStart, newSjEnd))
                    continue;

                // Move both endpoints to the intersection
                if (ei == 's') si.Start = pt; else si.End = pt;
                if (ej == 's') sj.Start = pt; else sj.End = pt;
                return; // one snap per pair is enough
            }
        }

        // ── Simple endpoint-to-endpoint midpoint snap ────────────────────

        private static void TrySnapEndpoints(
            List<FinalSegment> segs, int i, int j, double thresh2)
        {
            var si = segs[i]; var sj = segs[j];

            var pairs = new[]
            {
                (ei: 's', ej: 's', pi: si.Start, pj: sj.Start),
                (ei: 's', ej: 'e', pi: si.Start, pj: sj.End),
                (ei: 'e', ej: 's', pi: si.End,   pj: sj.Start),
                (ei: 'e', ej: 'e', pi: si.End,   pj: sj.End),
            };

            foreach (var (ei, ej, pi, pj) in pairs)
            {
                double d2 = Dist2(pi, pj);
                if (d2 > thresh2 || d2 < 1e-12) continue;

                var mid = ((pi.X + pj.X) / 2.0, (pi.Y + pj.Y) / 2.0);

                // Bug Fix #13 — angle-change guard (same logic as TrySnapCorner)
                var newSiStart = ei == 's' ? mid : si.Start;
                var newSiEnd   = ei == 'e' ? mid : si.End;
                var newSjStart = ej == 's' ? mid : sj.Start;
                var newSjEnd   = ej == 'e' ? mid : sj.End;
                if (!IsAngleChangeAcceptable(si.Start, si.End, newSiStart, newSiEnd) ||
                    !IsAngleChangeAcceptable(sj.Start, sj.End, newSjStart, newSjEnd))
                    continue;

                if (ei == 's') si.Start = mid; else si.End = mid;
                if (ej == 's') sj.Start = mid; else sj.End = mid;
                return;
            }
        }

        // ── Bug Fix #13: angle-preservation guard ────────────────────────

        /// <summary>
        /// Returns true when moving a segment from (a→b) to (a'→b') tilts
        /// it by no more than <see cref="MaxAngleChangeDeg"/>.  Returns
        /// true for degenerate (zero-length) segments since they have no
        /// meaningful angle to preserve.
        /// </summary>
        private static bool IsAngleChangeAcceptable(
            (double X, double Y) a,  (double X, double Y) b,
            (double X, double Y) a2, (double X, double Y) b2)
        {
            const double minLen2 = 1e-12;
            double dx0 = b.X - a.X,   dy0 = b.Y - a.Y;
            double dx1 = b2.X - a2.X, dy1 = b2.Y - a2.Y;
            if (dx0 * dx0 + dy0 * dy0 < minLen2) return true;
            if (dx1 * dx1 + dy1 * dy1 < minLen2) return true;

            double a0 = Math.Atan2(dy0, dx0);
            double a1 = Math.Atan2(dy1, dx1);
            double diffDeg = (a1 - a0) * 180.0 / Math.PI;
            while (diffDeg >  180) diffDeg -= 360;
            while (diffDeg < -180) diffDeg += 360;
            return Math.Abs(diffDeg) <= MaxAngleChangeDeg;
        }

        // ── Line-line intersection (2D) ──────────────────────────────────

        private static (double X, double Y)? LineLineIntersect(
            (double X, double Y) a1, (double X, double Y) a2,
            (double X, double Y) b1, (double X, double Y) b2)
        {
            double d1x = a2.X - a1.X, d1y = a2.Y - a1.Y;
            double d2x = b2.X - b1.X, d2y = b2.Y - b1.Y;
            double denom = d1x * d2y - d1y * d2x;
            if (Math.Abs(denom) < 1e-12) return null; // parallel

            double t = ((b1.X - a1.X) * d2y - (b1.Y - a1.Y) * d2x) / denom;
            return (a1.X + t * d1x, a1.Y + t * d1y);
        }

        private static double Dist2((double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return dx * dx + dy * dy;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Req 1, 9, 17 — Ekahau JSON builder
    //  - custom_attn removed (Req 1)
    //  - uses shared globalTypeCache so multi-floor ESXs share type IDs (Req 9)
    //  - per-segment debug logging (Req 17)
    // ═══════════════════════════════════════════════════════════════════════

    public static class EkahauJsonBuilder
    {
        private const double FeetToMetres = 0.3048;
        private const double MinSegLenM   = 0.01; // skip sub-1 cm segments

        /// <summary>
        /// Build Ekahau wall JSON for one floor plan.
        /// Wall types are accumulated into the shared <paramref name="globalTypeCache"/>
        /// and <paramref name="globalWallTypes"/> so identical preset keys across
        /// multiple floors share the same Ekahau type GUID.
        /// </summary>
        public static (List<Dictionary<string, object>> WallSegments,
                        List<Dictionary<string, object>> WallPoints)
            BuildWallJson(
                List<FinalSegment> finalSegments,
                string floorPlanId,
                Func<double, double, (double Ex, double Ey)> xform,
                Dictionary<string, Dictionary<string, object>> globalTypeCache,
                List<Dictionary<string, object>> globalWallTypes,
                StringBuilder debugLog = null)
        {
            var wallPoints   = new List<Dictionary<string, object>>();
            var wallSegments = new List<Dictionary<string, object>>();

            string GetOrCreateType(string presetKey, double thicknessM)
            {
                // Req 9: shared cache — same preset key → same Ekahau type GUID
                if (!globalTypeCache.TryGetValue(presetKey, out var wt))
                {
                    wt = MakeWallType(presetKey, thicknessM);
                    globalTypeCache[presetKey] = wt;
                    globalWallTypes.Add(wt);
                }
                return (string)wt["id"];
            }

            int segIdx = 0;
            foreach (var seg in finalSegments)
            {
                double dx = seg.End.X - seg.Start.X;
                double dy = seg.End.Y - seg.Start.Y;
                double lenM = Math.Sqrt(dx * dx + dy * dy) * FeetToMetres;
                if (lenM < MinSegLenM)
                {
                    debugLog?.AppendLine($"  [skip] seg#{segIdx} too short ({lenM:F4} m)");
                    segIdx++;
                    continue;
                }

                string wtId = GetOrCreateType(seg.PresetKey, seg.ThicknessMeters);

                var (ekX1, ekY1) = xform(seg.Start.X, seg.Start.Y);
                var (ekX2, ekY2) = xform(seg.End.X,   seg.End.Y);

                // Req 17: debug log per segment
                debugLog?.AppendLine(
                    $"  seg#{segIdx} {seg.TypeName}[{seg.PresetKey}/{seg.Source}] " +
                    $"len={lenM:F2}m  EK:({ekX1:F1},{ekY1:F1})→({ekX2:F1},{ekY2:F1})");

                string pt1Id = Guid.NewGuid().ToString();
                string pt2Id = Guid.NewGuid().ToString();

                wallPoints.Add(new Dictionary<string, object>
                {
                    ["id"]     = pt1Id,
                    ["location"] = new Dictionary<string, object>
                    {
                        ["floorPlanId"] = floorPlanId,
                        ["coord"]       = new Dictionary<string, object>
                        {
                            ["x"] = Math.Round(ekX1, 6),
                            ["y"] = Math.Round(ekY1, 6),
                        },
                    },
                    ["status"] = "CREATED",
                });
                wallPoints.Add(new Dictionary<string, object>
                {
                    ["id"]     = pt2Id,
                    ["location"] = new Dictionary<string, object>
                    {
                        ["floorPlanId"] = floorPlanId,
                        ["coord"]       = new Dictionary<string, object>
                        {
                            ["x"] = Math.Round(ekX2, 6),
                            ["y"] = Math.Round(ekY2, 6),
                        },
                    },
                    ["status"] = "CREATED",
                });

                wallSegments.Add(new Dictionary<string, object>
                {
                    ["id"]          = Guid.NewGuid().ToString(),
                    ["wallTypeId"]  = wtId,
                    ["wallPoints"]  = new[] { pt1Id, pt2Id },
                    ["originType"]  = "REVIT_EXPORT",
                    ["status"]      = "CREATED",
                });

                segIdx++;
            }

            return (wallSegments, wallPoints);
        }

        /// <summary>
        /// Req 1: Build Ekahau wallType dict from preset — no custom_attn.
        /// </summary>
        private static Dictionary<string, object> MakeWallType(
            string presetKey, double thicknessM)
        {
            var preset = EkahauPresets.All.GetValueOrDefault(presetKey)
                      ?? EkahauPresets.All["Generic"];

            return new Dictionary<string, object>
            {
                ["id"]        = Guid.NewGuid().ToString(),
                ["name"]      = preset.Name,
                ["key"]       = presetKey,
                ["color"]     = preset.Color,
                ["thickness"] = Math.Round(thicknessM, 4),
                ["lowerEdge"] = 0.0,
                ["upperEdge"] = 12.2,
                ["propagationProperties"] = new object[]
                {
                    PropEntry("TWO",  preset.AttenuationTwoGHz,  preset),
                    PropEntry("FIVE", preset.AttenuationFiveGHz, preset),
                    PropEntry("SIX",  preset.AttenuationSixGHz,  preset),
                },
                ["status"] = "CREATED",
            };
        }

        private static Dictionary<string, object> PropEntry(
            string band, double attn, EkahauPreset preset)
            => new Dictionary<string, object>
            {
                ["band"]                  = band,
                ["attenuationFactor"]     = attn,
                ["reflectionCoefficient"] = preset.ReflectionCoefficient,
                ["diffractionCoefficient"]= preset.DiffractionCoefficient,
            };
    }
}
