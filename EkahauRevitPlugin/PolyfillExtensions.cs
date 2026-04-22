#if REVIT_LEGACY
using System;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// Polyfills for APIs introduced in .NET Core 2.1 / .NET Standard 2.1
    /// that aren't available in .NET Framework 4.8.  Compiled only into the
    /// net48 build (Revit 2023-2024).  On the net8 build these methods come
    /// from the BCL and the file is excluded from compilation.
    /// </summary>
    internal static class PolyfillExtensions
    {
        /// <summary>
        /// String.Contains(string value, StringComparison comparison) was
        /// added in .NET Core 2.1.  This shim implements it via IndexOf.
        /// </summary>
        public static bool Contains(this string str, string value, StringComparison comparison)
        {
            if (str == null || value == null) return false;
            return str.IndexOf(value, comparison) >= 0;
        }
    }
}
#endif
