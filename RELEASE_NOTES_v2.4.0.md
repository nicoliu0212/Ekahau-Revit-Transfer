# Ekahau ↔ Revit Transfer v2.4.0

First public release. Bridges Autodesk Revit and Ekahau AI Pro for WiFi-planning workflows across Revit 2023 → 2027.

## ✨ What's in this release

### Five ribbon commands

| Command | What it does |
|---|---|
| **Param Config** | Creates the `Ekahau_WallType` shared parameter and walks the active view, suggesting RF material presets via keyword + material-layer matching (with linked-model support via ExtensibleStorage). |
| **ESX Export** | Floor plans → Ekahau `.esx` with wall/door/window geometry, RF material presets, embedded PNG. 5 resolution options (2 000 — 15 000 px), 300 DPI, aspect-aware fit direction. |
| **DWG Export** | Clean DWG export tuned for Ekahau (mm units, AutoCAD R2018, AIA layers) + sidecar `.ekahau-cal.json` calibration file for accurate AP round-trip. |
| **ESX Read** | Parses `.esx`, places preview markers, writes per-project staging. **Three-tier coordinate calibration**: `revitAnchor` → `.ekahau-cal.json` → **two-point manual calibration** (pick 2 points + type Ekahau coords). Mandatory image-overlay verification step. |
| **AP Place** | Replaces preview markers with real family instances. Three-column family picker, batch transactions, workset assignment, **12 Ekahau shared parameters** auto-created and populated, optional WiFi Plan view + per-level AP schedule. |

### Multi-version Revit support

| Revit version | Runtime | DLL | Install |
|:---:|:---:|:---:|:---:|
| 2023 / 2024 | .NET Framework 4.8 | `bin/Release/net48/` | `install.ps1` |
| 2025 / 2026 | .NET 8 | `bin/Release/net8.0-windows/` | MSI |
| 2027 | .NET 10 | `bin/Release/net10.0-windows/` | MSI |

Cross-version Revit API differences are abstracted in a single `VersionCompat` class (no `#if` directives in business logic).

### Per-user install

The MSI installs **per-user** to `%APPDATA%\Autodesk\Revit\Addins\YYYY\` — **no admin / UAC required**, no writes to `Program Files` or `ProgramData`, all registry entries under HKCU.

## 📦 Installation

**Recommended (Revit 2025 / 2026 / 2027)** — download `EkahauWiFiTools-v2.4.0.msi` below, double-click, install.

**Revit 2023 / 2024** — clone the repo and run:
```powershell
.\install.ps1
```

See the [README](https://github.com/nicoliu0212/Ekahau-Revit-Transfer#-installation) for detailed installation paths and the [User Guide](https://github.com/nicoliu0212/Ekahau-Revit-Transfer/blob/main/USER_GUIDE.md) for full documentation.

## 🚀 Quick workflow

```
Param Config  →  ESX Export  →  Ekahau Pro design  →  ESX Read  →  AP Place
```

After AP Place, optional one-click generators:
- **WiFi Plan view** per level — only WiFi-relevant categories visible, APs highlighted
- **AP Schedule** per level — 12-column schedule of every placed AP

## 🔄 Round-trip flexibility

```
Revit ──[ESX Export]──► Ekahau ──[design]──► .esx ──[ESX Read]──► Revit
Revit ──[DWG Export + .cal]──► Ekahau ──[design]──► .esx ──[ESX Read finds .cal]──► Revit
External .esx (no anchor / no cal) ──[ESX Read with two-point manual cal]──► Revit
```

## 📜 Full changelog

See [CHANGELOG.md](https://github.com/nicoliu0212/Ekahau-Revit-Transfer/blob/main/CHANGELOG.md) for the complete version history.
