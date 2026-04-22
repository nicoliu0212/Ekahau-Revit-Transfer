<div align="center">

# Ekahau ↔ Revit Transfer

**Bi-directional bridge between Autodesk Revit and Ekahau AI Pro for WiFi planning workflows.**

[![Revit](https://img.shields.io/badge/Revit-2023%20%7C%202024%20%7C%202025%20%7C%202026%20%7C%202027-0696D7?logo=autodesk&logoColor=white)](https://www.autodesk.com/products/revit)
[![.NET](https://img.shields.io/badge/.NET-Framework%204.8%20%7C%208.0%20%7C%2010.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/release/nicoliu0212/Ekahau-Revit-Transfer?include_prereleases&sort=semver)](https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases)

[Quick Start](#-quick-start) · [Features](#-features) · [Installation](#-installation) · [User Guide](USER_GUIDE.md) · [Build from Source](#-build-from-source) · [Changelog](CHANGELOG.md)

</div>

---

## 🎯 What it does

Engineers planning WiFi coverage need to round-trip between **Autodesk Revit** (the source of truth for the building model) and **Ekahau AI Pro** (the WiFi simulator). This add-in eliminates the manual copy-paste:

```
Revit  ──[ESX Export]──► Ekahau Pro  ──design APs──► .esx file
                                                        │
Revit  ◄────[ESX Read]──── stage AP positions   ◄───────┘
   │
   └──[AP Place]──► real Revit family instances + WiFi plan view + AP schedule
```

Or via DWG:

```
Revit  ──[DWG Export]──► .dwg + .ekahau-cal.json sidecar
                                    │
                              Ekahau imports DWG, designs APs, saves .esx
                                    │
Revit  ◄──[ESX Read]──── reads .esx + finds matching .ekahau-cal.json ◄
```

---

## ✨ Features

### Export
- **ESX Export** — Floor plan views → Ekahau `.esx` with wall/door/window geometry, RF material presets, embedded PNG. Supports linked models and curtain walls. Resolution selectable from 2 000 to 15 000 px at 300 DPI.
- **DWG Export** — One-click "Clean" DWG export tuned for Ekahau (mm units, AutoCAD R2018, AIA layers) + sidecar `.ekahau-cal.json` calibration file for round-trip.

### Import
- **ESX Read** — Parse `.esx`, place crosshair preview markers, write per-project staging JSON.
  - Three-tier coordinate calibration: `revitAnchor` ➝ `.ekahau-cal.json` ➝ **two-point manual calibration** (pick 2 reference points, type Ekahau coordinates).
  - Mandatory image-overlay verification step: places the Ekahau image + green CropBox corner crosses, asks the user to confirm alignment.
  - Auto-cleanup of old preview markers across runs.
- **AP Place** — Replaces preview markers with real family instances. Three-column family picker, per-AP confirmation dialog, batch transactions, workset assignment, **12 Ekahau shared parameters** automatically created and populated, optional **WiFi Plan view** and **per-level AP schedule** generation.

### Configuration
- **Param Config** — Auto-create the `Ekahau_WallType` shared parameter, walk active view, suggest material presets via keyword & material-layer matching, support linked-model overrides via ExtensibleStorage.

### Multi-version
- **Revit 2023-2027** supported via three build flavours (`net48`, `net8.0-windows`, `net10.0-windows`).
- Cross-version API differences are abstracted in a single `VersionCompat` class — no `#if` directives in business logic.

### Per-user install
- Installs to `%APPDATA%\Autodesk\Revit\Addins\YYYY\` — **no admin / UAC required**.
- Files are visible only to the installing user; nothing written to `Program Files` or `ProgramData`.

---

## 🚀 Quick Start

1. **Download** the latest [`EkahauWiFiTools-vX.Y.Z.msi`](https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/latest)
2. Double-click → install proceeds without UAC prompt
3. Open Revit → top of the ribbon there's a new **WiFi Tools** tab
4. (Revit 2023 / 2024 only) Run `install.ps1` from PowerShell — also no admin required

```
WiFi Tools
├── Export & Read     │ Param Config │ ESX Export │ DWG Export │ ESX Read
└── Access Point      │ AP Place
```

---

## 📦 Installation

### Recommended — MSI installer

| Revit version | Runtime | Source |
|:---:|:---:|:---:|
| 2025 / 2026 | .NET 8 | `EkahauWiFiTools-vX.Y.Z.msi` |
| 2027 | .NET 10 | `EkahauWiFiTools-vX.Y.Z.msi` |
| 2023 / 2024 | .NET Framework 4.8 | `install.ps1` (extracts net48 build + supporting BCL DLLs) |

**Why two paths?** The net48 build pulls in 9 supporting BCL DLLs (`System.Text.Json` and friends) which would bloat the MSI ~3×. The MSI keeps Revit 2025-2027 lean; the PowerShell script handles the 2023 / 2024 path on demand.

### Install location

All install paths are **per-user under AppData**:

```
%APPDATA%\Autodesk\Revit\Addins\YYYY\
    EkahauRevitPlugin.addin
    EkahauWiFiTools\
        EkahauRevitPlugin.dll
        EkahauRevitPlugin.deps.json
        (… supporting DLLs for net48 only)
```

The MSI does **not** request elevation, does **not** write to `Program Files` or `ProgramData`, and does **not** modify HKLM — everything is HKCU + per-user file system.

### Manual install (PowerShell)

```powershell
.\install.ps1
```

Detects which Revit versions are installed and copies the matching DLL into `%APPDATA%\Autodesk\Revit\Addins\YYYY\` for each one.

---

## 📖 Documentation

- **[USER_GUIDE.md](USER_GUIDE.md)** — Comprehensive user manual covering every feature, common issues, and recommended workflow.
- **[CHANGELOG.md](CHANGELOG.md)** — Version history.

---

## 🛠 Build from Source

### Prerequisites
- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview/RC) — required only for the Revit 2027 target
- [WiX v6](https://wixtoolset.org/) — required only to build the MSI

### Build all three runtime targets

```powershell
git clone https://github.com/nicoliu0212/Ekahau-Revit-Transfer.git
cd Ekahau-Revit-Transfer
dotnet build -c Release
```

Output:

```
EkahauRevitPlugin\bin\Release\net48\EkahauRevitPlugin.dll              → Revit 2023-2024
EkahauRevitPlugin\bin\Release\net8.0-windows\EkahauRevitPlugin.dll     → Revit 2025-2026
EkahauRevitPlugin\bin\Release\net10.0-windows\EkahauRevitPlugin.dll    → Revit 2027
```

### Build the MSI

```powershell
dotnet tool install --global wix --version 6.0.0
cd Installer
wix build Package.wxs -o EkahauWiFiTools.msi
```

---

## 🏗 Architecture

```
EkahauRevitPlugin/
├── App.cs                       Ribbon registration (WiFi Tools tab + 5 buttons)
├── EkahauPresets.cs             Ekahau RF preset table (Concrete, GlassWall, etc.)
├── EkahauRevitPlugin.addin      Revit add-in manifest
├── EsxExportCommand.cs          ESX Export full pipeline
├── EsxReadCommand.cs            ESX Read full pipeline (incl. 3-tier calibration)
├── EsxModels.cs                 Shared data models (staging JSON, etc.)
├── EsxDialogs.cs                ESX Export WPF dialogs
├── EsxReadDialogs.cs            ESX Read WPF dialogs (incl. TwoPointPixelDialog)
├── ApPlaceCommand.cs            AP Place + WiFi view + schedule
├── ApPlaceDialogs.cs            AP Place WPF dialogs
├── DwgExportCommand.cs          DWG Export with calibration sidecar
├── ParamConfigCommand.cs        Shared parameter setup
├── MappingDialog.cs             Wall-type → preset mapping UI
├── KeywordMatcher.cs            Material/type-name → preset matching
├── VersionCompat.cs             Cross-version Revit API shim (2023-2027)
├── PolyfillExtensions.cs        net48 BCL polyfills (string.Contains overload)
├── IconHelper.cs                Embedded PNG → BitmapImage
├── RevitHelpers.cs              Misc Revit utilities
├── LinkedModelSelectorDialog.cs Linked model picker
└── Resources/                   Ribbon icons (32x32 + 16x16 PNG)
Installer/
└── Package.wxs                  WiX MSI definition (per-user, AppData scope)
```

### Key design decisions

| Decision | Why |
|---|---|
| Multi-target `net48;net8.0-windows;net10.0-windows` | One source tree, three Revit runtimes |
| `VersionCompat` API shim | Hide `ElementId.Value` (long) vs `IntegerValue` (int), `SpecTypeId` vs `ParameterType`, `ImageType.Create` overload differences |
| Project-isolated staging directory (MD5 of doc path) | Safe to run multiple Revit projects in parallel |
| `BoxPlacement.Center` for image overlay | Simpler than BottomLeft + manual offset math; correct on rotated views |
| Three-tier calibration (revitAnchor → .cal.json → manual two-point) | Graceful degradation; works with .esx files exported by anyone |
| Per-user AppData install | No admin / UAC; HKCU only; doesn't write to Program Files or ProgramData |

---

## 🤝 Contributing

Contributions welcome! Please:
1. Open an [issue](https://github.com/nicoliu0212/Ekahau-Revit-Transfer/issues) to discuss the change first
2. Keep PRs focused — one feature/fix per PR
3. Verify all three target frameworks build clean (`dotnet build -c Release`)
4. Test in at least one Revit version

For bug reports, attach the relevant Revit journal:
`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit YYYY\Journals\journal.NNNN.txt`

---

## 📜 License

[MIT](LICENSE) © 2026 Nico Liu

---

## 🙏 Acknowledgments

- **Autodesk** for the [Revit API](https://www.revitapidocs.com/)
- **Ekahau** for the open `.esx` format (it's a ZIP — long live JSON)
- **WiX Toolset** for the MSI builder
