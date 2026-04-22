using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// Abstracts Revit API differences across 2023-2027.
    ///
    /// One assembly is compiled per runtime:
    ///   • net48           ⇒ Revit 2023 + 2024  (REVIT_LEGACY)
    ///   • net8.0-windows  ⇒ Revit 2025 + 2026 + 2027  (REVIT_NET8)
    ///
    /// Within the net48 assembly the SDK reference is Revit 2024.  Any
    /// API that was removed/marked-internal in 2024 (e.g. ParameterType)
    /// must be reached via reflection so the assembly still loads on
    /// Revit 2023 where the type is still public.
    ///
    /// Calls that might fail to JIT on the older runtime are isolated in
    /// their own NoInlining methods so a try/catch around the call site
    /// can still recover.
    /// </summary>
    public static class VersionCompat
    {
        // ══════════════════════════════════════════════════════════════
        //  ElementId — int (2023-2024) vs long (2025+)
        // ══════════════════════════════════════════════════════════════

        /// <summary>Numeric value of an ElementId as long on every Revit version.</summary>
        public static long GetIdValue(ElementId id)
        {
            if (id == null) return -1;
#if REVIT_NET8 || REVIT_NET10
            return id.Value;
#else
#pragma warning disable CS0618 // IntegerValue is the only API on Revit 2023
            return id.IntegerValue;
#pragma warning restore CS0618
#endif
        }

        /// <summary>Construct an ElementId from a long, narrowing on net48 if needed.</summary>
        public static ElementId MakeId(long value)
        {
#if REVIT_NET8 || REVIT_NET10
            return new ElementId(value);
#else
#pragma warning disable CS0618 // ElementId(int) is the only ctor on Revit 2023
            return new ElementId((int)value);
#pragma warning restore CS0618
#endif
        }

        /// <summary>True when an ElementId is null or InvalidElementId.</summary>
        public static bool IsInvalidId(ElementId id)
            => id == null || id == ElementId.InvalidElementId;

        // ══════════════════════════════════════════════════════════════
        //  Shared parameter creation —
        //  ParameterType (2023) vs SpecTypeId (2024+)
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Build an <see cref="ExternalDefinitionCreationOptions"/> for a
        /// shared parameter, hiding the ParameterType→SpecTypeId switch.
        /// </summary>
        public static ExternalDefinitionCreationOptions CreateParamOptions(
            string name, bool isText)
        {
            ExternalDefinitionCreationOptions opts = null;
#if REVIT_NET8 || REVIT_NET10
            // Revit 2025+: SpecTypeId only (ParameterType has been removed).
            opts = CreateOptionsWithSpecTypeId(name, isText);
#else
            // Revit 2024 SDK: SpecTypeId compiles, but on a Revit 2023 host
            // the type isn't loaded — try the modern path first, and on
            // any failure use reflection to reach ParameterType (which is
            // still public on 2023, just internal in the 2024 SDK shim).
            try { opts = CreateOptionsWithSpecTypeId(name, isText); }
            catch { /* fall through */ }
            if (opts == null) opts = CreateOptionsWithParameterTypeReflection(name, isText);
#endif
            if (opts != null) opts.Visible = true;
            return opts;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ExternalDefinitionCreationOptions CreateOptionsWithSpecTypeId(
            string name, bool isText)
        {
            var specType = isText ? SpecTypeId.String.Text : SpecTypeId.Number;
            return new ExternalDefinitionCreationOptions(name, specType);
        }

#if REVIT_LEGACY
        /// <summary>
        /// Reflection fallback for Revit 2023.  ParameterType is internal
        /// in the 2024 SDK NuGet, so we cannot reference it directly, but
        /// it is still public in the live RevitAPI.dll on a 2023 host.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ExternalDefinitionCreationOptions CreateOptionsWithParameterTypeReflection(
            string name, bool isText)
        {
            try
            {
                var revitApiAsm = typeof(ExternalDefinitionCreationOptions).Assembly;
                var ptType = revitApiAsm.GetType(
                    "Autodesk.Revit.DB.ParameterType", throwOnError: false);
                if (ptType == null || !ptType.IsEnum) return null;

                object ptValue = Enum.Parse(ptType, isText ? "Text" : "Number");

                var ctor = typeof(ExternalDefinitionCreationOptions)
                    .GetConstructor(new[] { typeof(string), ptType });
                if (ctor == null) return null;

                return (ExternalDefinitionCreationOptions)ctor.Invoke(new[] { (object)name, ptValue });
            }
            catch { return null; }
        }
#endif

        // ══════════════════════════════════════════════════════════════
        //  Image type creation
        //  Revit 2024 SDK and 2025+ both require ImageTypeOptions; only
        //  the 3-arg form (with ImageTypeSource) exists in 2025.  On the
        //  2024 SDK the 2-arg form is also unavailable in this NuGet, so
        //  net48 falls back to reflection if needed.
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Create an ImageType from a file path, returning null on failure.
        /// </summary>
        public static ImageType CreateImageType(Document doc, string imagePath)
        {
#if REVIT_NET8 || REVIT_NET10
            try { return CreateImageTypeWithSource(doc, imagePath); }
            catch { return null; }
#else
            // Try the 2024-style 2-arg ImageTypeOptions ctor via reflection,
            // then the 2023-style ImageType.Create(doc, path) via reflection.
            try { return CreateImageTypeReflection(doc, imagePath); }
            catch { return null; }
#endif
        }

#if REVIT_NET8 || REVIT_NET10
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImageType CreateImageTypeWithSource(Document doc, string imagePath)
        {
            var opts = new ImageTypeOptions(imagePath, false, ImageTypeSource.Import);
            return ImageType.Create(doc, opts);
        }
#endif

#if REVIT_LEGACY
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImageType CreateImageTypeReflection(Document doc, string imagePath)
        {
            // Try ImageTypeOptions(string, bool) — Revit 2024 runtime
            try
            {
                var optsType = typeof(ImageTypeOptions);
                var ctor2 = optsType.GetConstructor(new[] { typeof(string), typeof(bool) });
                if (ctor2 != null)
                {
                    var opts = ctor2.Invoke(new object[] { imagePath, false });
                    var createMI = typeof(ImageType).GetMethod(
                        "Create", new[] { typeof(Document), optsType });
                    if (createMI != null)
                        return (ImageType)createMI.Invoke(null, new[] { doc, opts });
                }
            }
            catch { }

            // Try ImageType.Create(Document, string) — Revit 2023 runtime
            try
            {
                var createMI = typeof(ImageType).GetMethod(
                    "Create", new[] { typeof(Document), typeof(string) });
                if (createMI != null)
                    return (ImageType)createMI.Invoke(null, new object[] { doc, imagePath });
            }
            catch { }

            return null;
        }
#endif

        // ══════════════════════════════════════════════════════════════
        //  Tag creation —
        //  IndependentTag.Create exists on every supported version.
        //  doc.Create.NewTag was removed by the 2024 SDK NuGet, so on
        //  legacy we'd need reflection; in practice ApPlaceCommand uses
        //  TextNote (Bug Fix #12), so this helper is just a safety net.
        // ══════════════════════════════════════════════════════════════

        public static IndependentTag CreateTag(
            Document doc, View view, Element element,
            XYZ tagPoint, bool addLeader = false)
        {
            try { return CreateTagModern(doc, view, element, tagPoint, addLeader); }
            catch { return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IndependentTag CreateTagModern(
            Document doc, View view, Element element, XYZ tagPoint, bool addLeader)
        {
            return IndependentTag.Create(
                doc, view.Id, new Reference(element),
                addLeader, TagMode.TM_ADDBY_CATEGORY,
                TagOrientation.Horizontal, tagPoint);
        }

        // ══════════════════════════════════════════════════════════════
        //  Filter rules — HasNoValue (2023+) with Equals fallback
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Build a "<paramref name="paramId"/> has no value" rule for a
        /// view/schedule filter.  Falls back to Equals empty string if the
        /// HasNoValue rule is unavailable.
        /// </summary>
        public static FilterRule CreateHasNoValueRule(ElementId paramId)
        {
            try
            {
                return ParameterFilterRuleFactory
                    .CreateHasNoValueParameterRule(paramId);
            }
            catch
            {
                try
                {
                    return ParameterFilterRuleFactory
                        .CreateEqualsRule(paramId, "");
                }
                catch { return null; }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Image export — view selection
        // ══════════════════════════════════════════════════════════════

        /// <summary>Configure ImageExportOptions to export a single view.</summary>
        public static void SetExportViews(ImageExportOptions opts, ElementId viewId)
        {
            var ids = new List<ElementId> { viewId };
            opts.SetViewsAndSheets(ids);
            opts.ExportRange = ExportRange.SetOfViews;
        }
    }
}
