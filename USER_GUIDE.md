# Ekahau ↔ Revit Transfer — User Guide

**Version**: v2.4.0
**Supported Revit**: 2023, 2024, 2025, 2026, 2027

---

## Table of contents

1. [Installation & launch](#installation--launch)
2. [Recommended workflow](#recommended-workflow)
3. [Param Config](#1-param-config)
4. [ESX Export](#2-esx-export)
5. [DWG Export](#3-dwg-export)
6. [ESX Read](#4-esx-read)
7. [AP Place](#5-ap-place)
8. [FAQ](#faq)

---

## Installation & launch

### Install options

**Option A — MSI installer (recommended for Revit 2025/2026/2027)**
1. Double-click `EkahauWiFiTools-v2.4.0.msi`
2. Install proceeds **without UAC prompt** — no admin privileges required
3. Files install to `%APPDATA%\Autodesk\Revit\Addins\YYYY\` (per-user, only visible to you)
4. Launch Revit

**Option B — PowerShell script (covers Revit 2023-2027)**
```powershell
.\install.ps1
```
The script auto-detects which Revit versions are installed and copies the matching DLL into `%APPDATA%\Autodesk\Revit\Addins\YYYY\` for each one. No admin needed.

### After install

Open Revit → top of the ribbon there's a new **WiFi Tools** tab with two panels:

| Panel | Buttons |
|------|---------|
| **Export & Read** | Param Config / ESX Export / DWG Export / ESX Read |
| **Access Point** | AP Place |

Each button has its own icon and hover tooltip.

---

## Recommended workflow

```
┌──────────────┐  ┌─────────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐
│ Param Config │→ │ ESX Export  │→ │ Ekahau   │→ │ ESX Read │→ │ AP Place │
│ wall types   │  │ floor plans │  │ design   │  │ stage AP │  │ place    │
└──────────────┘  └─────────────┘  └──────────┘  └──────────┘  └──────────┘
   one-time         per-view         external        preview        actual
   per project                       WiFi design     markers        family
                                                                    instances
```

**Typical timeline:**
1. First time on a Revit project → run **Param Config** once
2. For each floor plan view → run **ESX Export** once
3. WiFi engineer designs APs in Ekahau Pro → saves `.esx`
4. Run **ESX Read** to import AP positions (placed as preview markers first)
5. Run **AP Place** to replace preview markers with real AP family instances + auto-generate WiFi Plan view + AP Schedule

---

## 1. Param Config

### Purpose

Writes the `Ekahau_WallType` shared parameter onto Wall / Door / Window **types**. ESX Export reads this parameter to decide which Ekahau RF preset (e.g. `BrickWall`, `GlassWall`, `Concrete`) each wall segment uses.

### Prerequisites

- The active view must be a **floor plan view**
- The view must contain at least one wall, door, or window instance

### Steps

1. **Open the floor plan view you want to configure**

2. **Click `WiFi Tools → Param Config`**

3. **Shared parameters auto-created**
   - First run: creates the shared parameter file at `%APPDATA%\Ekahau_SharedParams.txt`
   - Auto-binds `Ekahau_WallType` to Wall / Door / Window categories
   - Confirmation dialog appears

4. **(Optional) Linked-model selection**
   - If linked Revit models are loaded (e.g. structural model), a "Linked Model Selection" dialog appears
   - Pick a linked model to configure together, or pick "Host only"

5. **Type mapping dialog**
   - Lists every wall / door / window **type** in the current view (one row per type, not per instance)
   - Each row shows: Type name, Category, Current value, Suggested preset, Source (Parameter / Keyword / Fallback)
   - **Suggestion sources:**
     - `Param: xxx` — value already set
     - `Name match: 'concrete'` — keyword found in type name
     - `Material match: 'glass'` — keyword found in compound-structure layer material
     - `Curtain Wall detected` — auto-recognised curtain wall
     - `Default for wall/door/window` — fallback default

6. **Adjust the dropdowns**
   - Each row has a dropdown to pick `Skip` or a specific preset
   - Linked-model types are tagged `[Link]` — their config saves into ExtensibleStorage (does not modify the link source file)

7. **Click "OK" to apply**
   - Host model types → write to `Ekahau_WallType` parameter
   - Linked model types → write to ExtensibleStorage (project-local)
   - Summary dialog: Updated / Skipped / Total

### Key points

- **Only needs to be done once** per project. Re-run when new wall types are added
- Skipped types use the default preset during ESX Export
- Type names containing keywords like `concrete`, `brick`, `glass`, `drywall` get auto-suggested matches

---

## 2. ESX Export

### Purpose

Exports the current Revit project's floor plan views (with wall/door/window geometry) as `.esx` files openable in Ekahau Pro. Ekahau engineers then design WiFi coverage on top.

### Prerequisites

- **Param Config** done (recommended; otherwise all walls use default preset)
- At least one floor-plan view with **active CropBox**

### Steps

1. **Click `WiFi Tools → ESX Export`**

2. **View selector**
   - Lists all floor-plan views with active CropBox
   - Use checkboxes to select views (multi-select)
   - Search box filters by name

3. **Resolution dialog** (5 options)
   - 2 000 px (draft — fast)
   - **4 000 px (recommended default)**
   - 8 000 px (high quality)
   - 10 000 px (very high — slower)
   - 15 000 px (max quality, large file)

4. **Export mode**
   - **Merge All** — all views combined into one `.esx`, each view as a floor
   - **Separate** — one `.esx` per view

5. **Linked-model selection** (similar to Param Config)

6. **Per-view mapping check**
   - For each view, confirms the wall-type → preset mapping again
   - Highlights any types not configured in Param Config
   - Three buttons per view: `Export` / `Skip View` / `Cancel All`

7. **Save dialog**
   - Pick `.esx` output location (Merge mode) or folder (Separate mode)

8. **Export progress**
   - PNG render of each view at chosen resolution + 300 DPI
   - Geometry extraction (wall endpoints, door/window positions in world coords)
   - Unit conversion (feet → metres, pixels → metres)
   - Pack into ZIP-format `.esx`

9. **Done dialog** — output path, view count, segment count

### Key points

- Each view's **CropBox extent** determines the export region — too large = huge PNG, slow Ekahau load
- **View scale** affects PNG clarity (1:100 or 1:200 recommended)
- Rotated views auto-convert coordinates to true north

---

## 3. DWG Export

### Purpose

Exports floor-plan views as `.dwg` tuned for Ekahau import (millimetre units, AutoCAD R2018 format, AIA layer mapping). Also writes a `.ekahau-cal.json` calibration sidecar so AP coordinates can round-trip back to Revit even when the resulting `.esx` has no native `revitAnchor`.

### Steps

1. **Click `WiFi Tools → DWG Export`**

2. **View selector** (reuses ESX Export's picker)

3. **Mode dialog**
   - **Clean (recommended)** — duplicates the view, hides everything except WiFi-relevant categories (Walls, Doors, Windows, Columns, Floors, Stairs, Rooms, Grids, Room Separation Lines)
   - **Full (as-is)** — exports the view exactly as displayed

4. **Output folder picker**

5. **Per view**: produces two files
   - `<view>.dwg` — geometry for Ekahau
   - `<view>.ekahau-cal.json` — calibration data (CropBox bounds in Revit feet, transform, view name)

6. **Summary dialog** with file count, output path, Ekahau import instructions, "Open output folder" button

### In Ekahau

1. Open Ekahau AI Pro → File → New Project (or open existing)
2. Add Floor Plan → Import from File
3. Select the `.dwg`
4. Set unit to **Millimeters** when prompted — scale resolves automatically
5. Design APs, save as `.esx`

### Round-trip back

Run **ESX Read** in Revit. If the `.esx` has no `revitAnchor`, ESX Read scans the same folder for `*.ekahau-cal.json` and applies it automatically (Tier 2 calibration).

---

## 4. ESX Read

### Purpose

Reads a designed `.esx` and stages AP positions back into Revit. Places **temporary preview markers** (crosshairs + name labels + corner crosses + image overlay) for visual verification, then writes a per-project staging JSON consumed by AP Place.

### Steps

1. **Click `WiFi Tools → ESX Read`**

2. **Pick the `.esx` file** (standard file dialog)

3. **Parse progress** — typically 1-3 seconds (unzips + reads JSON entries)

4. **Three-tier coordinate calibration** (automatic):

   | Tier | Source | When |
   |------|--------|------|
   | **1** | `revitAnchor` inside the `.esx` | ESX Export round-trip — best |
   | **2** | `*.ekahau-cal.json` next to the `.esx` | DWG Export round-trip — auto-discovered, name-matched, picker, or browse-for-file fallback |
   | **3a** | Two-point manual calibration | External `.esx` — pick 2 reference points + type Ekahau coordinates |
   | **3b** | CropBox auto-fit | Final fallback — assumes image fills the CropBox |

5. **Floor matching dialog**
   - Left column: floor plan names from the `.esx`
   - Right column: dropdown to pick the matching Revit ViewPlan
   - Auto-matches by exact name, case-insensitive name, or unique substring containment
   - Uncheck floors you don't want to import

6. **Per-floor AP review**
   - Lists every AP on this floor with name, vendor, model, bands, coords, mounting height
   - Checkboxes to include/exclude
   - "Select all / none" buttons

7. **Mandatory image-overlay verification**
   - Places the Ekahau floor-plan PNG as a reference overlay in the matched view
   - Draws four green `+` crosses at the CropBox corners
   - Switches to the view + refreshes
   - Three-button verification dialog:
     - **Continue** — alignment looks correct, proceed to AP markers
     - **Abort this floor** — alignment is off, skip this floor (other floors still process), troubleshooting tips shown
     - **Skip verification** — keep overlay, proceed without confirming

8. **Two-point manual calibration** *(only if no anchor and no calibration file)*
   - Intro dialog explains the process
   - You click Point A in the Revit view → enter the Ekahau coordinates (in metres or pixels)
   - You click Point B (far from A) → enter Ekahau coordinates
   - Plugin computes scale + translation transform, sanity-checks against the `.esx`'s `metersPerUnit`, warns on > 20% scale mismatch
   - **Tip:** in Ekahau Pro, hover over a point — the bottom status bar shows X / Y in metres

9. **AP markers placed**
   - Crosshair (DetailLine + DetailArc) + AP name TextNote per AP
   - Color-coded by frequency band (2.4 GHz blue, 5 GHz green, 6 GHz orange, multi-band purple)
   - Adaptive marker size based on view extent
   - Old markers from previous runs are auto-cleaned (no dialog)

10. **Staging JSON written**
    - Path: `%TEMP%\EkahauRevitPlugin\{project}_{12-char-hash}\ap_staging.json`
    - Contains every AP's world coords, mounting height, all 12 radio summary fields (vendor, model, bands, technology, TxPower, channels, MIMO streams, antenna info, etc.)
    - Project-isolated by MD5 hash of doc path → multiple Revit projects open in parallel don't conflict

11. **Summary dialog** — markers placed / floors / staging path

### Key points

- Preview markers are **temporary** — delete manually any time, AP Place auto-cleans them
- **Re-running ESX Read** auto-cleans previous markers and re-places — no duplicates
- Staging persists across Revit sessions — close + reopen, AP Place still finds it

---

## 5. AP Place

### Purpose

Reads ESX Read's staging JSON, lets you pick an AP family type, places **real Revit family instances** at every AP position, populates 12 Ekahau shared parameters, optionally generates a WiFi Plan view and per-level AP Schedule.

### Prerequisites

- **ESX Read** has been run for the same project
- At least one AP family loaded in the project (recommended: a WiFi AP family in Generic Models)

### Steps

#### 1. Launch
`WiFi Tools → AP Place`

#### 2. Staging validation
- Reads `%TEMP%`'s `ap_staging.json`
- Validates project path hash (prevents cross-project misuse)
- Validates referenced views still exist (renamed/deleted views are skipped with notice)

#### 3. Three-column family picker
- **Left** column: Category (Generic Models / Electrical Equipment / etc.)
- **Middle** column: Family
- **Right** column: Type
- Top search box filters across all three columns
- Plugin **remembers the last selection** for next time

#### 4. Auto-create 12 Ekahau shared parameters
- `Ekahau_AP_Name`, `Ekahau_Vendor`, `Ekahau_Model`
- `Ekahau_Mounting`, `Ekahau_Bands`, `Ekahau_Technology`
- `Ekahau_TxPower`, `Ekahau_Channels`, `Ekahau_Streams`
- `Ekahau_Antenna`, `Ekahau_Tags`
- `Ekahau_MountHeight_m` (numeric)
- All bound as **InstanceBinding** to the AP family's category

#### 5. Confirmation dialog
- AP list grouped by floor with per-AP checkboxes
- Workset dropdown (if project is workshared)

#### 6. Batch placement (20 APs per transaction)
- Per AP:
  - `NewFamilyInstance` at the recorded world coords
  - Z = mounting height (from `.esx`)
  - Sets `Mark = AP name`
  - Sets `Comments = Vendor | Model | Bands | Height | Tags`
  - Writes all 12 Ekahau shared parameters
  - Assigns to selected workset (if applicable)

#### 7. Cleanup (unconditional)
- Removes ESX Read preview markers (per-AP)
- Removes overlay artefacts (image, corner crosses, legend) — per-floor
- Workset safety net — sweeps any leftover element on the `Ekahau AP Markers` workset

#### 8. Result summary
- Total placed / failed / per-floor breakdown + warnings

#### 9. (Optional) WiFi Plan view
For each floor with placed APs, prompt: "Create WiFi Plan view?"
- **Yes** → creates one view per level: `WiFi Plan - {level name}`
  - Visible: Walls, Doors, Windows, Columns, Stairs, Floors (halftone), Grids, **Data Devices, Conduits, Cable Trays** (low-voltage infrastructure context), placed APs
  - Hidden: furniture, MEP, annotations, etc.
  - Filter: `Ekahau - Hide Non-AP Elements` view filter (created automatically) — hides any Generic Model with empty `Ekahau_AP_Name`
  - APs highlighted with thick blue lines + name labels
- **No** → skip

#### 10. (Optional) AP Schedule
Per level prompt: "Create AP Schedule?"
- **Yes** → one schedule per level: `Ekahau AP Schedule - {level name}`
  - Filter: `Ekahau_AP_Name has value` + `Level = current floor` (only this floor's Ekahau APs)
  - Columns (user-friendly headers): AP Name / Vendor / Model / WiFi Standard / Frequency Bands / Mount Height (m) / Mount Type / Tx Power / Channels / MIMO Streams / Antenna / Tags
  - Sorted by Mark ascending
- **No** → skip

### Key points

- Family selection **remembered** between runs
- Staging persists — re-running AP Place won't double-place already-placed APs
- WiFi views and Schedules **reusable** — re-running AP Place reuses same-named views/schedules; the live filter automatically picks up new APs
- Batch transactions — 20 APs per transaction; failed batches roll back without affecting other batches

---

## FAQ

### Q1. "Param Config" says "no walls/doors/windows found"
The active view isn't a floor plan, or contains no such elements. Switch to the right floor plan and retry.

### Q2. After ESX Export every wall uses `Generic` preset
Those wall types aren't configured in Param Config. Either run Param Config first, or pick presets manually in the ESX Export "Mapping check" dialog per view.

### Q3. After ESX Read I see no preview markers
Check:
1. Did you pick the right Revit view in the floor matching dialog?
2. Did you uncheck all APs in the AP review dialog?
3. Is the Detail Items category visible in the view?

### Q4. AP Place says "no staging data found"
Run ESX Read first. If it ran but says "project mismatch", you opened a different Revit project (staging is isolated by project path hash).

### Q5. After AP Place all APs are stacked at origin (0, 0)
The `.esx`'s floor plan scale is wrong. Check Ekahau's "Scale" setting on the floor plan — it must match the original Revit view scale. Re-export the `.esx` and re-run ESX Read.

### Q6. WiFi Plan view shows lots of unrelated Generic Models
The view filter didn't take effect. Verify:
1. The `Ekahau_AP_Name` shared parameter was created (auto-done in AP Place step 4)
2. Your project's existing Generic Models don't already have `Ekahau_AP_Name` set

### Q7. AP Schedule includes non-AP Generic Models too
Same as Q6 — the schedule filter relies on `Ekahau_AP_Name`. AP Place creates it automatically; if the schedule was created manually, add a `Ekahau_AP_Name has value` filter manually.

### Q8. Revit 2023 says "could not find SpecTypeId"
v2.4.0 uses a reflection fallback to reach `ParameterType` on Revit 2023. Confirm the installed DLL is the **net48** build (under `Addins\2023\EkahauWiFiTools\`, not net8). `install.ps1` picks the right one automatically.

### Q9. Which DLL goes where?

| Revit | Runtime | DLL location |
|---|---|---|
| 2023, 2024 | .NET Framework 4.8 | `bin\Release\net48\` |
| 2025, 2026 | .NET 8 | `bin\Release\net8.0-windows\` |
| 2027 | .NET 10 | `bin\Release\net10.0-windows\` |

**Revit 2027 dropped .NET 8** — the net8 DLL **will not load** in Revit 2027. `install.ps1` and the MSI both auto-pick the right runtime.

### Q10. Where is staging stored?
`%TEMP%\EkahauRevitPlugin\{project}_{12-char-hash}\ap_staging.json`

Different projects auto-isolate via the hash — safe to open multiple Revit projects in parallel.

### Q11. Where does the installer put the plugin?
`%APPDATA%\Autodesk\Revit\Addins\YYYY\` (per-user install).
Visible only to your user account, no admin needed, doesn't write to Program Files / ProgramData.

---

## Version & support

- **Current version**: v2.4.0
- **Revit support**: 2023 / 2024 / 2025 / 2026 / 2027
- **MSI installer**: covers Revit 2025-2027 (per-user, no admin)
- **PowerShell script**: covers Revit 2023-2027

### Runtime mapping

| Revit | Runtime |
|-------|---------|
| 2023 / 2024 | .NET Framework 4.8 |
| 2025 / 2026 | .NET 8 |
| 2027 | .NET 10 |

For technical issues or suggestions, please file an issue on GitHub.
