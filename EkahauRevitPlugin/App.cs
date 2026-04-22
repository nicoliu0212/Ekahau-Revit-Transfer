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

            var esxReadBtn = new PushButtonData(
                "ESXRead",
                "ESX\nRead",
                assemblyPath,
                "EkahauRevitPlugin.EsxReadCommand")
            {
                ToolTip = "Read an Ekahau .esx file and stage AP positions\n" +
                          "as preview markers for placement in Revit views.",
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
