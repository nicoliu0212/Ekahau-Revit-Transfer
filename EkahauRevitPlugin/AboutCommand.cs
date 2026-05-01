using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  About Command  —  shows plugin version, install path, Revit version,
    //  links to the GitHub repo / issue tracker / licence.  Read-only.
    // ═══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            string dllPath  = "(unknown)";
            string buildTs  = "";
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                dllPath  = asm.Location;
                buildTs  = File.GetLastWriteTime(dllPath).ToString("yyyy-MM-dd HH:mm");
            }
            catch { }

            string revitVer = "(unknown)";
            try { revitVer = commandData.Application.Application.VersionNumber; }
            catch { }

            string runtime = "";
#if REVIT_NET10
            runtime = ".NET 10";
#elif REVIT_NET8
            runtime = ".NET 8";
#elif REVIT_LEGACY
            runtime = ".NET Framework 4.8";
#endif

            // Detect install scope (per-user under %APPDATA% vs all-users)
            string scope = "(unknown)";
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData) &&
                    dllPath.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
                    scope = "Per-user (AppData)";
                else if (dllPath.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase))
                    scope = "All-users (ProgramData)";
                else if (dllPath.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase))
                    scope = "All-users (Program Files)";
            }
            catch { }

            var dlg = new TaskDialog("About — Ekahau WiFi Tools")
            {
                MainInstruction = $"Ekahau WiFi Tools  v{VersionInfo.Version}",
                MainContent =
                    $"Released:        {VersionInfo.ReleaseDate}\n" +
                    $"Build:           {buildTs}\n" +
                    $"Runtime:         {runtime}\n" +
                    $"Revit:           {revitVer}\n" +
                    $"Install scope:   {scope}\n" +
                    $"\n" +
                    $"DLL:\n  {dllPath}\n" +
                    $"\n" +
                    $"Bi-directional bridge between Autodesk Revit and Ekahau AI Pro " +
                    $"for WiFi planning workflows.",
                FooterText = "MIT License — © 2026 Nico Liu",
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Open project page on GitHub",
                VersionInfo.RepoUrl);
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Check for updates / view releases",
                VersionInfo.RepoUrl + "/releases");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                "Report a bug / request a feature",
                VersionInfo.IssuesUrl);
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink4,
                "Open the install folder",
                "Useful when reporting issues or copying the DLL to another machine");
            dlg.CommonButtons = TaskDialogCommonButtons.Close;
            dlg.DefaultButton = TaskDialogResult.Close;

            var resp = dlg.Show();
            switch (resp)
            {
                case TaskDialogResult.CommandLink1:
                    OpenUrl(VersionInfo.RepoUrl);
                    break;
                case TaskDialogResult.CommandLink2:
                    OpenUrl(VersionInfo.RepoUrl + "/releases");
                    break;
                case TaskDialogResult.CommandLink3:
                    OpenUrl(VersionInfo.IssuesUrl);
                    break;
                case TaskDialogResult.CommandLink4:
                    try
                    {
                        string folder = Path.GetDirectoryName(dllPath);
                        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                            Process.Start(new ProcessStartInfo("explorer.exe",
                                $"\"{folder}\"") { UseShellExecute = true });
                    }
                    catch { }
                    break;
            }

            return Result.Succeeded;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
