using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Data models
    // ═══════════════════════════════════════════════════════════════════════

    public class TypeItem
    {
        public Element Element { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }      // "wall" | "door" | "window"
        public string CurrentValue { get; set; }  // existing Ekahau_WallType value if any
        public string Suggested { get; set; }     // suggested preset key
        public string SuggestSource { get; set; } = ""; // Req 7: human-readable hint
        public string Source { get; set; } = "host";    // Req 3: "host" | "link"
        public string LinkName { get; set; } = "";      // Req 3c: "Structure.rvt"
        public string TypeUniqueId { get; set; } = "";  // Req 3d: for ExtStorage key
    }

    public class LinkedModelInfo
    {
        public RevitLinkInstance Instance { get; set; }
        public Document Document { get; set; }
        public Transform Transform { get; set; }
        public string Name { get; set; }
        public string UniqueId { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Command
    // ═══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class ParamConfigCommand : IExternalCommand
    {
        private const string ParamGroupName = "Ekahau WiFi";

        // Req 1: Only Ekahau_WallType — three attenuation params removed
        private static readonly List<(string Name, bool IsText)> ParamsToCreate =
            new List<(string, bool)> { ("Ekahau_WallType", true) };

        private static readonly BuiltInCategory[] TargetCategories =
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
        };

        // Req 3d: Stable schema GUID for ExtensibleStorage
        private static readonly Guid SchemaGuid = new Guid("E7B8C9D0-F1A2-3456-BCDE-F0123456789A");
        private const string SchemaName = "EkahauLinkWallMapping";
        private const string FieldName  = "MappingJson";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc   = uiDoc.Document;
            var app   = commandData.Application.Application;
            var activeView = uiDoc.ActiveView;
            string viewName = RevitHelpers.SafeName(activeView);

            // ── Step 1: Create shared parameters ────────────────────────────
            // Req 9: save/restore SharedParametersFilename
            string originalSpPath = app.SharedParametersFilename;
            try
            {
                using (var t = new Transaction(doc, "Create Ekahau Shared Parameters"))
                {
                    t.Start();
                    try
                    {
                        int created = CreateSharedParameters(doc, app);
                        t.Commit();
                        if (created > 0)
                            TaskDialog.Show("Parameters Created",
                                $"{created} Ekahau parameter(s) created.\n\nScanning types in active view...");
                    }
                    catch (Exception ex)
                    {
                        t.RollBack();
                        TaskDialog.Show("Error", $"Failed to create shared parameters:\n\n{ex.Message}");
                        return Result.Failed;
                    }
                }
            }
            finally
            {
                // Req 9: restore original path regardless of success/failure
                if (!string.IsNullOrEmpty(originalSpPath))
                {
                    try { app.SharedParametersFilename = originalSpPath; } catch { }
                }
            }

            // ── Step 2: Select linked model (Req 3a) ────────────────────────
            LinkedModelInfo selectedLink = SelectLinkedModel(doc);

            // ── Step 3: Collect types from host + optional linked model ──────
            var typeItems = CollectViewTypeElements(doc, activeView, selectedLink);
            if (typeItems.Count == 0)
            {
                TaskDialog.Show("Nothing Found",
                    $"No Wall / Door / Window elements found in\n\"{viewName}\".\n\n" +
                    "Make sure you are in a floor plan view that contains walls, doors, or windows.");
                return Result.Succeeded;
            }

            // ── Step 4: Show WPF mapping dialog ─────────────────────────────
            var dlg = new MappingDialog(typeItems, viewName, selectedLink?.Name);
            bool? dialogResult = dlg.ShowDialog();
            if (dialogResult != true || dlg.Result == null)
                return Result.Succeeded;

            var selections = dlg.Result; // Dictionary<int, string>: rowIndex → presetKey
            if (selections.Count == 0)
            {
                TaskDialog.Show("Nothing to Apply", "All types set to \"Skip\". No changes made.");
                return Result.Succeeded;
            }

            // ── Step 5: Apply mappings ───────────────────────────────────────
            using (var t2 = new Transaction(doc, "Set Ekahau WallType Parameters"))
            {
                t2.Start();
                try
                {
                    var (updated, skipped) = ApplyMappings(doc, typeItems, selections, selectedLink);
                    t2.Commit();
                    TaskDialog.Show("Done",
                        $"Configuration Complete\n\n" +
                        $"  Updated:  {updated}\n  Skipped:  {skipped}\n" +
                        $"  Total:    {typeItems.Count}\n\nValues saved in the project.");
                }
                catch (Exception ex)
                {
                    t2.RollBack();
                    TaskDialog.Show("Error", $"Failed:\n\n{ex.Message}");
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Step 1 helpers — Shared Parameter Creation
        // ═══════════════════════════════════════════════════════════════════

        private DefinitionFile EnsureSharedParameterFile(
            Autodesk.Revit.ApplicationServices.Application app)
        {
            var spFile = app.OpenSharedParameterFile();
            if (spFile != null) return spFile;

            string spPath = app.SharedParametersFilename;
            if (string.IsNullOrEmpty(spPath) || !File.Exists(spPath))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                spPath = Path.Combine(appData, "Ekahau_SharedParams.txt");
                if (!File.Exists(spPath)) File.WriteAllText(spPath, "");
                app.SharedParametersFilename = spPath;
            }
            return app.OpenSharedParameterFile();
        }

        private DefinitionGroup GetOrCreateGroup(DefinitionFile spFile)
        {
            var group = spFile.Groups.get_Item(ParamGroupName);
            return group ?? spFile.Groups.Create(ParamGroupName);
        }

        private ExternalDefinition CreateDefinition(DefinitionGroup group, string name, bool isText)
        {
            if (group.Definitions.get_Item(name) is ExternalDefinition existing)
                return existing;
            try
            {
                var opts = VersionCompat.CreateParamOptions(name, isText);
                return group.Definitions.Create(opts) as ExternalDefinition;
            }
            catch { return null; }
        }

        private CategorySet BuildCategorySet(Document doc)
        {
            var catSet = new CategorySet();
            foreach (var bic in TargetCategories)
            {
                try
                {
                    var cat = doc.Settings.Categories.get_Item(bic);
                    if (cat != null) catSet.Insert(cat);
                }
                catch { }
            }
            return catSet;
        }

        private int CreateSharedParameters(
            Document doc, Autodesk.Revit.ApplicationServices.Application app)
        {
            var spFile = EnsureSharedParameterFile(app)
                ?? throw new InvalidOperationException("Cannot open or create shared parameter file.");
            var group  = GetOrCreateGroup(spFile);
            var catSet = BuildCategorySet(doc);
            int created = 0;

            foreach (var (paramName, isText) in ParamsToCreate)
            {
                var defn = CreateDefinition(group, paramName, isText);
                if (defn == null) continue;

                var existingBinding = doc.ParameterBindings.get_Item(defn);
                if (existingBinding != null)
                {
                    // Req 2: Fix incomplete category binding — check all TARGET_CATEGORIES present
                    if (existingBinding is TypeBinding tb)
                    {
                        bool allPresent = TargetCategories.All(bic =>
                        {
                            try
                            {
                                var cat = doc.Settings.Categories.get_Item(bic);
                                return cat == null || tb.Categories.Contains(cat);
                            }
                            catch { return true; }
                        });

                        if (!allPresent)
                        {
                            // Rebuild binding with complete category set
                            try { doc.ParameterBindings.ReInsert(defn, new TypeBinding(catSet)); }
                            catch { }
                        }
                    }
                    continue;
                }

                // New parameter — Insert binding
                try
                {
                    if (doc.ParameterBindings.Insert(defn, new TypeBinding(catSet)))
                        created++;
                }
                catch { }
            }
            return created;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Step 2 — Linked model selection (Req 3a)
        // ═══════════════════════════════════════════════════════════════════

        private LinkedModelInfo SelectLinkedModel(Document doc)
        {
            // Find all loaded RevitLinkInstances
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .Where(l => l.GetLinkDocument() != null)
                .ToList();

            if (links.Count == 0) return null; // no loaded links — skip step

            var linkNames = links.Select(l =>
            {
                try { return Path.GetFileName(l.GetLinkDocument().PathName); }
                catch { return RevitHelpers.SafeName(l); }
            }).ToList();

            var selectorDlg = new LinkedModelSelectorDialog(linkNames);
            bool? ok = selectorDlg.ShowDialog();
            if (ok != true || selectorDlg.SelectedIndex < 0)
                return null; // user chose host only or cancelled

            var inst = links[selectorDlg.SelectedIndex];
            var linkDoc = inst.GetLinkDocument();
            return new LinkedModelInfo
            {
                Instance  = inst,
                Document  = linkDoc,
                Transform = inst.GetTotalTransform(),
                Name      = linkNames[selectorDlg.SelectedIndex],
                UniqueId  = inst.UniqueId,
            };
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Step 3 — Collect types (Req 3b + Req 7)
        // ═══════════════════════════════════════════════════════════════════

        private List<TypeItem> CollectViewTypeElements(
            Document doc, View view, LinkedModelInfo link)
        {
            var result = new List<TypeItem>();
            var seenHostTypeIds = new HashSet<long>();

            var catMap = new[]
            {
                (BuiltInCategory.OST_Walls,   "wall"),
                (BuiltInCategory.OST_Doors,   "door"),
                (BuiltInCategory.OST_Windows, "window"),
            };

            // ── Host model ────────────────────────────────────────────────
            foreach (var (bic, catLabel) in catMap)
            {
                IList<Element> instances;
                try
                {
                    instances = new FilteredElementCollector(doc, view.Id)
                        .OfCategory(bic).WhereElementIsNotElementType().ToElements();
                }
                catch { continue; }

                foreach (var inst in instances)
                {
                    var typeId = inst.GetTypeId();
                    if (typeId == null || typeId == ElementId.InvalidElementId) continue;
                    long tid = typeId.Value;
                    if (!seenHostTypeIds.Add(tid)) continue;

                    var elemType = doc.GetElement(typeId);
                    if (elemType == null) continue;

                    string name = RevitHelpers.SafeName(elemType);
                    string currentVal = RevitHelpers.ReadStringParam(elemType, "Ekahau_WallType");

                    string suggested, suggestSource;
                    if (!string.IsNullOrEmpty(currentVal))
                    {
                        suggested = currentVal;
                        suggestSource = $"Param: {currentVal}"; // Req 7
                    }
                    else
                    {
                        (suggested, suggestSource) = RevitHelpers.SuggestPreset(doc, elemType, catLabel);
                    }

                    result.Add(new TypeItem
                    {
                        Element = elemType, Name = name, Category = catLabel,
                        CurrentValue = currentVal, Suggested = suggested,
                        SuggestSource = suggestSource,
                        Source = "host", TypeUniqueId = elemType.UniqueId,
                    });
                }
            }

            // ── Linked model (Req 3b) ────────────────────────────────────
            if (link != null)
            {
                var (minX, minY, maxX, maxY) = GetCropBoxBounds(view);
                var seenLinkTypeIds = new HashSet<long>();

                foreach (var (bic, catLabel) in catMap)
                {
                    IList<Element> instances;
                    try
                    {
                        instances = new FilteredElementCollector(link.Document)
                            .OfCategory(bic).WhereElementIsNotElementType().ToElements();
                    }
                    catch { continue; }

                    foreach (var inst in instances)
                    {
                        // Spatial filter: transform location to host space, check CropBox
                        if (!IsInstanceInBounds(inst, link.Transform, minX, minY, maxX, maxY))
                            continue;

                        var typeId = inst.GetTypeId();
                        if (typeId == null || typeId == ElementId.InvalidElementId) continue;
                        long tid = typeId.Value;
                        if (!seenLinkTypeIds.Add(tid)) continue;

                        var elemType = link.Document.GetElement(typeId);
                        if (elemType == null) continue;

                        string name = RevitHelpers.SafeName(elemType);
                        // Linked types never have Ekahau_WallType param (read-only model)
                        var (suggested, suggestSource) =
                            RevitHelpers.SuggestPreset(link.Document, elemType, catLabel);

                        result.Add(new TypeItem
                        {
                            Element = elemType, Name = name, Category = catLabel,
                            CurrentValue = "", Suggested = suggested,
                            SuggestSource = suggestSource,
                            Source = "link", LinkName = link.Name,
                            TypeUniqueId = elemType.UniqueId,
                        });
                    }
                }
            }

            return result;
        }

        private (double MinX, double MinY, double MaxX, double MaxY) GetCropBoxBounds(View view)
        {
            try
            {
                var cb = view.CropBox;
                var t  = cb.Transform;
                var corners = new[]
                {
                    t.OfPoint(new XYZ(cb.Min.X, cb.Min.Y, 0)),
                    t.OfPoint(new XYZ(cb.Max.X, cb.Min.Y, 0)),
                    t.OfPoint(new XYZ(cb.Max.X, cb.Max.Y, 0)),
                    t.OfPoint(new XYZ(cb.Min.X, cb.Max.Y, 0)),
                };
                return (
                    corners.Min(p => p.X), corners.Min(p => p.Y),
                    corners.Max(p => p.X), corners.Max(p => p.Y)
                );
            }
            catch { return (double.MinValue, double.MinValue, double.MaxValue, double.MaxValue); }
        }

        private bool IsInstanceInBounds(Element inst, Transform linkXform,
            double minX, double minY, double maxX, double maxY)
        {
            const double buffer = 3.0; // feet
            try
            {
                if (inst.Location is LocationCurve lc)
                {
                    var p0 = linkXform.OfPoint(lc.Curve.GetEndPoint(0));
                    var p1 = linkXform.OfPoint(lc.Curve.GetEndPoint(1));
                    return InBounds(p0.X, p0.Y, minX, minY, maxX, maxY, buffer)
                        || InBounds(p1.X, p1.Y, minX, minY, maxX, maxY, buffer);
                }
                if (inst.Location is LocationPoint lp)
                {
                    var pt = linkXform.OfPoint(lp.Point);
                    return InBounds(pt.X, pt.Y, minX, minY, maxX, maxY, buffer);
                }
            }
            catch { }
            return false;
        }

        private static bool InBounds(double x, double y,
            double minX, double minY, double maxX, double maxY, double buffer)
            => x >= minX - buffer && x <= maxX + buffer
            && y >= minY - buffer && y <= maxY + buffer;

        // ═══════════════════════════════════════════════════════════════════
        //  Step 5 — Apply mappings (Req 3d)
        //  Host types → Ekahau_WallType shared parameter
        //  Link types → ExtensibleStorage JSON on host document
        // ═══════════════════════════════════════════════════════════════════

        private (int Updated, int Skipped) ApplyMappings(
            Document doc,
            List<TypeItem> typeItems,
            Dictionary<int, string> selections,  // rowIndex → presetKey
            LinkedModelInfo selectedLink)
        {
            int updated = 0, skipped = 0;
            var linkMappings = new Dictionary<string, string>(); // typeUniqueId → presetKey

            for (int i = 0; i < typeItems.Count; i++)
            {
                if (!selections.TryGetValue(i, out string presetKey)) { skipped++; continue; }

                var item = typeItems[i];
                if (item.Source == "link")
                {
                    // Req 3d: collect for ExtensibleStorage
                    linkMappings[item.TypeUniqueId] = presetKey;
                    updated++;
                }
                else
                {
                    // Host element: write shared parameter
                    try
                    {
                        var p = item.Element.LookupParameter("Ekahau_WallType");
                        if (p != null && !p.IsReadOnly) { p.Set(presetKey); updated++; }
                        else skipped++;
                    }
                    catch { skipped++; }
                }
            }

            // Write link mappings to ExtensibleStorage
            if (linkMappings.Count > 0 && selectedLink != null)
                WriteExtensibleStorage(doc, selectedLink, linkMappings);

            return (updated, skipped);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Req 3d — Extensible Storage helpers
        // ═══════════════════════════════════════════════════════════════════

        private Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetVendorId("EkahauTransfer");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldName, typeof(string));
            return builder.Finish();
        }

        private DataStorage GetOrCreateDataStorage(Document doc, Schema schema)
        {
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)))
            {
                var entity = ds.GetEntity(schema);
                if (entity.IsValid()) return ds;
            }
            return DataStorage.Create(doc);
        }

        private void WriteExtensibleStorage(
            Document doc, LinkedModelInfo link, Dictionary<string, string> mappings)
        {
            try
            {
                var schema = GetOrCreateSchema();
                var ds     = GetOrCreateDataStorage(doc, schema);
                var entity = new Entity(schema);
                entity.Set(FieldName, BuildMappingJson(link.Name, link.UniqueId, mappings));
                ds.SetEntity(entity);
            }
            catch { /* non-critical; don't block the transaction */ }
        }

        private string BuildMappingJson(string linkName, string linkUniqueId,
            Dictionary<string, string> mappings)
        {
            // Manual JSON building to avoid package dependencies
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"selectedLinkName\":\"{EscapeJson(linkName)}\",");
            sb.Append($"\"selectedLinkUniqueId\":\"{EscapeJson(linkUniqueId)}\",");
            sb.Append("\"mappings\":{");
            sb.Append(string.Join(",", mappings.Select(
                kvp => $"\"{EscapeJson(kvp.Key)}\":\"{EscapeJson(kvp.Value)}\"")));
            sb.Append("}}");
            return sb.ToString();
        }

        private static string EscapeJson(string s)
            => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
    }
}
