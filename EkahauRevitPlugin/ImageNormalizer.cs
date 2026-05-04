using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        /// <summary>
        /// Sniff a raster's magic bytes and return a matching file extension
        /// (with leading dot), e.g. ".png", ".jpg", ".bmp", ".gif", ".tif",
        /// ".webp".  Falls back to ".png" when nothing matches — that's the
        /// historical default but means the caller has already lost the
        /// information needed for Revit's WIC engine to dispatch correctly.
        ///
        /// Why this matters: Revit's <c>ImageType.Create</c> reads the file
        /// extension to choose its WIC decoder; feeding it JPEG bytes in a
        /// .png-named file makes the PNG decoder fail and ImageType.Create
        /// returns NULL with no exception — exactly the v2.5.14 symptom
        /// after we started shipping the JPEG <c>bitmapImageId</c> companion
        /// (header <c>FF D8 FF E0</c> = JFIF) inside an <c>EkahauVisCal_*.png</c>
        /// temp file.
        /// </summary>
        public static string DetectExtension(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return ".png";

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
                return ".png";

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return ".jpg";

            // BMP: 42 4D ("BM")
            if (bytes[0] == 0x42 && bytes[1] == 0x4D)
                return ".bmp";

            // GIF: 47 49 46 38 ("GIF8")
            if (bytes.Length >= 4 &&
                bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                return ".gif";

            // TIFF: 49 49 2A 00 (LE) or 4D 4D 00 2A (BE)
            if (bytes.Length >= 4 &&
                ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
                 (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A)))
                return ".tif";

            // WebP: "RIFF" .... "WEBP"
            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return ".webp";

            return ".png";
        }

        /// <summary>
        /// Re-encode an arbitrary raster as a clean baseline PNG via the
        /// same WIC engine Revit's <c>ImageType.Create</c> uses.
        ///
        /// Why: even a perfectly valid JPEG (e.g. 5000×3571 baseline 8-bit
        /// RGB JFIF, as Ekahau ships in <c>bitmapImageId</c>) can make
        /// Revit's <c>ImageType.Create</c> return NULL silently — the
        /// Autodesk Revit API forum has multiple confirmed reports of
        /// "JPEG response data from certain sources may not be readable…
        /// while PNG or BMP has no issue with the same code".  Round-
        /// tripping through <see cref="BitmapDecoder"/> +
        /// <see cref="PngBitmapEncoder"/> normalises pixel format,
        /// strips colour profiles, and produces a vanilla PNG that
        /// Revit reliably accepts.
        ///
        /// Optionally downscales when either dimension exceeds
        /// <paramref name="maxDim"/>.  Older Revit versions had an
        /// undocumented internal cap around 8000 px and Revit's "Import"
        /// source embeds the entire decoded raster into the .rvt, so a
        /// generous cap keeps file size manageable too.  4000 px is a
        /// safe default for floor-plan overlays — that's still ≥1px per
        /// inch on a 333-foot building.
        ///
        /// Returns the new PNG bytes on success, or null + a diagnostic
        /// in <paramref name="detail"/> on failure (caller surfaces this
        /// in the error dialog).
        /// </summary>
        public static byte[] NormalizeForRevit(
            byte[] inputBytes, out string detail, int maxDim = 4000)
        {
            detail = "";
            if (inputBytes == null || inputBytes.Length == 0)
            {
                detail = "no input bytes";
                return null;
            }

            try
            {
                BitmapSource src;
                using (var ms = new MemoryStream(inputBytes))
                {
                    var decoder = BitmapDecoder.Create(
                        ms,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);   // OnLoad = release stream
                    if (decoder.Frames.Count == 0)
                    {
                        detail = "decoder returned 0 frames";
                        return null;
                    }
                    src = decoder.Frames[0];
                }

                int origW = src.PixelWidth;
                int origH = src.PixelHeight;

                // Force Bgra32 — strips ICC profiles + neutralises any
                // odd source pixel format that Revit's import path
                // rejects.
                if (src.Format != PixelFormats.Bgra32)
                    src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

                // Optional downscale.  Preserves aspect ratio.
                double scale = 1.0;
                if (origW > maxDim || origH > maxDim)
                    scale = Math.Min((double)maxDim / origW, (double)maxDim / origH);

                if (scale < 0.999)
                {
                    src = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                    src.Freeze();
                }

                int outW = src.PixelWidth;
                int outH = src.PixelHeight;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(src));
                using (var outMs = new MemoryStream())
                {
                    encoder.Save(outMs);
                    var result = outMs.ToArray();
                    detail = $"{origW}x{origH} → {outW}x{outH} PNG " +
                             $"({inputBytes.Length:N0} → {result.Length:N0} bytes)";
                    return result;
                }
            }
            catch (Exception ex)
            {
                detail = $"WPF/WIC re-encode failed: {ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"[ImageNormalizer] {detail}");
                return null;
            }
        }
    }
}
