using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using AppServices = Autodesk.Revit.ApplicationServices;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  AP Place Command  —  Feature 4
    //  Reads the staging JSON written by ESX Read and places real Revit
    //  family instances at the recorded world coordinates.
    //
    //  18-REQ specification:
    //    REQ 1  — Staging path isolation (GetStagingPath + hash)
    //    REQ 2  — Project path hash validation
    //    REQ 3  — Per-AP mounting height
    //    REQ 4  — Multi-floor staging format
    //    REQ 5  — Clean up ESX Read preview markers
    //    REQ 6  — Remove unnecessary "Click OK" dialogs
    //    REQ 7  — Remember last selected family
    //    REQ 8  — WPF confirmation dialog
    //    REQ 9  — Per-AP checkboxes
    //    REQ 10 — Select + zoom after placement
    //    REQ 11 — Write Mark + Comments parameters
    //    REQ 12 — Workset assignment
    //    REQ 13 — Record placement history
    //    REQ 14 — Unified three-column family picker
    //    REQ 15 — Merge symbol activation into placement transaction
    //    REQ 16 — Batch transaction (BATCH_SIZE = 20)
    //    REQ 17 — Tag grouping in confirmation dialog
    //    REQ 18 — Validate staging view consistency
    // ═══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class ApPlaceCommand : IExternalCommand
    {
        private const double FeetToMetres = 0.3048;
        private const int BATCH_SIZE = 20;  // REQ 16

        // REQ 7: Remember last selected family across sessions
        private static string _lastCategoryName;
        private static string _lastFamilyName;
        private static string _lastTypeName;

        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // ── 1. Read staging JSON (REQ 1+4) ─────────────────────────
            string jsonPath = RevitHelpers.GetStagingJsonPath(doc);
            if (!File.Exists(jsonPath))
            {
                TaskDialog.Show("AP Place",
                    "No staging data found.\n\n" +
                    "Run \"ESX Read\" first to import access point positions " +
                    "from an Ekahau .esx file.");
                return Result.Failed;
            }

            ApStagingData staging;
            try
            {
                string json = File.ReadAllText(jsonPath);
                staging = JsonSerializer.Deserialize<ApStagingData>(json);
                if (staging == null || staging.Floors == null || staging.Floors.Count == 0)
                {
                    TaskDialog.Show("AP Place",
                        "Staging file contains no floor/AP data.\n" +
                        "Re-run ESX Read to refresh the staging data.");
                    return Result.Failed;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AP Place — Error",
                    $"Failed to read staging file:\n{ex.Message}");
                return Result.Failed;
            }

            // ── 2. Validate project hash (REQ 2) ─────────────────────
            string currentHash = RevitHelpers.GetProjectPathHash(doc);
            if (!string.IsNullOrEmpty(staging.ProjectPathHash) &&
                staging.ProjectPathHash != currentHash)
            {
                var hashDlg = new TaskDialog("AP Place — Project Mismatch")
                {
                    MainContent =
                        "The staging data was created from a different project file.\n\n" +
                        $"Staging: {staging.ProjectName}\n" +
                        $"Current: {doc.Title}\n\n" +
                        "Continue anyway?",
                };
                hashDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Continue (coordinates may be wrong)");
                hashDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                    "Cancel");
                if (hashDlg.Show() != TaskDialogResult.CommandLink1)
                    return Result.Cancelled;
            }

            // ── 3. Validate views exist (REQ 18) ─────────────────────
            var viewLookup = new Dictionary<long, ViewPlan>();
            foreach (var floor in staging.Floors)
            {
                try
                {
                    var elem = doc.GetElement(VersionCompat.MakeId(floor.ViewId));
                    if (elem is ViewPlan vp)
                        viewLookup[floor.ViewId] = vp;
                }
                catch { }
            }

            // Filter to floors whose views still exist
            var validFloors = staging.Floors
                .Where(f => viewLookup.ContainsKey(f.ViewId))
                .ToList();

            if (validFloors.Count == 0)
            {
                TaskDialog.Show("AP Place",
                    "None of the views referenced in the staging data exist " +
                    "in the current model.\n\nRe-run ESX Read.");
                return Result.Failed;
            }

            if (validFloors.Count < staging.Floors.Count)
            {
                int missing = staging.Floors.Count - validFloors.Count;
                var warnDlg = new TaskDialog("AP Place — Missing Views")
                {
                    MainContent =
                        $"{missing} floor(s) reference views that no longer exist " +
                        $"and will be skipped.\n\n" +
                        $"Remaining floors: {validFloors.Count}",
                };
                warnDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Continue");
                warnDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel");
                if (warnDlg.Show() != TaskDialogResult.CommandLink1)
                    return Result.Cancelled;
            }

            // ── 4. Count available (not-yet-placed) APs ──────────────
            var allAps = validFloors
                .SelectMany(f => f.AccessPoints
                    .Where(ap => !ap.Placed)
                    .Select(ap => (Floor: f, Ap: ap)))
                .ToList();

            if (allAps.Count == 0)
            {
                TaskDialog.Show("AP Place",
                    "All access points have already been placed.\n\n" +
                    "To re-place, delete the staging file and re-run ESX Read:\n" +
                    jsonPath);
                return Result.Failed;
            }

            // ── 5. Family picker dialog (REQ 14 + REQ 7) ─────────────
            var familyData = CollectFamilyData(doc);
            if (familyData.Count == 0)
            {
                TaskDialog.Show("AP Place",
                    "No loadable families found in the project.\n" +
                    "Load an AP family first (Insert → Load Family).");
                return Result.Failed;
            }

            var pickerDlg = new ApPlaceFamilyPickerDialog(
                familyData, _lastCategoryName, _lastFamilyName, _lastTypeName);

            if (pickerDlg.ShowDialog() != true || pickerDlg.SelectedSymbolId == null)
                return Result.Cancelled;

            // REQ 7: Remember selection
            _lastCategoryName = pickerDlg.SelectedCategoryName;
            _lastFamilyName   = pickerDlg.SelectedFamilyName;
            _lastTypeName     = pickerDlg.SelectedTypeName;

            var selectedSymbolId = pickerDlg.SelectedSymbolId;
            var symbol = doc.GetElement(selectedSymbolId) as FamilySymbol;
            if (symbol == null)
            {
                TaskDialog.Show("AP Place", "Selected family type is no longer valid.");
                return Result.Failed;
            }

            // ── 5b. Create Ekahau shared parameters on the AP category ──
            var app = commandData.Application.Application;
            try
            {
                EnsureEkahauApParameters(doc, app, symbol.Category);
            }
            catch
            {
                // Non-fatal: parameters won't be populated, but placement proceeds
            }

            // ── 6. Confirmation dialog (REQ 8+9+17) ──────────────────
            string familyLabel =
                $"{symbol.Category?.Name} : {symbol.Family.Name} : {symbol.Name}";

            // Build per-floor AP lists for the dialog
            var confirmFloors = new List<ApPlaceConfirmFloor>();
            foreach (var floor in validFloors)
            {
                var unplaced = floor.AccessPoints.Where(ap => !ap.Placed).ToList();
                if (unplaced.Count == 0) continue;
                confirmFloors.Add(new ApPlaceConfirmFloor
                {
                    FloorName     = floor.FloorPlanName,
                    ViewName      = floor.ViewName,
                    AccessPoints  = unplaced,
                });
            }

            // Collect workset names if workshared (REQ 12)
            var worksetNames = new List<string>();
            string activeWorksetName = "";
            if (doc.IsWorkshared)
            {
                try
                {
                    var wsTable = doc.GetWorksetTable();
                    var activeWsId = wsTable.GetActiveWorksetId();
                    foreach (var ws in new FilteredWorksetCollector(doc)
                        .OfKind(WorksetKind.UserWorkset)
                        .OrderBy(w => w.Name))
                    {
                        worksetNames.Add(ws.Name);
                        if (ws.Id == activeWsId)
                            activeWorksetName = ws.Name;
                    }
                }
                catch { }
            }

            var confirmDlg = new ApPlaceConfirmDialog(
                familyLabel, confirmFloors, worksetNames, activeWorksetName);

            if (confirmDlg.ShowDialog() != true)
                return Result.Cancelled;

            // Marker cleanup is now unconditional (see section 9 below).
            string selectedWorkset = confirmDlg.SelectedWorkset;

            // ── 7. Resolve selected workset ID (REQ 12) ──────────────
            WorksetId targetWorksetId = WorksetId.InvalidWorksetId;
            if (doc.IsWorkshared && !string.IsNullOrEmpty(selectedWorkset))
            {
                try
                {
                    var ws = new FilteredWorksetCollector(doc)
                        .OfKind(WorksetKind.UserWorkset)
                        .FirstOrDefault(w => w.Name == selectedWorkset);
                    if (ws != null) targetWorksetId = ws.Id;
                }
                catch { }
            }

            // ── 8. Place family instances (REQ 15+16) ─────────────────
            var placedIds = new List<ElementId>();
            var placedByFloor = new Dictionary<string, int>();
            var warnings = new List<string>();
            int totalPlaced = 0;

            // Gather all APs to place (with floor context) from the confirmed list
            var apsToPlace = new List<(ApStagingFloor Floor, ApStagingEntry Ap)>();
            foreach (var cf in confirmFloors)
            {
                var floor = validFloors.First(f => f.FloorPlanName == cf.FloorName);
                foreach (var ap in cf.AccessPoints.Where(a => a.Include))
                {
                    apsToPlace.Add((floor, ap));
                }
            }

            if (apsToPlace.Count == 0)
            {
                TaskDialog.Show("AP Place", "No access points selected for placement.");
                return Result.Cancelled;
            }

            // Process in batches (REQ 16)
            for (int batch = 0; batch < apsToPlace.Count; batch += BATCH_SIZE)
            {
                var batchAps = apsToPlace.Skip(batch).Take(BATCH_SIZE).ToList();
                string txName = apsToPlace.Count <= BATCH_SIZE
                    ? "AP Place"
                    : $"AP Place (batch {batch / BATCH_SIZE + 1})";

                using var tx = new Transaction(doc, txName);
                try
                {
                    tx.Start();

                    // REQ 15: Activate symbol within the first transaction
                    if (batch == 0 && !symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();  // Required after Activate before NewFamilyInstance
                    }

                    foreach (var (floor, ap) in batchAps)
                    {
                        try
                        {
                            var view = viewLookup[floor.ViewId];
                            var level = view.GenLevel;
                            if (level == null)
                            {
                                // Fallback: find nearest level
                                level = new FilteredElementCollector(doc)
                                    .OfClass(typeof(Level))
                                    .Cast<Level>()
                                    .OrderBy(l => l.Elevation)
                                    .FirstOrDefault();
                            }
                            if (level == null) continue;

                            // REQ 3: Per-AP mounting height (metres → feet)
                            double zOffset = ap.MountingHeight / FeetToMetres;
                            var point = new XYZ(ap.WorldX, ap.WorldY, zOffset);

                            var instance = doc.Create.NewFamilyInstance(
                                point, symbol, level,
                                StructuralType.NonStructural);

                            if (instance != null)
                            {
                                placedIds.Add(instance.Id);
                                totalPlaced++;

                                // Track per-floor count
                                if (!placedByFloor.ContainsKey(floor.FloorPlanName))
                                    placedByFloor[floor.FloorPlanName] = 0;
                                placedByFloor[floor.FloorPlanName]++;

                                // REQ 11: Write Mark + Comments parameters
                                TryWriteParam(instance, "Mark", ap.Name);
                                string comment = BuildComment(ap);
                                TryWriteParam(instance, "Comments", comment);

                                // Write all 12 Ekahau shared parameters
                                WriteEkahauParams(instance, ap);

                                // REQ 12: Workset assignment
                                if (targetWorksetId != WorksetId.InvalidWorksetId)
                                {
                                    try
                                    {
                                        var wsPar = instance.get_Parameter(
                                            BuiltInParameter.ELEM_PARTITION_PARAM);
                                        if (wsPar != null && !wsPar.IsReadOnly)
                                            wsPar.Set(targetWorksetId.IntegerValue);
                                    }
                                    catch { }
                                }

                                // REQ 13: Record placement
                                ap.Placed          = true;
                                ap.PlacedElementId = VersionCompat.GetIdValue(instance.Id);
                                ap.PlacedTimestamp  = DateTime.Now.ToString("o");
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Failed to place '{ap.Name}': {ex.Message}");
                        }
                    }

                    tx.Commit();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    warnings.Add($"Batch transaction failed: {ex.Message}");
                }
            }

            // ── 9. Auto-clean ALL ESX Read preview artefacts ─────────
            //   - Per-AP marker IDs   (crosshairs + name TextNotes)
            //   - Per-floor overlay IDs (image overlay, corner crosses,
            //                            band legend)
            //   - Workset safety net  (any stragglers on the
            //                          "Ekahau AP Markers" workset)
            //   No dialog — these are temporary preview elements that
            //   serve no purpose once real family instances are placed.
            int markersDeleted = 0;
            if (totalPlaced > 0)
            {
                // 9a. Collect IDs from staging — both per-AP and per-floor
                var markerIds = new HashSet<long>();
                foreach (var (floor, ap) in apsToPlace.Where(x => x.Ap.Placed))
                {
                    if (ap.MarkerElementIds != null)
                        foreach (long mid in ap.MarkerElementIds)
                            if (mid > 0) markerIds.Add(mid);
                }
                foreach (var floor in validFloors)
                {
                    if (floor.OverlayElementIds != null)
                        foreach (long mid in floor.OverlayElementIds)
                            if (mid > 0) markerIds.Add(mid);
                }

                if (markerIds.Count > 0)
                {
                    try
                    {
                        using var tx = new Transaction(doc, "AP Place — Clean up preview artefacts");
                        tx.Start();
                        foreach (long mid in markerIds)
                        {
                            try
                            {
                                var eid = VersionCompat.MakeId(mid);
                                if (doc.GetElement(eid) != null)
                                {
                                    doc.Delete(eid);
                                    markersDeleted++;
                                }
                            }
                            catch { /* element already gone or undeletable — skip */ }
                        }
                        tx.Commit();
                    }
                    catch { }
                }

                // 9b. Workset safety net — sweep any leftovers on the
                //     "Ekahau AP Markers" workset in any of the source
                //     views (catches markers from older runs whose IDs
                //     were lost when staging was rewritten).
                if (doc.IsWorkshared)
                {
                    try
                    {
                        var ws = new FilteredWorksetCollector(doc)
                            .OfKind(WorksetKind.UserWorkset)
                            .FirstOrDefault(w => w.Name == "Ekahau AP Markers");
                        if (ws != null)
                        {
                            int wsId = ws.Id.IntegerValue;
                            using var tx = new Transaction(doc, "AP Place — Workset cleanup");
                            tx.Start();
                            foreach (var floor in validFloors)
                            {
                                if (!viewLookup.TryGetValue(floor.ViewId, out var vp)) continue;
                                IList<ElementId> orphans;
                                try
                                {
                                    orphans = new FilteredElementCollector(doc, vp.Id)
                                        .WhereElementIsNotElementType()
                                        .ToElements()
                                        .Where(e =>
                                        {
                                            try
                                            {
                                                var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                                return p != null && p.AsInteger() == wsId;
                                            }
                                            catch { return false; }
                                        })
                                        .Select(e => e.Id)
                                        .ToList();
                                }
                                catch { continue; }

                                foreach (var oid in orphans)
                                {
                                    try { doc.Delete(oid); markersDeleted++; }
                                    catch { }
                                }
                            }
                            tx.Commit();
                        }
                    }
                    catch { }
                }

                // 9c. Clear marker IDs from staging so a re-run doesn't
                //     try to delete elements that no longer exist.
                foreach (var (floor, ap) in apsToPlace.Where(x => x.Ap.Placed))
                    ap.MarkerElementIds = new List<long>();
                foreach (var floor in validFloors)
                    floor.OverlayElementIds = new List<long>();
            }

            // ── 10. Update staging JSON with placement history (REQ 13)
            try
            {
                staging.Timestamp = DateTime.Now.ToString("o");
                var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(jsonPath,
                    JsonSerializer.Serialize(staging, jsonOpts));
            }
            catch { /* non-critical */ }

            // ── 11. (REQ 10 superseded) — no automatic view navigation
            //     after placement.  The user's current active view stays
            //     intact; the optional WiFi Plan view creation below
            //     handles its own view switching.

            // ── 12. Summary (REQ 6: no unnecessary clicks) ───────────
            var summaryDlg = new ApPlaceSummaryDialog(
                totalPlaced, placedByFloor, warnings,
                familyLabel, markersDeleted);
            summaryDlg.ShowDialog();

            // ── 13. Offer WiFi Floor Plan view creation ──────────────
            if (totalPlaced > 0)
            {
                try
                {
                    var wifiTd = new TaskDialog("Create WiFi Floor Plan View?")
                    {
                        MainContent =
                            "Generate a dedicated floor plan view showing only\n" +
                            "WiFi-relevant elements (walls, APs, network devices)?\n\n" +
                            "This view can be placed on a sheet for documentation.",
                    };
                    wifiTd.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Yes, create view(s)");
                    wifiTd.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "No thanks");

                    if (wifiTd.Show() == TaskDialogResult.CommandLink1)
                    {
                        // Group placed AP IDs by floor → view
                        var placedByView = new Dictionary<long, List<ElementId>>();
                        foreach (var (floor, ap) in apsToPlace.Where(x => x.Ap.Placed))
                        {
                            if (!placedByView.ContainsKey(floor.ViewId))
                                placedByView[floor.ViewId] = new List<ElementId>();
                            if (ap.PlacedElementId.HasValue)
                                placedByView[floor.ViewId].Add(
                                    VersionCompat.MakeId(ap.PlacedElementId.Value));
                        }

                        var createdViewNames = new List<string>();
                        ViewPlan lastCreated = null;

                        foreach (var kv in placedByView)
                        {
                            if (!viewLookup.TryGetValue(kv.Key, out var srcView)) continue;
                            var level = srcView.GenLevel;
                            if (level == null) continue;

                            var (wifiView, viewName) = CreateWifiFloorPlanView(
                                doc, srcView, level, kv.Value, symbol.Category);
                            if (wifiView != null)
                            {
                                createdViewNames.Add(viewName);
                                lastCreated = wifiView;
                            }
                        }

                        // Switch to the last created view
                        if (lastCreated != null)
                        {
                            try { uiDoc.ActiveView = lastCreated; } catch { }
                        }

                        if (createdViewNames.Count > 0)
                        {
                            string viewList = string.Join("\n  ", createdViewNames.Select(n => $"  \u2022 {n}"));
                            TaskDialog.Show("WiFi Floor Plan Created",
                                $"Created {createdViewNames.Count} WiFi view(s):\n\n" +
                                viewList + "\n\n" +
                                "Each shows only WiFi-relevant elements:\n" +
                                "  \u2022 Walls, doors, windows (RF barriers)\n" +
                                "  \u2022 Placed Access Points (highlighted in blue)\n" +
                                "  \u2022 Communication & data devices\n" +
                                "  \u2022 Floor layout and grids\n\n" +
                                "You can place these views on sheets for documentation.");
                        }
                    }
                }
                catch { /* WiFi view creation is optional — never block */ }

                // ── 14. Offer AP Schedule creation (one per level) ──
                try
                {
                    var schedDlg = new TaskDialog("Create AP Schedule?")
                    {
                        MainContent =
                            "Generate one AP Schedule per floor listing the placed\n" +
                            "access points and their Ekahau parameters?\n\n" +
                            "Columns: AP Name, Vendor, Model, WiFi Standard,\n" +
                            "Frequency Bands, Mount Height, Tx Power, Channels,\n" +
                            "MIMO Streams, Antenna, Tags.",
                    };
                    schedDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                        "Yes, create schedule(s)");
                    schedDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                        "No thanks");

                    if (schedDlg.Show() == TaskDialogResult.CommandLink1)
                    {
                        // Resolve AP BuiltInCategory once
                        BuiltInCategory apBic = BuiltInCategory.OST_GenericModel;
                        try { apBic = (BuiltInCategory)VersionCompat.GetIdValue(symbol.Category.Id); }
                        catch { }

                        // Build unique level set from the placed APs
                        var levelMap = new Dictionary<long, Level>();
                        foreach (var (floor, ap) in apsToPlace.Where(x => x.Ap.Placed))
                        {
                            if (!viewLookup.TryGetValue(floor.ViewId, out var v)) continue;
                            var lvl = v.GenLevel;
                            if (lvl == null) continue;
                            levelMap[VersionCompat.GetIdValue(lvl.Id)] = lvl;
                        }

                        var createdNames = new List<string>();
                        foreach (var lvl in levelMap.Values)
                        {
                            string n = CreateApSchedule(doc, apBic, lvl);
                            if (!string.IsNullOrEmpty(n)) createdNames.Add(n);
                        }

                        if (createdNames.Count > 0)
                        {
                            string list = string.Join("\n",
                                createdNames.Select(n => $"  \u2022 {n}"));
                            TaskDialog.Show("AP Schedule(s) Created",
                                $"{createdNames.Count} schedule(s) ready:\n\n" +
                                list + "\n\n" +
                                "Find them in the Project Browser under " +
                                "Schedules/Quantities.\n" +
                                "Filter: Ekahau_AP_Name not empty + Level = {floor}, " +
                                "so only Ekahau APs on that floor are listed.");
                        }
                    }
                }
                catch { /* schedule creation is optional — never block */ }
            }

            return Result.Succeeded;
        }

        // ══════════════════════════════════════════════════════════════
        //  WiFi Floor Plan View creation
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Create a dedicated WiFi floor plan view showing only
        /// RF-relevant elements + placed APs (with name tags + colors).
        /// Returns (view, finalName) or (null, null).
        /// </summary>
        private static (ViewPlan View, string Name) CreateWifiFloorPlanView(
            Document doc, ViewPlan sourceView, Level level,
            List<ElementId> placedApIds, Category apCategory)
        {
            string viewName = $"WiFi Plan - {level.Name}";
            ViewPlan wifiView = null;
            string finalName = viewName;

            // Resolve AP's BuiltInCategory (used for filtering + tag selection)
            BuiltInCategory apBic = BuiltInCategory.OST_GenericModel;
            try
            {
                if (apCategory != null)
                    apBic = (BuiltInCategory)VersionCompat.GetIdValue(apCategory.Id);
            }
            catch { }

            using (var t = new Transaction(doc, "Create WiFi Floor Plan View"))
            {
                t.Start();

                // 1. Duplicate the source view
                var newViewId = sourceView.Duplicate(ViewDuplicateOption.Duplicate);
                wifiView = doc.GetElement(newViewId) as ViewPlan;
                if (wifiView == null)
                {
                    t.RollBack();
                    return (null, null);
                }

                // 2. Set unique name
                var existingNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .Where(v => v.Id != wifiView.Id)
                    .Select(v => v.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                int counter = 1;
                while (existingNames.Contains(finalName))
                    finalName = $"{viewName} ({counter++})";

                try { wifiView.Name = finalName; }
                catch { /* name collision — use whatever Revit assigned */ }

                // 3. Detail Level = Coarse
                try { wifiView.DetailLevel = ViewDetailLevel.Coarse; } catch { }

                // 4. Remove view template
                try { wifiView.ViewTemplateId = ElementId.InvalidElementId; } catch { }

                // 5. Categories to KEEP visible
                //    NOTE: OST_GenericModel is in the list (so AP families
                //    placed under that category remain visible), but the
                //    ParameterFilterElement applied below (Bug Fix #11)
                //    hides every Generic Model whose Ekahau_AP_Name is
                //    empty — i.e. only our placed APs survive the filter.
                //    The same filter does NOT apply to Data Devices,
                //    Conduits, Cable Trays — those categories show all
                //    elements (which is what we want for low-voltage
                //    infrastructure context).
                var keepVisible = new HashSet<BuiltInCategory>
                {
                    // ── RF barriers ──────────────────────────────────
                    BuiltInCategory.OST_Walls,
                    BuiltInCategory.OST_Doors,
                    BuiltInCategory.OST_Windows,
                    BuiltInCategory.OST_Columns,
                    BuiltInCategory.OST_StructuralColumns,

                    // ── WiFi / network devices ───────────────────────
                    BuiltInCategory.OST_CommunicationDevices,
                    BuiltInCategory.OST_DataDevices,
                    BuiltInCategory.OST_TelephoneDevices,
                    BuiltInCategory.OST_SecurityDevices,
                    BuiltInCategory.OST_ElectricalEquipment,
                    BuiltInCategory.OST_ElectricalFixtures,
                    BuiltInCategory.OST_GenericModel,    // AP families (filtered by Bug Fix #11)

                    // ── Low-voltage infrastructure ───────────────────
                    BuiltInCategory.OST_Conduit,
                    BuiltInCategory.OST_ConduitFitting,
                    BuiltInCategory.OST_CableTray,
                    BuiltInCategory.OST_CableTrayFitting,

                    // ── Spatial reference ────────────────────────────
                    BuiltInCategory.OST_Floors,
                    BuiltInCategory.OST_Rooms,
                    BuiltInCategory.OST_Areas,
                    BuiltInCategory.OST_Grids,
                    BuiltInCategory.OST_Stairs,
                };
                // Always keep the AP's own category visible (idempotent
                // when apBic is already one of OST_GenericModel /
                // OST_ElectricalEquipment / etc. above).
                keepVisible.Add(apBic);

                // 6. Hide every other category
                foreach (Category cat in doc.Settings.Categories)
                {
                    try
                    {
                        if (cat.CategoryType == CategoryType.Annotation)
                        {
                            if ((BuiltInCategory)VersionCompat.GetIdValue(cat.Id) != BuiltInCategory.OST_Grids)
                            {
                                if (wifiView.CanCategoryBeHidden(cat.Id))
                                    wifiView.SetCategoryHidden(cat.Id, true);
                            }
                        }
                        else if (cat.CategoryType == CategoryType.Model)
                        {
                            bool keep = false;
                            try { keep = keepVisible.Contains((BuiltInCategory)VersionCompat.GetIdValue(cat.Id)); }
                            catch { }

                            if (!keep && wifiView.CanCategoryBeHidden(cat.Id))
                                wifiView.SetCategoryHidden(cat.Id, true);
                        }
                    }
                    catch { }
                }

                // 6b. Bug Fix #11 — For "noisy" categories (Generic Models,
                //     Electrical Equipment, etc.) the AP's category is kept
                //     visible, but a ParameterFilterElement is added to the
                //     view that hides any element whose Ekahau_AP_Name is
                //     empty.  This is more reliable than HideElements:
                //       • category-level visibility wins over element-level
                //         unhiding, so we cannot just unhide our APs;
                //       • the filter persists with the view, so future
                //         non-AP elements added later are also hidden.
                var noisyCategories = new HashSet<BuiltInCategory>
                {
                    BuiltInCategory.OST_GenericModel,
                    BuiltInCategory.OST_ElectricalEquipment,
                    BuiltInCategory.OST_ElectricalFixtures,
                    BuiltInCategory.OST_MechanicalEquipment,
                    BuiltInCategory.OST_SpecialityEquipment,
                };

                if (noisyCategories.Contains(apBic))
                {
                    try
                    {
                        ApplyHideNonApFilter(doc, wifiView, apBic);
                    }
                    catch
                    {
                        // Filter creation failed — fall through; user will
                        // see all elements in this category, but APs are
                        // still highlighted by the colour overrides below.
                    }
                }

                // 7. Floors as halftone
                try
                {
                    var floorCatId = new ElementId(BuiltInCategory.OST_Floors);
                    var floorOgs = new OverrideGraphicSettings();
                    floorOgs.SetHalftone(true);
                    wifiView.SetCategoryOverrides(floorCatId, floorOgs);
                }
                catch { }

                // 8. Fix 3 — Visual enhancement: color APs by Ekahau_Tags value,
                //    heavy line weight, solid fill so symbols are visible at scale.
                ApplyApVisualOverrides(doc, wifiView, placedApIds);

                // 9. CropBox
                try
                {
                    wifiView.CropBoxActive = sourceView.CropBoxActive;
                    wifiView.CropBoxVisible = true;
                }
                catch { }

                // 10. Bug Fix #12 — Label every placed AP with a TextNote.
                //     IndependentTag is unreliable: its content is bound to
                //     whatever parameter the tag family's label is mapped
                //     to (usually Type Mark, not the instance Mark we set).
                //     TextNote lets us write the exact string we want.
                TagPlacedAps(doc, wifiView, placedApIds);

                t.Commit();
            }

            return (wifiView, finalName);
        }

        /// <summary>
        /// Fix 3 — Apply per-AP color/line-weight/fill overrides.
        /// APs are coloured by their Ekahau_Tags value; APs with the same tag
        /// share a colour from a fixed 6-colour palette.  APs without a tag
        /// fall back to default blue.
        /// </summary>
        private static void ApplyApVisualOverrides(
            Document doc, View view, List<ElementId> placedApIds)
        {
            var tagColorMap = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            var palette = new[]
            {
                new Color(220,  50,  50),  // Red
                new Color( 50,  80, 220),  // Blue
                new Color( 40, 170,  60),  // Green
                new Color(230, 140,  20),  // Orange
                new Color(150,  50, 200),  // Purple
                new Color( 20, 170, 190),  // Teal
            };
            int colorIdx = 0;

            // Find a solid-fill pattern (used for surface foreground)
            FillPatternElement solidPattern = null;
            try
            {
                solidPattern = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp =>
                    {
                        try { return fp.GetFillPattern().IsSolidFill; }
                        catch { return false; }
                    });
            }
            catch { }

            foreach (var apId in placedApIds)
            {
                try
                {
                    var apElem = doc.GetElement(apId);
                    if (apElem == null) continue;

                    Color apColor = new Color(0, 100, 220);  // default blue

                    string tagValue = null;
                    try
                    {
                        var tagParam = apElem.LookupParameter("Ekahau_Tags");
                        if (tagParam != null) tagValue = tagParam.AsString();
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(tagValue))
                    {
                        if (!tagColorMap.TryGetValue(tagValue, out apColor))
                        {
                            apColor = palette[colorIdx % palette.Length];
                            tagColorMap[tagValue] = apColor;
                            colorIdx++;
                        }
                    }

                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(apColor);
                    ogs.SetProjectionLineWeight(6);     // heavy weight for visibility
                    ogs.SetSurfaceForegroundPatternColor(apColor);
                    if (solidPattern != null)
                    {
                        try { ogs.SetSurfaceForegroundPatternId(solidPattern.Id); }
                        catch { }
                    }

                    view.SetElementOverrides(apId, ogs);
                }
                catch { }
            }
        }

        /// <summary>
        /// Bug Fix #12 — Label each placed AP with a TextNote whose
        /// content is the AP's instance Mark (or Ekahau_AP_Name fallback).
        /// We deliberately do NOT use IndependentTag because its label is
        /// bound to whichever parameter the tag family's label is mapped
        /// to — typically Type Mark (shared across instances) instead of
        /// the unique instance Mark we set during placement.  TextNote
        /// content is fully controlled by us and works for every AP
        /// category without needing a loaded tag family.
        /// </summary>
        private static void TagPlacedAps(
            Document doc, View view, List<ElementId> placedApIds)
        {
            // Pick the smallest TextNoteType available for cleaner labels
            ElementId textTypeId = ElementId.InvalidElementId;
            try
            {
                var textTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .OrderBy(t =>
                    {
                        try
                        {
                            var p = t.get_Parameter(BuiltInParameter.TEXT_SIZE);
                            return p != null ? p.AsDouble() : 999.0;
                        }
                        catch { return 999.0; }
                    })
                    .ToList();

                if (textTypes.Count > 0)
                    textTypeId = textTypes[0].Id;
            }
            catch
            {
                try
                {
                    textTypeId = new FilteredElementCollector(doc)
                        .OfClass(typeof(TextNoteType))
                        .FirstElementId();
                }
                catch { }
            }

            if (textTypeId == null || textTypeId == ElementId.InvalidElementId)
                return;  // no TextNoteType available — skip labelling

            // Tag offset scales with view scale so labels stay readable
            double tagOffsetFt;
            try
            {
                int viewScale = view.Scale;
                tagOffsetFt = Math.Max(1.0, (5.0 / 304.8) * viewScale);  // ~5 mm on paper
            }
            catch { tagOffsetFt = 3.0; }

            foreach (var apId in placedApIds)
            {
                try
                {
                    var apElem = doc.GetElement(apId);
                    if (apElem == null) continue;

                    XYZ apLocation = (apElem.Location as LocationPoint)?.Point;
                    if (apLocation == null) continue;

                    // Read the INSTANCE Mark parameter (NOT Type Mark)
                    string apName = "";
                    try
                    {
                        var markParam = apElem.get_Parameter(
                            BuiltInParameter.ALL_MODEL_MARK);
                        if (markParam != null && markParam.HasValue)
                            apName = markParam.AsString() ?? "";
                    }
                    catch { }

                    // Fallback to Ekahau_AP_Name (also instance-level)
                    if (string.IsNullOrEmpty(apName))
                    {
                        try
                        {
                            var ekParam = apElem.LookupParameter("Ekahau_AP_Name");
                            if (ekParam != null && ekParam.HasValue)
                                apName = ekParam.AsString() ?? "";
                        }
                        catch { }
                    }

                    if (string.IsNullOrEmpty(apName)) continue;

                    var tagPoint = new XYZ(
                        apLocation.X,
                        apLocation.Y + tagOffsetFt,
                        apLocation.Z);

                    var opts = new TextNoteOptions(textTypeId)
                    {
                        HorizontalAlignment = HorizontalTextAlignment.Center,
                    };
                    var textNote = TextNote.Create(
                        doc, view.Id, tagPoint, apName, opts);

                    // Match the AP's tag-group colour (set in
                    // ApplyApVisualOverrides) so the label matches the dot.
                    try
                    {
                        var apOgs = view.GetElementOverrides(apId);
                        if (apOgs != null && textNote != null)
                        {
                            var textOgs = new OverrideGraphicSettings();
                            textOgs.SetProjectionLineColor(apOgs.ProjectionLineColor);
                            view.SetElementOverrides(textNote.Id, textOgs);
                        }
                    }
                    catch { }
                }
                catch { }
            }

            // Ensure TextNotes category is visible in the WiFi view
            try
            {
                var textCatId = new ElementId(BuiltInCategory.OST_TextNotes);
                if (view.CanCategoryBeHidden(textCatId))
                    view.SetCategoryHidden(textCatId, false);
            }
            catch { }
        }

        /// <summary>
        /// Bug Fix #11 — Add (or reuse) a project-level
        /// <see cref="ParameterFilterElement"/> that hides every element of
        /// <paramref name="apCategory"/> whose <c>Ekahau_AP_Name</c>
        /// shared parameter is empty.  Then bind the filter to
        /// <paramref name="view"/> with visibility = false (hide matching).
        /// </summary>
        private static void ApplyHideNonApFilter(
            Document doc, View view, BuiltInCategory apCategory)
        {
            ElementId paramId = FindSharedParameterId(doc, "Ekahau_AP_Name");
            if (paramId == null || paramId == ElementId.InvalidElementId)
                return;  // shared param not found → can't build the filter

            const string filterName = "Ekahau - Hide Non-AP Elements";

            // Reuse existing filter if it has the same name
            var viewFilter = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(f => f.Name == filterName);

            if (viewFilter == null)
            {
                var filterCatIds = new List<ElementId>
                {
                    new ElementId(apCategory),
                };

                // Rule: Ekahau_AP_Name has NO value (= elements to HIDE).
                // VersionCompat handles the Revit-version differences in
                // ParameterFilterRuleFactory.
                FilterRule rule = VersionCompat.CreateHasNoValueRule(paramId);
                if (rule == null) return;

                var elementFilter = new ElementParameterFilter(rule);
                viewFilter = ParameterFilterElement.Create(
                    doc, filterName, filterCatIds, elementFilter);
            }
            else
            {
                // Reused filter — make sure its category set covers apCategory.
                try
                {
                    var cats = viewFilter.GetCategories();
                    var apCatId = new ElementId(apCategory);
                    if (!cats.Contains(apCatId))
                    {
                        var newCats = new List<ElementId>(cats) { apCatId };
                        viewFilter.SetCategories(newCats);
                    }
                }
                catch { }
            }

            try { view.AddFilter(viewFilter.Id); }
            catch { /* already bound to this view */ }

            try { view.SetFilterVisibility(viewFilter.Id, false); }
            catch { }
        }

        /// <summary>
        /// Look up the ElementId of a shared parameter by name.
        /// Used to build <see cref="FilterRule"/>s for view filters.
        /// </summary>
        private static ElementId FindSharedParameterId(Document doc, string paramName)
        {
            try
            {
                var sp = new FilteredElementCollector(doc)
                    .OfClass(typeof(SharedParameterElement))
                    .Cast<SharedParameterElement>()
                    .FirstOrDefault(s => s.Name == paramName);
                if (sp != null) return sp.Id;
            }
            catch { }
            return ElementId.InvalidElementId;
        }

        // ══════════════════════════════════════════════════════════════
        //  Helper: Collect family data for the picker (REQ 14)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Collect all loadable FamilySymbol instances, grouped by
        /// Category → Family → Type. Returns the hierarchy for the picker.
        /// </summary>
        private static Dictionary<string, Dictionary<string, List<FamilyTypeInfo>>>
            CollectFamilyData(Document doc)
        {
            var result = new Dictionary<string, Dictionary<string, List<FamilyTypeInfo>>>(
                StringComparer.OrdinalIgnoreCase);

            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null)
                .ToList();

            foreach (var sym in symbols)
            {
                string catName = sym.Category?.Name ?? "Uncategorized";
                string famName = sym.Family.Name;
                string typeName = sym.Name;

                if (!result.TryGetValue(catName, out var families))
                {
                    families = new Dictionary<string, List<FamilyTypeInfo>>(
                        StringComparer.OrdinalIgnoreCase);
                    result[catName] = families;
                }

                if (!families.TryGetValue(famName, out var types))
                {
                    types = new List<FamilyTypeInfo>();
                    families[famName] = types;
                }

                types.Add(new FamilyTypeInfo
                {
                    SymbolId     = sym.Id,
                    CategoryName = catName,
                    FamilyName   = famName,
                    TypeName     = typeName,
                });
            }

            // Sort types within each family
            foreach (var cat in result.Values)
                foreach (var fam in cat.Values)
                    fam.Sort((a, b) => string.Compare(a.TypeName, b.TypeName,
                        StringComparison.OrdinalIgnoreCase));

            return result;
        }

        // ══════════════════════════════════════════════════════════════
        //  Helper: Build Comments string (REQ 11)
        // ══════════════════════════════════════════════════════════════

        private static string BuildComment(ApStagingEntry ap)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ap.Vendor))
                parts.Add(ap.Vendor);
            if (!string.IsNullOrEmpty(ap.Model))
                parts.Add(ap.Model);
            if (ap.Bands != null && ap.Bands.Count > 0)
                parts.Add(EsxMarkerOps.FormatBands(ap.Bands));
            parts.Add($"Height: {ap.MountingHeight:F1} m");
            if (ap.Tags != null && ap.Tags.Count > 0)
                parts.Add($"Tags: {string.Join(", ", ap.Tags)}");
            return string.Join(" | ", parts);
        }

        private static void TryWriteParam(FamilyInstance inst, string paramName, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            try
            {
                var p = inst.LookupParameter(paramName);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                    p.Set(value);
            }
            catch { }
        }

        private static void TryWriteNumericParam(FamilyInstance inst, string paramName, double value)
        {
            try
            {
                var p = inst.LookupParameter(paramName);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                    p.Set(value);
            }
            catch { }
        }

        /// <summary>
        /// Write all 12 Ekahau shared parameters to a placed AP instance.
        /// Called inside the placement transaction for each AP.
        /// </summary>
        private static void WriteEkahauParams(FamilyInstance inst, ApStagingEntry ap)
        {
            TryWriteParam(inst, "Ekahau_AP_Name",     ap.Name);
            TryWriteParam(inst, "Ekahau_Vendor",       ap.Vendor);
            TryWriteParam(inst, "Ekahau_Model",        ap.Model);
            TryWriteParam(inst, "Ekahau_Mounting",     ap.Mounting);
            TryWriteParam(inst, "Ekahau_Bands",        ap.BandsSummary);
            TryWriteParam(inst, "Ekahau_Technology",   ap.Technology);
            TryWriteParam(inst, "Ekahau_TxPower",      ap.TxPowerSummary);
            TryWriteParam(inst, "Ekahau_Channels",     ap.ChannelsSummary);
            TryWriteParam(inst, "Ekahau_Streams",      ap.StreamsSummary);
            TryWriteParam(inst, "Ekahau_Antenna",      ap.AntennaInfo);
            TryWriteNumericParam(inst, "Ekahau_MountHeight_m", ap.MountingHeight);
            TryWriteParam(inst, "Ekahau_Tags",
                ap.Tags != null && ap.Tags.Count > 0
                    ? string.Join(", ", ap.Tags) : "");
        }

        // ══════════════════════════════════════════════════════════════
        //  Shared Parameter creation for AP instances
        // ══════════════════════════════════════════════════════════════

        /// <summary>12 Ekahau AP parameters: 11 text + 1 numeric.</summary>
        private static readonly List<(string Name, bool IsText)> EkahauApParams =
            new List<(string, bool)>
            {
                ("Ekahau_AP_Name",       true),
                ("Ekahau_Vendor",        true),
                ("Ekahau_Model",         true),
                ("Ekahau_Mounting",      true),
                ("Ekahau_Bands",         true),
                ("Ekahau_Technology",    true),
                ("Ekahau_TxPower",       true),
                ("Ekahau_Channels",      true),
                ("Ekahau_Streams",       true),
                ("Ekahau_Antenna",       true),
                ("Ekahau_MountHeight_m", false),  // numeric
                ("Ekahau_Tags",          true),
            };

        /// <summary>
        /// Ensure all 12 Ekahau AP shared parameters exist and are bound
        /// as InstanceBinding to the AP family's category.
        /// Follows the same save/restore pattern as ParamConfigCommand.
        /// </summary>
        private static void EnsureEkahauApParameters(
            Document doc, AppServices.Application app, Category apCategory)
        {
            if (apCategory == null) return;

            // Save original shared parameters filename (restore in finally)
            string originalSpPath = app.SharedParametersFilename;
            try
            {
                // 1. Open or create shared parameter file
                var spFile = app.OpenSharedParameterFile();
                if (spFile == null)
                {
                    string spPath = app.SharedParametersFilename;
                    if (string.IsNullOrEmpty(spPath) || !File.Exists(spPath))
                    {
                        string appData = Environment.GetFolderPath(
                            Environment.SpecialFolder.ApplicationData);
                        spPath = Path.Combine(appData, "Ekahau_SharedParams.txt");
                        if (!File.Exists(spPath)) File.WriteAllText(spPath, "");
                        app.SharedParametersFilename = spPath;
                    }
                    spFile = app.OpenSharedParameterFile();
                    if (spFile == null) return;
                }

                // 2. Get or create the "Ekahau WiFi" definition group
                const string groupName = "Ekahau WiFi";
                var group = spFile.Groups.get_Item(groupName);
                if (group == null)
                    group = spFile.Groups.Create(groupName);

                // 3. Build CategorySet with the AP family's category
                var catSet = new CategorySet();
                catSet.Insert(apCategory);

                // 4. Create definitions and bind
                using var tx = new Transaction(doc, "Create Ekahau AP Parameters");
                tx.Start();

                foreach (var (paramName, isText) in EkahauApParams)
                {
                    // Get or create the external definition
                    ExternalDefinition defn = null;
                    if (group.Definitions.get_Item(paramName) is ExternalDefinition existing)
                    {
                        defn = existing;
                    }
                    else
                    {
                        try
                        {
                            var opts = VersionCompat.CreateParamOptions(paramName, isText);
                            defn = group.Definitions.Create(opts) as ExternalDefinition;
                        }
                        catch { }
                    }
                    if (defn == null) continue;

                    // Check existing binding
                    var existingBinding = doc.ParameterBindings.get_Item(defn);
                    if (existingBinding != null)
                    {
                        // Ensure the AP category is included in the binding
                        if (existingBinding is InstanceBinding ib)
                        {
                            if (!ib.Categories.Contains(apCategory))
                            {
                                ib.Categories.Insert(apCategory);
                                try { doc.ParameterBindings.ReInsert(defn, ib); }
                                catch { }
                            }
                        }
                        else if (existingBinding is TypeBinding)
                        {
                            // Wrong binding type for AP instances — re-insert as Instance
                            try
                            {
                                doc.ParameterBindings.Remove(defn);
                                doc.ParameterBindings.Insert(defn, new InstanceBinding(catSet));
                            }
                            catch { }
                        }
                        continue;
                    }

                    // New parameter — insert as InstanceBinding
                    try { doc.ParameterBindings.Insert(defn, new InstanceBinding(catSet)); }
                    catch { }
                }

                tx.Commit();
            }
            finally
            {
                // Restore original shared parameters filename
                if (!string.IsNullOrEmpty(originalSpPath))
                {
                    try { app.SharedParametersFilename = originalSpPath; } catch { }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  AP Schedule creation
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Create a per-level ViewSchedule listing only Ekahau APs on the
        /// given level.  Filtering by <c>Ekahau_AP_Name HasValue</c> ensures
        /// other elements in the same category (e.g. unrelated Generic
        /// Models) are excluded.  Returns the schedule name, or null on
        /// failure.  If a schedule with the target name already exists it
        /// is reused (live filter picks up new APs automatically).
        /// </summary>
        private static string CreateApSchedule(
            Document doc, BuiltInCategory apCategory, Level level)
        {
            if (level == null) return null;

            string baseName = $"Ekahau AP Schedule - {level.Name}";

            // Reuse existing schedule with this exact name (live filter)
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(vs => vs.Name.Equals(baseName,
                    StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Name;

            using var tx = new Transaction(doc, "Create Ekahau AP Schedule");
            tx.Start();

            try
            {
                var schedule = ViewSchedule.CreateSchedule(
                    doc, new ElementId(apCategory));
                if (schedule == null) { tx.RollBack(); return null; }

                // Handle duplicate names
                string finalName = baseName;
                int counter = 1;
                var existingNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Where(v => v.Id != schedule.Id)
                    .Select(v => v.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                while (existingNames.Contains(finalName))
                    finalName = $"{baseName} ({counter++})";
                try { schedule.Name = finalName; } catch { }

                var def = schedule.Definition;
                var schedulableFields = def.GetSchedulableFields();

                // ── Step 1: Fields in display order ───────────────────
                var fieldNames = new[]
                {
                    "Mark",
                    "Ekahau_Vendor",
                    "Ekahau_Model",
                    "Ekahau_Technology",
                    "Ekahau_Bands",
                    "Ekahau_MountHeight_m",
                    "Ekahau_Mounting",
                    "Ekahau_TxPower",
                    "Ekahau_Channels",
                    "Ekahau_Streams",
                    "Ekahau_Antenna",
                    "Ekahau_Tags",
                    "Ekahau_AP_Name",   // for filter, hidden later
                    "Level",            // for filter, hidden later
                };

                var addedFields = new Dictionary<string, ScheduleFieldId>();
                foreach (string fieldName in fieldNames)
                {
                    SchedulableField match = null;
                    foreach (var sf in schedulableFields)
                    {
                        try
                        {
                            if (sf.GetName(doc) == fieldName) { match = sf; break; }
                        }
                        catch { }
                    }
                    if (match != null)
                    {
                        try
                        {
                            var field = def.AddField(match);
                            addedFields[fieldName] = field.FieldId;
                        }
                        catch { }
                    }
                }

                // ── Step 2: Filter — Ekahau_AP_Name has a value ──────
                if (addedFields.TryGetValue("Ekahau_AP_Name", out var apNameFieldId))
                {
                    bool filterAdded = false;
                    try
                    {
                        // Revit 2023+: HasValue filter
                        def.AddFilter(new ScheduleFilter(
                            apNameFieldId, ScheduleFilterType.HasValue));
                        filterAdded = true;
                    }
                    catch { }
                    if (!filterAdded)
                    {
                        try
                        {
                            // Fallback: NotEqual to empty string
                            def.AddFilter(new ScheduleFilter(
                                apNameFieldId, ScheduleFilterType.NotEqual, ""));
                            filterAdded = true;
                        }
                        catch { }
                    }
                    if (!filterAdded)
                    {
                        try
                        {
                            // Last resort: Contains "AP"
                            def.AddFilter(new ScheduleFilter(
                                apNameFieldId, ScheduleFilterType.Contains, "AP"));
                        }
                        catch { }
                    }

                    // Hide the column — Mark already shows the name
                    try
                    {
                        var f = def.GetField(apNameFieldId);
                        if (f != null) f.IsHidden = true;
                    }
                    catch { }
                }

                // ── Step 3: Filter — Level equals this floor ─────────
                if (addedFields.TryGetValue("Level", out var levelFieldId))
                {
                    try
                    {
                        def.AddFilter(new ScheduleFilter(
                            levelFieldId, ScheduleFilterType.Equal, level.Name));
                    }
                    catch { }

                    // Hide the column — redundant when filtered to one level
                    try
                    {
                        var f = def.GetField(levelFieldId);
                        if (f != null) f.IsHidden = true;
                    }
                    catch { }
                }

                // ── Step 4: Sort by Mark ascending ───────────────────
                if (addedFields.TryGetValue("Mark", out var markFieldId))
                {
                    try
                    {
                        var sg = new ScheduleSortGroupField(markFieldId)
                        {
                            SortOrder = ScheduleSortOrder.Ascending,
                        };
                        def.AddSortGroupField(sg);
                    }
                    catch { }
                }

                // ── Step 5: User-friendly column headers ─────────────
                var headers = new Dictionary<string, string>
                {
                    { "Mark",                  "AP Name" },
                    { "Ekahau_Vendor",         "Vendor" },
                    { "Ekahau_Model",          "Model" },
                    { "Ekahau_Technology",     "WiFi Standard" },
                    { "Ekahau_Bands",          "Frequency Bands" },
                    { "Ekahau_MountHeight_m",  "Mount Height (m)" },
                    { "Ekahau_Mounting",       "Mount Type" },
                    { "Ekahau_TxPower",        "Tx Power" },
                    { "Ekahau_Channels",       "Channels" },
                    { "Ekahau_Streams",        "MIMO Streams" },
                    { "Ekahau_Antenna",        "Antenna" },
                    { "Ekahau_Tags",           "Tags" },
                };
                foreach (var kvp in headers)
                {
                    if (addedFields.TryGetValue(kvp.Key, out var fid))
                    {
                        try
                        {
                            var f = def.GetField(fid);
                            if (f != null) f.ColumnHeading = kvp.Value;
                        }
                        catch { }
                    }
                }

                tx.Commit();
                return schedule.Name;
            }
            catch
            {
                if (tx.HasStarted()) tx.RollBack();
                return null;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helper data classes for AP Place
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Family picker data item.</summary>
    public class FamilyTypeInfo
    {
        public ElementId SymbolId { get; set; }
        public string CategoryName { get; set; } = "";
        public string FamilyName { get; set; } = "";
        public string TypeName { get; set; } = "";
    }

    /// <summary>
    /// Data passed to the confirmation dialog — one floor's worth of APs.
    /// The <see cref="ApStagingEntry.Include"/> field is set to true by default
    /// and flipped by checkboxes in the dialog (REQ 9).
    /// </summary>
    public class ApPlaceConfirmFloor
    {
        public string FloorName { get; set; } = "";
        public string ViewName { get; set; } = "";
        public List<ApStagingEntry> AccessPoints { get; set; } = new List<ApStagingEntry>();
    }
}
