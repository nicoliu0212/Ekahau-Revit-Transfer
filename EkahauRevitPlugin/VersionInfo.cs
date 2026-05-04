namespace EkahauRevitPlugin
{
    /// <summary>
    /// Single source of truth for the plugin version.  Bumped manually on
    /// each release alongside Installer/Package.wxs's Version attribute.
    /// Read by AboutCommand to display in the version dialog.
    /// </summary>
    public static class VersionInfo
    {
        public const string Version     = "2.5.14";
        public const string ReleaseDate = "2026-05-02";
        public const string RepoUrl     = "https://github.com/nicoliu0212/Ekahau-Revit-Transfer";
        public const string LicenseUrl  = "https://github.com/nicoliu0212/Ekahau-Revit-Transfer/blob/main/LICENSE";
        public const string IssuesUrl   = "https://github.com/nicoliu0212/Ekahau-Revit-Transfer/issues";
    }
}
