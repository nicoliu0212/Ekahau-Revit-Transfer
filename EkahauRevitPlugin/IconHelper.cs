using System.Reflection;
using System.Windows.Media.Imaging;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// Loads ribbon-button icons from embedded PNG resources in the
    /// Resources\ folder of this assembly.
    ///
    /// Drop matching 32×32 + 16×16 PNGs into <c>EkahauRevitPlugin\Resources\</c>;
    /// the .csproj wildcard <c>&lt;EmbeddedResource Include="Resources\*.png" /&gt;</c>
    /// embeds them automatically on next build.
    /// </summary>
    public static class IconHelper
    {
        /// <summary>
        /// Load an embedded PNG as a BitmapImage suitable for assignment to
        /// PushButtonData.LargeImage / PushButtonData.Image.
        /// </summary>
        /// <param name="resourceName">
        /// Just the file name, e.g. <c>"ESXExport_32.png"</c>.  The method
        /// searches all manifest resources for one whose name ends with
        /// <c>"." + resourceName</c> (handles whatever default namespace is
        /// in effect).  Returns <c>null</c> when no matching resource is
        /// embedded — callers should treat null as "skip the icon".
        /// </summary>
        public static BitmapImage LoadIcon(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return null;

            var assembly = Assembly.GetExecutingAssembly();

            string fullName = null;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith("." + resourceName) || name.EndsWith(resourceName))
                {
                    fullName = name;
                    break;
                }
            }

            if (fullName == null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[IconHelper] Resource not found: {resourceName}");
                return null;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(fullName);
                if (stream == null) return null;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption  = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();   // Required: Revit's ribbon thread != load thread
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
