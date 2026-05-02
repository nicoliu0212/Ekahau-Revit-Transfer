using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// Normalises an arbitrary "image" byte stream from a .esx into a
    /// raster format (PNG/JPEG) that Revit's WIC engine can render.
    ///
    /// Why: some Ekahau exports store floor plans as SVG (XML) inside the
    /// .esx ZIP — usually as a thin wrapper around an embedded base64-
    /// encoded raster.  Revit's <c>ImageType.Create</c> needs raster
    /// bytes (PNG / JPEG / BMP / TIFF / GIF), so we detect SVG content,
    /// pull the embedded raster, and pass that to Revit.
    ///
    /// Full SVG rasterisation (vector → PNG via a renderer like Svg.NET
    /// or SkiaSharp.Svg) would require a NuGet dependency and add ~5 MB
    /// to the MSI; we defer that until we hit a real .esx that needs it.
    /// </summary>
    internal static class ImageNormalizer
    {
        /// <summary>
        /// Returns true when the byte stream looks like SVG / XML content
        /// (rather than a raster image header).  Skips a UTF-8 BOM if
        /// present and trims leading whitespace before checking the first
        /// few bytes for <c>&lt;?xml</c> or <c>&lt;svg</c>.
        /// </summary>
        public static bool IsSvgOrXmlContent(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 5) return false;

            int start = 0;
            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                start = 3;

            // Look at the first 256 bytes (or all of them, whichever is smaller)
            int probeLen = Math.Min(256, bytes.Length - start);
            if (probeLen <= 0) return false;

            string head;
            try { head = Encoding.UTF8.GetString(bytes, start, probeLen); }
            catch { return false; }

            head = head.TrimStart();
            return head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                   head.StartsWith("<svg",  StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Try to extract an embedded base64-encoded raster image
        /// (typically PNG or JPEG) from inside an SVG document.
        ///
        /// Looks for the standard SVG <c>&lt;image href="data:image/...;base64,..."&gt;</c>
        /// pattern (and the older <c>xlink:href</c> variant).
        ///
        /// Returns the decoded raster bytes (ready to write as a .png /
        /// .jpg file) or null when no embedded raster is found.
        /// </summary>
        public static byte[] TryExtractEmbeddedRaster(byte[] svgBytes)
        {
            if (svgBytes == null || svgBytes.Length < 100) return null;

            // Allocate a string view of the SVG so Regex can scan it.
            // For a 100 MB SVG this allocates ~200 MB transiently — fine
            // on modern machines with multiple GB of RAM.
            string xml;
            try { xml = Encoding.UTF8.GetString(svgBytes); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageNormalizer] UTF-8 decode failed: {ex.Message}");
                return null;
            }

            // Match: href="data:image/png;base64,XXXX..."  (or xlink:href, single quotes)
            // Captures: 1=href attr name, 2=mime subtype, 3=base64 payload
            var m = Regex.Match(xml,
                @"(?:xlink:)?href\s*=\s*[""']data:image/([a-z0-9+\-.]+);base64,([A-Za-z0-9+/=\s]+?)[""']",
                RegexOptions.IgnoreCase);

            if (!m.Success)
            {
                Debug.WriteLine(
                    "[ImageNormalizer] SVG contains no embedded base64 raster " +
                    "(no <image href=\"data:image/...;base64,...\"> match found).");
                return null;
            }

            string mime = m.Groups[1].Value.ToLowerInvariant();
            string base64 = m.Groups[2].Value;

            // Strip any whitespace from the base64 payload (XML attributes
            // sometimes wrap long strings across lines).
            if (base64.IndexOfAny(new[] { ' ', '\r', '\n', '\t' }) >= 0)
                base64 = Regex.Replace(base64, @"\s+", "");

            try
            {
                byte[] raster = Convert.FromBase64String(base64);
                Debug.WriteLine(
                    $"[ImageNormalizer] Extracted embedded {mime} raster: " +
                    $"{raster.Length:N0} bytes (from {svgBytes.Length:N0}-byte SVG).");
                return raster;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ImageNormalizer] base64 decode of embedded {mime} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Combined helper — detect SVG, try to extract embedded raster,
        /// return either the extracted raster or the original bytes
        /// unchanged.  When detection succeeds but extraction fails,
        /// returns the original bytes so callers can show a meaningful
        /// "SVG without embedded raster" error.
        /// </summary>
        public static (byte[] Bytes, bool WasSvg, bool ExtractionSucceeded)
            NormalizeIfSvg(byte[] inputBytes)
        {
            if (!IsSvgOrXmlContent(inputBytes))
                return (inputBytes, WasSvg: false, ExtractionSucceeded: false);

            byte[] raster = TryExtractEmbeddedRaster(inputBytes);
            if (raster != null && raster.Length > 100)
                return (raster, WasSvg: true, ExtractionSucceeded: true);

            return (inputBytes, WasSvg: true, ExtractionSucceeded: false);
        }
    }
}
