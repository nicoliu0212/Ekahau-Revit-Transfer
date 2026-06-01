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
        // ══════════════════════════════════════════════════════════════
        //  v2.6.3: design-area cropping
        //
        //  When Ekahau ships a bitmap that's the full PDF/Sheet (title
        //  block + notes + design area), the user's visual reference is
        //  the design area only (that's what Ekahau Pro shows in its
        //  heat-map view).  Cropping the bitmap to floorPlans.json's
        //  cropMin/Max region before placing it in Revit lines up the
        //  displayed image with the user's mental model — and lets AP
        //  positions land naturally on the floor plan content instead
        //  of being scattered over title-block whitespace.
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Result of <see cref="CropToDesignArea"/>.  When
        /// <see cref="WasCropped"/> is false, <see cref="Reason"/>
        /// explains why we returned the original bytes unchanged.
        /// </summary>
        public struct CropInfo
        {
            public bool   WasCropped;
            public int    CropOffsetBitmapX;
            public int    CropOffsetBitmapY;
            public int    CroppedBitmapWidth;
            public int    CroppedBitmapHeight;
            public int    OriginalBitmapWidth;
            public int    OriginalBitmapHeight;
            public double FpCropMinX;
            public double FpCropMinY;
            public double FpCropMaxX;
            public double FpCropMaxY;
            public string Reason;
        }

        /// <summary>
        /// Crop a bitmap to the floor plan's "design area" (the region
        /// defined by <c>cropMinX/Y, cropMaxX/Y</c> in floorPlans.json,
        /// expressed in fp-space coords).  The bitmap is assumed to be
        /// a uniform-scale render of the full fp space — i.e.,
        /// <c>bitmap.Width / fp.Width == bitmap.Height / fp.Height</c>.
        /// We sanity-check that before cropping.
        ///
        /// Falls back to returning the original bytes (with
        /// <see cref="CropInfo.WasCropped"/> = false and a diagnostic
        /// in <see cref="CropInfo.Reason"/>) when:
        /// <list type="bullet">
        /// <item>fp dimensions are zero or negative</item>
        /// <item>crop bounds are invalid (min &gt;= max, or NaN)</item>
        /// <item>crop covers ≥99% of the image (no actual cropping
        ///   needed — likely a metadata default)</item>
        /// <item>bitmap aspect ratio differs from fp aspect by &gt;1%
        ///   (non-uniform scale — cropping would distort)</item>
        /// <item>WIC decode of the input bytes fails</item>
        /// <item>resulting crop would be smaller than 100×100 px</item>
        /// </list>
        ///
        /// On success: returns a PNG byte[] of just the design area,
        /// with crop offsets / sizes captured in the out parameter.
        /// </summary>
        public static byte[] CropToDesignArea(
            byte[] inputBytes,
            double fpWidth, double fpHeight,
            double cropMinX, double cropMinY,
            double cropMaxX, double cropMaxY,
            out CropInfo info)
        {
            info = new CropInfo
            {
                FpCropMinX = cropMinX,
                FpCropMinY = cropMinY,
                FpCropMaxX = cropMaxX,
                FpCropMaxY = cropMaxY,
            };

            if (inputBytes == null || inputBytes.Length == 0)
            {
                info.Reason = "no input bytes";
                return inputBytes;
            }
            if (fpWidth <= 0 || fpHeight <= 0)
            {
                info.Reason = $"fp.Width={fpWidth} or fp.Height={fpHeight} not positive";
                return inputBytes;
            }
            if (double.IsNaN(cropMinX) || double.IsNaN(cropMinY) ||
                double.IsNaN(cropMaxX) || double.IsNaN(cropMaxY) ||
                cropMinX >= cropMaxX || cropMinY >= cropMaxY)
            {
                info.Reason = $"crop bounds invalid: ({cropMinX:F1},{cropMinY:F1})..({cropMaxX:F1},{cropMaxY:F1})";
                return inputBytes;
            }
            double cropFpW = cropMaxX - cropMinX;
            double cropFpH = cropMaxY - cropMinY;
            if (cropFpW / fpWidth > 0.99 && cropFpH / fpHeight > 0.99)
            {
                info.Reason = $"crop ≈ full image ({cropFpW:F0}x{cropFpH:F0} / {fpWidth:F0}x{fpHeight:F0}) — no cropping needed";
                return inputBytes;
            }

            // Decode to learn the bitmap's actual pixel dimensions.
            BitmapSource src;
            try
            {
                using var ms = new MemoryStream(inputBytes);
                var decoder = BitmapDecoder.Create(
                    ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                src = decoder.Frames[0];
            }
            catch (Exception ex)
            {
                info.Reason = $"WIC decode failed: {ex.Message}";
                return inputBytes;
            }

            int bw = src.PixelWidth, bh = src.PixelHeight;
            info.OriginalBitmapWidth  = bw;
            info.OriginalBitmapHeight = bh;

            // Uniform-scale assumption check.  fp aspect and bitmap aspect
            // must match for a uniform crop to be safe — otherwise the
            // crop region in bitmap pixels would distort the content.
            double fpAspect = fpWidth / fpHeight;
            double bpAspect = (double)bw / bh;
            if (Math.Abs(fpAspect - bpAspect) / fpAspect > 0.01)
            {
                info.Reason = $"bitmap aspect ({bpAspect:F4}) differs from fp aspect ({fpAspect:F4}) by >1% — not safe to crop";
                return inputBytes;
            }

            // Compute crop in bitmap pixels.  Use X and Y scale factors
            // independently to be robust against ≤1% aspect mismatch.
            double sx = (double)bw / fpWidth;
            double sy = (double)bh / fpHeight;
            int cx0 = (int)Math.Round(cropMinX * sx);
            int cy0 = (int)Math.Round(cropMinY * sy);
            int cx1 = (int)Math.Round(cropMaxX * sx);
            int cy1 = (int)Math.Round(cropMaxY * sy);

            // Clamp to bitmap bounds.
            cx0 = Math.Max(0, Math.Min(cx0, bw - 1));
            cy0 = Math.Max(0, Math.Min(cy0, bh - 1));
            cx1 = Math.Max(cx0 + 1, Math.Min(cx1, bw));
            cy1 = Math.Max(cy0 + 1, Math.Min(cy1, bh));

            int cropW = cx1 - cx0;
            int cropH = cy1 - cy0;
            if (cropW < 100 || cropH < 100)
            {
                info.Reason = $"crop too small ({cropW}x{cropH}) — possibly bad metadata";
                return inputBytes;
            }

            // Crop + re-encode as PNG.
            try
            {
                var rect = new System.Windows.Int32Rect(cx0, cy0, cropW, cropH);
                var cropped = new CroppedBitmap(src, rect);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                using var oms = new MemoryStream();
                encoder.Save(oms);

                info.WasCropped          = true;
                info.CropOffsetBitmapX   = cx0;
                info.CropOffsetBitmapY   = cy0;
                info.CroppedBitmapWidth  = cropW;
                info.CroppedBitmapHeight = cropH;
                info.Reason              =
                    $"cropped {bw}x{bh} → {cropW}x{cropH} at ({cx0},{cy0}); " +
                    $"fp crop ({cropMinX:F1},{cropMinY:F1})..({cropMaxX:F1},{cropMaxY:F1})";
                Debug.WriteLine($"[ImageNormalizer] CropToDesignArea: {info.Reason}");
                return oms.ToArray();
            }
            catch (Exception ex)
            {
                info.Reason = $"crop encode failed: {ex.Message}";
                return inputBytes;
            }
        }

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

                // Force Bgr24 (8-bit RGB, no alpha) — strips ICC
                // profiles + neutralises any odd source pixel format,
                // and crucially DROPS the alpha channel.  Revit's
                // ImageType.Create import path is known to silently
                // return NULL for 32-bit RGBA PNGs on some versions
                // (v2.5.16's Bgra32-encoded PNG hit exactly this);
                // 24-bit RGB is the most universally accepted variant.
                if (src.Format != PixelFormats.Bgr24)
                    src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);

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
