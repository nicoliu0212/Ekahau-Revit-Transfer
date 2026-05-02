# Changelog

All notable changes to **Ekahau ↔ Revit Transfer** are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

---

## [2.5.6] — 2026-05-01

### Fixed
- **Image overlay verification step was silently skipped when a floor had 0 APs.** The user reported "no overlay shown, no manual alignment offered, jumps straight to 0 APs placed". Two early-exit guards (`floorAps.Count == 0` for AP-to-floor-plan ID mismatches, and `apsToPlace.Count == 0` for user-unchecked-all) ran BEFORE the overlay step, so a wrong floor match or stale AP list would dismiss the floor with no visual feedback.

### Changed
- **Overlay verification now runs unconditionally per floor**, BEFORE the AP-count check. Users always get to confirm floor alignment + manually align if needed, even when zero APs end up matching this floor.
- **New "no APs on this floor" diagnostic dialog**: when `floorAps.Count == 0`, the user now sees an explicit dialog explaining the AP-vs-floor-plan ID mismatch with the three most likely causes (truly empty floor / wrong view matched / floor renamed in Ekahau) — instead of a silent skip.
- Added Debug.WriteLine of the AP→FloorPlan id matching: total AP count, this floor's match count, list of all distinct FloorPlanIds in the .esx. Visible in DebugView for troubleshooting.

## [2.5.5] — 2026-05-01

### Changed
- **Manual alignment now reachable from the per-floor verification step** — works for ALL calibration tiers, not just Tier 3. Previously the visual two-point alignment only ran when the .esx had no `revitAnchor` and no `.ekahau-cal.json`. Now even a Tier-1 (revitAnchor present) or Tier-2 (.cal.json found) auto-calibration can be manually corrected if the user spots misalignment in the verification overlay.
- Verification dialog renamed CommandLink2 from **"Alignment is off — abort this floor"** to **"Image is misaligned — manually align with two points"**. Picking it triggers the visual two-point alignment, then recurses back into the verification step so the user can confirm or re-align again.
- A separate **Cancel** button on the verification dialog still aborts the floor.
- `OfferVisualAlignmentCore` gains a `skipIntro` parameter — when called from the verification step the intro dialog is skipped (the user already opted in via "Manually align").

### Result
- After a Bug Fix #14 scenario (anchor exists but is wrong), the user can now fix it in-place without re-running ESX Read or re-exporting the .esx.

## [2.5.4] — 2026-05-01

### Removed
- **Manual coordinate-input dialog** (`TwoPointPixelDialog`) and the typed-coordinates calibration path entirely. Users have no reliable way to look up Ekahau pixel/metre values for arbitrary reference points; visual alignment (clicking on the placed image) replaces it cleanly. Bug Fix #16.
- The "Type Ekahau coordinates" command link is gone from the manual-calibration intro dialog. Two options remain: **Start visual alignment** / **Skip**.
- `BuildAnchorFromTwoPoints` helper deleted (only the visual-alignment path's rotation-aware anchor synthesis is kept).

### Result
- Zero coordinate typing anywhere in the workflow. All four calibration clicks happen directly in the Revit view (two on the Revit model, two on the placed Ekahau image).

## [2.5.3] — 2026-05-01

### Fixed
- **Revit 2024 / 2023 startup crash**: `System.IO.FileNotFoundException: Could not load file or assembly 'System.Runtime, Version=8.0.0.0'`. Caused by `System.Text.Json 8.0.5` (the version we pulled in v2.5.2) which has transitive references to `System.Runtime 8.0.0.0` — a .NET 8 facade assembly that doesn't exist on .NET Framework 4.8.  Revit also doesn't honour `<dll>.dll.config` binding redirects so a redirect-based fix was off the table.
- Pinned `System.Text.Json` to `6.0.10` for the net48 build — the last version that compiles cleanly against the net48 facade graph (all references resolve to net48-compatible BCL polyfills like `System.Memory 4.0.1.1`, `Microsoft.Bcl.AsyncInterfaces 6.0.0.0`).
- Verified via `MetadataLoadContext` that the new `System.Text.Json.dll` references contain **no `8.0.0.0` versions** — only 4.x / 6.x facades that .NET Framework 4.8 can load.

### Unaffected
- net8 (Revit 2025 / 2026) and net10 (Revit 2027) builds are unchanged — those runtimes ship `System.Text.Json` in the BCL itself, no NuGet needed.

## [2.5.2] — 2026-05-01

### Fixed
- **MSI now installs to Revit 2023 + 2024** in addition to 2025/2026/2027. Previous releases skipped the legacy versions because the net48 build needs 8 supporting BCL DLLs (System.Text.Json + dependencies) which were only available via `install.ps1`. v2.5.2 ships those DLLs inside the MSI so a single double-click covers all five Revit versions.

### Changed
- MSI grew from ~232 KB to ~440 KB to accommodate the net48 BCL DLLs for Revit 2023 / 2024.
- Per-user AppData install scope unchanged — all five versions install to `%APPDATA%\Autodesk\Revit\Addins\YYYY\` with no admin / UAC.

## [2.5.1] — 2026-05-01

### Added
- **About / Version dialog** — new ribbon button on a small "Help" panel. Shows:
  - Plugin version + release date + last build timestamp of the loaded DLL
  - .NET runtime (Framework 4.8 / .NET 8 / .NET 10)
  - Detected Revit version
  - Install scope (Per-user / All-users) — auto-detected from DLL path
  - Full DLL path
  - Buttons: open project page, view releases (check for updates), report issue, open install folder

### Confirmed
- MSI install scope is **per-user** under `%APPDATA%\Autodesk\Revit\Addins\YYYY\` (set in v2.4.0). No UAC prompt, no admin required, no writes to Program Files / ProgramData / HKLM. Suitable for IT-managed machines that block writes to system locations.

## [2.5.0] — 2026-04-22

### Added
- **ESX Read — Visual two-point alignment** (Tier 3b calibration). When a `.esx` has no `revitAnchor` and no `.ekahau-cal.json`, the user can now visually align the floor plan instead of typing Ekahau coordinates:
  1. Plugin drops the Ekahau image into the matched view at an estimated position
  2. User clicks **two pairs** of matching points: each pair = same point clicked once on the Revit model and once on the floor-plan image (in the same view)
  3. Plugin recovers **scale + rotation + translation** from the two correspondences
  4. Image snaps into the calibrated pose (rotation included) for visual verification
  5. Verification dialog shows scale (mm/px), rotation (°), Pair-2 residual (mm), with retry / continue / cancel
- The synthesised `EsxRevitAnchorData` populates `XformBasisXx/XY/YX/YY` (rotation matrix) so the existing Mode-1 `BuildEkahauToRevitXform` handles rotation correctly — no special-casing in the AP-coordinate pipeline.
- The manual-calibration intro dialog now offers three options: **Visual alignment (recommended)** / Type Ekahau coordinates / Skip.

### Why
Designer creates the Ekahau project from a PDF (no Revit involvement) → the resulting `.esx` has no coordinate relationship to the Revit model. Typing Ekahau pixel coordinates from another window is error-prone. Clicking the same point twice — once on the model, once on the image — is more intuitive and naturally handles rotation between the two coordinate systems.

## [2.4.1] — 2026-04-22

### Added
- **DWG Export ribbon icon** — 32 px and 16 px PNGs added to `Resources/`. Visually distinct red right-arrow + "DWG" label vs ESX Export's blue arrow, so the two export commands are easy to tell apart at a glance.

## [2.4.0] — 2026-04-22

### Added
- **ESX Read — Two-point manual calibration** (Tier 3a fallback). When a `.esx` has no `revitAnchor` and no `.ekahau-cal.json` is found, the user can now click two reference points in the Revit view and type the corresponding Ekahau coordinates (in metres or pixels). The plugin computes a scale + translation transform, sanity-checks it against the `.esx`'s declared `metersPerUnit`, and synthesises an `EsxRevitAnchorData` so the rest of the import pipeline works unchanged.
- New `TwoPointPixelDialog` WPF dialog with metres/pixels input mode toggle.

### Changed
- **MSI installer is now per-user** (`Scope="perUser"`), installs to `%APPDATA%\Autodesk\Revit\Addins\YYYY\`. No admin / UAC required, no writes to `Program Files` or `ProgramData`, all registry entries under HKCU. Same path for Revit 2025 / 2026 / 2027 — Revit's per-user path was unaffected by the 2027 all-users path change.
- `install.ps1` simplified — no longer takes a `-Scope` parameter; always installs per-user to AppData.
- `fix-revit-2027.bat` now installs to AppData (no admin needed); still cleans up any prior installs from the system locations.

## [2.3.5] — 2026-04-21

### Changed
- **WiFi Plan view**: added `OST_ElectricalEquipment`, `OST_ElectricalFixtures`, `OST_GenericModel`, `OST_Conduit`, `OST_ConduitFitting`, `OST_CableTray`, `OST_CableTrayFitting` to the visible-categories set. Generic Models are still filtered down to placed APs only by the existing `Ekahau - Hide Non-AP Elements` view filter (Bug Fix #11).

## [2.3.4] — 2026-04-21

### Changed
- **AP Place cleanup is now unconditional** (REQ 5 dialog removed). After successful placement, the plugin removes:
  - All per-AP marker IDs (`MarkerElementIds`)
  - All per-floor overlay IDs (`OverlayElementIds`) — image overlay, CropBox corner crosses, band legend
  - Workset safety net — sweeps any element on the `Ekahau AP Markers` workset in the source views
  - Clears marker IDs from staging JSON so re-runs don't try to delete already-gone elements

### Added
- New `OverlayElementIds` field on `ApStagingFloor` for floor-level temp elements.

## [2.3.3] — 2026-04-21

### Fixed
- **Bug Fix #15** — `PlaceFloorPlanImage` rewritten for robustness: reads actual PNG dimensions, uses `BoxPlacement.Center`, places at `view.GenLevel.Elevation`, sets width via `imgInst.Width` property with `RASTER_SHEETWIDTH` parameter fallback, verifies via bounding-box read-back, comprehensive `Debug.WriteLine` logging at every step, user-facing TaskDialog on real failures (instead of silent null returns).

## [2.3.2] — 2026-04-21

### Changed
- **ESX Export resolution dialog**: replaced 3 options (1 000 / 2 000 / 4 000 px) with **5 options** (2 000 / 4 000 / 8 000 / 10 000 / 15 000 px); default raised from 2 000 to **4 000**.
- **Image export DPI**: 72 → **300**.
- **FitDirection**: now auto-picked from CropBox aspect (Vertical when `cropH > cropW × 1.2`, otherwise Horizontal) so PixelSize applies to the longer dimension.

## [2.3.1] — 2026-04-21

### Added
- **DWG calibration fallback enhancements** in ESX Read:
  - Browse-for-file fallback when no `.ekahau-cal.json` is auto-discovered.
  - Multi-cal-file picker when 2-4 candidates exist (TaskDialog with command links).
  - Pixel-offset math now centres the CropBox inside the Ekahau image (handles Ekahau's typical DWG-import margins).

## [2.3.0] — 2026-04-21

### Added
- **DWG Export** command (new ribbon button between ESX Export and ESX Read). Exports floor-plan views as `.dwg` tuned for Ekahau (mm units, AutoCAD R2018, AIA layers, ACIS solids, true colour, shared coords). Optional Clean mode duplicates the view and hides everything except WiFi-relevant categories.
- **Calibration sidecar** (`<view>.ekahau-cal.json`) written next to each `.dwg`. Stores CropBox bounds in Revit feet, DWG export unit, view transform, and Ekahau import instructions.
- **ESX Read calibration fallback** (Tier 2): when an `.esx` has no `revitAnchor`, scan the same folder for `.ekahau-cal.json`, name-match against `revitViewName`, apply the calibration to anchorless floor plans.

### Changed
- `csproj` now uses `<UseWindowsForms>true</UseWindowsForms>` alongside `UseWPF` so `FolderBrowserDialog` resolves on all three frameworks.

## [2.2.0] — 2026-04-21

### Added
- **ESX Read — mandatory image overlay verification step**. After cleanup and before AP marker placement, places the Ekahau floor-plan PNG as a reference overlay in the matched view, draws four green `+` crosses at the CropBox corners, switches to the view, and asks the user to confirm alignment. Three-button choice: continue / abort this floor / skip verification.
- New `EsxMarkerOps.PlaceFloorPlanImage` and `EsxMarkerOps.DrawReferenceCrosses` helpers.

## [2.1.5] — 2026-04-20

### Added
- Defensive **polymorphic `object` JSON converter** in `EsxExportCommand` so nested `Dictionary<string, object>` values in `floorPlans.json` (notably `revitAnchor`) are guaranteed to serialise via runtime type — protects against the .NET 6+ behaviour change where `object`-typed properties default to declared-type serialisation.

## [2.1.2 / 2.1.3] — 2026-04-20

### Fixed
- **Revit 2027 install path** corrected. Revit 2027 rejects manifests under `C:\ProgramData\...` (stricter add-in isolation) and reserves `C:\Program Files\Autodesk\Revit 2027\AddIns\` for code-signed Autodesk-internal add-ins. The MSI now installs to `C:\Program Files\Autodesk\Revit\Addins\2027\` (the new third-party all-users path) using the standard layered manifest layout.

## [2.1.0] — 2026-04-20

### Added
- **Revit 2027 support** via a third build target (`net10.0-windows`). `VersionCompat` reuses the modern API path for both `REVIT_NET8` and `REVIT_NET10`. The `Autodesk.Revit.SDK 2027.0.0` NuGet hasn't shipped yet, so the net10 build references `2026.0.0.9999` as a placeholder; bump the version when 2027 ships.
- `install.ps1` updated for the new Revit 2027 paths.

## [2.0.0] — 2026-04-20

### Added
- **Multi-version Revit compatibility** (2023-2027) via multi-targeted `csproj`:
  - `net48` for Revit 2023 / 2024 (Revit 2024 SDK)
  - `net8.0-windows` for Revit 2025 / 2026 (Revit 2025 SDK)
- **`VersionCompat` API shim** abstracts every version-sensitive API:
  - `ElementId.Value` (long) ↔ `IntegerValue` (int)
  - `SpecTypeId` ↔ `ParameterType` (reflection-based fallback for Revit 2023)
  - `ImageType.Create` overload differences
  - `ParameterFilterRuleFactory.CreateHasNoValueParameterRule` with fallback
- **`PolyfillExtensions`** for net48: `string.Contains(string, StringComparison)` (introduced in .NET Core 2.1).
- `install.ps1` multi-version installer; WiX MSI extended to install on all detected versions.

### Changed
- `LangVersion` lowered to 10.0 (works on both net48 and net8/10).
- `JIT` safety: APIs that might fail to load on the older runtime are isolated in `[MethodImpl(MethodImplOptions.NoInlining)]` methods so try/catch can recover gracefully.

## [1.9.0] — Earlier

### Added
- **Ribbon button icons** — 10 PNG resources embedded into the assembly, loaded via `IconHelper.LoadIcon`.

## [1.8.x] — Earlier

### Fixed
- **Bug Fix #11** — WiFi Plan view: hide non-AP Generic Models via a project-level `ParameterFilterElement` (filter rule: `Ekahau_AP_Name` has no value). Removes false-positive Generic Models from the view.
- **Bug Fix #12** — AP labels: switched from `IndependentTag` (which shows whatever the tag family's label is bound to — often Type Mark) to `TextNote` containing the explicit instance Mark.
- **Bug Fix #13** — `EndpointSnapper` threshold reduced from 2.0 ft to 0.5 ft + angle-preservation guard (rejects snaps that tilt either segment by more than 2°). Stops the snapper from pulling unrelated walls together.
- **Bug Fix #14** — `revitAnchor` defensive serialisation (polymorphic converter).

## [1.4.0] — Earlier

### Added
- **AP Place** command: 18-REQ implementation including three-column family picker, per-AP confirmation, batch transactions, workset assignment, **12 Ekahau shared parameters**, optional WiFi Plan view, optional per-level AP schedule.
- **WiFi Plan view** generator (Bug Fix #11 view filter, AP highlighting, TextNote labels).
- **AP Schedule** generator with per-level filtering.

## [1.0.0 — 1.3.x]

Initial implementation:
- **Param Config** — `Ekahau_WallType` shared parameter creation, type collection, mapping dialog, ExtensibleStorage for linked-model overrides.
- **ESX Export** — full pipeline (PNG render, wall/door/window geometry, mapping review, ESX ZIP).
- **ESX Read** — `.esx` parsing, floor matching, AP review, preview marker placement, staging JSON.

[2.4.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.4.0
[2.3.5]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.5
[2.3.4]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.4
[2.3.3]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.3
[2.3.2]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.2
[2.3.1]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.1
[2.3.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.0
[2.2.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.2.0
[2.1.5]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.1.5
[2.1.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.1.0
[2.0.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.0.0
