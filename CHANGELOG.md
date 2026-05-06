# Changelog

All notable changes to **Ekahau ↔ Revit Transfer** are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

---

## [2.5.22] — 2026-05-05

### Fixed (Bug Fix #17)
- **Visual-cal synthesised anchor now lives in fp-space (`fp.Width × fp.Height`) — the same coordinate system AP coords use.** v2.5.19 stored the anchor in image-pixel-space (`imgPxW × imgPxH`) and bridged the gap with an `apScale` conversion in `BuildEkahauToRevitXform`. The math was correct, but the two-coordinate-systems design was confusing and made future bugs harder to diagnose. v2.5.22 keeps everything in fp-space throughout, so AP coords flow straight through the transform with no scaling conversion.
- **Rotation sanity check** — when the visual cal computes a rotation greater than 45° (the v2.5.21 log had one attempt at 208° because the user reversed the click order), the user now sees a clear "Unusual Rotation Detected" dialog with three options: Retry / Continue anyway / Cancel. Previously a wrong-order pick would silently produce a wildly wrong calibration.

### Changed
- `EsxRevitAnchorData` semantics for visual-cal-synthesised anchors:
  - `CropPixelWidth/Height` = `fp.Width × fp.Height` (fp-space, matches AP coord units)
  - `ImageWidth/Height` = actual bitmap pixel dimensions (informational only)
  - `MetersPerUnit` = "meters per fp-pixel" (was "meters per bitmap pixel" before v2.5.22)
  - `LocalMin/Max` = computed in fp-space using fp-space `ftPerPx`
  - `XformBasis` = unchanged (rotation is coordinate-space-invariant)
- `EsxMarkerOps.PlaceFloorPlanImage` now computes image dimensions from `anchor.CropPixelWidth × ftPerPx` instead of `actualPixelW × ftPerPx`. For ESX-Export anchors (where `ImageWidth ≈ CropPixelWidth ≈ fp.Width`) the result is identical to the old formula. For Option A visual-cal anchors (where `CropPixelWidth = fp.Width` but `ImageWidth = bitmap pixel width`) it gives the correct physical width.
- `PlaceFloorPlanImage`'s padding-compensation block now skips itself when `CropPixel` and `ImageWidth/Height` are in different coordinate spaces (the Option A case) — the AABB-based centre is already correct in that case.
- `BuildEkahauToRevitXform` Mode 1's `apScale` (added in v2.5.19) becomes `1.0` for Option A anchors (because `cW = fp.Width` so the ratio is 1) — kept in place for backward compatibility with any legacy anchors that might still be in the wild.

### Added
- Diagnostic log now records BOTH image-space `(ek1_img, ek2_img)` AND fp-space `(ek1, ek2)` picks plus the conversion ratio, so it's instantly clear which space each number lives in.
- `[Visual Cal] === computed transform ===` log now records both `ftPerPx (fp)` and `ftPerPx (img)` separately.
- The synthesised-anchor log line shows `expected apScale = (1.0000x1.0000)` to confirm Option A is in effect.

### Why this is the right design
v2.5.19's apScale fix produced mathematically correct AP positions, but mixing two coordinate systems through the same data structure is the kind of design choice that bites later. Option A puts a hard line at one boundary: image placement uses image-pixel-space, AP coordinate transformation uses fp-space, the two never meet inside the same calculation. If a future bug appears in one path, the other can't be implicated.

## [2.5.21] — 2026-05-05

### Added
- **File-based diagnostic log** — v2.5.20 routed every visual-cal + AP-placement number through `Debug.WriteLine` for SysInternals DebugView capture, but DebugView setup is non-trivial. v2.5.21 ALSO writes the same content to a plain-text file at:
  ```
  %USERPROFILE%\Documents\EkahauRevitPlugin_diag.log
  ```
  Each ESX Read run starts with a session header showing the plugin version, floor-plan name, and matched view name. Subsequent lines log the same picks / transform / anchor / per-AP placement data the v2.5.20 release added. Just send the file as an attachment — no SysInternals install required.
- New `EsxReadCommand.DiagLog(string message)` helper — writes to BOTH `Debug.WriteLine` (DebugView) AND the file (append-only). Used at every diagnostic point in the visual-cal pipeline.
- `BuildEkahauToRevitXform Mode 1` log line now also includes the basis matrix, origin, and local-bounds rectangle so we can verify the rotation/scale is applied in the right direction.

### How to use
1. Install v2.5.21 over the top of any earlier version.
2. Run ESX Read against the problematic `.esx`, do the manual two-point alignment, let it place the AP markers.
3. Open `%USERPROFILE%\Documents\EkahauRevitPlugin_diag.log` (e.g., `C:\Users\<you>\Documents\EkahauRevitPlugin_diag.log`).
4. Send the file (or copy the latest `========== ESX Read ==========` session block).

The file grows on every run — delete it manually if it gets too large.

## [2.5.20] — 2026-05-05

### Added
- **End-to-end diagnostic logging for the visual-cal → AP placement pipeline.** v2.5.19's apScale fix should have aligned APs with the rotated image, but a user reports APs still appear rotated 90° relative to the view despite the image being correctly placed. The on-paper math checks out, so this release ships verbose `Debug.WriteLine` output at every transformation boundary so we can capture the actual numbers via SysInternals DebugView and pinpoint where the 90° offset originates.
- Logging covers:
  - **Visual-cal picks** — `modelPt1/2`, `imagePt1/2`, derived `ek1/ek2` pixel coords, image dimensions, `fp.Width/Height`, and `fp.MetersPerUnit`.
  - **Visual-cal computed transform** — `modelDist`, `modelAngle°`, `ekDist`, `ekAngle°`, `rotation°`, `ftPerPx`, `cosR`, `sinR`.
  - **Synthesised anchor** — every field (CropWorld, CropPixel, ImageWidth/Height, anchorEk, anchorR, Local bounds, basis matrix, derived MetersPerUnit) plus the expected `apScale` factor.
  - **First 3 AP placements** — input `(ap.PixelX, ap.PixelY)` and resulting world `(wx, wy)` from the calibrated xform. Limited to the first 3 to avoid flooding the log with 360+ lines.
  - The existing `BuildEkahauToRevitXform Mode 1` log line (added in v2.5.19) reports the actual `apScale` at xform-build time.

### How to capture
1. Install v2.5.20.
2. Download **DebugView** from SysInternals (https://learn.microsoft.com/en-us/sysinternals/downloads/debugview), run it as admin, and tick "Capture Win32" + "Capture Global Win32".
3. Filter for `[Visual Cal]` and `[ESX Read]` to keep the noise down.
4. Run ESX Read, do the manual two-point alignment, save the captured log.

The log output will pin down exactly which transformation step produces the 90° offset — whether it's in the angle calculation (`ekAngle` vs `modelAngle`), the basis matrix, the apScale, or somewhere else entirely.

## [2.5.19] — 2026-05-05

### Fixed
- **AP markers placed at wrong positions for third-party Ekahau files** (where bitmap raster resolution ≠ floorPlans.json logical dimensions). The user's `.esx` had:
  - `floorPlans.json width = 3024.0` (logical floor plan units, where AP coords live)
  - `images.json bitmapImageId resolutionWidth = 5000` (rendered raster resolution, downscaled to 4000 by v2.5.16's `NormalizeForRevit`)
  - AP `coord X` ranges from 265 to 2620 — fits in 3024-space, NOT in 4000-space
  
  v2.5.18 fixed the IMAGE rotation, but APs still landed in the wrong positions because `BuildEkahauToRevitXform` Mode 1 fed AP coords (in 3024-space) into a transform whose `CropPixelWidth = 4000` — missing the mark by the (4000/3024 ≈ 1.32) ratio.

### Changed
- **`BuildEkahauToRevitXform` Mode 1 + Mode 2 now scale AP coords from `floorPlan.Width`-space to `anchor.CropPixelWidth`-space** before applying the transform:
  ```csharp
  double apScaleX = (floorPlan.Width  > 0) ? cW / floorPlan.Width  : 1.0;
  double apScaleY = (floorPlan.Height > 0) ? cH / floorPlan.Height : 1.0;
  return (ex, ey) =>
  {
      double sx = ex * apScaleX;
      double sy = ey * apScaleY;
      // ... existing transform on (sx, sy) ...
  };
  ```
- ESX-Export-derived anchors are unaffected: they have `CropPixelWidth == fp.Width` (both written from `imgW` in `EsxExportCommand`), so `apScale = 1.0` and the transform is identical to before.

### Added
- Debug log line on every Mode 1 build, e.g.:
  ```
  [ESX Read] BuildEkahauToRevitXform Mode 1: CropPixel=(4000.0x2857.0), fp=(3024.0x2160.0), apScale=(1.3228x1.3227)
  ```
  This makes it instantly visible in DebugView whether the scaling is being applied and what factor it computed.

### Why this is the right fix
The bug was a coordinate-system mismatch, not a rotation/positioning bug per se. Two coordinate systems were leaking across the abstraction boundary:
- **Image / image-pixel space** = the actual raster's pixel dimensions (5000×3571 → downscaled to 4000×2857)
- **Logical / floorPlans.json space** = where AP coords are stored (3024×2160 in this file)

For ESX-Export-derived files these are the same — the exported PNG is what fp.Width refers to. For third-party Ekahau files (especially when the floor plan was imported from a high-res source like a PDF), they diverge. The fix makes `BuildEkahauToRevitXform` aware of both spaces and bridges them at the boundary.

## [2.5.18] — 2026-05-05

### Fixed
- **Manual visual alignment: image rotates correctly but AP markers ignored the rotation** — after the user picked their two reference pairs, `OfferVisualAlignmentCore` synthesised an `EsxRevitAnchorData` whose basis vectors encoded the rotation `(cos R, sin R, -sin R, cos R)`. AP markers placed via `BuildEkahauToRevitXform` honoured that rotation, but `EsxMarkerOps.PlaceFloorPlanImage` (which re-places the verification overlay during the recursive `PlaceImageAndAskForVerification` call after manual alignment) never read the basis vectors — it placed the image axis-aligned at the AABB centre. The mismatch made the AP markers look like they had the wrong rotation, when in fact it was the image that was missing the rotation.

### Changed
- `EsxMarkerOps.PlaceFloorPlanImage` now reads the rotation from the anchor's basis vectors and applies it to the placed image:
  ```csharp
  if (anchor.HasTransform)
  {
      double rotation = Math.Atan2(anchor.XformBasisXy, anchor.XformBasisXx);
      if (Math.Abs(rotation) > 1e-4)
          ElementTransformUtils.RotateElement(doc, imgInst.Id, axis, rotation);
  }
  ```
  The AABB centre we compute IS the rotation centre (centroid of the 4 rotated corners is preserved by rotation), so a single `RotateElement` around `(centerX, centerY)` lands the image exactly where `BuildEkahauToRevitXform` expects it. AP markers (which use the same xform) then line up with image features as intended.
- The Debug log records every rotation: `[ESX Read] Image rotated by 12.34° around (123.45, 67.89) to match anchor basis.`

### Why this is the right fix
The root cause was that two different code paths consumed the anchor: `BuildEkahauToRevitXform` (used for AP placement) consulted `XformBasis*` and applied rotation; `PlaceFloorPlanImage` (used for the image overlay) consulted only `CropWorld*` AABB bounds and ignored rotation. v2.5.18 closes that gap so all anchor consumers honour the rotation. ESX-Export-derived anchors (which historically have `XformBasisXx = 1, XformBasisXy = 0` → rotation = 0) are unaffected — they place the image identically to before.

## [2.5.17] — 2026-05-04

### Fixed
- **`ImageType.Create` still returned NULL on the v2.5.16-encoded PNG** — diagnostic showed a perfectly valid PNG (header `89 50 4E 47 0D 0A 1A 0A …`) at 5.5 MB, but Revit's import path silently rejected it. Two likely causes addressed in this release.

### Changed
- **`NormalizeForRevit` now produces 24-bit RGB PNGs (no alpha)** instead of 32-bit RGBA. Revit's `ImageType.Create` import path is known to silently return NULL for 32-bit RGBA PNGs on certain versions; 24-bit RGB is the most universally accepted variant. Pixel format conversion is `Bgra32` → `Bgr24` via `FormatConvertedBitmap` before encoding.
- **`VersionCompat.CreateImageType` now tries multiple strategies in sequence** until one succeeds:
  1. `ImageTypeSource.Import` + 3-arg ctor — embeds the raster into the .rvt (existing behaviour, kept first because it works in the common case).
  2. `ImageTypeSource.Link` + 3-arg ctor — keeps the file as an external link rather than embedding it. Much more permissive in Revit's internal validation; works for some files that Import silently rejects.
  3. (REVIT_LEGACY only) 2-arg ctor `ImageTypeOptions(string, bool)` — Revit 2024 fallback.
  4. (REVIT_LEGACY only) 1-arg `ImageType.Create(Document, string)` — Revit 2023 fallback.

### Added
- New `out string strategyTrace` parameter on `VersionCompat.CreateImageType` that captures the result of every attempted strategy:
  ```
  Strategies tried:
    Import: null
    Link  : OK
  ```
  When all strategies fail, the diagnostic dialog now shows exactly which approaches were tried and what each one returned/threw — so the next iteration has zero ambiguity about what worked vs. didn't.
- The 2-out and 1-out overloads are kept so existing call sites compile unchanged.

### Why this is a focused, multi-pronged fix
The Revit API forum has multiple threads where users hit "ImageType.Create returns null" specifically for files that should work — the resolutions vary (drop alpha, use Link instead of Import, downscale, switch ctor). Rather than guess which one applies here, v2.5.17 tries each in turn and reports the result. Worst case: we get a definitive trace showing every approach Revit refused, which narrows the diagnosis to "this is an environment/document-specific issue, not a file-format issue".

## [2.5.16] — 2026-05-04

### Fixed
- **`ImageType.Create` silently rejects "valid" JPEGs from certain sources** — Autodesk-confirmed Revit API behaviour: *"JPEG response data from certain sources may not be readable in the Revit API environment, while PNG or BMP has no issue with the same code"* ([forum thread](https://forums.autodesk.com/t5/revit-api-forum/quot-imagetype-create-quot-method-causes-unexpected-internal/td-p/10263771)). v2.5.15 correctly wrote `EkahauVisCal_*.jpg` (extension matched the JPEG content), but Revit's import path STILL refused it — `ImageType.Create` returned NULL with no exception (`Underlying error: (no inner exception captured)`). The Ekahau JPEG is a perfectly vanilla baseline 8-bit RGB JFIF — `file` reports it as standard — but Revit's WIC import dispatch refuses it anyway.

### Added
- `ImageNormalizer.NormalizeForRevit(byte[], out string detail, int maxDim = 4000)` — round-trips the raster through the same `BitmapDecoder` + `PngBitmapEncoder` (WIC) that Revit uses internally:
  1. Decodes via `BitmapDecoder.Create(BitmapCacheOption.OnLoad)` so the source stream can be released immediately.
  2. Forces `PixelFormats.Bgra32` to strip ICC profiles and neutralise odd source pixel formats.
  3. Optionally downscales (default cap: 4000 px in either dimension) — older Revit versions had an undocumented internal cap around 8000 px and `ImageTypeSource.Import` embeds the entire decoded raster into the .rvt, so a generous cap also keeps file size manageable. 4000 px is still ≥1 pixel per inch on a 333-foot building — plenty for floor-plan overlays.
  4. Re-encodes as PNG via `PngBitmapEncoder` and returns the new bytes.
- Both image-creation entry points (`PlaceImageAndAskForVerification` step 2 + `OfferVisualAlignmentCoreImpl` initial-image transaction) now route through `NormalizeForRevit` BEFORE writing the temp file. The normalised PNG always ends up as `EkahauRead_*.png` / `EkahauVisCal_*.png`, regardless of the source format.
- The Debug log records the normalisation result on every read, e.g. `[ESX Read] WIC re-encode: 5000x3571 → 4000x2857 PNG (1,816,148 → 8,234,567 bytes)`.

### Why this is the right fix
- It addresses a known, reproduced Revit API quirk rather than treating the symptom.
- It uses the same WIC engine Revit uses internally, so if WIC can decode the source, the resulting PNG is guaranteed to be Revit-acceptable.
- It's defence-in-depth — combines with v2.5.13 (`bitmapImageId`) and v2.5.15 (extension matching) without removing either, so any of the three protections still catches edge cases.
- Failure mode is explicit — if WIC itself can't decode the input, `NormalizeForRevit` returns null and surfaces the WIC error, which we'll see in the v2.5.12 diagnostic dialog.

## [2.5.15] — 2026-05-04

### Fixed
- **`ImageType.Create` returned null because the temp file extension didn't match its actual content** — v2.5.14's diagnostic dialog made this visible:
  ```
  Temp file : EkahauVisCal_44c5228…b2e338f65.png
  First 16 hex : FFD8FFE0 00104A46 49460001 02000001
                  ^^^^^^^^                              ← JPEG/JFIF
  Underlying error: (no inner exception captured)
  ```
  We were writing the JPEG bytes from `bitmapImageId` (v2.5.13) into a `.png`-named temp file, then handing that path to Revit. Revit's `ImageType.Create` looks at the file extension to choose its WIC decoder; with `.png` it tries the PNG decoder, gets garbage, returns NULL **without throwing** — exactly matching the user's symptom.

### Added
- `ImageNormalizer.DetectExtension(byte[])` — sniffs PNG / JPEG / BMP / GIF / TIFF (LE+BE) / WebP magic bytes and returns the matching extension (`.png`, `.jpg`, `.bmp`, `.gif`, `.tif`, `.webp`). Falls back to `.png` only when nothing matches.
- All three image-write sites now pick the extension dynamically:
  - `OfferVisualAlignmentCoreImpl` → `EkahauVisCal_<guid><ext>`
  - `PlaceImageAndAskForVerification` → `EkahauRead_<guid><ext>`
  - Staging save (REQ 21) → `floor_<name><ext>`
- Each write also logs the chosen path + extension via `Debug.WriteLine` for DebugView traceability.

### Why this is the right fix
Revit's WIC dispatch is extension-driven and silent on mismatch. Previously we forced `.png` for every write because Ekahau exports were historically PNG; the `bitmapImageId` raster companion is JPEG, so the same path stopped working the moment v2.5.13 routed through it. Detecting the format from magic bytes (rather than trusting `images.json`'s `imageFormat` field, which we'd also have to thread through) keeps the fix narrow and correct for future formats too.

## [2.5.14] — 2026-05-02

### Fixed
- **CI build broken since v2.5.12** (no MSI was published for v2.5.12 or v2.5.13). Two compile errors I introduced when adding the diagnostic surface in v2.5.12:
  - `EsxMarkerOps.PlaceFloorPlanImage` called `ReadFirstBytesHex(...)` unqualified, but that helper lives on `EsxReadCommand` (different class) → `error CS0103: The name 'ReadFirstBytesHex' does not exist in the current context`. Fixed by qualifying the call (same pattern as the existing `EsxReadCommand.ReadImageDimensions(...)` call earlier in the same method).
  - The new `long fileSize = 0;` shadowed an existing `long fileSize` declared earlier in `PlaceFloorPlanImage` → `error CS0136: A local or parameter named 'fileSize' cannot be declared in this scope...`. Fixed by reusing the outer variable instead of redeclaring.
- The same diagnostic block in `OfferVisualAlignmentCoreImpl` was already in a fresh scope, so it compiled fine on its own — the breakage was specific to the second call site I added.

### Why no v2.5.12 / v2.5.13 MSI exists
GitHub Actions reported these runs as failed (build step took only ~11 seconds — too fast to actually compile), but a stale render of the Actions page made it look successful. Confirmed via the API:
```
v2.5.13 → conclusion: failure  (run 25304947681)
v2.5.12 → conclusion: failure  (run 25304649231)
v2.5.11 → conclusion: success  (run 25246405107)  ← last working release
```
v2.5.14 ships the v2.5.12 + v2.5.13 changes plus the compile fix, so installing v2.5.14 gives you all of: SVG raster extraction (v2.5.11), real ImageType.Create error surfacing (v2.5.12), and the `bitmapImageId` raster-companion lookup (v2.5.13) that targets your specific .esx file.

## [2.5.13] — 2026-05-02

### Fixed
- **Use Ekahau's pre-rendered raster companion for SVG floor plans** — when a floor plan's primary `imageId` points at an SVG image entry, Ekahau also ships a JPEG/PNG raster identified by `bitmapImageId` in the same `floorPlans.json` record. The plugin was ignoring `bitmapImageId` and trying to feed the 102 MB SVG to Revit's WIC engine, which doesn't render SVG. Now we read `bitmapImageId` and prefer it whenever it's present, so Revit gets a directly-renderable JPEG/PNG without any SVG decoding required.

### Why this is the real root-cause fix
- v2.5.11 added an SVG normaliser that extracts the embedded base64 raster from the SVG document. That works for `.esx` files where Ekahau wraps the raster inside the SVG, but the user's actual file (`Related Digital Michigan 20251119.0_QC Review 1.esx`) has the raster as a *separate* entry referenced by `bitmapImageId` — not embedded inside the SVG. Direct inspection of the .esx confirmed:
  - `image-7f9053c8-…` = 102 MB SVG (the primary `imageId`)
  - `image-beeceb21-…` = 1.8 MB JPEG (`bitmapImageId`, header `FF D8 FF E0` = JFIF, 5000×3571 px)
  - `images.json` flags them with `imageFormat: "SVG"` and `imageFormat: "JPEG"` respectively.
- The SVG normaliser from v2.5.11 stays as a fallback — handles the *other* common Ekahau export pattern where the raster IS embedded in the SVG.

### Added
- `EsxFloorPlanData.BitmapImageId` — parsed from `floorPlans[].bitmapImageId` in `floorPlans.json`.
- `TryLookupOne(esxData, id, out result)` helper — factors out the exact / +ext / fuzzy lookup logic so both `BitmapImageId` and `ImageId` use it.
- `LookupImageBytes` debug log shows when the bitmap companion was used (e.g. `Using bitmapImageId='beeceb21-…' (1,816,148 bytes) instead of imageId='7f9053c8-…' (SVG → raster companion)`).

### Changed
- The "save floor plan images to staging" step (REQ 21) now also uses `LookupImageBytes` so downstream tools see the JPEG raster, not the SVG.

## [2.5.12] — 2026-05-02

### Fixed
- **`VersionCompat.CreateImageType` no longer swallows the underlying exception** — was returning bare `null` on every failure path, which left the user with the unhelpful "VersionCompat.CreateImageType returned null. Check that the temp PNG file is valid…" message. The new overload (`CreateImageType(doc, path, out Exception lastError)`) propagates the real reason (e.g., `FileNotFoundException`, `Autodesk.Revit.Exceptions.ArgumentException`, `OutOfMemoryException`, etc.) so the caller can show it to the user.
- Reflection-wrapped `TargetInvocationException`s are unwrapped to the real inner exception before being reported, so the user sees `ArgumentException: Image format not supported` instead of `TargetInvocationException: Exception has been thrown by the target of an invocation`.

### Added
- Both image-creation entry points (`PlaceImageAndAskForVerification` step 5 + `OfferVisualAlignmentCoreImpl` initial-image transaction) now show, on failure:
  - The underlying exception type + message
  - The temp file path
  - The file size in bytes
  - The first 16 bytes in hex (with the same PNG/JPEG/BMP/GIF/TIFF/WebP signature reference table as v2.5.10's dimension-probe diagnostic)

### Why
v2.5.11 successfully extracted the embedded raster from SVG-wrapped `.esx` floor plans, but the user's next dialog was `"VersionCompat.CreateImageType returned null"` with no explanation of why Revit refused the extracted raster. The new diagnostic surfaces both the exception and the file's actual header bytes, so the next iteration can target the real root cause (whether the embedded raster has a valid PNG/JPEG header, whether Revit's WIC engine rejected it, or whether something else in the placement transaction failed).

## [2.5.11] — 2026-05-02

### Fixed
- **SVG floor-plan support** — some Ekahau exports store floor plans as SVG (XML) inside the `.esx` ZIP rather than raw PNG/JPEG. v2.5.10's hex dump diagnostic revealed the file content starts with `3C3F786D 6C207665` (`<?xml ve…`), confirming SVG/XML — which Revit's WIC engine cannot render directly. The plugin now detects SVG content and extracts the embedded base64-encoded raster (the typical Ekahau wrapper pattern), then hands the raster to Revit unchanged.

### Added
- New `ImageNormalizer` helper (`ImageNormalizer.cs`):
  - `IsSvgOrXmlContent(bytes)` — sniffs UTF-8 BOM + leading whitespace, then checks for `<?xml` / `<svg` prefix.
  - `TryExtractEmbeddedRaster(svgBytes)` — regex-extracts `<image href="data:image/png;base64,…">` (and the `xlink:href` variant), strips intra-attribute whitespace, base64-decodes the payload, returns the raster bytes.
  - `NormalizeIfSvg(inputBytes)` — single-call helper returning `(Bytes, WasSvg, ExtractionSucceeded)`. Callers can branch on whether SVG was detected and whether extraction succeeded.
- Both `PlaceImageAndAskForVerification` and `OfferVisualAlignmentCoreImpl` route image bytes through `NormalizeIfSvg` before writing to the temp file.
- When SVG is detected but no embedded raster is found, the user sees a clear dialog with workaround steps (re-save with PNG output in Ekahau Pro) instead of an opaque "couldn't determine image dimensions" error.

### Why this matters
The user's `Related Digital Michigan 20251119.0_QC Review 1.esx` has a 102 MB SVG-wrapped floor plan. Without normalization, Revit's WIC engine refuses to decode the XML, the dimensions read fails, and the whole overlay/alignment flow short-circuits to "0 APs placed". With normalization, the embedded PNG raster is extracted (~typically a small fraction of the SVG size) and the existing pipeline runs unchanged.

### Why we didn't add a full SVG renderer
Full vector→raster SVG rendering (via Svg.NET or SkiaSharp.Svg) would add ~5 MB to the MSI and a NuGet dependency that complicates the multi-target net48/net8/net10 build. The base64-extraction approach handles the common Ekahau export pattern with zero new dependencies. We can revisit if a real "vector-only" SVG (no embedded raster) appears in the wild.

## [2.5.10] — 2026-05-02

### Added
- **WPF/WIC fallback** in `ReadImageDimensions` — if the manual PNG header parse fails AND GDI+ fails, we now try `System.Windows.Media.Imaging.BitmapDecoder.Create` which uses the same WIC engine Revit itself uses. WIC handles JPEG, BMP, TIFF, GIF, WebP, plus exotic PNG variants that both GDI+ and our PNG parser may reject.
- **Hex dump of the first 16 bytes** in the dimension-failure error dialog — lets you (and us) instantly identify what format Ekahau actually exported, even if it's something unexpected:
  ```
  File size : 524,984 bytes
  First 16 bytes (hex):
    89504E47 0D0A1A0A 0000000D 49484452

  Common signatures:
    PNG  = 89 50 4E 47 0D 0A 1A 0A
    JPEG = FF D8 FF
    BMP  = 42 4D
    GIF  = 47 49 46 38
    TIFF = 49 49 2A 00 (LE) or 4D 4D 00 2A (BE)
    WebP = 52 49 46 46 ... 57 45 42 50
  ```

### Why
v2.5.9's diagnostic dialog showed "Could not determine image dimensions" — meaning both PNG header parse and GDI+ failed. The hex dump will reveal whether the file is actually a non-PNG format (JPEG/WebP/etc.) that we should add explicit support for, or something else entirely (corrupt bytes, JSON metadata, etc.).

## [2.5.9] — 2026-05-02

### Fixed
- **Image dimension read no longer goes through GDI+** — was throwing `System.OutOfMemoryException` (a GDI+ misnomer that actually means "I don't understand this PNG variant"). Ekahau's `.esx` PNGs use 16-bit color depth or exotic interlace modes that GDI+ rejects, even though the files are perfectly valid PNGs.
- **Visual alignment overlay is now placed correctly** — was silently failing on Ekahau-exported PNGs because both `PlaceFloorPlanImage` AND `OfferVisualAlignmentCore` used `System.Drawing.Image.FromFile`. v2.5.8 surfaced the error; v2.5.9 fixes the underlying cause.

### Added
- New `ReadImageDimensions(path)` helper parses the PNG IHDR chunk directly from file bytes — no GDI+ dependency. Reads:
  - bytes 0-7   PNG signature `89 50 4E 47 0D 0A 1A 0A`
  - bytes 16-19 Width (big-endian 32-bit)
  - bytes 20-23 Height (big-endian 32-bit)

  Falls back to GDI+ for non-PNG formats (JPEG / BMP / TIFF). Returns `(0, 0)` only when both paths fail.

- `EsxMarkerOps.PlaceFloorPlanImage` and `EsxReadCommand.OfferVisualAlignmentCoreImpl` both use the new helper. No more silent failures on Ekahau PNGs.

### Why this works for Revit when GDI+ fails
Revit's `ImageType.Create` uses Windows Imaging Component (WIC) under the hood, which is much more permissive about PNG variants than legacy GDI+. So Revit can place the image fine — we just couldn't read its dimensions to scale it. With the manual PNG header parse, dimensions come straight from the file bytes and Revit handles the rendering.

## [2.5.8] — 2026-05-02

### Fixed
- **Manual alignment was failing silently** when invoked from the verification dialog — clicking "Image is misaligned — manually align" went straight to "0 APs placed" with no PickPoint, no intro, no error. User saw the verification dialog but the alignment workflow itself never appeared.

### Added
- Wrapped `OfferVisualAlignmentCore` in a top-level try/catch (`OfferVisualAlignmentCoreImpl` is the actual body now). Any unhandled exception is:
  - Logged via `Debug.WriteLine` with full stack trace (DebugView)
  - Surfaced to the user in a TaskDialog naming the exception type + message
- Replaced 3 silent `return null` paths with explicit `throw new InvalidOperationException(...)` carrying the failed step name (write temp file / read PNG dimensions / create ImageType / place ImageInstance).
- Added `Debug.WriteLine` checkpoints at every major step:
  ```
  [ESX Read] OfferVisualAlignmentCore: start (skipIntro=True, fp='Level 1')
  [ESX Read] About to place initial image: center=(...,...,...), size=(...x...) ft
  [ESX Read] Initial image placed, ElementId=12345
  ```
- Verification handler now wraps the alignment call in try/catch, surfacing any thrown exception as a TaskDialog so the user sees WHY alignment failed instead of a silent skip.

### Result
On v2.5.8 if you click "manually align" and it still fails, you'll see a precise error dialog (or DebugView trace) showing the exact step + exception type that caused the silent skip — no more guessing.

## [2.5.7] — 2026-05-02

### Fixed
- **Image extraction now uses robust 4-tier matching** (was: exact match only). Different `.esx` exports name image entries differently — some have `image-{uuid}`, some have `image-{uuid}.png`. Previous parser stripped only the `image-` prefix, so an entry called `image-abc-123.png` became key `abc-123.png` and didn't match `floorPlans[].imageId == "abc-123"`. Result: image extraction silently failed → no overlay → no manual alignment → straight to "0 APs placed".

### Added
- **`LookupImageBytes` helper** with 4-tier matching:
  1. Exact match on `fp.ImageId`
  2. `fp.ImageId` + common image extensions (`.png`/`.jpg`/`.jpeg`/`.bmp`)
  3. Fuzzy: any key starting with `fp.ImageId`
  4. Single-image fallback (when there's exactly one image entry)
- **Parser now strips common image extensions** when storing entry keys, AND keeps the original full key as a defensive duplicate. Either lookup direction works.
- **"No Image" diagnostic dialog now includes**:
  - Floor plan name + ID
  - The exact ID being looked up
  - Total image-entry count in the .esx
  - Up to 10 available image keys (so you can spot the mismatch immediately)
  - Likely-cause checklist
  - "Please screenshot this dialog when reporting the issue"
- `Debug.WriteLine` of every image lookup attempt (visible in DebugView):
  ```
  [ESX Read] Image lookup for 'Level 1': fp.ImageId='abc-123',
    matched=YES, bytes=524984, available=2 keys=[abc-123, xyz-789]
  ```

## [2.5.6] — 2026-05-01

### Fixed
- **Image overlay verification step was silently skipped when a floor had 0 APs.** The user reported "no overlay shown, no manual alignment offered, jumps straight to 0 APs placed". Two early-exit guards (`floorAps.Count == 0` for AP-to-floor-plan ID mismatches, and `apsToPlace.Count == 0` for user-unchecked-all) ran BEFORE the overlay step, so a wrong floor match or stale AP list would dismiss the floor with no visual feedback.

### Changed
- **Overlay verification now runs unconditionally per floor**, BEFORE the AP-count check. Users always get to confirm floor alignment + manually align if needed, even when zero APs end up matching this floor.
- **New "no APs on this floor" diagnostic dialog**: when `floorAps.Count == 0`, the user now sees an explicit dialog explaining the AP-vs-floor-plan ID mismatch with the three most likely causes (truly empty floor / wrong view matched / floor renamed in Ekahau) — instead of a silent skip.
- Added Debug.WriteLine of the AP→FloorPlan id matching: total AP count, this floor's match count, list of all distinct FloorPlanIds in the .esx. Visible in DebugView for troubleshooting.

## [2.5.5] — 2026-05-01

### Changed
- **Manual alignment now reachable from the per-floor verification step** — works for ALL calibration tiers, not just Tier 3. Previously the visual two-point alignment only ran when the .esx had no `revitAnchor` and no `.ekahau-cal.json`. Now even a Tier-1 (revitAnchor present) or Tier-2 (.cal.json found) auto-calibration can be manually corrected if the user spots misalignment in the verification overlay.
- Verification dialog renamed CommandLink2 from **"Alignment is off — abort this floor"** to **"Image is misaligned — manually align with two points"**. Picking it triggers the visual two-point alignment, then recurses back into the verification step so the user can confirm or re-align again.
- A separate **Cancel** button on the verification dialog still aborts the floor.
- `OfferVisualAlignmentCore` gains a `skipIntro` parameter — when called from the verification step the intro dialog is skipped (the user already opted in via "Manually align").

### Result
- After a Bug Fix #14 scenario (anchor exists but is wrong), the user can now fix it in-place without re-running ESX Read or re-exporting the .esx.

## [2.5.4] — 2026-05-01

### Removed
- **Manual coordinate-input dialog** (`TwoPointPixelDialog`) and the typed-coordinates calibration path entirely. Users have no reliable way to look up Ekahau pixel/metre values for arbitrary reference points; visual alignment (clicking on the placed image) replaces it cleanly. Bug Fix #16.
- The "Type Ekahau coordinates" command link is gone from the manual-calibration intro dialog. Two options remain: **Start visual alignment** / **Skip**.
- `BuildAnchorFromTwoPoints` helper deleted (only the visual-alignment path's rotation-aware anchor synthesis is kept).

### Result
- Zero coordinate typing anywhere in the workflow. All four calibration clicks happen directly in the Revit view (two on the Revit model, two on the placed Ekahau image).

## [2.5.3] — 2026-05-01

### Fixed
- **Revit 2024 / 2023 startup crash**: `System.IO.FileNotFoundException: Could not load file or assembly 'System.Runtime, Version=8.0.0.0'`. Caused by `System.Text.Json 8.0.5` (the version we pulled in v2.5.2) which has transitive references to `System.Runtime 8.0.0.0` — a .NET 8 facade assembly that doesn't exist on .NET Framework 4.8.  Revit also doesn't honour `<dll>.dll.config` binding redirects so a redirect-based fix was off the table.
- Pinned `System.Text.Json` to `6.0.10` for the net48 build — the last version that compiles cleanly against the net48 facade graph (all references resolve to net48-compatible BCL polyfills like `System.Memory 4.0.1.1`, `Microsoft.Bcl.AsyncInterfaces 6.0.0.0`).
- Verified via `MetadataLoadContext` that the new `System.Text.Json.dll` references contain **no `8.0.0.0` versions** — only 4.x / 6.x facades that .NET Framework 4.8 can load.

### Unaffected
- net8 (Revit 2025 / 2026) and net10 (Revit 2027) builds are unchanged — those runtimes ship `System.Text.Json` in the BCL itself, no NuGet needed.

## [2.5.2] — 2026-05-01

### Fixed
- **MSI now installs to Revit 2023 + 2024** in addition to 2025/2026/2027. Previous releases skipped the legacy versions because the net48 build needs 8 supporting BCL DLLs (System.Text.Json + dependencies) which were only available via `install.ps1`. v2.5.2 ships those DLLs inside the MSI so a single double-click covers all five Revit versions.

### Changed
- MSI grew from ~232 KB to ~440 KB to accommodate the net48 BCL DLLs for Revit 2023 / 2024.
- Per-user AppData install scope unchanged — all five versions install to `%APPDATA%\Autodesk\Revit\Addins\YYYY\` with no admin / UAC.

## [2.5.1] — 2026-05-01

### Added
- **About / Version dialog** — new ribbon button on a small "Help" panel. Shows:
  - Plugin version + release date + last build timestamp of the loaded DLL
  - .NET runtime (Framework 4.8 / .NET 8 / .NET 10)
  - Detected Revit version
  - Install scope (Per-user / All-users) — auto-detected from DLL path
  - Full DLL path
  - Buttons: open project page, view releases (check for updates), report issue, open install folder

### Confirmed
- MSI install scope is **per-user** under `%APPDATA%\Autodesk\Revit\Addins\YYYY\` (set in v2.4.0). No UAC prompt, no admin required, no writes to Program Files / ProgramData / HKLM. Suitable for IT-managed machines that block writes to system locations.

## [2.5.0] — 2026-04-22

### Added
- **ESX Read — Visual two-point alignment** (Tier 3b calibration). When a `.esx` has no `revitAnchor` and no `.ekahau-cal.json`, the user can now visually align the floor plan instead of typing Ekahau coordinates:
  1. Plugin drops the Ekahau image into the matched view at an estimated position
  2. User clicks **two pairs** of matching points: each pair = same point clicked once on the Revit model and once on the floor-plan image (in the same view)
  3. Plugin recovers **scale + rotation + translation** from the two correspondences
  4. Image snaps into the calibrated pose (rotation included) for visual verification
  5. Verification dialog shows scale (mm/px), rotation (°), Pair-2 residual (mm), with retry / continue / cancel
- The synthesised `EsxRevitAnchorData` populates `XformBasisXx/XY/YX/YY` (rotation matrix) so the existing Mode-1 `BuildEkahauToRevitXform` handles rotation correctly — no special-casing in the AP-coordinate pipeline.
- The manual-calibration intro dialog now offers three options: **Visual alignment (recommended)** / Type Ekahau coordinates / Skip.

### Why
Designer creates the Ekahau project from a PDF (no Revit involvement) → the resulting `.esx` has no coordinate relationship to the Revit model. Typing Ekahau pixel coordinates from another window is error-prone. Clicking the same point twice — once on the model, once on the image — is more intuitive and naturally handles rotation between the two coordinate systems.

## [2.4.1] — 2026-04-22

### Added
- **DWG Export ribbon icon** — 32 px and 16 px PNGs added to `Resources/`. Visually distinct red right-arrow + "DWG" label vs ESX Export's blue arrow, so the two export commands are easy to tell apart at a glance.

## [2.4.0] — 2026-04-22

### Added
- **ESX Read — Two-point manual calibration** (Tier 3a fallback). When a `.esx` has no `revitAnchor` and no `.ekahau-cal.json` is found, the user can now click two reference points in the Revit view and type the corresponding Ekahau coordinates (in metres or pixels). The plugin computes a scale + translation transform, sanity-checks it against the `.esx`'s declared `metersPerUnit`, and synthesises an `EsxRevitAnchorData` so the rest of the import pipeline works unchanged.
- New `TwoPointPixelDialog` WPF dialog with metres/pixels input mode toggle.

### Changed
- **MSI installer is now per-user** (`Scope="perUser"`), installs to `%APPDATA%\Autodesk\Revit\Addins\YYYY\`. No admin / UAC required, no writes to `Program Files` or `ProgramData`, all registry entries under HKCU. Same path for Revit 2025 / 2026 / 2027 — Revit's per-user path was unaffected by the 2027 all-users path change.
- `install.ps1` simplified — no longer takes a `-Scope` parameter; always installs per-user to AppData.
- `fix-revit-2027.bat` now installs to AppData (no admin needed); still cleans up any prior installs from the system locations.

## [2.3.5] — 2026-04-21

### Changed
- **WiFi Plan view**: added `OST_ElectricalEquipment`, `OST_ElectricalFixtures`, `OST_GenericModel`, `OST_Conduit`, `OST_ConduitFitting`, `OST_CableTray`, `OST_CableTrayFitting` to the visible-categories set. Generic Models are still filtered down to placed APs only by the existing `Ekahau - Hide Non-AP Elements` view filter (Bug Fix #11).

## [2.3.4] — 2026-04-21

### Changed
- **AP Place cleanup is now unconditional** (REQ 5 dialog removed). After successful placement, the plugin removes:
  - All per-AP marker IDs (`MarkerElementIds`)
  - All per-floor overlay IDs (`OverlayElementIds`) — image overlay, CropBox corner crosses, band legend
  - Workset safety net — sweeps any element on the `Ekahau AP Markers` workset in the source views
  - Clears marker IDs from staging JSON so re-runs don't try to delete already-gone elements

### Added
- New `OverlayElementIds` field on `ApStagingFloor` for floor-level temp elements.

## [2.3.3] — 2026-04-21

### Fixed
- **Bug Fix #15** — `PlaceFloorPlanImage` rewritten for robustness: reads actual PNG dimensions, uses `BoxPlacement.Center`, places at `view.GenLevel.Elevation`, sets width via `imgInst.Width` property with `RASTER_SHEETWIDTH` parameter fallback, verifies via bounding-box read-back, comprehensive `Debug.WriteLine` logging at every step, user-facing TaskDialog on real failures (instead of silent null returns).

## [2.3.2] — 2026-04-21

### Changed
- **ESX Export resolution dialog**: replaced 3 options (1 000 / 2 000 / 4 000 px) with **5 options** (2 000 / 4 000 / 8 000 / 10 000 / 15 000 px); default raised from 2 000 to **4 000**.
- **Image export DPI**: 72 → **300**.
- **FitDirection**: now auto-picked from CropBox aspect (Vertical when `cropH > cropW × 1.2`, otherwise Horizontal) so PixelSize applies to the longer dimension.

## [2.3.1] — 2026-04-21

### Added
- **DWG calibration fallback enhancements** in ESX Read:
  - Browse-for-file fallback when no `.ekahau-cal.json` is auto-discovered.
  - Multi-cal-file picker when 2-4 candidates exist (TaskDialog with command links).
  - Pixel-offset math now centres the CropBox inside the Ekahau image (handles Ekahau's typical DWG-import margins).

## [2.3.0] — 2026-04-21

### Added
- **DWG Export** command (new ribbon button between ESX Export and ESX Read). Exports floor-plan views as `.dwg` tuned for Ekahau (mm units, AutoCAD R2018, AIA layers, ACIS solids, true colour, shared coords). Optional Clean mode duplicates the view and hides everything except WiFi-relevant categories.
- **Calibration sidecar** (`<view>.ekahau-cal.json`) written next to each `.dwg`. Stores CropBox bounds in Revit feet, DWG export unit, view transform, and Ekahau import instructions.
- **ESX Read calibration fallback** (Tier 2): when an `.esx` has no `revitAnchor`, scan the same folder for `.ekahau-cal.json`, name-match against `revitViewName`, apply the calibration to anchorless floor plans.

### Changed
- `csproj` now uses `<UseWindowsForms>true</UseWindowsForms>` alongside `UseWPF` so `FolderBrowserDialog` resolves on all three frameworks.

## [2.2.0] — 2026-04-21

### Added
- **ESX Read — mandatory image overlay verification step**. After cleanup and before AP marker placement, places the Ekahau floor-plan PNG as a reference overlay in the matched view, draws four green `+` crosses at the CropBox corners, switches to the view, and asks the user to confirm alignment. Three-button choice: continue / abort this floor / skip verification.
- New `EsxMarkerOps.PlaceFloorPlanImage` and `EsxMarkerOps.DrawReferenceCrosses` helpers.

## [2.1.5] — 2026-04-20

### Added
- Defensive **polymorphic `object` JSON converter** in `EsxExportCommand` so nested `Dictionary<string, object>` values in `floorPlans.json` (notably `revitAnchor`) are guaranteed to serialise via runtime type — protects against the .NET 6+ behaviour change where `object`-typed properties default to declared-type serialisation.

## [2.1.2 / 2.1.3] — 2026-04-20

### Fixed
- **Revit 2027 install path** corrected. Revit 2027 rejects manifests under `C:\ProgramData\...` (stricter add-in isolation) and reserves `C:\Program Files\Autodesk\Revit 2027\AddIns\` for code-signed Autodesk-internal add-ins. The MSI now installs to `C:\Program Files\Autodesk\Revit\Addins\2027\` (the new third-party all-users path) using the standard layered manifest layout.

## [2.1.0] — 2026-04-20

### Added
- **Revit 2027 support** via a third build target (`net10.0-windows`). `VersionCompat` reuses the modern API path for both `REVIT_NET8` and `REVIT_NET10`. The `Autodesk.Revit.SDK 2027.0.0` NuGet hasn't shipped yet, so the net10 build references `2026.0.0.9999` as a placeholder; bump the version when 2027 ships.
- `install.ps1` updated for the new Revit 2027 paths.

## [2.0.0] — 2026-04-20

### Added
- **Multi-version Revit compatibility** (2023-2027) via multi-targeted `csproj`:
  - `net48` for Revit 2023 / 2024 (Revit 2024 SDK)
  - `net8.0-windows` for Revit 2025 / 2026 (Revit 2025 SDK)
- **`VersionCompat` API shim** abstracts every version-sensitive API:
  - `ElementId.Value` (long) ↔ `IntegerValue` (int)
  - `SpecTypeId` ↔ `ParameterType` (reflection-based fallback for Revit 2023)
  - `ImageType.Create` overload differences
  - `ParameterFilterRuleFactory.CreateHasNoValueParameterRule` with fallback
- **`PolyfillExtensions`** for net48: `string.Contains(string, StringComparison)` (introduced in .NET Core 2.1).
- `install.ps1` multi-version installer; WiX MSI extended to install on all detected versions.

### Changed
- `LangVersion` lowered to 10.0 (works on both net48 and net8/10).
- `JIT` safety: APIs that might fail to load on the older runtime are isolated in `[MethodImpl(MethodImplOptions.NoInlining)]` methods so try/catch can recover gracefully.

## [1.9.0] — Earlier

### Added
- **Ribbon button icons** — 10 PNG resources embedded into the assembly, loaded via `IconHelper.LoadIcon`.

## [1.8.x] — Earlier

### Fixed
- **Bug Fix #11** — WiFi Plan view: hide non-AP Generic Models via a project-level `ParameterFilterElement` (filter rule: `Ekahau_AP_Name` has no value). Removes false-positive Generic Models from the view.
- **Bug Fix #12** — AP labels: switched from `IndependentTag` (which shows whatever the tag family's label is bound to — often Type Mark) to `TextNote` containing the explicit instance Mark.
- **Bug Fix #13** — `EndpointSnapper` threshold reduced from 2.0 ft to 0.5 ft + angle-preservation guard (rejects snaps that tilt either segment by more than 2°). Stops the snapper from pulling unrelated walls together.
- **Bug Fix #14** — `revitAnchor` defensive serialisation (polymorphic converter).

## [1.4.0] — Earlier

### Added
- **AP Place** command: 18-REQ implementation including three-column family picker, per-AP confirmation, batch transactions, workset assignment, **12 Ekahau shared parameters**, optional WiFi Plan view, optional per-level AP schedule.
- **WiFi Plan view** generator (Bug Fix #11 view filter, AP highlighting, TextNote labels).
- **AP Schedule** generator with per-level filtering.

## [1.0.0 — 1.3.x]

Initial implementation:
- **Param Config** — `Ekahau_WallType` shared parameter creation, type collection, mapping dialog, ExtensibleStorage for linked-model overrides.
- **ESX Export** — full pipeline (PNG render, wall/door/window geometry, mapping review, ESX ZIP).
- **ESX Read** — `.esx` parsing, floor matching, AP review, preview marker placement, staging JSON.

[2.4.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.4.0
[2.3.5]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.5
[2.3.4]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.4
[2.3.3]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.3
[2.3.2]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.2
[2.3.1]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.1
[2.3.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.3.0
[2.2.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.2.0
[2.1.5]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.1.5
[2.1.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.1.0
[2.0.0]: https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.0.0
