using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ESX Read Command  —  Feature 3
    //  Reads an Ekahau .esx project file and places AP markers on
    //  matching Revit floor-plan views using the revitAnchor calibration.
    // ═══════════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════════
    //  Data models for ESX Read
    // ══════════════════════════════════════════════════════════════════════

    public class EsxFloorPlanData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public double Width { get; set; }
        public double Height { get; set; }
        public double MetersPerUnit { get; set; }
        public string ImageId { get; set; } = "";
        /// <summary>
        /// Optional rasterised companion of <see cref="ImageId"/>.  Ekahau
        /// stores SVG floor plans with a parallel JPEG/PNG raster that
        /// renderers can use directly — when present, this is the
        /// raster's image-entry id (without the "image-" prefix).  We
        /// prefer this over <see cref="ImageId"/> in the placement path
        /// because Revit's WIC engine can't render SVG.
        /// </summary>
        public string BitmapImageId { get; set; } = "";
        public EsxRevitAnchorData RevitAnchor { get; set; }
    }

    public class EsxRevitAnchorData
    {
        // World-space CropBox bounds (feet)
        public double CropWorldMinX_ft { get; set; }
        public double CropWorldMinY_ft { get; set; }
        public double CropWorldMaxX_ft { get; set; }
        public double CropWorldMaxY_ft { get; set; }
        public double MetersPerUnit { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        // Padding-aware pixel region within PNG
        public double CropPixelOffsetX { get; set; }
        public double CropPixelOffsetY { get; set; }
        public double CropPixelWidth { get; set; }
        public double CropPixelHeight { get; set; }
        // CropBox Transform for rotated-view support
        public double XformOriginX_ft { get; set; }
        public double XformOriginY_ft { get; set; }
        public double XformBasisXx { get; set; }
        public double XformBasisXy { get; set; }
        public double XformBasisYx { get; set; }
        public double XformBasisYy { get; set; }
        // View-local CropBox bounds
        public double LocalMinX { get; set; }
        public double LocalMinY { get; set; }
        public double LocalMaxX { get; set; }
        public double LocalMaxY { get; set; }
        /// <summary>True if xformBasisXx etc. are present.</summary>
        public bool HasTransform { get; set; }
        /// <summary>True if cropWorldMinX_ft etc. are present.</summary>
        public bool HasWorldBounds { get; set; }
    }

    public class EsxAccessPointData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FloorPlanId { get; set; } = "";
        public double PixelX { get; set; }
        public double PixelY { get; set; }
        public double MountingHeight { get; set; } = 2.7;
        public string Vendor { get; set; } = "";
        public string Model { get; set; } = "";
        public string AntennaTypeId { get; set; } = "";
        public List<string> Tags { get; set; } = new List<string>();
        /// <summary>Radio bands: "TWO", "FIVE", "SIX".</summary>
        public List<string> Bands { get; set; } = new List<string>();
        public bool Include { get; set; } = true;
        // ── Radio summary fields (built from simulatedRadios + antennaTypes) ──
        public string Mounting { get; set; } = "";        // "CEILING" / "WALL"
        public string BandsSummary { get; set; } = "";    // "2.4GHz, 5GHz, 6GHz"
        public string Technology { get; set; } = "";      // "WiFi 6E (AX)"
        public string TxPowerSummary { get; set; } = "";  // "2G:14dBm / 5G:14dBm"
        public string ChannelsSummary { get; set; } = ""; // "2G:1 / 5G:149"
        public string StreamsSummary { get; set; } = "";   // "2G:4x4 / 5G:4x4"
        public string AntennaInfo { get; set; } = "";      // "Internal" / "External"
    }

    public class EsxRadioData
    {
        public string Id { get; set; } = "";
        public string AccessPointId { get; set; } = "";
        public string Band { get; set; } = "";
        public string Channel { get; set; } = "";
        public double TxPower { get; set; }
        public bool Enabled { get; set; } = true;
        public string Technology { get; set; } = "";      // "AX", "AC", "N", "BE"
        public int SpatialStreamCount { get; set; }
        public string AntennaMounting { get; set; } = ""; // "CEILING", "WALL"
        public string AntennaTypeId { get; set; } = "";
    }

    public class EsxAntennaTypeData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string ApCoupling { get; set; } = "";  // "INTERNAL_ANTENNA" / "EXTERNAL_ANTENNA"
    }

    public class EsxReadResult
    {
        public string ProjectName { get; set; } = "";
        public List<EsxFloorPlanData> FloorPlans { get; set; } = new List<EsxFloorPlanData>();
        public List<EsxAccessPointData> AccessPoints { get; set; } = new List<EsxAccessPointData>();
        public List<EsxRadioData> SimulatedRadios { get; set; } = new List<EsxRadioData>();
        public List<EsxRadioData> MeasuredRadios { get; set; } = new List<EsxRadioData>();
        public List<EsxAntennaTypeData> AntennaTypes { get; set; } = new List<EsxAntennaTypeData>();
        public Dictionary<string, byte[]> ImageEntries { get; set; } = new Dictionary<string, byte[]>();
    }

    /// <summary>Per-floor plan import result for the summary dialog.</summary>
    public class EsxReadFloorResult
    {
        public string FloorPlanName { get; set; } = "";
        public string MatchedViewName { get; set; } = "";
        public int ApsPlaced { get; set; }
        public int ApsSkipped { get; set; }
        public string Warning { get; set; } = "";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  REQ 1+2+19+20 — Unified ESX ZIP Reader
    //  Strategy: try standard ZipArchive first, fallback to byte-by-byte
    //  Local File Header scanning for non-standard ESX files.
    // ══════════════════════════════════════════════════════════════════════

    public static class EsxZipReader
    {
        /// <summary>
        /// Read all entries from an ESX (ZIP) file.
        /// Returns a dictionary of entry name → uncompressed byte content.
        /// </summary>
        public static Dictionary<string, byte[]> ReadEntries(string filePath)
        {
            // REQ 1: Try standard ZipArchive first
            try
            {
                using var stream = File.OpenRead(filePath);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    using var es = entry.Open();
                    using var ms = new MemoryStream();
                    es.CopyTo(ms);
                    entries[entry.FullName] = ms.ToArray();
                }
                if (entries.Count > 0) return entries;
            }
            catch { /* fall through to manual parser */ }

            // REQ 2: Fallback — byte-by-byte LFH scanning
            return ReadEntriesManual(File.ReadAllBytes(filePath));
        }

        /// <summary>
        /// REQ 2: Manual Local File Header scanner for non-standard ZIP files
        /// (Ekahau .esx files may lack EOCD marker).
        /// LFH signature = 0x04034b50 (little-endian: 50 4B 03 04).
        /// </summary>
        private static Dictionary<string, byte[]> ReadEntriesManual(byte[] data)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            int pos = 0;

            while (pos < data.Length - 30)
            {
                // Search for LFH signature
                if (data[pos] != 0x50 || data[pos + 1] != 0x4B ||
                    data[pos + 2] != 0x03 || data[pos + 3] != 0x04)
                {
                    pos++;
                    continue;
                }

                try
                {
                    ushort flags    = BitConverter.ToUInt16(data, pos + 6);
                    ushort method   = BitConverter.ToUInt16(data, pos + 8);
                    uint   compSize = BitConverter.ToUInt32(data, pos + 18);
                    ushort nameLen  = BitConverter.ToUInt16(data, pos + 26);
                    ushort extraLen = BitConverter.ToUInt16(data, pos + 28);

                    int nameStart = pos + 30;
                    if (nameStart + nameLen > data.Length) break;
                    string name = Encoding.UTF8.GetString(data, nameStart, nameLen);
                    int dataStart = nameStart + nameLen + extraLen;

                    bool hasDataDescriptor = (flags & 0x08) != 0;

                    // If data descriptor flag is set and sizes are zero, find next header
                    if (compSize == 0 && hasDataDescriptor)
                    {
                        int nextHeader = FindNextHeader(data, dataStart);
                        if (nextHeader < 0) break;

                        // Check for data descriptor (with or without signature)
                        if (nextHeader >= dataStart + 16 &&
                            data[nextHeader - 16] == 0x50 && data[nextHeader - 15] == 0x4B &&
                            data[nextHeader - 14] == 0x07 && data[nextHeader - 13] == 0x08)
                        {
                            compSize = BitConverter.ToUInt32(data, nextHeader - 8);
                        }
                        else if (nextHeader >= dataStart + 12)
                        {
                            compSize = (uint)(nextHeader - dataStart - 12);
                        }
                        else
                        {
                            compSize = (uint)(nextHeader - dataStart);
                        }
                    }

                    if (dataStart + compSize > data.Length)
                    {
                        pos++;
                        continue;
                    }

                    byte[] entryData;
                    if (method == 0) // Stored
                    {
                        entryData = new byte[compSize];
                        Array.Copy(data, dataStart, entryData, 0, (int)compSize);
                    }
                    else if (method == 8) // Deflate
                    {
                        using var compStream = new MemoryStream(data, dataStart, (int)compSize);
                        using var deflate = new DeflateStream(compStream, CompressionMode.Decompress);
                        using var outStream = new MemoryStream();
                        deflate.CopyTo(outStream);
                        entryData = outStream.ToArray();
                    }
                    else
                    {
                        // Unknown compression — skip
                        pos = dataStart + (int)compSize;
                        continue;
                    }

                    entries[name] = entryData;
                    pos = dataStart + (int)compSize;
                }
                catch
                {
                    pos++;
                }
            }
            return entries;
        }

        /// <summary>Find the next PK signature (LFH or Central Dir) after the given offset.</summary>
        private static int FindNextHeader(byte[] data, int startPos)
        {
            for (int i = startPos + 1; i < data.Length - 4; i++)
            {
                if (data[i] != 0x50 || data[i + 1] != 0x4B) continue;
                if ((data[i + 2] == 0x03 && data[i + 3] == 0x04) || // LFH
                    (data[i + 2] == 0x01 && data[i + 3] == 0x02))   // Central Dir
                    return i;
            }
            return -1;
        }

        // ── REQ 19+20 — Parse ESX JSON content ───────────────────────────

        /// <summary>
        /// Parse the ESX JSON content into structured data.
        /// Reads: project.json, floorPlans.json, accessPoints.json,
        /// simulatedRadios.json, measuredRadios.json, antennaTypes.json,
        /// and image-* entries.
        /// </summary>
        public static EsxReadResult ParseEsx(Dictionary<string, byte[]> entries)
        {
            var result = new EsxReadResult();

            // ── project.json ──────────────────────────────────────────
            if (entries.TryGetValue("project.json", out var projBytes))
            {
                try
                {
                    using var doc = JsonDocument.Parse(projBytes);
                    if (doc.RootElement.TryGetProperty("project", out var proj))
                        result.ProjectName = GetStr(proj, "name", "Unknown");
                }
                catch { }
            }

            // ── floorPlans.json ───────────────────────────────────────
            if (entries.TryGetValue("floorPlans.json", out var fpBytes))
            {
                try
                {
                    using var doc = JsonDocument.Parse(fpBytes);
                    if (doc.RootElement.TryGetProperty("floorPlans", out var arr))
                    {
                        foreach (var fp in arr.EnumerateArray())
                        {
                            var plan = new EsxFloorPlanData
                            {
                                Id            = GetStr(fp, "id"),
                                Name          = GetStr(fp, "name", "Unnamed"),
                                Width         = GetDbl(fp, "width"),
                                Height        = GetDbl(fp, "height"),
                                MetersPerUnit = GetDbl(fp, "metersPerUnit", 0.0264583),
                                ImageId       = GetStr(fp, "imageId"),
                                // Ekahau attaches a rendered raster
                                // alongside SVG floor plans — prefer it
                                // when present (Revit can't render SVG).
                                BitmapImageId = GetStr(fp, "bitmapImageId"),
                            };
                            if (fp.TryGetProperty("revitAnchor", out var anchor))
                                plan.RevitAnchor = ParseRevitAnchor(anchor);
                            result.FloorPlans.Add(plan);
                        }
                    }
                }
                catch { }
            }

            // ── accessPoints.json ─────────────────────────────────────
            if (entries.TryGetValue("accessPoints.json", out var apBytes))
            {
                try
                {
                    using var doc = JsonDocument.Parse(apBytes);
                    if (doc.RootElement.TryGetProperty("accessPoints", out var arr))
                    {
                        foreach (var ap in arr.EnumerateArray())
                        {
                            var apData = new EsxAccessPointData
                            {
                                Id             = GetStr(ap, "id"),
                                Name           = GetStr(ap, "name", "AP"),
                                MountingHeight = GetDbl(ap, "mountingHeight", 2.7),
                                Vendor         = GetStr(ap, "vendor"),
                                Model          = GetStr(ap, "model"),
                                AntennaTypeId  = GetStr(ap, "antennaTypeId"),
                            };
                            if (ap.TryGetProperty("location", out var loc))
                            {
                                apData.FloorPlanId = GetStr(loc, "floorPlanId");
                                if (loc.TryGetProperty("coord", out var coord))
                                {
                                    apData.PixelX = GetDbl(coord, "x");
                                    apData.PixelY = GetDbl(coord, "y");
                                }
                            }
                            if (ap.TryGetProperty("tags", out var tags) &&
                                tags.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var t in tags.EnumerateArray())
                                    apData.Tags.Add(t.GetString() ?? "");
                            }
                            result.AccessPoints.Add(apData);
                        }
                    }
                }
                catch { }
            }

            // ── simulatedRadios.json ──────────────────────────────────
            ParseRadios(entries, "simulatedRadios.json", "simulatedRadios", result.SimulatedRadios);

            // ── measuredRadios.json ───────────────────────────────────
            ParseRadios(entries, "measuredRadios.json", "measuredRadios", result.MeasuredRadios);

            // ── antennaTypes.json ─────────────────────────────────────
            if (entries.TryGetValue("antennaTypes.json", out var atBytes))
            {
                try
                {
                    using var doc = JsonDocument.Parse(atBytes);
                    if (doc.RootElement.TryGetProperty("antennaTypes", out var arr))
                    {
                        foreach (var at in arr.EnumerateArray())
                        {
                            result.AntennaTypes.Add(new EsxAntennaTypeData
                            {
                                Id         = GetStr(at, "id"),
                                Name       = GetStr(at, "name"),
                                Vendor     = GetStr(at, "vendor"),
                                ApCoupling = GetStr(at, "apCoupling"),
                            });
                        }
                    }
                }
                catch { }
            }

            // ── image-* entries ───────────────────────────────────────
            //   Strip the "image-" prefix AND any common image extension
            //   (some Ekahau exports include .png in the entry name,
            //    which would otherwise prevent the imageId-based lookup
            //    in floorPlans.json from matching).
            foreach (var kv in entries)
            {
                if (!kv.Key.StartsWith("image-", StringComparison.Ordinal)) continue;
                string key = kv.Key.Substring(6);
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
                {
                    if (key.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        key = key.Substring(0, key.Length - ext.Length);
                        break;
                    }
                }
                // Store both the stripped key (preferred lookup) AND the
                // full original key (defensive — handles edge cases where
                // floorPlans.json includes the extension in imageId).
                result.ImageEntries[key] = kv.Value;
                if (!result.ImageEntries.ContainsKey(kv.Key.Substring(6)))
                    result.ImageEntries[kv.Key.Substring(6)] = kv.Value;
            }

            // ── REQ 8: Attach radio band info + design data to each AP ─
            var allRadios = result.SimulatedRadios
                .Concat(result.MeasuredRadios)
                .ToList();
            var antennaMap = result.AntennaTypes
                .ToDictionary(a => a.Id, a => a);

            foreach (var ap in result.AccessPoints)
            {
                var apRadios = allRadios
                    .Where(r => r.AccessPointId == ap.Id && r.Enabled)
                    .ToList();

                ap.Bands = apRadios
                    .Select(r => r.Band)
                    .Where(b => !string.IsNullOrEmpty(b))
                    .Distinct()
                    .OrderBy(b => b)
                    .ToList();

                // REQ 8: Resolve vendor/model from antennaType if not on AP
                if (string.IsNullOrEmpty(ap.Vendor) && !string.IsNullOrEmpty(ap.AntennaTypeId))
                {
                    if (antennaMap.TryGetValue(ap.AntennaTypeId, out var at))
                    {
                        if (string.IsNullOrEmpty(ap.Vendor))
                            ap.Vendor = at.Vendor;
                        if (string.IsNullOrEmpty(ap.Model))
                            ap.Model = at.Name;
                    }
                }

                // ── Build radio summary fields for scheduling ─────────
                BuildRadioSummaries(ap, apRadios, antennaMap);
            }

            return result;
        }

        private static void ParseRadios(
            Dictionary<string, byte[]> entries,
            string fileName, string rootKey,
            List<EsxRadioData> target)
        {
            if (!entries.TryGetValue(fileName, out var bytes)) return;
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (!doc.RootElement.TryGetProperty(rootKey, out var arr)) return;
                foreach (var r in arr.EnumerateArray())
                {
                    target.Add(new EsxRadioData
                    {
                        Id                 = GetStr(r, "id"),
                        AccessPointId      = GetStr(r, "accessPointId"),
                        Band               = GetStr(r, "band"),
                        Channel            = GetStr(r, "channel"),
                        TxPower            = GetDbl(r, "transmitPower"),
                        Enabled            = r.TryGetProperty("enabled", out var en)
                                             ? en.ValueKind != JsonValueKind.False
                                             : true,
                        Technology         = GetStr(r, "technology"),
                        SpatialStreamCount = (int)GetDbl(r, "numberOfSpatialStreams",
                                                  GetDbl(r, "antennaCount")),
                        AntennaMounting    = GetStr(r, "antennaMounting"),
                        AntennaTypeId      = GetStr(r, "antennaTypeId"),
                    });
                }
            }
            catch { }
        }

        private static EsxRevitAnchorData ParseRevitAnchor(JsonElement anchor)
        {
            var a = new EsxRevitAnchorData
            {
                CropWorldMinX_ft = GetDbl(anchor, "cropWorldMinX_ft"),
                CropWorldMinY_ft = GetDbl(anchor, "cropWorldMinY_ft"),
                CropWorldMaxX_ft = GetDbl(anchor, "cropWorldMaxX_ft"),
                CropWorldMaxY_ft = GetDbl(anchor, "cropWorldMaxY_ft"),
                MetersPerUnit    = GetDbl(anchor, "metersPerUnit"),
                ImageWidth       = (int)GetDbl(anchor, "imageWidth"),
                ImageHeight      = (int)GetDbl(anchor, "imageHeight"),
                CropPixelOffsetX = GetDbl(anchor, "cropPixelOffsetX"),
                CropPixelOffsetY = GetDbl(anchor, "cropPixelOffsetY"),
                CropPixelWidth   = GetDbl(anchor, "cropPixelWidth"),
                CropPixelHeight  = GetDbl(anchor, "cropPixelHeight"),
                XformOriginX_ft  = GetDbl(anchor, "xformOriginX_ft"),
                XformOriginY_ft  = GetDbl(anchor, "xformOriginY_ft"),
                XformBasisXx     = GetDbl(anchor, "xformBasisXx"),
                XformBasisXy     = GetDbl(anchor, "xformBasisXy"),
                XformBasisYx     = GetDbl(anchor, "xformBasisYx"),
                XformBasisYy     = GetDbl(anchor, "xformBasisYy"),
                LocalMinX        = GetDbl(anchor, "localMinX"),
                LocalMinY        = GetDbl(anchor, "localMinY"),
                LocalMaxX        = GetDbl(anchor, "localMaxX"),
                LocalMaxY        = GetDbl(anchor, "localMaxY"),
            };

            // Detect which mode is available
            a.HasTransform = anchor.TryGetProperty("xformBasisXx", out _);
            a.HasWorldBounds = anchor.TryGetProperty("cropWorldMinX_ft", out _) &&
                               Math.Abs(a.CropWorldMaxX_ft - a.CropWorldMinX_ft) > 1e-6;
            return a;
        }

        // ── JSON helpers ──────────────────────────────────────────────

        private static string GetStr(JsonElement el, string prop, string def = "")
        {
            if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? def;
            return def;
        }

        private static double GetDbl(JsonElement el, string prop, double def = 0)
        {
            if (!el.TryGetProperty(prop, out var v)) return def;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                return d;
            return def;
        }

        // ── Radio summary builder ────────────────────────────────────

        /// <summary>
        /// Build human-readable radio summaries on an AP from its radios
        /// and antenna data.  Used for Revit parameter scheduling.
        /// </summary>
        private static void BuildRadioSummaries(
            EsxAccessPointData ap,
            List<EsxRadioData> radios,
            Dictionary<string, EsxAntennaTypeData> antennaMap)
        {
            if (radios.Count == 0) return;

            // Bands summary: "2.4GHz, 5GHz, 6GHz"
            ap.BandsSummary = string.Join(", ",
                radios.Select(r => BandToDisplay(r.Band))
                      .Where(b => b != null)
                      .Distinct()
                      .OrderBy(b => b));

            // Technology: pick the highest
            var techs = radios
                .Select(r => r.Technology)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
            if (techs.Contains("BE"))      ap.Technology = "WiFi 7 (BE)";
            else if (techs.Contains("AX")) ap.Technology = "WiFi 6/6E (AX)";
            else if (techs.Contains("AC")) ap.Technology = "WiFi 5 (AC)";
            else if (techs.Contains("N"))  ap.Technology = "WiFi 4 (N)";
            else if (techs.Count > 0)      ap.Technology = string.Join("/", techs);

            // Tx power per band: "2G:14dBm / 5G:14dBm / 6G:14dBm"
            ap.TxPowerSummary = string.Join(" / ",
                radios.Where(r => r.TxPower > 0)
                      .Select(r => $"{BandShort(r.Band)}:{r.TxPower:F0}dBm"));

            // Channel per band: "2G:Ch1 / 5G:Ch149 / 6G:Ch5"
            ap.ChannelsSummary = string.Join(" / ",
                radios.Where(r => !string.IsNullOrEmpty(r.Channel))
                      .Select(r => $"{BandShort(r.Band)}:Ch{FormatChannel(r.Channel)}"));

            // Spatial streams: "2G:4x4 / 5G:4x4"
            ap.StreamsSummary = string.Join(" / ",
                radios.Where(r => r.SpatialStreamCount > 0)
                      .Select(r => $"{BandShort(r.Band)}:{r.SpatialStreamCount}x{r.SpatialStreamCount}"));

            // Mounting (take first non-empty)
            ap.Mounting = radios
                .Select(r => r.AntennaMounting)
                .FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "";

            // Antenna info (Internal/External)
            var couplings = radios
                .Where(r => !string.IsNullOrEmpty(r.AntennaTypeId))
                .Select(r => antennaMap.TryGetValue(r.AntennaTypeId, out var at)
                    ? at.ApCoupling : "")
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .ToList();
            if (couplings.Any(c => c.Contains("EXTERNAL")))
                ap.AntennaInfo = "External";
            else if (couplings.Count > 0)
                ap.AntennaInfo = "Internal";
        }

        private static string BandToDisplay(string band) => band switch
        {
            "TWO"  => "2.4GHz",
            "FIVE" => "5GHz",
            "SIX"  => "6GHz",
            _      => band,
        };

        private static string BandShort(string band) => band switch
        {
            "TWO"  => "2G",
            "FIVE" => "5G",
            "SIX"  => "6G",
            _      => band,
        };

        /// <summary>
        /// If channel value is a frequency in MHz (>1000), convert to channel number.
        /// Otherwise return as-is.
        /// </summary>
        private static string FormatChannel(string channelStr)
        {
            if (string.IsNullOrEmpty(channelStr)) return "";
            if (!int.TryParse(channelStr, out int val)) return channelStr;
            if (val <= 1000) return val.ToString();
            // Frequency → channel conversion
            if (val >= 2412 && val <= 2484)
                return (val == 2484 ? 14 : (val - 2407) / 5).ToString();
            if (val >= 5170 && val <= 5835)
                return ((val - 5000) / 5).ToString();
            if (val >= 5925 && val <= 7125)
                return ((val - 5950) / 5).ToString();
            return val.ToString();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  REQ 4 — Coordinate Transform: Ekahau pixel → Revit world (feet)
    //  Three modes:
    //    1. Full transform (rotated view with xformBasis* fields)
    //    2. Simple world bounds (non-rotated, cropWorld* fields)
    //    3. No revitAnchor — calibrate from matched Revit view CropBox
    // ══════════════════════════════════════════════════════════════════════

    public static class EsxCoordXform
    {
        private const double FeetToMetres = 0.3048;

        /// <summary>
        /// Build the inverse coordinate transform: Ekahau pixel (ex, ey) → Revit world (wx, wy).
        /// </summary>
        public static Func<double, double, (double Wx, double Wy)> BuildEkahauToRevitXform(
            EsxFloorPlanData floorPlan, ViewPlan matchedView, Document doc)
        {
            var anchor = floorPlan.RevitAnchor;

            // ── Mode 1: Full transform (rotated-view support) ─────────
            if (anchor != null && anchor.HasTransform &&
                anchor.CropPixelWidth > 0 && anchor.CropPixelHeight > 0)
            {
                double offX = anchor.CropPixelOffsetX;
                double offY = anchor.CropPixelOffsetY;
                double cW   = anchor.CropPixelWidth;
                double cH   = anchor.CropPixelHeight;
                double lMinX = anchor.LocalMinX;
                double lMaxX = anchor.LocalMaxX;
                double lMinY = anchor.LocalMinY;
                double lMaxY = anchor.LocalMaxY;
                double cropW = lMaxX - lMinX;
                double cropH = lMaxY - lMinY;
                double oX  = anchor.XformOriginX_ft;
                double oY  = anchor.XformOriginY_ft;
                double bXx = anchor.XformBasisXx;
                double bXy = anchor.XformBasisXy;
                double bYx = anchor.XformBasisYx;
                double bYy = anchor.XformBasisYy;

                // AP coordinates in `.esx` are in floorPlans.json logical
                // units (floorPlan.Width × floorPlan.Height).  The
                // anchor's CropPixelWidth/Height frame may be in a
                // DIFFERENT pixel resolution — e.g., visual-cal anchors
                // synthesised after the v2.5.16 NormalizeForRevit
                // downscale store the bitmap-pixel dimensions (4000×2857
                // for a 5000×3571 source clamped to maxDim=4000), NOT
                // floorPlans.json's logical 3024×2160.  ESX-Export-
                // derived anchors have CropPixelWidth == floorPlan.Width
                // so apScale = 1.0 (no behavioural change for those).
                //
                // For third-party Ekahau files where the bitmap raster is
                // a higher-resolution rendering of the logical floor plan
                // (the v2.5.18 symptom), apScale ≠ 1.0 — without this
                // scaling step AP markers land at fp.Width-space positions
                // through a CropPixelWidth-space transform, missing the
                // mark by the (CropPixelWidth / fp.Width) ratio.
                double apScaleX = (floorPlan.Width  > 0) ? cW / floorPlan.Width  : 1.0;
                double apScaleY = (floorPlan.Height > 0) ? cH / floorPlan.Height : 1.0;
                Debug.WriteLine(
                    $"[ESX Read] BuildEkahauToRevitXform Mode 1: " +
                    $"CropPixel=({cW:F1}x{cH:F1}), fp=({floorPlan.Width:F1}x{floorPlan.Height:F1}), " +
                    $"apScale=({apScaleX:F4}x{apScaleY:F4})");

                return (ex, ey) =>
                {
                    // Convert AP coord from fp.Width-space to anchor frame
                    double sx = ex * apScaleX;
                    double sy = ey * apScaleY;
                    // Pixel → view-local
                    double vx = lMinX + (sx - offX) / cW * cropW;
                    double vy = lMaxY - (sy - offY) / cH * cropH;
                    // View-local → world via CropBox Transform
                    double wx = oX + bXx * vx + bYx * vy;
                    double wy = oY + bXy * vx + bYy * vy;
                    return (wx, wy);
                };
            }

            // ── Mode 2: Simple world bounds (non-rotated) ─────────────
            if (anchor != null && anchor.HasWorldBounds)
            {
                double offX  = anchor.CropPixelOffsetX;
                double offY  = anchor.CropPixelOffsetY;
                double cW    = anchor.CropPixelWidth > 0 ? anchor.CropPixelWidth : floorPlan.Width;
                double cH    = anchor.CropPixelHeight > 0 ? anchor.CropPixelHeight : floorPlan.Height;
                double wMinX = anchor.CropWorldMinX_ft;
                double wMaxX = anchor.CropWorldMaxX_ft;
                double wMinY = anchor.CropWorldMinY_ft;
                double wMaxY = anchor.CropWorldMaxY_ft;
                double worldW = wMaxX - wMinX;
                double worldH = wMaxY - wMinY;

                // Same fp.Width-space → anchor-frame scaling as Mode 1
                // (see comment above).  When CropPixelWidth was missing
                // we already fell back to fp.Width above, so apScale
                // becomes 1.0 in that branch — only matters when both
                // anchor.CropPixelWidth and floorPlan.Width are present
                // and differ.
                double apScaleX = (floorPlan.Width  > 0) ? cW / floorPlan.Width  : 1.0;
                double apScaleY = (floorPlan.Height > 0) ? cH / floorPlan.Height : 1.0;

                return (ex, ey) =>
                {
                    double sx = ex * apScaleX;
                    double sy = ey * apScaleY;
                    double wx = wMinX + (sx - offX) / cW * worldW;
                    double wy = wMaxY - (sy - offY) / cH * worldH;
                    return (wx, wy);
                };
            }

            // ── Mode 3: No revitAnchor — use matched view CropBox ─────
            if (matchedView != null && matchedView.CropBoxActive)
            {
                var cropBox  = matchedView.CropBox;
                var xform    = cropBox.Transform;
                double lMinX = cropBox.Min.X;
                double lMaxX = cropBox.Max.X;
                double lMinY = cropBox.Min.Y;
                double lMaxY = cropBox.Max.Y;
                double cropW = lMaxX - lMinX;
                double cropH = lMaxY - lMinY;
                double imgW  = floorPlan.Width;
                double imgH  = floorPlan.Height;

                // Compute padding-aware pixel region (same as export)
                double cropAR = cropH > 0 ? cropW / cropH : 1.0;
                double pngAR  = imgH > 0 ? imgW / imgH : cropAR;
                double contentW, contentH;
                if (cropAR >= pngAR)
                { contentW = imgW; contentH = imgW / cropAR; }
                else
                { contentH = imgH; contentW = imgH * cropAR; }
                double offX = (imgW - contentW) / 2.0;
                double offY = (imgH - contentH) / 2.0;

                double oX  = xform.Origin.X;
                double oY  = xform.Origin.Y;
                double bXx = xform.BasisX.X;
                double bXy = xform.BasisX.Y;
                double bYx = xform.BasisY.X;
                double bYy = xform.BasisY.Y;

                return (ex, ey) =>
                {
                    double vx = lMinX + (ex - offX) / contentW * cropW;
                    double vy = lMaxY - (ey - offY) / contentH * cropH;
                    double wx = oX + bXx * vx + bYx * vy;
                    double wy = oY + bXy * vx + bYy * vy;
                    return (wx, wy);
                };
            }

            // ── Fallback: scale-only from metersPerUnit ───────────────
            double mpu = floorPlan.MetersPerUnit > 0 ? floorPlan.MetersPerUnit : 0.0264583;
            double fpp = mpu / FeetToMetres;
            return (ex, ey) =>
            {
                return (ex * fpp, -ey * fpp);
            };
        }

        /// <summary>
        /// REQ 3: Validate scale consistency between ESX and matched view.
        /// Returns warning message or null if OK.
        /// </summary>
        public static string ValidateScale(
            EsxFloorPlanData floorPlan, ViewPlan view)
        {
            if (view == null || !view.CropBoxActive) return null;
            if (floorPlan.MetersPerUnit <= 0) return null;

            var cropBox = view.CropBox;
            double cropW_m = (cropBox.Max.X - cropBox.Min.X) * FeetToMetres;
            double imgW_m  = floorPlan.Width * floorPlan.MetersPerUnit;

            if (imgW_m < 1e-6) return null;
            double diff = Math.Abs(cropW_m - imgW_m) / imgW_m;

            if (diff > 0.10) // >10% mismatch
                return $"Scale mismatch: ESX image represents {imgW_m:F1} m " +
                       $"but view crop is {cropW_m:F1} m wide ({diff:P0} difference).";

            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  REQ 8+13+14+16 — Marker Operations
    //  Place AP markers (circle + cross + label), cleanup old markers,
    //  adaptive sizing, color-coding by radio band, legend placement.
    // ══════════════════════════════════════════════════════════════════════

    public static class EsxMarkerOps
    {
        // ── REQ 8: Band color palette ─────────────────────────────────
        private static readonly Color ColorBand2   = new Color(21, 101, 192);   // #1565C0 blue
        private static readonly Color ColorBand5   = new Color(46, 125, 50);    // #2E7D32 green
        private static readonly Color ColorBand6   = new Color(230, 81, 0);     // #E65100 orange
        private static readonly Color ColorMulti   = new Color(106, 27, 154);   // #6A1B9A purple
        private static readonly Color ColorUnknown = new Color(97, 97, 97);     // #616161 grey

        /// <summary>Prefix for all ESX Read text notes — used for cleanup.</summary>
        public const string MarkerPrefix = "[EK] ";

        /// <summary>REQ 8: Get marker colour based on radio bands.</summary>
        public static Color GetBandColor(List<string> bands)
        {
            if (bands == null || bands.Count == 0) return ColorUnknown;
            bool has2 = bands.Contains("TWO");
            bool has5 = bands.Contains("FIVE");
            bool has6 = bands.Contains("SIX");
            int count = (has2 ? 1 : 0) + (has5 ? 1 : 0) + (has6 ? 1 : 0);
            if (count > 1) return ColorMulti;
            if (has6) return ColorBand6;
            if (has5) return ColorBand5;
            if (has2) return ColorBand2;
            return ColorUnknown;
        }

        /// <summary>REQ 8: Format band list for display.</summary>
        public static string FormatBands(List<string> bands)
        {
            if (bands == null || bands.Count == 0) return "Unknown";
            var formatted = bands.Select(b => b switch
            {
                "TWO"  => "2.4",
                "FIVE" => "5",
                "SIX"  => "6",
                _      => b,
            }).Distinct().OrderBy(b => b);
            return string.Join("/", formatted) + " GHz";
        }

        // ── REQ 14: Cleanup old markers ───────────────────────────────

        /// <summary>
        /// Find all ESX Read marker elements in a view (TextNotes with [EK] prefix
        /// and nearby detail curves).
        /// Bug Fix #9: Entire method is fail-safe — returns empty list on any error.
        /// </summary>
        public static List<ElementId> FindOldMarkers(Document doc, View view)
        {
            var ids = new List<ElementId>();

            try
            {
                // Validate inputs
                if (doc == null || view == null) return ids;
                try { var _ = view.Id; } catch { return ids; } // view may be disposed/invalid

                // Find [EK] text notes
                List<TextNote> textNotes;
                try
                {
                    textNotes = new FilteredElementCollector(doc, view.Id)
                        .OfClass(typeof(TextNote))
                        .Cast<TextNote>()
                        .Where(tn =>
                        {
                            try { return tn.Text != null && tn.Text.StartsWith(MarkerPrefix); }
                            catch { return false; }
                        })
                        .ToList();
                }
                catch
                {
                    return ids; // Collector failed (view deleted, etc.)
                }

                foreach (var tn in textNotes)
                {
                    try { ids.Add(tn.Id); }
                    catch { }
                }

                // Collect detail curve locations near text notes
                if (textNotes.Count > 0)
                {
                    var noteLocations = new List<XYZ>();
                    foreach (var tn in textNotes)
                    {
                        try { noteLocations.Add(tn.Coord); }
                        catch { }
                    }

                    if (noteLocations.Count > 0)
                    {
                        double searchRadius = 8.0; // feet — generous search around AP centers

                        IList<Element> detailCurves;
                        IList<Element> detailLines;
                        try
                        {
                            detailCurves = new FilteredElementCollector(doc, view.Id)
                                .OfClass(typeof(DetailArc))
                                .ToElements();
                        }
                        catch { detailCurves = Array.Empty<Element>(); }

                        try
                        {
                            detailLines = new FilteredElementCollector(doc, view.Id)
                                .OfClass(typeof(DetailLine))
                                .ToElements();
                        }
                        catch { detailLines = Array.Empty<Element>(); }

                        foreach (var elem in detailCurves.Concat(detailLines))
                        {
                            try
                            {
                                if (elem.Location is LocationCurve lc)
                                {
                                    var mid = lc.Curve.Evaluate(0.5, true);
                                    foreach (var nl in noteLocations)
                                    {
                                        if (mid.DistanceTo(nl) < searchRadius)
                                        {
                                            ids.Add(elem.Id);
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { /* individual element failed — skip */ }
                        }
                    }
                }
            }
            catch
            {
                // Bug Fix #9: Entire search failed — return whatever we found so far
            }

            return ids;
        }

        /// <summary>
        /// Delete old markers found by FindOldMarkers.
        /// Bug Fix #9: Fail-safe — never throws. Returns count of successfully deleted elements.
        /// </summary>
        public static int CleanupMarkers(Document doc, View view)
        {
            try
            {
                var ids = FindOldMarkers(doc, view);
                if (ids.Count == 0) return 0;

                int deleted = 0;
                using var tx = new Transaction(doc, "Remove old ESX markers");
                tx.Start();
                foreach (var id in ids)
                {
                    try
                    {
                        var elem = doc.GetElement(id);
                        if (elem == null) continue;      // already deleted
                        if (elem.Pinned) continue;       // pinned — don't touch
                        doc.Delete(id);
                        deleted++;
                    }
                    catch { /* individual delete failed — skip */ }
                }
                if (tx.HasStarted()) tx.Commit();
                return deleted;
            }
            catch
            {
                // Bug Fix #9: Entire cleanup transaction failed — don't crash
                return 0;
            }
        }

        // ── REQ 13: Adaptive marker sizing ─────────────────────────────

        /// <summary>
        /// Compute marker radius based on view extent.
        /// Scales with the view to remain visible at all zoom levels.
        /// </summary>
        public static double GetAdaptiveRadius(ViewPlan view)
        {
            double extent = 200; // default fallback (feet)
            try
            {
                if (view.CropBoxActive)
                {
                    var cb = view.CropBox;
                    extent = Math.Max(cb.Max.X - cb.Min.X, cb.Max.Y - cb.Min.Y);
                }
            }
            catch { }
            // 0.5% of view extent, clamped to [1, 5] feet
            return Math.Max(1.0, Math.Min(5.0, extent * 0.005));
        }

        // ── Place AP marker (circle + cross + label) ──────────────────

        /// <summary>
        /// Place one AP marker on a view.  Returns created element IDs.
        /// </summary>
        public static List<ElementId> PlaceApMarker(
            Document doc, View view,
            double worldX, double worldY,
            string apName, string bandInfo,
            Color color, double radius)
        {
            var created = new List<ElementId>();
            var center  = new XYZ(worldX, worldY, 0);

            // ── Circle (two semicircular arcs) ─────────────────────────
            try
            {
                var arc1 = Arc.Create(center, radius, 0, Math.PI,
                                      XYZ.BasisX, XYZ.BasisY);
                var dc1  = doc.Create.NewDetailCurve(view, arc1);
                created.Add(dc1.Id);

                var arc2 = Arc.Create(center, radius, Math.PI, 2 * Math.PI,
                                      XYZ.BasisX, XYZ.BasisY);
                var dc2  = doc.Create.NewDetailCurve(view, arc2);
                created.Add(dc2.Id);
            }
            catch { }

            // ── Cross at centre ────────────────────────────────────────
            double crossSz = radius * 0.4;
            try
            {
                var hLine = Line.CreateBound(
                    new XYZ(worldX - crossSz, worldY, 0),
                    new XYZ(worldX + crossSz, worldY, 0));
                created.Add(doc.Create.NewDetailCurve(view, hLine).Id);

                var vLine = Line.CreateBound(
                    new XYZ(worldX, worldY - crossSz, 0),
                    new XYZ(worldX, worldY + crossSz, 0));
                created.Add(doc.Create.NewDetailCurve(view, vLine).Id);
            }
            catch { }

            // ── Text label ─────────────────────────────────────────────
            try
            {
                var textTypeId = GetDefaultTextNoteTypeId(doc);
                if (textTypeId != ElementId.InvalidElementId)
                {
                    string label = MarkerPrefix + apName;
                    if (!string.IsNullOrEmpty(bandInfo))
                        label += "  [" + bandInfo + "]";

                    double labelOffset = radius + 0.4;
                    var note = TextNote.Create(doc, view.Id,
                        new XYZ(worldX, worldY + labelOffset, 0),
                        label, textTypeId);
                    created.Add(note.Id);
                }
            }
            catch { }

            // ── Colour override ────────────────────────────────────────
            if (color != null)
            {
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(color);
                ogs.SetProjectionLineWeight(3);
                foreach (var id in created)
                {
                    try { view.SetElementOverrides(id, ogs); }
                    catch { }
                }
            }

            return created;
        }

        // ── REQ 16: Legend placement ────────────────────────────────────

        /// <summary>
        /// Place a small colour legend in the top-right of the view.
        /// </summary>
        public static List<ElementId> PlaceLegend(
            Document doc, ViewPlan view,
            List<(string BandLabel, Color Clr)> legendItems)
        {
            var created = new List<ElementId>();
            if (legendItems.Count == 0) return created;

            try
            {
                var textTypeId = GetDefaultTextNoteTypeId(doc);
                if (textTypeId == ElementId.InvalidElementId) return created;

                // Place in top-right of crop box
                double startX, startY;
                if (view.CropBoxActive)
                {
                    var cb = view.CropBox;
                    var xf = cb.Transform;
                    var topRight = xf.OfPoint(new XYZ(cb.Max.X, cb.Max.Y, 0));
                    startX = topRight.X - 5.0;
                    startY = topRight.Y - 2.0;
                }
                else
                {
                    startX = 0;
                    startY = 0;
                }

                double spacing = 2.5;

                // Title
                var titleNote = TextNote.Create(doc, view.Id,
                    new XYZ(startX, startY, 0),
                    MarkerPrefix + "--- AP Legend ---", textTypeId);
                created.Add(titleNote.Id);

                for (int i = 0; i < legendItems.Count; i++)
                {
                    var (label, clr) = legendItems[i];
                    double y = startY - (i + 1) * spacing;

                    var note = TextNote.Create(doc, view.Id,
                        new XYZ(startX, y, 0),
                        MarkerPrefix + label, textTypeId);
                    created.Add(note.Id);

                    // Colour the text
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(clr);
                    view.SetElementOverrides(note.Id, ogs);
                }
            }
            catch { }

            return created;
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static ElementId _cachedTextTypeId = null;

        private static ElementId GetDefaultTextNoteTypeId(Document doc)
        {
            if (_cachedTextTypeId != null && _cachedTextTypeId != ElementId.InvalidElementId)
            {
                // Verify it's still valid
                try { if (doc.GetElement(_cachedTextTypeId) != null) return _cachedTextTypeId; }
                catch { }
            }
            _cachedTextTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .OrderBy(t => t.Name)
                .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
            return _cachedTextTypeId;
        }

        // ══════════════════════════════════════════════════════════════
        //  Floor-plan image overlay + reference crosses
        //  Used as a mandatory verification step in ESX Read so the user
        //  can confirm the Ekahau→Revit coordinate transform is correct
        //  before AP markers go down.
        // ══════════════════════════════════════════════════════════════

        private const double FtPerMeter_Inv = 1.0 / 0.3048;  // 1/0.3048

        /// <summary>
        /// Place the Ekahau floor-plan PNG into <paramref name="view"/> as an
        /// ImageInstance, scaled and positioned so its CropBox region aligns
        /// with the view's world coordinates.  Caller MUST be inside an open
        /// Transaction.  Returns the placed ImageInstance's ElementId, or
        /// null on any failure.
        /// </summary>
        public static ElementId PlaceFloorPlanImage(
            Document doc, ViewPlan view, string imagePath,
            EsxFloorPlanData fp, int? worksetId = null)
        {
            // ── Step 1: Validate inputs ────────────────────────────────
            if (doc == null || view == null) return null;
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                Debug.WriteLine($"[ESX Read] Image file not found: {imagePath}");
                return null;
            }
            if (fp == null || fp.RevitAnchor == null)
            {
                Debug.WriteLine("[ESX Read] No floor plan anchor data; cannot place overlay.");
                return null;
            }

            long fileSize;
            try { fileSize = new FileInfo(imagePath).Length; }
            catch { fileSize = 0; }
            if (fileSize < 100)
            {
                Debug.WriteLine($"[ESX Read] Image file too small ({fileSize} bytes): {imagePath}");
                return null;
            }

            // ── Step 2: Read actual image dimensions from the PNG ───────
            //   We trust the file over fp.Width/Height because the .esx
            //   image entries occasionally don't match the floorPlans.json
            //   declarations (Ekahau may have re-rasterised on save).
            //   Uses ReadImageDimensions (manual PNG header parse) to
            //   bypass GDI+ which throws misleading "Out of memory" errors
            //   for PNGs it doesn't fully understand (Ekahau's PNGs hit
            //   this often — 16-bit color, exotic interlace, etc.).
            var (actualPixelW, actualPixelH) = EsxReadCommand.ReadImageDimensions(imagePath);
            if (actualPixelW <= 0 || actualPixelH <= 0)
            {
                Debug.WriteLine($"[ESX Read] Image has zero size or unreadable: " +
                    $"{actualPixelW}x{actualPixelH}");
                return null;
            }
            Debug.WriteLine(
                $"[ESX Read] Image: {actualPixelW}x{actualPixelH} px, {fileSize:N0} bytes");

            // ── Step 3: Compute placement centre in Revit feet ─────────
            var anchor = fp.RevitAnchor;
            double mpu = anchor.MetersPerUnit > 0 ? anchor.MetersPerUnit : fp.MetersPerUnit;
            if (mpu <= 0)
            {
                Debug.WriteLine("[ESX Read] metersPerUnit is zero; cannot scale image.");
                return null;
            }

            double ftPerPx      = mpu * FtPerMeter_Inv;
            double imageWidthFt  = actualPixelW * ftPerPx;
            double imageHeightFt = actualPixelH * ftPerPx;

            double cropMinX = anchor.CropWorldMinX_ft;
            double cropMinY = anchor.CropWorldMinY_ft;
            double cropMaxX = anchor.CropWorldMaxX_ft;
            double cropMaxY = anchor.CropWorldMaxY_ft;
            double centerX  = (cropMinX + cropMaxX) / 2.0;
            double centerY  = (cropMinY + cropMaxY) / 2.0;

            // Padding compensation: when the image has annotation padding
            // around the actual CropBox, shift the placement so the crop
            // region's centre lands at the world centre we just computed.
            double cropPxOffX = anchor.CropPixelOffsetX;
            double cropPxOffY = anchor.CropPixelOffsetY;
            double cropPxW    = anchor.CropPixelWidth;
            double cropPxH    = anchor.CropPixelHeight;
            if (cropPxW > 0 && cropPxH > 0)
            {
                double cropCxPx = cropPxOffX + cropPxW / 2.0;
                double cropCyPx = cropPxOffY + cropPxH / 2.0;
                double imgCxPx  = actualPixelW / 2.0;
                double imgCyPx  = actualPixelH / 2.0;
                double dxFt     = (imgCxPx - cropCxPx) * ftPerPx;
                double dyFt     = (imgCyPx - cropCyPx) * ftPerPx;
                centerX += dxFt;     // shift right when image-centre is right of crop-centre
                centerY -= dyFt;     // pixel Y is flipped relative to world Y
            }

            // ── Step 4: Z = the view's level elevation ─────────────────
            double zElev = 0;
            try { if (view.GenLevel != null) zElev = view.GenLevel.Elevation; }
            catch { }

            Debug.WriteLine(
                $"[ESX Read] Placement: centre=({centerX:F2},{centerY:F2},{zElev:F2}) ft, " +
                $"size=({imageWidthFt:F2}x{imageHeightFt:F2}) ft");

            // ── Step 5: Create ImageType ───────────────────────────────
            ImageType imgType = null;
            Exception imgTypeErr = null;
            string strategyTrace = "";
            try { imgType = VersionCompat.CreateImageType(doc, imagePath, out imgTypeErr, out strategyTrace); }
            catch (Exception ex)
            {
                imgTypeErr = ex;
                Debug.WriteLine($"[ESX Read] ImageType.Create failed: {ex.Message}");
            }
            if (imgType == null)
            {
                // `fileSize` is already declared earlier in this method
                // (the dimension-probe block); reuse it instead of
                // shadowing.  ReadFirstBytesHex lives on EsxReadCommand,
                // so we qualify the call from EsxMarkerOps.
                string hex = EsxReadCommand.ReadFirstBytesHex(imagePath, 16);
                string detail = imgTypeErr != null
                    ? $"{imgTypeErr.GetType().Name}: {imgTypeErr.Message}"
                    : "(no inner exception captured)";

                TaskDialog.Show("ESX Read — Image Error",
                    "Could not create the floor plan ImageType after trying " +
                    "every fallback strategy.\n\n" +
                    $"Strategies tried:\n{strategyTrace}\n" +
                    $"Last error   : {detail}\n\n" +
                    $"Temp file    : {imagePath}\n" +
                    $"File size    : {fileSize:N0} bytes\n" +
                    $"First 16 hex :\n  {hex}\n\n" +
                    "The reference overlay won't be shown for this floor.\n" +
                    "AP markers will still be placed.");
                return null;
            }
            Debug.WriteLine($"[ESX Read] ImageType created: Id={imgType.Id}");

            // ── Step 6: Create ImageInstance at the centre ─────────────
            ImageInstance imgInst = null;
            var placementPoint = new XYZ(centerX, centerY, zElev);
            try
            {
                var opts = new ImagePlacementOptions(placementPoint, BoxPlacement.Center);
                imgInst = ImageInstance.Create(doc, view, imgType.Id, opts);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ESX Read] ImageInstance.Create failed: {ex.Message}");
            }
            if (imgInst == null)
            {
                TaskDialog.Show("ESX Read — Image Error",
                    "Could not place the floor plan image in the view.\n\n" +
                    $"Centre: ({centerX:F2}, {centerY:F2}, {zElev:F2}) ft\n" +
                    $"Size:   {imageWidthFt:F2} x {imageHeightFt:F2} ft\n\n" +
                    "AP markers will still be placed without the reference image.");
                return null;
            }
            Debug.WriteLine($"[ESX Read] ImageInstance created: Id={imgInst.Id}");

            // ── Step 7: Set image width — Width property first, then param ──
            bool widthSet = false;
            try
            {
                imgInst.Width = imageWidthFt;
                widthSet = true;
                Debug.WriteLine($"[ESX Read] ImageInstance.Width = {imageWidthFt:F2} ft");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ESX Read] Setting Width property failed: {ex.Message}");
            }
            if (!widthSet)
            {
                try
                {
                    var wParam = imgInst.get_Parameter(BuiltInParameter.RASTER_SHEETWIDTH);
                    if (wParam != null && !wParam.IsReadOnly)
                    {
                        wParam.Set(imageWidthFt);
                        Debug.WriteLine($"[ESX Read] RASTER_SHEETWIDTH = {imageWidthFt:F2} ft");
                    }
                }
                catch { /* both paths failed — accept native size */ }
            }

            // ── Step 8: Workset assignment (optional) ──────────────────
            if (worksetId.HasValue)
            {
                try
                {
                    var p = imgInst.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p != null && !p.IsReadOnly) p.Set(worksetId.Value);
                }
                catch { }
            }

            // ── Step 8b: Rotation (visual-cal anchors carry rotation) ──
            //   When the user manually aligned the image via the two-point
            //   visual calibration, the synthesised anchor encodes the
            //   rotation in the basis vectors:
            //       XformBasisXx = cos(R), XformBasisXy = sin(R)
            //       XformBasisYx = -sin(R), XformBasisYy = cos(R)
            //   The AABB-based centre we computed above IS the rotation
            //   centre (centroid of the 4 rotated corners), so a single
            //   RotateElement call around (centerX, centerY) by R lands
            //   the image exactly where BuildEkahauToRevitXform expects
            //   it — and AP markers (which use the same xform) will then
            //   line up with image features.
            //
            //   Without this step the visual-cal flow leaves the image
            //   axis-aligned while APs go through the rotated transform,
            //   producing the v2.5.18 symptom: "image alignment is fine
            //   but APs ignore rotation".
            if (anchor.HasTransform)
            {
                double rotation = Math.Atan2(anchor.XformBasisXy, anchor.XformBasisXx);
                if (Math.Abs(rotation) > 1e-4)
                {
                    try
                    {
                        var axis = Line.CreateBound(
                            new XYZ(centerX, centerY, zElev),
                            new XYZ(centerX, centerY, zElev + 1));
                        ElementTransformUtils.RotateElement(doc, imgInst.Id, axis, rotation);
                        Debug.WriteLine(
                            $"[ESX Read] Image rotated by {rotation * 180.0 / Math.PI:F2}° " +
                            $"around ({centerX:F2}, {centerY:F2}) to match anchor basis.");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ESX Read] Image rotation failed: {ex.Message}");
                    }
                }
            }

            // ── Step 9: Read-back verification — overlap with CropBox ──
            try
            {
                var bb = imgInst.get_BoundingBox(view);
                if (bb != null)
                {
                    Debug.WriteLine(
                        $"[ESX Read] Image bbox: ({bb.Min.X:F2},{bb.Min.Y:F2}) -> " +
                        $"({bb.Max.X:F2},{bb.Max.Y:F2})");

                    bool overlaps = !(bb.Max.X < cropMinX || bb.Min.X > cropMaxX ||
                                      bb.Max.Y < cropMinY || bb.Min.Y > cropMaxY);
                    if (!overlaps)
                    {
                        Debug.WriteLine(
                            "[ESX Read] WARNING: image bbox does NOT overlap CropBox.");
                        TaskDialog.Show("ESX Read — Image off-view",
                            "The floor plan image was placed but its bounding box does not " +
                            "overlap the CropBox.  The image may not be visible.\n\n" +
                            $"Image bbox: ({bb.Min.X:F1}, {bb.Min.Y:F1}) → ({bb.Max.X:F1}, {bb.Max.Y:F1})\n" +
                            $"CropBox:    ({cropMinX:F1}, {cropMinY:F1}) → ({cropMaxX:F1}, {cropMaxY:F1})\n\n" +
                            "Try Zoom Extents (ZE) to find the image, or check the " +
                            "calibration / revitAnchor data.");
                    }
                }
                else
                {
                    Debug.WriteLine("[ESX Read] WARNING: image has no bounding box in this view.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ESX Read] Bounding-box readback failed: {ex.Message}");
            }

            return imgInst.Id;
        }

        /// <summary>
        /// Draw four green "+" reference crosses at the world-space corners
        /// of the CropBox.  These should align exactly with the corners of
        /// the imported image — a quick visual sanity check.
        /// Caller MUST be inside an open Transaction.  Returns the IDs of
        /// the created DetailLines (8 lines = 2 arms × 4 corners).
        /// </summary>
        public static List<ElementId> DrawReferenceCrosses(
            Document doc, View view, EsxFloorPlanData fp)
        {
            var ids = new List<ElementId>();
            if (doc == null || view == null || fp?.RevitAnchor == null) return ids;

            var a = fp.RevitAnchor;
            double minX = a.CropWorldMinX_ft, minY = a.CropWorldMinY_ft;
            double maxX = a.CropWorldMaxX_ft, maxY = a.CropWorldMaxY_ft;
            if (maxX <= minX || maxY <= minY) return ids;

            // Arm length = 2% of the shorter CropBox dimension
            double arm = Math.Min(maxX - minX, maxY - minY) * 0.02;
            if (arm < 0.5) arm = 0.5;  // never less than 6 inches

            var corners = new (double X, double Y)[]
            {
                (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY),
            };

            var green = new Color(0, 200, 0);
            var ogs = new OverrideGraphicSettings();
            try
            {
                ogs.SetProjectionLineColor(green);
                ogs.SetProjectionLineWeight(5);
            }
            catch { }

            foreach (var (cx, cy) in corners)
            {
                // Horizontal arm
                try
                {
                    var line = Line.CreateBound(
                        new XYZ(cx - arm, cy, 0), new XYZ(cx + arm, cy, 0));
                    var dc = doc.Create.NewDetailCurve(view, line);
                    try { view.SetElementOverrides(dc.Id, ogs); } catch { }
                    ids.Add(dc.Id);
                }
                catch { }

                // Vertical arm
                try
                {
                    var line = Line.CreateBound(
                        new XYZ(cx, cy - arm, 0), new XYZ(cx, cy + arm, 0));
                    var dc = doc.Create.NewDetailCurve(view, line);
                    try { view.SetElementOverrides(dc.Id, ogs); } catch { }
                    ids.Add(dc.Id);
                }
                catch { }
            }

            return ids;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ESX Read Command — Main entry point
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class EsxReadCommand : IExternalCommand
    {
        private const double FeetToMetres = 0.3048;

        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uiDoc.Document;

            // ── 1. File open dialog ───────────────────────────────────
            var openDlg = new Microsoft.Win32.OpenFileDialog
            {
                Title     = "Open Ekahau .esx Project File",
                Filter    = "Ekahau Site Survey (*.esx)|*.esx|All files (*.*)|*.*",
                DefaultExt = ".esx",
            };
            if (openDlg.ShowDialog() != true) return Result.Cancelled;
            string esxPath = openDlg.FileName;

            // ── 2. Parse ESX file ─────────────────────────────────────
            EsxReadResult esxData;
            try
            {
                var entries = EsxZipReader.ReadEntries(esxPath);
                if (entries.Count == 0)
                {
                    TaskDialog.Show("ESX Read", "Failed to read .esx file — no entries found.");
                    return Result.Failed;
                }
                esxData = EsxZipReader.ParseEsx(entries);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ESX Read — Error",
                    $"Failed to parse .esx file:\n{ex.Message}");
                return Result.Failed;
            }

            if (esxData.FloorPlans.Count == 0)
            {
                TaskDialog.Show("ESX Read", "No floor plans found in the .esx file.");
                return Result.Failed;
            }

            // ── 2b. DWG-import fallback: if any floor plan is missing
            //       revitAnchor (typical for Ekahau projects that imported
            //       a DWG instead of an .esx), look for the matching
            //       .ekahau-cal.json sidecar that DWG Export wrote next
            //       to the .dwg.  Apply it to all anchorless floor plans.
            TryApplyDwgCalibrationFallback(esxData, esxPath);

            // REQ 5+6: AP count summary
            int totalAps = esxData.AccessPoints.Count;
            if (totalAps == 0)
            {
                var confirmDlg = new TaskDialog("ESX Read — No Access Points")
                {
                    MainContent = $"Project: {esxData.ProjectName}\n" +
                        $"Floor plans: {esxData.FloorPlans.Count}\n" +
                        $"Access points: 0\n\n" +
                        "No access points to import. Continue anyway?",
                };
                confirmDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Continue");
                confirmDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel");
                if (confirmDlg.Show() != TaskDialogResult.CommandLink1)
                    return Result.Cancelled;
            }

            // ── 3. Floor plan selector dialog ─────────────────────────
            var fpSelector = new EsxReadFloorSelectorDialog(
                esxData.ProjectName,
                esxData.FloorPlans.Select(fp => fp.Name).ToList(),
                esxData.FloorPlans.Select(fp =>
                    esxData.AccessPoints.Count(ap => ap.FloorPlanId == fp.Id)).ToList());

            if (fpSelector.ShowDialog() != true) return Result.Cancelled;
            var selectedIndices = fpSelector.SelectedIndices;
            if (selectedIndices.Count == 0)
            {
                TaskDialog.Show("ESX Read", "No floor plans selected.");
                return Result.Cancelled;
            }

            // ── 4. Collect Revit floor plan views for matching ────────
            var revitViews = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                .OrderBy(v => v.Name)
                .ToList();

            if (revitViews.Count == 0)
            {
                TaskDialog.Show("ESX Read",
                    "No floor plan views found in the Revit model.");
                return Result.Failed;
            }

            // ── 5. REQ 11: View matching ──────────────────────────────
            var selectedFloorPlans = selectedIndices
                .Select(i => esxData.FloorPlans[i]).ToList();

            // Auto-match
            var autoMatches = new Dictionary<string, ViewPlan>(); // fpId → ViewPlan
            foreach (var fp in selectedFloorPlans)
            {
                var match = AutoMatchView(fp.Name, revitViews);
                if (match != null)
                    autoMatches[fp.Id] = match;
            }

            // Show view match dialog
            var matchDlg = new EsxReadViewMatchDialog(
                selectedFloorPlans.Select(fp => fp.Name).ToList(),
                revitViews.Select(v => v.Name).ToList(),
                selectedFloorPlans.Select(fp =>
                    autoMatches.TryGetValue(fp.Id, out var m)
                        ? revitViews.IndexOf(m) : -1).ToList());

            if (matchDlg.ShowDialog() != true) return Result.Cancelled;
            var matchedViewIndices = matchDlg.MatchedViewIndices;

            // ── 6. REQ 21: Staging path ───────────────────────────────
            string stagingDir = RevitHelpers.GetStagingPath(doc);

            // ── 7. REQ 22: Multi-floor loop ───────────────────────────
            var progress = new EsxReadProgressWindow();
            progress.Show();
            DoEvents();

            var floorResults = new List<EsxReadFloorResult>();
            var allCreatedIds = new List<ElementId>();
            int totalPlaced  = 0;
            int totalSkipped = 0;
            var stagingFloors = new List<ApStagingFloor>();  // For AP Place staging JSON

            for (int fi = 0; fi < selectedFloorPlans.Count; fi++)
            {
                var fp = selectedFloorPlans[fi];
                int viewIdx = matchedViewIndices[fi];

                if (viewIdx < 0 || viewIdx >= revitViews.Count)
                {
                    floorResults.Add(new EsxReadFloorResult
                    {
                        FloorPlanName   = fp.Name,
                        MatchedViewName = "(skipped)",
                        Warning         = "No matching Revit view selected.",
                    });
                    continue;
                }

                var view = revitViews[viewIdx];
                progress.Update($"Processing: {fp.Name}", $"Matched to: {view.Name}");
                DoEvents();

                var result = new EsxReadFloorResult
                {
                    FloorPlanName   = fp.Name,
                    MatchedViewName = view.Name,
                };

                // ── REQ 3: Scale validation ───────────────────────────
                string scaleWarn = EsxCoordXform.ValidateScale(fp, view);
                if (scaleWarn != null)
                {
                    result.Warning = scaleWarn;
                    // Don't block — just warn in the summary
                }

                // ── Build coordinate transform ────────────────────────
                Func<double, double, (double Wx, double Wy)> xform;
                try
                {
                    xform = EsxCoordXform.BuildEkahauToRevitXform(fp, view, doc);
                }
                catch (Exception ex)
                {
                    result.Warning = $"Coordinate transform failed: {ex.Message}";
                    floorResults.Add(result);
                    continue;
                }

                // ── Filter APs for this floor plan ────────────────────
                //   Diagnostic logging: surfaces FloorPlanId mismatches
                //   between .esx access points and the matched floor.
                var floorAps = esxData.AccessPoints
                    .Where(ap => ap.FloorPlanId == fp.Id)
                    .ToList();

                Debug.WriteLine(
                    $"[ESX Read] Floor '{fp.Name}' (id={fp.Id}): " +
                    $"{floorAps.Count}/{esxData.AccessPoints.Count} APs match. " +
                    $"All AP FloorPlanIds: " +
                    string.Join(",", esxData.AccessPoints.Select(a => a.FloorPlanId).Distinct()));

                // ══════════════════════════════════════════════════════════
                //  ALWAYS run the image overlay verification step, even when
                //  this floor has zero APs.  Two reasons:
                //    1. User can confirm floor matching is correct (a wrong
                //       floor match is a common cause of "0 APs found").
                //    2. User can manually align if calibration is wrong —
                //       useful even with zero APs because it persists into
                //       staging for any future re-run.
                //  Previously the overlay step ran AFTER the AP-count gate,
                //  which silently skipped verification whenever no APs
                //  matched — leaving the user with "0 APs placed" and no
                //  visual feedback about why.
                // ══════════════════════════════════════════════════════════
                var overlayIds_pre = PlaceImageAndAskForVerification(
                    doc, uiDoc, view, fp, esxData, progress, result,
                    out bool userAbortedVerification_pre);
                allCreatedIds.AddRange(overlayIds_pre);

                if (userAbortedVerification_pre)
                {
                    floorResults.Add(result);
                    continue;
                }

                // After verification (and possibly manual alignment), the
                // anchor may have changed — rebuild the xform.
                try { xform = EsxCoordXform.BuildEkahauToRevitXform(fp, view, doc); }
                catch { /* keep existing xform */ }

                // Surface the "no APs" condition to the user EXPLICITLY,
                // now that they've had a chance to verify the overlay.
                if (floorAps.Count == 0)
                {
                    progress.Hide();
                    var noApsDlg = new TaskDialog("ESX Read — No APs on this floor")
                    {
                        MainInstruction = $"No access points on \"{fp.Name}\"",
                        MainContent =
                            $"The .esx file has {esxData.AccessPoints.Count} AP(s) total, " +
                            $"but none of them reference this floor plan's ID:\n" +
                            $"  {fp.Id}\n\n" +
                            "Possible causes:\n" +
                            "  • The .esx truly has no APs on this floor (designed empty)\n" +
                            "  • You matched the wrong Revit view to this Ekahau floor\n" +
                            "  • The floor plan was renamed in Ekahau after AP placement\n\n" +
                            "The overlay you just verified will be cleaned up.  " +
                            "AP placement is skipped for this floor.",
                    };
                    noApsDlg.CommonButtons = TaskDialogCommonButtons.Ok;
                    try { noApsDlg.Show(); } catch { }
                    progress.Show(); DoEvents();

                    result.Warning = "No access points on this floor.";
                    floorResults.Add(result);
                    continue;
                }

                // ── AP review dialog ──────────────────────────────────
                progress.Hide();
                var apReview = new EsxReadApReviewDialog(fp.Name, floorAps);
                if (apReview.ShowDialog() != true)
                {
                    progress.Show();
                    DoEvents();
                    floorResults.Add(result);
                    continue;
                }
                progress.Show();
                DoEvents();

                var apsToPlace = floorAps.Where(ap => ap.Include).ToList();
                result.ApsSkipped = floorAps.Count - apsToPlace.Count;

                if (apsToPlace.Count == 0)
                {
                    floorResults.Add(result);
                    continue;
                }

                // ── REQ 14: Cleanup old markers ───────────────────────
                // Bug Fix #9: Entire cleanup block is fail-safe.
                // Bug Fix #10: Auto-cleanup — no dialog, old markers are
                // always stale when ESX Read runs again.
                try
                {
                    progress.Update($"Processing: {fp.Name}", "Removing old markers...");
                    DoEvents();

                    // Part A: Scan view for [EK] markers and auto-delete
                    int cleaned = EsxMarkerOps.CleanupMarkers(doc, view);

                    // Part B: Safety-net second pass — catch anything
                    // Part A missed (e.g., markers whose detail curves
                    // drifted out of proximity range, or manually-moved
                    // text notes). A second FindOldMarkers after deletion
                    // will catch orphaned [EK] TextNotes or stray arcs.
                    if (cleaned > 0)
                    {
                        int extra = EsxMarkerOps.CleanupMarkers(doc, view);
                        cleaned += extra;
                    }
                }
                catch (Exception cleanupEx)
                {
                    // Bug Fix #9: Cleanup failure must not block the main workflow.
                    System.Diagnostics.Debug.WriteLine(
                        $"Marker cleanup failed on '{view.Name}': {cleanupEx.Message}");
                    result.Warning = "Could not clean up old markers (continuing).";
                    try { progress.Show(); DoEvents(); } catch { }
                }

                // ── REQ 18: Workset safety check ──────────────────────
                if (doc.IsWorkshared)
                {
                    try
                    {
                        var wsTable = doc.GetWorksetTable();
                        var viewWsId = view.WorksetId;
                        if (viewWsId != WorksetId.InvalidWorksetId)
                        {
                            var ws = wsTable.GetWorkset(viewWsId);
                            if (!ws.IsEditable)
                            {
                                result.Warning = $"View workset '{ws.Name}' is not editable. " +
                                    "Markers will be placed on the active workset.";
                            }
                        }
                    }
                    catch { }
                }

                // (Image overlay verification was already done above —
                //  before the AP-count check — so the user always sees the
                //  overlay even when this floor has zero APs.  Re-using
                //  the captured overlay IDs here.)
                var overlayIds = overlayIds_pre;

                // ── Tier 3a: two-point manual calibration (optional) ─
                //   When the floor plan still has no anchor (no
                //   revitAnchor in the .esx and no .ekahau-cal.json),
                //   offer the user the chance to calibrate by clicking
                //   two reference points in the Revit view and entering
                //   their corresponding Ekahau coordinates.  If they
                //   accept and complete it, we synthesise an anchor and
                //   rebuild the xform so AP markers below land on the
                //   right spots.  If they skip, the existing Mode 3
                //   CropBox-fill fallback in BuildEkahauToRevitXform
                //   takes over.
                if (fp.RevitAnchor == null)
                {
                    var manualAnchor = OfferTwoPointCalibration(
                        uiDoc, view, fp, esxData, progress);
                    if (manualAnchor != null)
                    {
                        fp.RevitAnchor = manualAnchor;
                        try { xform = EsxCoordXform.BuildEkahauToRevitXform(fp, view, doc); }
                        catch (Exception ex)
                        {
                            result.Warning = $"Manual calibration applied but xform rebuild failed: {ex.Message}";
                        }
                    }
                }

                // ── Place AP markers ──────────────────────────────────
                progress.Update($"Processing: {fp.Name}",
                    $"Placing {apsToPlace.Count} AP markers...");
                DoEvents();

                double markerRadius = EsxMarkerOps.GetAdaptiveRadius(view);

                // Collect band info for legend
                var bandsSeen = new HashSet<string>();

                // Staging data for AP Place
                //   AccessPoints      ← per-AP MarkerElementIds  (filled below)
                //   OverlayElementIds ← image overlay + corner crosses + legend
                //                       (so AP Place can clean them up alongside
                //                        the AP markers after placement)
                var stagingFloor = new ApStagingFloor
                {
                    FloorPlanName     = fp.Name,
                    ViewName          = view.Name,
                    ViewId            = VersionCompat.GetIdValue(view.Id),
                    OverlayElementIds = overlayIds
                        .Select(eid => VersionCompat.GetIdValue(eid))
                        .ToList(),
                };

                using (var tx = new Transaction(doc, $"ESX Read — Place APs on {view.Name}"))
                {
                    try
                    {
                        tx.Start();

                        int apIdx = 0;
                        foreach (var ap in apsToPlace)
                        {
                            try
                            {
                                var (wx, wy) = xform(ap.PixelX, ap.PixelY);

                                // Diagnostic dump for the first 3 APs only
                                // (avoid flooding DebugView with 360+ lines).
                                if (apIdx < 3)
                                {
                                    Debug.WriteLine(
                                        $"[ESX Read] AP placement #{apIdx}: " +
                                        $"name='{ap.Name}', " +
                                        $"input pixel=({ap.PixelX:F2}, {ap.PixelY:F2}), " +
                                        $"world=({wx:F3}, {wy:F3}) ft");
                                }
                                apIdx++;

                                string bandStr = EsxMarkerOps.FormatBands(ap.Bands);
                                var    color   = EsxMarkerOps.GetBandColor(ap.Bands);

                                var ids = EsxMarkerOps.PlaceApMarker(
                                    doc, view, wx, wy,
                                    ap.Name, bandStr, color, markerRadius);

                                allCreatedIds.AddRange(ids);
                                result.ApsPlaced++;
                                totalPlaced++;

                                foreach (var b in ap.Bands)
                                    bandsSeen.Add(b);

                                // Track for AP Place staging
                                stagingFloor.AccessPoints.Add(new ApStagingEntry
                                {
                                    Id               = ap.Id,
                                    Name             = ap.Name,
                                    WorldX           = wx,
                                    WorldY           = wy,
                                    MountingHeight   = ap.MountingHeight,
                                    Vendor           = ap.Vendor,
                                    Model            = ap.Model,
                                    Bands            = new List<string>(ap.Bands),
                                    Tags             = new List<string>(ap.Tags),
                                    Mounting         = ap.Mounting,
                                    BandsSummary     = ap.BandsSummary,
                                    Technology       = ap.Technology,
                                    TxPowerSummary   = ap.TxPowerSummary,
                                    ChannelsSummary  = ap.ChannelsSummary,
                                    StreamsSummary   = ap.StreamsSummary,
                                    AntennaInfo      = ap.AntennaInfo,
                                    MarkerElementIds = ids.Select(eid => VersionCompat.GetIdValue(eid)).ToList(),
                                });
                            }
                            catch
                            {
                                result.ApsSkipped++;
                                totalSkipped++;
                            }
                        }

                        // REQ 16: Place legend
                        if (bandsSeen.Count > 0)
                        {
                            var legendItems = new List<(string, Color)>();
                            if (bandsSeen.Contains("TWO"))
                                legendItems.Add(("2.4 GHz", new Color(21, 101, 192)));
                            if (bandsSeen.Contains("FIVE"))
                                legendItems.Add(("5 GHz", new Color(46, 125, 50)));
                            if (bandsSeen.Contains("SIX"))
                                legendItems.Add(("6 GHz", new Color(230, 81, 0)));
                            if (bandsSeen.Count > 1)
                                legendItems.Add(("Multi-band", new Color(106, 27, 154)));

                            var legendIds = EsxMarkerOps.PlaceLegend(
                                doc, view, legendItems);
                            allCreatedIds.AddRange(legendIds);
                            stagingFloor.OverlayElementIds.AddRange(
                                legendIds.Select(eid => VersionCompat.GetIdValue(eid)));
                        }

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        result.Warning = $"Transaction failed: {ex.Message}";
                    }
                }

                if (stagingFloor.AccessPoints.Count > 0)
                    stagingFloors.Add(stagingFloor);

                floorResults.Add(result);
            }

            progress.Close();

            // ── 8. Save AP staging JSON for AP Place ──────────────────
            try
            {
                var staging = new ApStagingData
                {
                    ProjectName     = esxData.ProjectName,
                    ProjectPathHash = RevitHelpers.GetProjectPathHash(doc),
                    EsxFilePath     = esxPath,
                    Timestamp       = DateTime.Now.ToString("o"),
                    Floors          = stagingFloors,
                };
                string jsonPath = RevitHelpers.GetStagingJsonPath(doc);
                var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(jsonPath,
                    JsonSerializer.Serialize(staging, jsonOpts));
            }
            catch { /* non-critical */ }

            // ── 9. Save floor plan images to staging (REQ 21) ─────────
            //   Use the same lookup as the placement path so we save the
            //   raster companion (when present) instead of the SVG that
            //   downstream tools wouldn't be able to render either.
            try
            {
                foreach (var fp in selectedFloorPlans)
                {
                    var imgBytes = LookupImageBytes(esxData, fp);
                    if (imgBytes != null && imgBytes.Length > 0)
                    {
                        string safe = SanitizeFileName(fp.Name);
                        // Match staging-file extension to the actual raster
                        // format so downstream tools dispatch correctly too.
                        string ext = ImageNormalizer.DetectExtension(imgBytes);
                        string imgPath = Path.Combine(stagingDir, $"floor_{safe}{ext}");
                        File.WriteAllBytes(imgPath, imgBytes);
                    }
                }
            }
            catch { /* non-critical */ }

            // ── 9. REQ 17: Summary dialog ─────────────────────────────
            var summaryDlg = new EsxReadSummaryDialog(
                esxData.ProjectName, esxPath,
                floorResults, totalPlaced, totalSkipped,
                stagingDir);
            summaryDlg.ShowDialog();

            return Result.Succeeded;
        }

        // ── REQ 11: Auto-match ESX floor plan name to Revit view ──────

        private static ViewPlan AutoMatchView(string esxName, List<ViewPlan> views)
        {
            if (string.IsNullOrEmpty(esxName)) return null;

            // Exact match
            var exact = views.FirstOrDefault(v => v.Name == esxName);
            if (exact != null) return exact;

            // Case-insensitive exact match
            var ci = views.FirstOrDefault(v =>
                v.Name.Equals(esxName, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci;

            // Contains match (unique)
            var contains = views.Where(v =>
                v.Name.Contains(esxName, StringComparison.OrdinalIgnoreCase) ||
                esxName.Contains(v.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (contains.Count == 1) return contains[0];

            return null;
        }

        // ──────────────────────────────────────────────────────────────
        //  Mandatory floor-plan overlay verification step
        //  Always places the .esx PNG into the matched view as a reference
        //  overlay + draws CropBox corner crosses, then asks the user to
        //  visually confirm alignment before committing AP markers.
        //
        //  Returns the IDs of every element it created (image + crosses),
        //  to be added to allCreatedIds so the next ESX Read run cleans
        //  them up.  Sets <paramref name="userAborted"/> when the user
        //  reports a misalignment.
        // ──────────────────────────────────────────────────────────────

        private static List<ElementId> PlaceImageAndAskForVerification(
            Document doc, UIDocument uiDoc, ViewPlan view,
            EsxFloorPlanData fp, EsxReadResult esxData,
            EsxReadProgressWindow progress, EsxReadFloorResult result,
            out bool userAborted)
        {
            userAborted = false;
            var created = new List<ElementId>();

            // ── 1. Locate the image bytes for this floor plan ──
            //   Robust lookup — different Ekahau exports use slightly
            //   different naming for the image entries inside the .esx
            //   ZIP.  Try in order:
            //     a) exact match on fp.ImageId
            //     b) fp.ImageId + common image extensions
            //     c) fuzzy match (any key starting with fp.ImageId)
            //     d) single-image fallback (when there's exactly one
            //        image entry, use it regardless of name)
            byte[] imgBytes = LookupImageBytes(esxData, fp);
            Debug.WriteLine(
                $"[ESX Read] Image lookup for '{fp.Name}': " +
                $"fp.ImageId='{fp.ImageId}', " +
                $"matched={(imgBytes != null ? "YES" : "NO")}, " +
                $"bytes={(imgBytes?.Length ?? 0)}, " +
                $"available={esxData.ImageEntries.Count} keys=[" +
                string.Join(", ", esxData.ImageEntries.Keys.Take(5)) + "]");

            if (imgBytes == null || imgBytes.Length == 0)
            {
                // No image data — show a DIAGNOSTIC dialog so the user can
                // see exactly what was looked up and what's available.
                progress.Hide();
                string availableKeys = esxData.ImageEntries.Count > 0
                    ? "  • " + string.Join("\n  • ",
                        esxData.ImageEntries.Keys.Take(10))
                    : "  (zero image entries)";
                if (esxData.ImageEntries.Count > 10)
                    availableKeys += $"\n  (… {esxData.ImageEntries.Count - 10} more)";

                TaskDialog.Show("ESX Read — No Image",
                    $"Could not extract a floor-plan image for '{fp.Name}'.\n\n" +
                    "DIAGNOSTIC INFO:\n" +
                    $"  Floor plan name : {fp.Name}\n" +
                    $"  Floor plan ID   : {fp.Id}\n" +
                    $"  Looking for image with ID:\n    '{fp.ImageId}'\n" +
                    $"  Total image entries in .esx : {esxData.ImageEntries.Count}\n\n" +
                    $"Available image keys in .esx:\n{availableKeys}\n\n" +
                    "Likely causes:\n" +
                    "  • The .esx was saved without floor-plan rasters\n" +
                    "  • The image entry naming differs from 'image-{id}'\n" +
                    "  • The floorPlans.json imageId doesn't match any image\n\n" +
                    "Visual alignment verification is not available.\n" +
                    "AP markers will be placed using the staged coordinate " +
                    "transform without an overlay reference.\n\n" +
                    "Please screenshot this dialog when reporting the issue.");
                progress.Show();
                DoEvents();
                result.Warning = string.IsNullOrEmpty(result.Warning)
                    ? "No floor-plan image — alignment not verified."
                    : result.Warning + " | No floor-plan image — alignment not verified.";
                return created;
            }

            // SVG normalisation — extract embedded raster if needed.
            var norm = ImageNormalizer.NormalizeIfSvg(imgBytes);
            if (norm.WasSvg && !norm.ExtractionSucceeded)
            {
                progress.Hide();
                TaskDialog.Show("ESX Read — SVG Floor Plan Detected",
                    $"The floor plan image for '{fp.Name}' is stored as an SVG " +
                    "in the .esx file, but it doesn't contain an embedded raster " +
                    "image that the plugin can extract.\n\n" +
                    "Workaround:\n" +
                    "  1. Open the .esx in Ekahau Pro\n" +
                    "  2. Go to Project → Properties → Floor Plans\n" +
                    "  3. Re-save the floor plan with PNG output enabled\n" +
                    "  4. Save the .esx and try ESX Read again\n\n" +
                    "Visual alignment verification is not available for this floor.");
                progress.Show();
                DoEvents();
                result.Warning = "Floor plan is SVG without embedded raster — alignment skipped.";
                return created;
            }
            imgBytes = norm.Bytes;
            if (norm.WasSvg)
                Debug.WriteLine($"[ESX Read] SVG detected, embedded raster extracted ({imgBytes.Length:N0} bytes).");

            // ── 2. Write image to a temp file (ImageType.Create takes a path) ──
            //   Re-encode through WPF/WIC as a clean baseline PNG so
            //   Revit's import path doesn't silently reject the file
            //   (see v2.5.16 — even valid JPEGs trip this).
            byte[] normalized = ImageNormalizer.NormalizeForRevit(
                imgBytes, out string normDetail);
            Debug.WriteLine($"[ESX Read] WIC re-encode: {normDetail}");
            if (normalized != null && normalized.Length > 100)
                imgBytes = normalized;

            //   Match the file extension to the actual raster format —
            //   Revit's WIC dispatch is extension-driven.  After the
            //   normalisation above this should always be .png.
            string ext = ImageNormalizer.DetectExtension(imgBytes);
            string imgPath = Path.Combine(
                Path.GetTempPath(),
                $"EkahauRead_{Guid.NewGuid():N}{ext}");
            Debug.WriteLine($"[ESX Read] Temp image: {imgPath} ({imgBytes.Length:N0} bytes, ext={ext})");
            try
            {
                File.WriteAllBytes(imgPath, imgBytes);
            }
            catch
            {
                result.Warning = "Could not stage image file for verification.";
                return created;
            }

            // ── 3. Place image + reference crosses (single transaction) ──
            ElementId imageInstanceId = null;
            try
            {
                using var tx = new Transaction(doc, "ESX Read — Place verification overlay");
                tx.Start();

                imageInstanceId = EsxMarkerOps.PlaceFloorPlanImage(doc, view, imgPath, fp);
                if (imageInstanceId != null) created.Add(imageInstanceId);

                var crossIds = EsxMarkerOps.DrawReferenceCrosses(doc, view, fp);
                created.AddRange(crossIds);

                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ESX Read] Overlay placement failed for '{fp.Name}': {ex.Message}");
                result.Warning = "Could not place image overlay (continuing without verification).";
                TryDeleteFile(imgPath);
                return created;
            }

            // ── 4. Switch to the matched view + refresh so the user sees it ──
            try
            {
                uiDoc.ActiveView = view;
                try { uiDoc.RefreshActiveView(); } catch { }
                DoEvents();
            }
            catch { }

            // ── 5. Verification dialog ──
            progress.Hide();
            try
            {
                var verify = new TaskDialog("ESX Read — Verify Alignment")
                {
                    MainInstruction = $"Verify floor-plan alignment for \"{fp.Name}\"",
                    MainContent =
                        "The Ekahau floor plan image has been placed as a reference overlay " +
                        "and four green corner crosses mark the CropBox bounds.\n\n" +
                        "Please check:\n" +
                        "  \u2022 Do the Ekahau image walls line up with the Revit model walls?\n" +
                        "  \u2022 Are the green corner crosses at the CropBox edges?\n" +
                        "  \u2022 Does the overall scale look correct?\n\n" +
                        "If the image is misaligned, the AP positions will also be wrong. " +
                        "In that case, abort and re-export from Revit (ESX Export).",
                };
                verify.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Alignment looks correct — continue",
                    "Proceed to place the AP crosshair markers in this view");
                verify.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                    "Image is misaligned — manually align with two points",
                    "Click 4 points (2 pairs) to fix position + scale + rotation. " +
                    "Available even when revitAnchor exists but is wrong.");
                verify.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                    "Skip verification — continue anyway",
                    "Place AP markers without confirming alignment (advanced)");
                verify.CommonButtons = TaskDialogCommonButtons.Cancel; // Cancel = abort floor
                verify.DefaultButton = TaskDialogResult.CommandLink1;

                var resp = verify.Show();

                if (resp == TaskDialogResult.CommandLink2)
                {
                    // User wants to manually re-align.  Delete the current
                    // overlay (we'll re-place at the calibrated pose), run
                    // visual alignment, then RECURSE so the user gets a
                    // fresh image at the corrected position + a verification
                    // dialog they can either accept or re-align again.
                    Debug.WriteLine("[ESX Read] User clicked 'Manually align' — invoking visual alignment");
                    DeleteOverlayElements(doc, created, "ESX Read — pre-align cleanup");
                    created.Clear();
                    TryDeleteFile(imgPath);

                    EsxRevitAnchorData newAnchor = null;
                    try
                    {
                        newAnchor = OfferVisualAlignmentCore(
                            uiDoc, view, fp, esxData, skipIntro: true);
                        Debug.WriteLine($"[ESX Read] OfferVisualAlignmentCore returned: " +
                            (newAnchor != null ? "anchor (success)" : "null (cancelled or error)"));
                    }
                    catch (Exception exVis)
                    {
                        Debug.WriteLine(
                            $"[ESX Read] Visual alignment threw: {exVis.GetType().Name}: {exVis.Message}");
                        try
                        {
                            TaskDialog.Show("Visual Alignment — Error",
                                "Visual alignment threw an unexpected error:\n\n" +
                                $"{exVis.GetType().Name}: {exVis.Message}\n\n" +
                                "Please screenshot this dialog when reporting the issue.\n" +
                                "AP markers will not be placed for this floor.");
                        }
                        catch { }
                    }

                    if (newAnchor != null)
                    {
                        fp.RevitAnchor = newAnchor;
                        try { progress.Show(); DoEvents(); } catch { }
                        return PlaceImageAndAskForVerification(
                            doc, uiDoc, view, fp, esxData, progress, result, out userAborted);
                    }

                    // Alignment cancelled or failed — treat as floor abort
                    userAborted = true;
                    result.Warning = "Manual alignment did not complete.";
                    return new List<ElementId>();
                }
                if (resp == TaskDialogResult.Cancel || resp == TaskDialogResult.Close)
                {
                    // Explicit abort
                    userAborted = true;
                    DeleteOverlayElements(doc, created, "ESX Read — Remove verification overlay");
                    created.Clear();

                    result.Warning = "Aborted — alignment did not match.";
                    TaskDialog.Show("ESX Read — Aborted",
                        $"AP placement aborted for floor '{fp.Name}'.\n\n" +
                        "Troubleshooting:\n" +
                        "  1. Re-run ESX Export with this view\n" +
                        "  2. Confirm the view's CropBox hasn't changed since export\n" +
                        "  3. Check the view is not rotated\n" +
                        "  4. Verify the .esx hasn't been edited in Ekahau Pro\n\n" +
                        "After re-exporting, run ESX Read again.");
                }
                // CommandLink1 (continue) or CommandLink3 (skip) — keep
                // the overlay; it serves as a reference while the user
                // works with the placed AP markers afterwards.
            }
            finally
            {
                progress.Show();
                DoEvents();
                TryDeleteFile(imgPath);
            }

            return created;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        /// <summary>
        /// Read image dimensions WITHOUT going through GDI+ /
        /// System.Drawing.Image.FromFile.  GDI+ throws a misleading
        /// OutOfMemoryException for any image format it doesn't fully
        /// understand (16-bit PNG, exotic interlace, certain palette
        /// variants — Ekahau's PNGs trip this regularly).
        ///
        /// We parse the PNG IHDR chunk directly:
        ///   bytes  0..7   : PNG signature 89 50 4E 47 0D 0A 1A 0A
        ///   bytes  8..11  : IHDR chunk length (always 13)
        ///   bytes 12..15  : IHDR chunk type "IHDR"
        ///   bytes 16..19  : Width  (big-endian 32-bit)
        ///   bytes 20..23  : Height (big-endian 32-bit)
        ///
        /// Falls back to GDI+ for non-PNG formats (JPEG/BMP/etc).
        /// Returns (0, 0) when neither path succeeds.
        /// </summary>
        internal static (int Width, int Height) ReadImageDimensions(string path)
        {
            // ── 1. PNG fast path (manual header parse, no dependencies) ──
            try
            {
                byte[] header = new byte[24];
                using (var fs = File.OpenRead(path))
                {
                    int n = 0;
                    while (n < 24)
                    {
                        int r = fs.Read(header, n, 24 - n);
                        if (r <= 0) break;
                        n += r;
                    }
                    if (n >= 24 &&
                        header[0] == 0x89 && header[1] == 0x50 &&
                        header[2] == 0x4E && header[3] == 0x47 &&
                        header[4] == 0x0D && header[5] == 0x0A &&
                        header[6] == 0x1A && header[7] == 0x0A)
                    {
                        int w = (header[16] << 24) | (header[17] << 16) |
                                (header[18] <<  8) |  header[19];
                        int h = (header[20] << 24) | (header[21] << 16) |
                                (header[22] <<  8) |  header[23];
                        if (w > 0 && h > 0) return (w, h);
                    }
                }
            }
            catch { /* fall through to WIC */ }

            // ── 2. WIC via WPF BitmapDecoder (same engine Revit uses,
            //   far more permissive than GDI+ — handles JPEG, BMP, TIFF,
            //   GIF, WebP, plus exotic PNG variants).
            try
            {
                using var fs = File.OpenRead(path);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    fs,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat |
                    System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                    System.Windows.Media.Imaging.BitmapCacheOption.None);
                if (decoder.Frames.Count > 0)
                {
                    var f = decoder.Frames[0];
                    if (f.PixelWidth > 0 && f.PixelHeight > 0)
                        return (f.PixelWidth, f.PixelHeight);
                }
            }
            catch { /* fall through to GDI+ */ }

            // ── 3. GDI+ legacy fallback (mostly here for completeness) ──
            try
            {
                using var img = System.Drawing.Image.FromFile(path);
                return (img.Width, img.Height);
            }
            catch { /* all three paths failed */ }

            return (0, 0);
        }

        /// <summary>
        /// Dump the first <paramref name="count"/> bytes of a file as a
        /// hex string for diagnostic purposes (e.g. identifying unknown
        /// image formats by their magic bytes).  Never throws.
        /// </summary>
        internal static string ReadFirstBytesHex(string path, int count = 16)
        {
            try
            {
                byte[] buf = new byte[count];
                using var fs = File.OpenRead(path);
                int read = 0;
                while (read < count)
                {
                    int r = fs.Read(buf, read, count - read);
                    if (r <= 0) break;
                    read += r;
                }
                if (read == 0) return "(file empty)";
                var sb = new StringBuilder(read * 3);
                for (int i = 0; i < read; i++)
                {
                    if (i > 0 && i % 4 == 0) sb.Append(' ');
                    sb.Append(buf[i].ToString("X2"));
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"(read failed: {ex.Message})";
            }
        }

        /// <summary>
        /// Robust lookup of image bytes for a floor plan.
        ///
        /// Order of preference:
        ///   0. fp.BitmapImageId  — Ekahau's pre-rendered raster companion
        ///                          for SVG floor plans.  When the primary
        ///                          `imageId` points at SVG, Ekahau also
        ///                          ships a JPEG/PNG raster identified by
        ///                          `bitmapImageId`; Revit's WIC engine
        ///                          can render that directly, so we try
        ///                          it FIRST and skip the SVG entirely.
        ///   1. fp.ImageId        — exact match
        ///   2. fp.ImageId + ext  — common image extensions
        ///   3. fuzzy             — any key starting with the id
        ///   4. single-image fallback (when exactly one entry exists)
        ///
        /// Returns null when no plausible match is found.
        /// </summary>
        private static byte[] LookupImageBytes(EsxReadResult esxData, EsxFloorPlanData fp)
        {
            if (esxData?.ImageEntries == null) return null;

            byte[] result = null;

            // 0. Prefer the rasterised companion when present.
            if (!string.IsNullOrEmpty(fp?.BitmapImageId))
            {
                if (TryLookupOne(esxData, fp.BitmapImageId, out result))
                {
                    Debug.WriteLine(
                        $"[ESX Read] Using bitmapImageId='{fp.BitmapImageId}' " +
                        $"({result.Length:N0} bytes) instead of imageId='{fp.ImageId}' " +
                        "(SVG → raster companion).");
                    return result;
                }
                Debug.WriteLine(
                    $"[ESX Read] bitmapImageId='{fp.BitmapImageId}' present but no " +
                    "matching entry — falling back to imageId.");
            }

            // 1-3. Same logic as before, but factored into a helper so the
            //      bitmap path above stays small and readable.
            if (!string.IsNullOrEmpty(fp?.ImageId) &&
                TryLookupOne(esxData, fp.ImageId, out result))
                return result;

            // 4. Single-image fallback.
            if (esxData.ImageEntries.Count == 1)
                return esxData.ImageEntries.First().Value;

            return null;
        }

        /// <summary>
        /// Tier 1-3 lookup for a single id (exact / +ext / fuzzy).
        /// Used by <see cref="LookupImageBytes"/> for both ImageId and
        /// BitmapImageId.
        /// </summary>
        private static bool TryLookupOne(
            EsxReadResult esxData, string id, out byte[] result)
        {
            if (esxData.ImageEntries.TryGetValue(id, out result)) return true;
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
            {
                if (esxData.ImageEntries.TryGetValue(id + ext, out result))
                    return true;
            }
            var fuzzy = esxData.ImageEntries.Keys.FirstOrDefault(k =>
                k.StartsWith(id, StringComparison.OrdinalIgnoreCase));
            if (fuzzy != null)
            {
                result = esxData.ImageEntries[fuzzy];
                return true;
            }
            result = null;
            return false;
        }

        /// <summary>
        /// Best-effort delete of a list of overlay element IDs in a single
        /// transaction.  Never throws — used by the verification dialog
        /// when the user requests realignment or abort.
        /// </summary>
        private static void DeleteOverlayElements(
            Document doc, List<ElementId> ids, string txName)
        {
            if (ids == null || ids.Count == 0) return;
            try
            {
                using var tx = new Transaction(doc, txName);
                tx.Start();
                foreach (var id in ids)
                {
                    try { doc.Delete(id); } catch { }
                }
                tx.Commit();
            }
            catch { }
        }

        // ──────────────────────────────────────────────────────────────
        //  Tier 3a: Two-Point Manual Calibration
        //
        //  When a floor plan has no revitAnchor and no .ekahau-cal.json,
        //  let the user pick two reference points in the Revit view and
        //  enter their corresponding Ekahau coordinates.  We compute a
        //  scale + translation transform from those two correspondences
        //  and synthesise an EsxRevitAnchorData so the rest of the ESX
        //  Read pipeline works unchanged.
        //
        //  Returns null when the user skips, cancels, or any step fails.
        // ──────────────────────────────────────────────────────────────

        private static EsxRevitAnchorData OfferTwoPointCalibration(
            UIDocument uiDoc, ViewPlan view, EsxFloorPlanData fp,
            EsxReadResult esxData,
            EsxReadProgressWindow progress)
        {
            // Hide the progress popup so the user can interact with Revit
            try { progress.Hide(); DoEvents(); } catch { }

            try
            {
                // ── 1. Intro / opt-in dialog ──
                var intro = new TaskDialog("ESX Read — Manual Calibration")
                {
                    MainInstruction = $"No coordinate calibration is available for \"{fp.Name}\".",
                    MainContent =
                        "The .esx file has no Revit anchor and no DWG calibration was found.\n\n" +
                        "You can calibrate manually by clicking two reference points in the " +
                        "Revit view and typing their corresponding Ekahau coordinates.\n\n" +
                        "Good reference points:\n" +
                        "  \u2022 Building corners\n" +
                        "  \u2022 Column intersections\n" +
                        "  \u2022 Stair-core corners\n" +
                        "  \u2022 Any two points as far apart as possible\n\n" +
                        "TIP — Reading Ekahau coordinates:\n" +
                        "  1. Open the same project in Ekahau Pro\n" +
                        "  2. Hover over the reference point on the floor plan\n" +
                        "  3. Read the X / Y values (in metres) from the bottom status bar",
                };
                // Bug Fix #16: only one calibration option remains —
                // visual alignment.  The "type Ekahau coordinates" path
                // was removed because users have no reliable way to look
                // up Ekahau pixel/metre values for arbitrary reference
                // points.  All clicks now happen in the Revit view.
                intro.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Start visual alignment",
                    "Drops the Ekahau floor plan image into the view; you click two " +
                    "pairs of matching points (each point clicked once on the Revit " +
                    "model, once on the same spot on the image).  Handles scale + " +
                    "rotation + translation automatically.  No coordinate typing.");
                intro.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                    "Skip — use approximate placement",
                    "AP markers will be placed using the current view's CropBox " +
                    "(may be off by several feet).");
                intro.CommonButtons = TaskDialogCommonButtons.None;
                intro.DefaultButton = TaskDialogResult.CommandLink1;

                if (intro.Show() != TaskDialogResult.CommandLink1) return null;
                return OfferVisualAlignmentCore(uiDoc, view, fp, esxData);
            }
            finally
            {
                try { progress.Show(); DoEvents(); } catch { }
            }
        }


        // ══════════════════════════════════════════════════════════════
        //  Tier 3b: Visual Alignment Calibration
        //
        //  When the .esx has no revitAnchor and no .ekahau-cal.json (e.g.
        //  designer worked from a PDF in Ekahau, never touched Revit), we
        //  drop the Ekahau image into the view at an estimated position
        //  and ask the user to pick TWO point pairs:
        //
        //      Pair 1:  click a point on the REVIT MODEL
        //               click the SAME point on the EKAHAU IMAGE
        //      Pair 2:  same, but at a different location far from Pair 1
        //
        //  From those two correspondences we recover scale + rotation +
        //  translation, then re-place the image at the calibrated pose
        //  for visual confirmation.  Far more intuitive than typing
        //  Ekahau pixel/metre coordinates from another window.
        //
        //  The synthesised anchor uses HasTransform=true so the existing
        //  Mode 1 BuildEkahauToRevitXform handles the rotation correctly.
        // ══════════════════════════════════════════════════════════════

        private static EsxRevitAnchorData OfferVisualAlignment(
            UIDocument uiDoc, ViewPlan view, EsxFloorPlanData fp,
            EsxReadResult esxData, EsxReadProgressWindow progress)
        {
            try { progress.Hide(); DoEvents(); } catch { }

            try
            {
                return OfferVisualAlignmentCore(uiDoc, view, fp, esxData);
            }
            finally
            {
                try { progress.Show(); DoEvents(); } catch { }
            }
        }

        private static EsxRevitAnchorData OfferVisualAlignmentCore(
            UIDocument uiDoc, ViewPlan view, EsxFloorPlanData fp,
            EsxReadResult esxData,
            bool skipIntro = false)
        {
            try
            {
                return OfferVisualAlignmentCoreImpl(uiDoc, view, fp, esxData, skipIntro);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                Debug.WriteLine("[ESX Read] Visual alignment cancelled by user (Esc).");
                return null;
            }
            catch (Exception ex)
            {
                // SURFACE every previously-silent failure so the user
                // can report what's wrong instead of seeing "0 APs placed".
                Debug.WriteLine(
                    $"[ESX Read] Visual alignment unhandled exception: " +
                    $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
                try
                {
                    TaskDialog.Show("Visual Alignment — Error",
                        "Visual alignment failed with an unexpected error:\n\n" +
                        $"{ex.GetType().Name}: {ex.Message}\n\n" +
                        "AP markers will not be placed for this floor.\n" +
                        "Please screenshot this dialog when reporting the issue.");
                }
                catch { }
                return null;
            }
        }

        private static EsxRevitAnchorData OfferVisualAlignmentCoreImpl(
            UIDocument uiDoc, ViewPlan view, EsxFloorPlanData fp,
            EsxReadResult esxData,
            bool skipIntro)
        {
            Document doc = view.Document;
            Debug.WriteLine($"[ESX Read] OfferVisualAlignmentCore: start (skipIntro={skipIntro}, fp='{fp?.Name}')");

            // ── 1. Image bytes → temp file ──────────────────────────
            //   Same robust lookup as PlaceImageAndAskForVerification:
            //   exact match → +ext → fuzzy → single-image fallback.
            byte[] imgBytes = LookupImageBytes(esxData, fp);
            if (imgBytes == null || imgBytes.Length < 100)
            {
                TaskDialog.Show("Visual Alignment",
                    "The .esx has no usable floor-plan image — visual alignment can't run.\n\n" +
                    $"Looked up image ID '{fp.ImageId}' in {esxData.ImageEntries.Count} entries.");
                return null;
            }

            // ── 1b. SVG normalisation (some .esx files store floor plans
            //   as SVG wrappers around an embedded base64 raster).  When
            //   SVG is detected, extract the embedded raster.
            var norm = ImageNormalizer.NormalizeIfSvg(imgBytes);
            if (norm.WasSvg && !norm.ExtractionSucceeded)
            {
                throw new InvalidOperationException(
                    "The Ekahau floor plan is stored as an SVG without an " +
                    "embedded raster — the plugin can't render arbitrary SVG " +
                    "content yet.  In Ekahau Pro, open Project → Properties → " +
                    "Floor Plans and re-save the floor plan with PNG output, " +
                    "then re-export the .esx.");
            }
            imgBytes = norm.Bytes;
            if (norm.WasSvg)
                Debug.WriteLine($"[ESX Read] SVG detected, embedded raster extracted ({imgBytes.Length:N0} bytes).");

            // Re-encode through WPF/WIC as a clean baseline PNG.  Even
            // valid JPEGs can make Revit's ImageType.Create return NULL
            // silently (Autodesk-confirmed: "JPEG response data from
            // certain sources may not be readable… while PNG or BMP has
            // no issue with the same code").  Round-tripping through
            // the same WIC engine Revit uses produces a vanilla PNG that
            // Revit reliably accepts, and gives us an explicit failure
            // diagnostic if WIC itself can't decode the input.
            byte[] normalized = ImageNormalizer.NormalizeForRevit(
                imgBytes, out string normDetail);
            Debug.WriteLine($"[ESX Read] WIC re-encode: {normDetail}");
            if (normalized != null && normalized.Length > 100)
                imgBytes = normalized;

            // Pick the temp-file extension to match the actual raster
            // format — Revit's ImageType.Create dispatches its WIC
            // decoder by extension, so a JPEG inside a .png-named file
            // silently returns null (v2.5.14 symptom for SVG-companion
            // exports).  After NormalizeForRevit this should always be
            // .png, but DetectExtension is harmless either way.
            string ext = ImageNormalizer.DetectExtension(imgBytes);
            string imgPath = Path.Combine(
                Path.GetTempPath(),
                $"EkahauVisCal_{Guid.NewGuid():N}{ext}");
            Debug.WriteLine($"[ESX Read] Temp image: {imgPath} ({imgBytes.Length:N0} bytes, ext={ext})");
            try { File.WriteAllBytes(imgPath, imgBytes); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not write temp image to '{imgPath}': {ex.Message}", ex);
            }

            //   Tries (in order): manual PNG header parse → WPF/WIC
            //   BitmapDecoder → GDI+.  All three paths must fail before
            //   we throw — and we include the file's first 16 bytes in
            //   the error so the format can be identified at a glance.
            var (imgPxW, imgPxH) = ReadImageDimensions(imgPath);
            if (imgPxW <= 0 || imgPxH <= 0)
            {
                long fileSize = 0;
                try { fileSize = new FileInfo(imgPath).Length; } catch { }
                string hex = ReadFirstBytesHex(imgPath, 16);
                TryDeleteFile(imgPath);
                throw new InvalidOperationException(
                    $"Could not determine image dimensions.\n\n" +
                    $"File size : {fileSize:N0} bytes\n" +
                    $"First 16 bytes (hex):\n  {hex}\n\n" +
                    "Common signatures for reference:\n" +
                    "  PNG  = 89 50 4E 47 0D 0A 1A 0A\n" +
                    "  JPEG = FF D8 FF\n" +
                    "  BMP  = 42 4D\n" +
                    "  GIF  = 47 49 46 38\n" +
                    "  TIFF = 49 49 2A 00 (LE) or 4D 4D 00 2A (BE)\n" +
                    "  WebP = 52 49 46 46 ... 57 45 42 50\n\n" +
                    "Send this dialog as a screenshot when reporting the issue.");
            }

            // ── 2. Initial placement at CropBox centre ──────────────
            double mpu = fp.MetersPerUnit > 0 ? fp.MetersPerUnit : 0.0264583;
            double initFtPerPx = mpu / 0.3048;
            double initWidthFt  = imgPxW * initFtPerPx;
            double initHeightFt = imgPxH * initFtPerPx;

            // Compute CropBox world-space centre (handle rotated views)
            var cb = view.CropBox;
            var t  = cb.Transform;
            var c0 = t.OfPoint(new XYZ(cb.Min.X, cb.Min.Y, 0));
            var c1 = t.OfPoint(new XYZ(cb.Max.X, cb.Min.Y, 0));
            var c2 = t.OfPoint(new XYZ(cb.Max.X, cb.Max.Y, 0));
            var c3 = t.OfPoint(new XYZ(cb.Min.X, cb.Max.Y, 0));
            double initCenterX = (c0.X + c1.X + c2.X + c3.X) / 4.0;
            double initCenterY = (c0.Y + c1.Y + c2.Y + c3.Y) / 4.0;
            double zElev = view.GenLevel?.Elevation ?? 0;

            Debug.WriteLine($"[ESX Read] About to place initial image: " +
                $"center=({initCenterX:F2},{initCenterY:F2},{zElev:F2}) ft, " +
                $"size=({initWidthFt:F2}x{initHeightFt:F2}) ft, " +
                $"image=({imgPxW}x{imgPxH}) px");

            ElementId initImgId = null;
            try
            {
                using var tx = new Transaction(doc, "Visual Cal — initial image");
                tx.Start();
                var imgType = VersionCompat.CreateImageType(
                    doc, imgPath, out var imgTypeErr, out var strategyTrace);
                if (imgType == null)
                {
                    tx.RollBack();

                    // Capture diagnostics BEFORE deleting the temp file so
                    // the user can see exactly what bytes Revit refused.
                    long fileSize = 0;
                    try { fileSize = new FileInfo(imgPath).Length; } catch { }
                    string hex = ReadFirstBytesHex(imgPath, 16);

                    TryDeleteFile(imgPath);

                    string detail = imgTypeErr != null
                        ? $"{imgTypeErr.GetType().Name}: {imgTypeErr.Message}"
                        : "(no inner exception captured)";

                    throw new InvalidOperationException(
                        "VersionCompat.CreateImageType returned null after " +
                        "trying every fallback strategy.\n\n" +
                        $"Strategies tried:\n{strategyTrace}\n" +
                        $"Last error   : {detail}\n\n" +
                        $"Temp file    : {imgPath}\n" +
                        $"File size    : {fileSize:N0} bytes\n" +
                        $"First 16 hex :\n  {hex}\n\n" +
                        "Common signatures:\n" +
                        "  PNG  = 89 50 4E 47 0D 0A 1A 0A\n" +
                        "  JPEG = FF D8 FF\n" +
                        "  BMP  = 42 4D\n" +
                        "  GIF  = 47 49 46 38\n" +
                        "  TIFF = 49 49 2A 00 (LE) or 4D 4D 00 2A (BE)\n" +
                        "  WebP = 52 49 46 46 ... 57 45 42 50\n\n" +
                        "Send this dialog as a screenshot when reporting the issue.");
                }
                var opts = new ImagePlacementOptions(
                    new XYZ(initCenterX, initCenterY, zElev), BoxPlacement.Center);
                var inst = ImageInstance.Create(doc, view, imgType.Id, opts);
                try { inst.Width = initWidthFt; }
                catch
                {
                    try
                    {
                        var p = inst.get_Parameter(BuiltInParameter.RASTER_SHEETWIDTH);
                        if (p != null && !p.IsReadOnly) p.Set(initWidthFt);
                    }
                    catch { }
                }
                initImgId = inst.Id;
                tx.Commit();
                Debug.WriteLine($"[ESX Read] Initial image placed, ElementId={VersionCompat.GetIdValue(initImgId)}");
            }
            catch (Exception ex)
            {
                TryDeleteFile(imgPath);
                throw new InvalidOperationException(
                    $"Could not place initial image overlay: {ex.Message}", ex);
                return null;
            }

            // Make sure the image is visible to the user
            try { uiDoc.ActiveView = view; uiDoc.RefreshActiveView(); DoEvents(); } catch { }

            // ── 3. Intro dialog (skipped when called from verification
            //   step where the user has already opted in via "Manually
            //   align" — no need to nag them with a second confirmation).
            if (skipIntro) goto pickPoints;

            var intro = new TaskDialog("ESX Read — Visual Alignment")
            {
                MainInstruction = $"Visually align the floor plan for \"{fp.Name}\"",
                MainContent =
                    "The Ekahau floor-plan image has been placed in the view at an " +
                    "estimated position.  It is NOT correctly aligned yet — that's " +
                    "expected.\n\n" +
                    "You'll now pick TWO pairs of matching points:\n\n" +
                    "  PAIR 1  — first click a point on the REVIT MODEL,\n" +
                    "            then click the SAME point on the EKAHAU IMAGE.\n" +
                    "  PAIR 2  — same again, far from Pair 1.\n\n" +
                    "Tips:\n" +
                    "  • Choose two points as far apart as possible.\n" +
                    "  • Use easy-to-identify features (building corners, columns, " +
                    "stair cores).\n" +
                    "  • Zoom in for accuracy on each click.\n\n" +
                    "After both pairs are picked, the image will snap into alignment " +
                    "and AP positions will be mapped correctly.",
            };
            intro.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Start alignment — pick Pair 1");
            intro.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Cancel");
            intro.DefaultButton = TaskDialogResult.CommandLink1;

            if (intro.Show() != TaskDialogResult.CommandLink1)
            {
                DeleteImageInTx(doc, initImgId, "Visual Cal — cancel");
                TryDeleteFile(imgPath);
                return null;
            }

            pickPoints:
            // ── 4. Pick 4 points (2 pairs) ──────────────────────────
            XYZ modelPt1, imagePt1, modelPt2, imagePt2;
            try
            {
                modelPt1 = uiDoc.Selection.PickPoint(
                    "PAIR 1 / step 1 of 2 — click point on the REVIT MODEL");
                imagePt1 = uiDoc.Selection.PickPoint(
                    "PAIR 1 / step 2 of 2 — click the SAME point on the EKAHAU IMAGE");

                TaskDialog.Show("Pair 1 captured",
                    "Now pick PAIR 2.  Choose a point as FAR from Pair 1 as possible " +
                    "for the most accurate scale + rotation.");

                modelPt2 = uiDoc.Selection.PickPoint(
                    "PAIR 2 / step 1 of 2 — click point on the REVIT MODEL");
                imagePt2 = uiDoc.Selection.PickPoint(
                    "PAIR 2 / step 2 of 2 — click the SAME point on the EKAHAU IMAGE");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                DeleteImageInTx(doc, initImgId, "Visual Cal — cancel");
                TryDeleteFile(imgPath);
                return null;
            }
            catch
            {
                DeleteImageInTx(doc, initImgId, "Visual Cal — cancel");
                TryDeleteFile(imgPath);
                return null;
            }

            // ── 5. Convert image-clicks (Revit world) → Ekahau pixels ──
            // The initial image was placed at (initCenterX, initCenterY)
            // with width = initWidthFt, height = initHeightFt, no rotation.
            //   imgLeft = initCenterX − initWidthFt / 2
            //   imgTop  = initCenterY + initHeightFt / 2
            //   1 placed-foot = 1 pixel × initFtPerPx
            double imgLeft = initCenterX - initWidthFt  / 2.0;
            double imgTop  = initCenterY + initHeightFt / 2.0;
            double placedFtPerPx = initWidthFt / imgPxW;

            double ek1x = (imagePt1.X - imgLeft) / placedFtPerPx;
            double ek1y = (imgTop - imagePt1.Y) / placedFtPerPx;   // pixel Y flipped
            double ek2x = (imagePt2.X - imgLeft) / placedFtPerPx;
            double ek2y = (imgTop - imagePt2.Y) / placedFtPerPx;

            // ── 6. Compute scale + rotation ─────────────────────────
            double modelDist = Math.Sqrt(
                (modelPt2.X - modelPt1.X) * (modelPt2.X - modelPt1.X) +
                (modelPt2.Y - modelPt1.Y) * (modelPt2.Y - modelPt1.Y));
            double ekDist = Math.Sqrt(
                (ek2x - ek1x) * (ek2x - ek1x) +
                (ek2y - ek1y) * (ek2y - ek1y));

            if (modelDist < 1.0 || ekDist < 10.0)
            {
                TaskDialog.Show("Visual Alignment — Error",
                    "The two reference points are too close together to compute a " +
                    "stable scale.  Try again and pick points farther apart.");
                DeleteImageInTx(doc, initImgId, "Visual Cal — cancel");
                TryDeleteFile(imgPath);
                return null;
            }

            double ftPerPx    = modelDist / ekDist;
            double modelAngle = Math.Atan2(modelPt2.Y - modelPt1.Y, modelPt2.X - modelPt1.X);
            double ekAngle    = Math.Atan2(-(ek2y - ek1y), ek2x - ek1x);   // Y flip in pixel space
            double rotation   = modelAngle - ekAngle;
            double cosR       = Math.Cos(rotation);
            double sinR       = Math.Sin(rotation);

            // ── Diagnostic dump (v2.5.20) — captured in DebugView ──────
            //   Critical numbers for diagnosing AP/image alignment bugs
            //   when the user reports "image OK but APs rotated".
            Debug.WriteLine(
                "[Visual Cal] === picks ===\n" +
                $"  modelPt1 = ({modelPt1.X:F3}, {modelPt1.Y:F3}) ft\n" +
                $"  modelPt2 = ({modelPt2.X:F3}, {modelPt2.Y:F3}) ft\n" +
                $"  imagePt1 = ({imagePt1.X:F3}, {imagePt1.Y:F3}) ft\n" +
                $"  imagePt2 = ({imagePt2.X:F3}, {imagePt2.Y:F3}) ft\n" +
                $"  ek1 (px) = ({ek1x:F2}, {ek1y:F2})\n" +
                $"  ek2 (px) = ({ek2x:F2}, {ek2y:F2})\n" +
                $"  imgPxW   = {imgPxW}, imgPxH = {imgPxH}\n" +
                $"  fp.Width = {fp.Width:F1}, fp.Height = {fp.Height:F1}\n" +
                $"  fp.MetersPerUnit = {fp.MetersPerUnit:F6}");
            Debug.WriteLine(
                "[Visual Cal] === computed transform ===\n" +
                $"  modelDist = {modelDist:F3} ft, modelAngle = {modelAngle * 180.0 / Math.PI:F2}°\n" +
                $"  ekDist    = {ekDist:F2} px, ekAngle    = {ekAngle * 180.0 / Math.PI:F2}°\n" +
                $"  rotation  = {rotation * 180.0 / Math.PI:F2}°\n" +
                $"  ftPerPx   = {ftPerPx:F6} (ft per bitmap pixel)\n" +
                $"  cosR = {cosR:F4}, sinR = {sinR:F4}");

            // Sanity check vs declared metersPerUnit
            double mpuFtPerPx = mpu / 0.3048;
            double scaleErr   = mpuFtPerPx > 0
                ? Math.Abs(ftPerPx - mpuFtPerPx) / mpuFtPerPx : 0;

            // Verify Pair-2 residual
            double dx2 = ek2x - ek1x, dy2 = -(ek2y - ek1y);
            double checkX = modelPt1.X + (dx2 * cosR - dy2 * sinR) * ftPerPx;
            double checkY = modelPt1.Y + (dx2 * sinR + dy2 * cosR) * ftPerPx;
            double resFt  = Math.Sqrt(
                (checkX - modelPt2.X) * (checkX - modelPt2.X) +
                (checkY - modelPt2.Y) * (checkY - modelPt2.Y));
            double rotDeg = rotation * 180.0 / Math.PI;

            Debug.WriteLine(
                $"[ESX Read] Visual cal: ftPerPx={ftPerPx:F6} rot={rotDeg:F2}° " +
                $"residual={resFt:F3} ft  scale-err-vs-mpu={scaleErr * 100:F1}%");

            // ── 7. Reposition + rotate the image ────────────────────
            //   Pixel (imgPxW/2, imgPxH/2) → world via the calibrated transform
            double cdx = (imgPxW / 2.0) - ek1x;
            double cdy = -((imgPxH / 2.0) - ek1y);
            double newCenterX = modelPt1.X + (cdx * cosR - cdy * sinR) * ftPerPx;
            double newCenterY = modelPt1.Y + (cdx * sinR + cdy * cosR) * ftPerPx;
            double newWidthFt = imgPxW * ftPerPx;

            ElementId alignedImgId = null;
            try
            {
                using var tx = new Transaction(doc, "Visual Cal — re-place image");
                tx.Start();
                try { doc.Delete(initImgId); } catch { }
                var imgType2 = VersionCompat.CreateImageType(doc, imgPath);
                if (imgType2 != null)
                {
                    var opts2 = new ImagePlacementOptions(
                        new XYZ(newCenterX, newCenterY, zElev), BoxPlacement.Center);
                    var inst2 = ImageInstance.Create(doc, view, imgType2.Id, opts2);
                    try { inst2.Width = newWidthFt; } catch { }
                    alignedImgId = inst2.Id;

                    if (Math.Abs(rotation) > 1e-4)
                    {
                        try
                        {
                            var axis = Line.CreateBound(
                                new XYZ(newCenterX, newCenterY, zElev),
                                new XYZ(newCenterX, newCenterY, zElev + 1));
                            ElementTransformUtils.RotateElement(doc, alignedImgId, axis, rotation);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ESX Read] Image rotation failed: {ex.Message}");
                        }
                    }
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ESX Read] Image re-placement failed: {ex.Message}");
            }
            try { uiDoc.RefreshActiveView(); DoEvents(); } catch { }

            // ── 8. Verification dialog (continue / retry / cancel) ─
            var verify = new TaskDialog("Visual Alignment — Verify")
            {
                MainInstruction = "Confirm alignment is correct",
                MainContent =
                    $"Scale:    1 px = {ftPerPx * 304.8:F2} mm\n" +
                    $"Rotation: {rotDeg:F1}°\n" +
                    $"Pair-2 residual: {resFt * 304.8:F1} mm  " +
                    $"({(resFt > 1.0 ? "high — check pair selection" : "good")})\n" +
                    (scaleErr > 0.20
                        ? $"⚠ Scale differs from .esx metersPerUnit by {scaleErr * 100:F1}% — " +
                          "double-check your picks.\n"
                        : "") +
                    "\n" +
                    "Look at the view.  Do the image walls now line up with the Revit " +
                    "model walls?\n\n" +
                    "If alignment is wrong, retry — typical causes:\n" +
                    "  • The two pairs are too close together\n" +
                    "  • You clicked slightly off the matching point on the image\n" +
                    "  • The image and model represent different floors",
            };
            verify.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Alignment correct — continue with AP markers");
            verify.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Wrong — retry with new point pairs");
            verify.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                "Cancel — skip this floor");
            verify.DefaultButton = TaskDialogResult.CommandLink1;

            var resp = verify.Show();

            // Always delete the alignment image — the per-floor verification
            // step that runs immediately after will re-place a fresh overlay
            // using the calibrated anchor.  Avoids duplicate ImageInstances.
            DeleteImageInTx(doc, alignedImgId, "Visual Cal — done");
            TryDeleteFile(imgPath);

            if (resp == TaskDialogResult.CommandLink2)
                return OfferVisualAlignmentCore(uiDoc, view, fp, esxData);  // retry
            if (resp == TaskDialogResult.CommandLink3)
                return null;

            // ── 9. Build EsxRevitAnchorData (Mode 1, with rotation) ──
            //   The Mode-1 transform builder applies:
            //       vx = lMinX + (ex - offX) / cW * cropW
            //       vy = lMaxY - (ey - offY) / cH * cropH
            //       wx = oX + bXx * vx + bYx * vy
            //       wy = oY + bXy * vx + bYy * vy
            //   We pick the local + basis fields so this evaluates to
            //   exactly our visual-alignment formula:
            //       wx = anchorRevitX + (dx*cosR - dy*sinR) * ftPerPx
            //       wy = anchorRevitY + (dx*sinR + dy*cosR) * ftPerPx
            //   where dx = ex - anchorEkX, dy = -(ey - anchorEkY).
            double anchorEkX = ek1x, anchorEkY = ek1y;
            double anchorRX  = modelPt1.X, anchorRY = modelPt1.Y;

            // World-space AABB for informational fields (not used by Mode 1
            // but populated for completeness and downstream logging).
            double minWX = double.MaxValue, maxWX = double.MinValue;
            double minWY = double.MaxValue, maxWY = double.MinValue;
            void TransformPx(double px, double py)
            {
                double ddx = px - anchorEkX;
                double ddy = -(py - anchorEkY);
                double wx = anchorRX + (ddx * cosR - ddy * sinR) * ftPerPx;
                double wy = anchorRY + (ddx * sinR + ddy * cosR) * ftPerPx;
                if (wx < minWX) minWX = wx;
                if (wx > maxWX) maxWX = wx;
                if (wy < minWY) minWY = wy;
                if (wy > maxWY) maxWY = wy;
            }
            TransformPx(0, 0);
            TransformPx(imgPxW, 0);
            TransformPx(imgPxW, imgPxH);
            TransformPx(0, imgPxH);

            Debug.WriteLine(
                "[Visual Cal] === synthesised anchor ===\n" +
                $"  CropWorld   = ({minWX:F2}..{maxWX:F2}, {minWY:F2}..{maxWY:F2}) ft\n" +
                $"  CropPixel   = ({imgPxW}x{imgPxH})  (anchor frame)\n" +
                $"  ImageWidth  = {imgPxW}, ImageHeight = {imgPxH}\n" +
                $"  anchorEk    = ({anchorEkX:F2}, {anchorEkY:F2}) px\n" +
                $"  anchorR     = ({anchorRX:F3}, {anchorRY:F3}) ft\n" +
                $"  Local       = ({-anchorEkX * ftPerPx:F2}..{(imgPxW - anchorEkX) * ftPerPx:F2}, " +
                $"{-(imgPxH - anchorEkY) * ftPerPx:F2}..{anchorEkY * ftPerPx:F2}) ft\n" +
                $"  Basis       = [{cosR:F4} {sinR:F4}; {-sinR:F4} {cosR:F4}]\n" +
                $"  MetersPerUnit (synthesised) = {ftPerPx * 0.3048:F6} m/px\n" +
                $"  fp.Width = {fp.Width:F1}, fp.Height = {fp.Height:F1}\n" +
                $"  expected apScale (in BuildEkahauToRevitXform) = " +
                $"({(fp.Width  > 0 ? imgPxW / fp.Width  : 1.0):F4}x" +
                $"{(fp.Height > 0 ? imgPxH / fp.Height : 1.0):F4})");

            return new EsxRevitAnchorData
            {
                CropWorldMinX_ft = minWX,
                CropWorldMinY_ft = minWY,
                CropWorldMaxX_ft = maxWX,
                CropWorldMaxY_ft = maxWY,

                MetersPerUnit    = ftPerPx * 0.3048,
                ImageWidth       = imgPxW,
                ImageHeight      = imgPxH,
                CropPixelOffsetX = 0,
                CropPixelOffsetY = 0,
                CropPixelWidth   = imgPxW,
                CropPixelHeight  = imgPxH,

                // Local bounds (chosen so vx = (ex - anchorEkX)*ftPerPx,
                // vy = (anchorEkY - ey)*ftPerPx — see the maths above).
                LocalMinX = -anchorEkX * ftPerPx,
                LocalMaxX = (imgPxW - anchorEkX) * ftPerPx,
                LocalMinY = -(imgPxH - anchorEkY) * ftPerPx,
                LocalMaxY = anchorEkY * ftPerPx,

                XformOriginX_ft = anchorRX,
                XformOriginY_ft = anchorRY,
                XformBasisXx    = cosR,
                XformBasisXy    = sinR,
                XformBasisYx    = -sinR,
                XformBasisYy    = cosR,

                HasWorldBounds = true,
                HasTransform   = true,
            };
        }

        /// <summary>
        /// Delete an element inside a small dedicated transaction.  Never
        /// throws — safe to call even when the element is already gone.
        /// </summary>
        private static void DeleteImageInTx(Document doc, ElementId id, string txName)
        {
            if (id == null || id == ElementId.InvalidElementId) return;
            try
            {
                using var tx = new Transaction(doc, txName);
                tx.Start();
                try { doc.Delete(id); } catch { }
                tx.Commit();
            }
            catch { }
        }

        // ──────────────────────────────────────────────────────────────
        //  DWG Export round-trip — three-tier calibration strategy
        //
        //    Tier 1: revitAnchor in .esx        (best — from ESX Export)
        //    Tier 2: .ekahau-cal.json sidecar   (good — from DWG Export)
        //    Tier 3: manual CropBox matching    (fallback — no calibration)
        //
        //  This method handles Tier 2: when an .esx was created from a
        //  DWG-import in Ekahau Pro it has no `revitAnchor`, so we look
        //  for the .ekahau-cal.json sidecar that DWG Export wrote next
        //  to the .dwg.  Search order:
        //    1. Same folder as the .esx, name-matched on revitViewName
        //    2. Single .ekahau-cal.json in the folder → unambiguous
        //    3. Multi-file picker
        //    4. User browses for it via OpenFileDialog
        //    5. Skip — Tier 3 takes over
        //
        //  When applying a Tier-2 anchor, the pixel offsets are computed
        //  by centring the CropBox inside the Ekahau image (Ekahau
        //  typically adds equal margins around DWG content on import).
        // ──────────────────────────────────────────────────────────────

        private static void TryApplyDwgCalibrationFallback(
            EsxReadResult esxData, string esxPath)
        {
            if (esxData?.FloorPlans == null) return;

            var anchorless = esxData.FloorPlans
                .Where(fp => fp.RevitAnchor == null)
                .ToList();
            if (anchorless.Count == 0) return;  // Tier 1 already covers everything

            // ── Build the candidate cal-file pool (folder scan + browse) ──
            var calFiles = ScanCalibrationFiles(esxPath);

            if (calFiles.Count == 0)
            {
                // Offer to browse for one — or skip to Tier 3
                string browsed = AskUserToBrowseForCalFile(esxPath);
                if (!string.IsNullOrEmpty(browsed))
                    calFiles.Add(browsed);
                else
                    return;  // Tier 3 fallback
            }

            // ── Apply per anchorless floor plan ──
            int applied = 0;
            var notes   = new List<string>();
            foreach (var fp in anchorless)
            {
                string chosen = ChooseCalFileForFloor(calFiles, fp);
                if (chosen == null) continue;

                var (anchor, _) = TryParseCalibration(chosen, fp);
                if (anchor == null) continue;

                fp.RevitAnchor = anchor;
                applied++;
                notes.Add($"  \u2022 {fp.Name}  ←  {Path.GetFileName(chosen)}");
            }

            if (applied == 0) return;

            try
            {
                TaskDialog.Show("ESX Read — DWG Calibration Loaded",
                    $"Applied DWG calibration to {applied} floor plan(s):\n\n" +
                    string.Join("\n", notes) +
                    "\n\nThese floor plans had no revitAnchor in the .esx (typical of " +
                    "DWG-import Ekahau projects).  AP coordinates will be mapped via " +
                    "the calibration data.\n\n" +
                    "TIP: Use the image overlay verification step that follows " +
                    "to confirm alignment is correct.");
            }
            catch { }
        }

        /// <summary>
        /// Scan the .esx's folder for *.ekahau-cal.json files.
        /// Never throws.  Returns an empty list on any failure.
        /// </summary>
        private static List<string> ScanCalibrationFiles(string esxPath)
        {
            var list = new List<string>();
            try
            {
                string folder = Path.GetDirectoryName(esxPath);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;

                var files = Directory.GetFiles(folder, "*.ekahau-cal.json");
                if (files != null) list.AddRange(files);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Prompt the user when no .ekahau-cal.json was auto-discovered,
        /// offering to browse for one or skip to manual CropBox matching.
        /// Returns the chosen file path, or null when the user skips.
        /// </summary>
        private static string AskUserToBrowseForCalFile(string esxPath)
        {
            try
            {
                var td = new TaskDialog("ESX Read — No revitAnchor Found")
                {
                    MainInstruction = "This .esx file has no Revit coordinate data.",
                    MainContent =
                        "This usually means the floor plan was imported into Ekahau " +
                        "from a DWG file (not via ESX Export).\n\n" +
                        "If you used 'DWG Export' from the WiFi Tools tab, a calibration " +
                        "file (.ekahau-cal.json) was created alongside the DWG.  Locate " +
                        "it for accurate AP-coordinate round-trip.\n\n" +
                        "If you skip, ESX Read will fall back to manual CropBox matching " +
                        "(less accurate when the Ekahau image has padding).",
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Browse for calibration file",
                    "Pick the matching .ekahau-cal.json");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                    "Skip — use manual CropBox matching",
                    "Continue without calibration data (Tier 3 fallback)");
                td.CommonButtons = TaskDialogCommonButtons.Cancel;
                td.DefaultButton = TaskDialogResult.CommandLink1;

                var resp = td.Show();
                if (resp != TaskDialogResult.CommandLink1) return null;

                string initialDir = "";
                try { initialDir = Path.GetDirectoryName(esxPath) ?? ""; }
                catch { }

                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = "Select DWG Calibration File",
                    Filter = "Ekahau calibration (*.ekahau-cal.json)|*.ekahau-cal.json|" +
                             "All files (*.*)|*.*",
                    InitialDirectory = initialDir,
                    CheckFileExists = true,
                };
                if (ofd.ShowDialog() == true) return ofd.FileName;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// From a list of candidate .ekahau-cal.json files, pick the one
        /// that best matches a given floor plan.  Selection order:
        ///   1. Name match against revitViewName
        ///   2. Single-file → unambiguous
        ///   3. Show a picker (TaskDialog with up to 4 command links;
        ///      OpenFileDialog as ultimate fallback)
        /// Returns the chosen file path, or null when the user cancels.
        /// </summary>
        private static string ChooseCalFileForFloor(
            List<string> calFiles, EsxFloorPlanData fp)
        {
            if (calFiles == null || calFiles.Count == 0) return null;

            // 1. Name match
            foreach (var cf in calFiles)
            {
                string vn = ReadCalFileViewName(cf);
                if (!string.IsNullOrEmpty(vn) &&
                    vn.Equals(fp.Name, StringComparison.OrdinalIgnoreCase))
                    return cf;
            }

            // 2. Single file
            if (calFiles.Count == 1) return calFiles[0];

            // 3. Picker — TaskDialog supports up to 4 command links cleanly
            try
            {
                if (calFiles.Count <= 4)
                {
                    var td = new TaskDialog("ESX Read — Choose Calibration File")
                    {
                        MainInstruction = $"Multiple calibration files found.",
                        MainContent =
                            $"Pick the one for floor plan \"{fp.Name}\".  None of them " +
                            "matched the floor plan name automatically.",
                    };
                    var linkIds = new[]
                    {
                        TaskDialogCommandLinkId.CommandLink1,
                        TaskDialogCommandLinkId.CommandLink2,
                        TaskDialogCommandLinkId.CommandLink3,
                        TaskDialogCommandLinkId.CommandLink4,
                    };
                    var resultIds = new[]
                    {
                        TaskDialogResult.CommandLink1,
                        TaskDialogResult.CommandLink2,
                        TaskDialogResult.CommandLink3,
                        TaskDialogResult.CommandLink4,
                    };
                    for (int i = 0; i < calFiles.Count; i++)
                    {
                        string vn = ReadCalFileViewName(calFiles[i]);
                        td.AddCommandLink(linkIds[i],
                            Path.GetFileName(calFiles[i]),
                            string.IsNullOrEmpty(vn) ? "" : $"View: {vn}");
                    }
                    td.CommonButtons = TaskDialogCommonButtons.Cancel;
                    var resp = td.Show();
                    for (int i = 0; i < calFiles.Count; i++)
                        if (resp == resultIds[i]) return calFiles[i];
                    return null;
                }

                // Too many candidates — fall through to OpenFileDialog
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = $"Pick Calibration File for \"{fp.Name}\"",
                    Filter = "Ekahau calibration (*.ekahau-cal.json)|*.ekahau-cal.json",
                    InitialDirectory = Path.GetDirectoryName(calFiles[0]) ?? "",
                };
                if (ofd.ShowDialog() == true) return ofd.FileName;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Cheap read of just the revitViewName field from a cal file.
        /// Returns empty string on any failure.
        /// </summary>
        private static string ReadCalFileViewName(string calPath)
        {
            try
            {
                using var jdoc = JsonDocument.Parse(File.ReadAllText(calPath));
                if (jdoc.RootElement.TryGetProperty("revitViewName", out var vn) &&
                    vn.ValueKind == JsonValueKind.String)
                    return vn.GetString() ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Parse a `.ekahau-cal.json` and produce an
        /// <see cref="EsxRevitAnchorData"/> equivalent to revitAnchor.
        /// Pixel offsets are computed by centring the CropBox inside the
        /// Ekahau image (handles Ekahau's typical DWG-import margins).
        /// Returns the anchor + the view name embedded in the cal-file.
        /// </summary>
        private static (EsxRevitAnchorData Anchor, string ViewName)
            TryParseCalibration(string calPath, EsxFloorPlanData fp)
        {
            try
            {
                using var jdoc = JsonDocument.Parse(File.ReadAllText(calPath));
                var root = jdoc.RootElement;

                if (!root.TryGetProperty("cropBox", out var cb)) return (null, null);

                double minX = ReadDouble(cb, "minX_ft");
                double minY = ReadDouble(cb, "minY_ft");
                double maxX = ReadDouble(cb, "maxX_ft");
                double maxY = ReadDouble(cb, "maxY_ft");
                if (maxX <= minX || maxY <= minY) return (null, null);

                // Centre the CropBox inside the Ekahau image.
                //   cropW_m  = (maxX-minX) × 0.3048
                //   cropW_px = cropW_m / mpu     ← expected pixel width of CropBox
                //   offset_px = (imageW - cropW_px) / 2.0
                // Same for Y.  Falls back to (0, full image) when fp data is absent.
                double mpu  = (fp != null && fp.MetersPerUnit > 0)
                              ? fp.MetersPerUnit : 0.0264583;
                double imgW = fp != null ? fp.Width  : 0.0;
                double imgH = fp != null ? fp.Height : 0.0;

                double cropWFt = maxX - minX;
                double cropHFt = maxY - minY;
                double cropWPx = (cropWFt * 0.3048) / mpu;
                double cropHPx = (cropHFt * 0.3048) / mpu;

                double offX_px = imgW > cropWPx ? (imgW - cropWPx) / 2.0 : 0.0;
                double offY_px = imgH > cropHPx ? (imgH - cropHPx) / 2.0 : 0.0;
                if (imgW <= 0) { cropWPx = 0; offX_px = 0; }
                if (imgH <= 0) { cropHPx = 0; offY_px = 0; }

                var anchor = new EsxRevitAnchorData
                {
                    CropWorldMinX_ft = minX,
                    CropWorldMinY_ft = minY,
                    CropWorldMaxX_ft = maxX,
                    CropWorldMaxY_ft = maxY,
                    MetersPerUnit    = mpu,
                    ImageWidth       = (int)imgW,
                    ImageHeight      = (int)imgH,
                    CropPixelOffsetX = offX_px,
                    CropPixelOffsetY = offY_px,
                    CropPixelWidth   = cropWPx > 0 ? cropWPx : imgW,
                    CropPixelHeight  = cropHPx > 0 ? cropHPx : imgH,
                    LocalMinX        = minX,
                    LocalMinY        = minY,
                    LocalMaxX        = maxX,
                    LocalMaxY        = maxY,
                    HasWorldBounds   = true,
                };

                if (root.TryGetProperty("transform", out var tr))
                {
                    anchor.XformOriginX_ft = ReadDouble(tr, "originX_ft");
                    anchor.XformOriginY_ft = ReadDouble(tr, "originY_ft");
                    anchor.XformBasisXx    = ReadDouble(tr, "basisXx", 1.0);
                    anchor.XformBasisXy    = ReadDouble(tr, "basisXy", 0.0);
                    anchor.XformBasisYx    = ReadDouble(tr, "basisYx", 0.0);
                    anchor.XformBasisYy    = ReadDouble(tr, "basisYy", 1.0);
                    anchor.HasTransform    = true;
                }

                string viewName = null;
                if (root.TryGetProperty("revitViewName", out var vn) &&
                    vn.ValueKind == JsonValueKind.String)
                {
                    viewName = vn.GetString();
                }
                return (anchor, viewName);
            }
            catch
            {
                return (null, null);
            }
        }

        private static double ReadDouble(JsonElement obj, string name, double dflt = 0.0)
        {
            try
            {
                if (obj.TryGetProperty(name, out var v) &&
                    (v.ValueKind == JsonValueKind.Number) &&
                    v.TryGetDouble(out double d))
                    return d;
            }
            catch { }
            return dflt;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        private static void DoEvents()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }
    }
}
