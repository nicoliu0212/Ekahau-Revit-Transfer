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

            // v2.6.0: ESX Read split into two focused workflows.
            // "ESX Quick" handles round-trip imports (.esx came from
            // ESX Export, every floor has revitAnchor) in 3-4 clicks
            // with no manual alignment.  "ESX Align" handles external
            // Ekahau projects (no anchor, need 2-point visual cal).
            // The legacy "ESX Read" button stays as a router for
            // muscle-memory continuity.
            var esxQuickBtn = new PushButtonData(
                "ESXQuick",
                "ESX\nQuick",
                assemblyPath,
                "EkahauRevitPlugin.EsxReadQuickCommand")
            {
                ToolTip = "Quick import for round-trip .esx files (came from ESX Export).\n" +
                          "Requires revitAnchor on every floor; skips manual alignment,\n" +
                          "rotation picker, and Nudge dialog.  Use ESX Align instead for\n" +
                          ".esx files that were built from PDF / image in Ekahau Pro.",
                LongDescription =
                    "Fast 3–4-step flow: select .esx → pick floors → review APs → done.\n" +
                    "If the file has no revitAnchor, a warning offers to switch to ESX Align.",
            };
            ApplyIcons(esxQuickBtn, "ESXRead_32.png", "ESXRead_16.png");
            panelExport.AddItem(esxQuickBtn);

            var esxAlignBtn = new PushButtonData(
                "ESXAlign",
                "ESX\nAlign",
                assemblyPath,
                "EkahauRevitPlugin.EsxReadAlignCommand")
            {
                ToolTip = "Manual-alignment import for external Ekahau projects.\n" +
                          "Runs the full visual cal flow: image overlay verification,\n" +
                          "2-point manual alignment, rotation picker, and Nudge dialog.\n" +
                          "Use ESX Quick instead when the .esx came from ESX Export.",
                LongDescription =
                    "Full interactive flow.  If every floor already has revitAnchor, " +
                    "a warning suggests switching to ESX Quick (much faster).",
            };
            ApplyIcons(esxAlignBtn, "ESXRead_32.png", "ESXRead_16.png");
            panelExport.AddItem(esxAlignBtn);

            // Legacy ESX Read — kept so users with muscle memory aren't
            // disrupted.  Defaults to Mode = LegacyAuto which runs every
            // dialog exactly as it did pre-v2.6.0.
            var esxReadBtn = new PushButtonData(
                "ESXRead",
                "ESX\nRead",
                assemblyPath,
                "EkahauRevitPlugin.EsxReadCommand")
            {
                ToolTip = "Legacy ESX Read (pre-v2.6.0 behaviour). Runs every alignment\n" +
                          "dialog regardless of whether the .esx has revitAnchor.\n" +
                          "Prefer ESX Quick (round-trip) or ESX Align (external) instead.",
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
