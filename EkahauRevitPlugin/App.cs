using System.Reflection;
using Autodesk.Revit.UI;

namespace EkahauRevitPlugin
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tabName = "WiFi Tools";
            try { application.CreateRibbonTab(tabName); }
            catch { /* tab already exists */ }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // ── Panel 1: Export & Read ──────────────────────────────
            var panelExport = application.CreateRibbonPanel(tabName, "Export & Read");

            var paramConfigBtn = new PushButtonData(
                "ParamConfig",
                "Param\nConfig",
                assemblyPath,
                "EkahauRevitPlugin.ParamConfigCommand")
            {
                ToolTip = "Configure Ekahau RF material parameters on " +
                          "Wall/Door/Window types for accurate ESX export.",
            };
            ApplyIcons(paramConfigBtn, "ParamConfig_32.png", "ParamConfig_16.png");
            panelExport.AddItem(paramConfigBtn);

            var esxExportBtn = new PushButtonData(
                "ESXExport",
                "ESX\nExport",
                assemblyPath,
                "EkahauRevitPlugin.EsxExportCommand")
            {
                ToolTip = "Export floor plan views as Ekahau .esx project files\n" +
                          "with wall/door/window geometry and RF material properties.",
            };
            ApplyIcons(esxExportBtn, "ESXExport_32.png", "ESXExport_16.png");
            panelExport.AddItem(esxExportBtn);

            var dwgExportBtn = new PushButtonData(
                "DWGExport",
                "DWG\nExport",
                assemblyPath,
                "EkahauRevitPlugin.DwgExportCommand")
            {
                ToolTip = "Export floor plan views as DWG files tuned for Ekahau:\n" +
                          "millimetre unit, AutoCAD R2018 format, AIA layer mapping,\n" +
                          "plus a .ekahau-cal.json calibration sidecar that lets\n" +
                          "ESX Read map AP coordinates back to Revit.",
            };
            // Reuse ESXExport icons until DWG-specific PNGs are dropped into Resources\
            ApplyIcons(dwgExportBtn, "DwgExport_32.png", "DwgExport_16.png");
            panelExport.AddItem(dwgExportBtn);

            // v2.6.0: ESX Read split into ESX Quick + ESX Align.
            // v2.6.3: legacy "ESX Read" button removed.
            // v3.1.0: re-unified back to a single "ESX Read" button —
            //   the new EsxReadSetupDialog auto-detects Quick vs Align
            //   from revitAnchor / .ekahau-cal.json presence, so the
            //   user no longer has to choose a mode upfront.  The old
            //   wrapper commands (EsxReadQuickCommand / EsxReadAlignCommand)
            //   are KEPT in the assembly so any external Dynamo / macro
            //   scripts that invoke them by class name still work.
            var esxReadBtn = new PushButtonData(
                "ESXRead",
                "ESX\nRead",
                assemblyPath,
                "EkahauRevitPlugin.EsxReadCommand")
            {
                ToolTip = "Import AP positions from Ekahau .esx files.\n\n" +
                          "Auto-detects the right mode:\n" +
                          "  ⚡ Quick — when the .esx contains a revitAnchor " +
                          "(from ESX Export) or when a matching .ekahau-cal.json " +
                          "sidecar (from DWG Export) is found next to it.\n" +
                          "  🎯 Align — for external .esx files with no calibration; " +
                          "runs a 4-click visual alignment.",
                LongDescription =
                    "Single unified flow: select .esx → pick floor + view → start. " +
                    "Mode is shown in the setup dialog before you click Start.",
            };
            ApplyIcons(esxReadBtn, "ESXRead_32.png", "ESXRead_16.png");
            panelExport.AddItem(esxReadBtn);

            // ── Panel 2: Access Point ───────────────────────────────
            var panelAp = application.CreateRibbonPanel(tabName, "Access Point");

            var apPlaceBtn = new PushButtonData(
                "APPlace",
                "AP\nPlace",
                assemblyPath,
                "EkahauRevitPlugin.ApPlaceCommand")
            {
                ToolTip = "Place Ekahau access points as Revit family instances.\n" +
                          "Run ESX Read first to prepare staging data.",
            };
            ApplyIcons(apPlaceBtn, "APPlace_32.png", "APPlace_16.png");
            panelAp.AddItem(apPlaceBtn);

            // NOTE: Heat Map panel/button intentionally not added here yet —
            // the HeatMapCommand class has not been implemented.

            // ── Panel 3: Help / About ───────────────────────────────
            var panelHelp = application.CreateRibbonPanel(tabName, "Help");

            var aboutBtn = new PushButtonData(
                "About",
                "About",
                assemblyPath,
                "EkahauRevitPlugin.AboutCommand")
            {
                ToolTip = "Show plugin version, install path, runtime, and links to " +
                          $"the GitHub project.\n\nCurrent version: v{VersionInfo.Version}",
                LongDescription =
                    "Lightweight About dialog — no admin or network call required. " +
                    "Useful for confirming what's installed, checking for updates, " +
                    "or grabbing the install path when reporting an issue.",
            };
            ApplyIcons(aboutBtn, "About_32.png", "About_16.png");
            panelHelp.AddItem(aboutBtn);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
            => Result.Succeeded;

        /// <summary>
        /// Assign LargeImage (32 px) + Image (16 px) icons to a button when
        /// the resources are embedded.  Silently skips when an icon is
        /// missing — the button still works, it just shows text only.
        /// </summary>
        private static void ApplyIcons(
            PushButtonData btn, string largeName, string smallName)
        {
            var large = IconHelper.LoadIcon(largeName);
            if (large != null) btn.LargeImage = large;

            var small = IconHelper.LoadIcon(smallName);
            if (small != null) btn.Image = small;
        }
    }
}
