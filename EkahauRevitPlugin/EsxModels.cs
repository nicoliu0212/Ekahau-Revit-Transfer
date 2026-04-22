using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace EkahauRevitPlugin
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ESX Export — Data Models
    //  Used by EsxExportCommand, EsxDialogs, EkahauJsonBuilder, WallCollector
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Linked model wall-type mappings read from ExtensibleStorage.</summary>
    public class LinkWallMapping
    {
        /// <summary>UniqueId of the RevitLinkInstance selected in Param Config.</summary>
        public string SelectedLinkUniqueId { get; set; } = "";

        /// <summary>WallType.UniqueId → PresetKey, as configured in Param Config.</summary>
        public Dictionary<string, string> TypeUniqueIdToPreset { get; set; }
            = new Dictionary<string, string>();
    }

    /// <summary>One row of type data to be shown in the mapping review dialog.</summary>
    public class MappingEntry
    {
        public string TypeUniqueId  { get; set; }
        public string TypeName      { get; set; }
        public string InitialPreset { get; set; }
        /// <summary>"Parameter", "Keyword", or "Fallback".</summary>
        public string Source        { get; set; }
        /// <summary>"wall", "door", or "window".</summary>
        public string Category      { get; set; }
        /// <summary>True if the type comes from a linked model.</summary>
        public bool   IsLinked      { get; set; }
    }

    public enum ExportMode { MergeAll, Separate }

    public enum MappingReviewAction { Export, SkipView, CancelAll }

    public class MappingReviewResult
    {
        public MappingReviewAction Action { get; set; } = MappingReviewAction.Export;
        /// <summary>TypeUniqueId → overridden PresetKey (only entries the user changed).</summary>
        public Dictionary<string, string> Overrides { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>An AP family instance candidate found in the model.</summary>
    public class ApCandidate
    {
        public long   ElementId    { get; set; }
        public string Name         { get; set; }
        public double WorldX       { get; set; }
        public double WorldY       { get; set; }
        public double HeightMeters { get; set; }
        public bool   Include      { get; set; } = true;
    }

    /// <summary>
    /// All data produced from one processed floor-plan view, ready for
    /// JSON/ZIP building.
    /// </summary>
    public class PerViewData
    {
        public string    FloorPlanId { get; set; }
        public string    ImageId     { get; set; }
        public ElementId ViewId      { get; set; }
        public string    ViewName    { get; set; }

        // ── CropBox in VIEW-LOCAL feet ────────────────────────────────────
        public double CropMinX { get; set; }
        public double CropMinY { get; set; }
        public double CropMaxX { get; set; }
        public double CropMaxY { get; set; }

        // ── CropBox WORLD-SPACE bounds (for revitAnchor) ─────────────────
        public double CropWorldMinX { get; set; }
        public double CropWorldMinY { get; set; }
        public double CropWorldMaxX { get; set; }
        public double CropWorldMaxY { get; set; }

        // ── CropBox Transform fields (for revitAnchor rotated-view) ──────
        public double XformOriginX { get; set; }
        public double XformOriginY { get; set; }
        public double XformBasisXx { get; set; }
        public double XformBasisXy { get; set; }
        public double XformBasisYx { get; set; }
        public double XformBasisYy { get; set; }

        // ── Padding-aware pixel region of CropBox within PNG ─────────────
        public double CropPixelOffsetX { get; set; }
        public double CropPixelOffsetY { get; set; }
        public double CropPixelWidth   { get; set; }
        public double CropPixelHeight  { get; set; }

        // ── (Legacy fields kept for compatibility) ────────────────────────
        public double AnchorWorldX     { get; set; }
        public double AnchorWorldY     { get; set; }
        public double ViewRotationDeg  { get; set; }

        // ── PNG pixel dimensions ──────────────────────────────────────────
        public int    ImageWidth  { get; set; }
        public int    ImageHeight { get; set; }

        // ── Scale ─────────────────────────────────────────────────────────
        public double MpuX { get; set; }  // meters per pixel, X direction
        public double MpuY { get; set; }  // meters per pixel, Y direction

        // ── Raw PNG bytes for this floor ──────────────────────────────────
        public byte[] PngBytes { get; set; }

        // ── Built JSON objects (wall segments/points per view;
        //    wallTypes are kept in a shared global list) ───────────────────
        public List<Dictionary<string, object>> WallSegments { get; set; }
            = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> WallPoints { get; set; }
            = new List<Dictionary<string, object>>();

        /// <summary>APs already filtered by user confirmation.</summary>
        public List<ApCandidate> AccessPoints { get; set; }
            = new List<ApCandidate>();

        /// <summary>
        /// Coordinate transform closure: Revit world (X ft, Y ft) →
        /// Ekahau image pixel (Ex, Ey).
        /// </summary>
        public Func<double, double, (double Ex, double Ey)> WorldToEkahau { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AP Place — Staging Data Models
    //  Shared between ESX Read (writer) and AP Place (reader).
    //  REQ 4: Multi-floor staging format with backward compatibility.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Root staging file written by ESX Read, consumed by AP Place.</summary>
    public class ApStagingData
    {
        public int FormatVersion { get; set; } = 2;
        public string ProjectName { get; set; } = "";
        /// <summary>REQ 2: MD5 hash of document path for validation.</summary>
        public string ProjectPathHash { get; set; } = "";
        public string EsxFilePath { get; set; } = "";
        public string Timestamp { get; set; } = "";
        /// <summary>REQ 4: Per-floor staging data.</summary>
        public List<ApStagingFloor> Floors { get; set; } = new List<ApStagingFloor>();
    }

    /// <summary>Staging data for one floor plan / view pair.</summary>
    public class ApStagingFloor
    {
        public string FloorPlanName { get; set; } = "";
        public string ViewName { get; set; } = "";
        public long ViewId { get; set; }
        public List<ApStagingEntry> AccessPoints { get; set; } = new List<ApStagingEntry>();
        /// <summary>
        /// Floor-level temporary elements written by ESX Read that aren't
        /// tied to a specific AP — image overlay, CropBox corner crosses,
        /// band legend, etc.  AP Place removes these alongside the per-AP
        /// MarkerElementIds after successful family-instance placement.
        /// </summary>
        public List<long> OverlayElementIds { get; set; } = new List<long>();
    }

    /// <summary>One AP ready for family-instance placement.</summary>
    public class ApStagingEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public double WorldX { get; set; }
        public double WorldY { get; set; }
        /// <summary>REQ 3: Mounting height in metres (from ESX data).</summary>
        public double MountingHeight { get; set; } = 2.7;
        public string Vendor { get; set; } = "";
        public string Model { get; set; } = "";
        public List<string> Bands { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        // ── Radio summary fields for Revit parameter scheduling ──────
        public string Mounting { get; set; } = "";
        public string BandsSummary { get; set; } = "";
        public string Technology { get; set; } = "";
        public string TxPowerSummary { get; set; } = "";
        public string ChannelsSummary { get; set; } = "";
        public string StreamsSummary { get; set; } = "";
        public string AntennaInfo { get; set; } = "";
        /// <summary>REQ 5: Element IDs of the preview markers placed by ESX Read.</summary>
        public List<long> MarkerElementIds { get; set; } = new List<long>();
        /// <summary>REQ 9: Transient — set by confirmation dialog checkboxes.</summary>
        public bool Include { get; set; } = true;
        /// <summary>REQ 13: True once a family instance has been placed for this AP.</summary>
        public bool Placed { get; set; }
        /// <summary>REQ 13: ElementId of the placed family instance.</summary>
        public long? PlacedElementId { get; set; }
        /// <summary>REQ 13: ISO timestamp of placement.</summary>
        public string PlacedTimestamp { get; set; }
    }
}
