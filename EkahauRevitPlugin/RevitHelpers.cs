using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace EkahauRevitPlugin
{
    public static class RevitHelpers
    {
        /// <summary>Safely get the Name of an element, trying multiple approaches.</summary>
        public static string SafeName(Element elem)
        {
            if (elem == null) return "(unknown)";
            try { string n = elem.Name; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try
            {
                var p = elem.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);
                if (p != null && p.HasValue) return p.AsString();
            }
            catch { }
            try
            {
                var p = elem.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME);
                if (p != null && p.HasValue) return p.AsString();
            }
            catch { }
            return "(unknown)";
        }

        /// <summary>Read a string parameter value from an element.</summary>
        public static string ReadStringParam(Element elem, string paramName)
        {
            try
            {
                var p = elem.LookupParameter(paramName);
                if (p != null && p.HasValue)
                    return (p.AsString() ?? "").Trim();
            }
            catch { }
            return "";
        }

        /// <summary>Get compound structure material names from a wall type.</summary>
        public static List<string> GetCompoundMaterialNames(Document doc, WallType wallType)
        {
            var names = new List<string>();
            try
            {
                var cs = wallType.GetCompoundStructure();
                if (cs == null) return names;
                foreach (var layer in cs.GetLayers())
                {
                    var mid = layer.MaterialId;
                    if (mid == null || mid == ElementId.InvalidElementId) continue;
                    var mat = doc.GetElement(mid);
                    if (mat != null) names.Add(SafeName(mat));
                }
            }
            catch { }
            return names;
        }

        /// <summary>
        /// Req 7: Auto-suggest an Ekahau preset for a given element type.
        /// Returns (PresetKey, SourceDescription) — source description is displayed
        /// in the dialog tooltip so the user understands why a preset was chosen.
        /// </summary>
        public static (string PresetKey, string SourceDescription) SuggestPreset(
            Document doc, Element elemType, string category)
        {
            // Check curtain wall
            if (elemType is WallType wt)
            {
                try
                {
                    if (wt.Kind == WallKind.Curtain)
                        return ("CurtainWall", "Curtain Wall detected");
                }
                catch { }
            }

            // Keyword match on type name
            string typeName = SafeName(elemType);
            var nameMatch = KeywordMatcher.MatchWithKeyword(typeName);
            if (nameMatch.HasValue)
                return (nameMatch.Value.PresetKey, $"Name match: '{nameMatch.Value.MatchedKeyword}'");

            // Keyword match on compound structure materials
            if (elemType is WallType wallType)
            {
                foreach (string matName in GetCompoundMaterialNames(doc, wallType))
                {
                    var matMatch = KeywordMatcher.MatchWithKeyword(matName);
                    if (matMatch.HasValue)
                        return (matMatch.Value.PresetKey, $"Material match: '{matName}'");
                }
            }

            // Fallback by category
            switch (category)
            {
                case "door":   return ("WoodDoor", $"Default for {category}");
                case "window": return ("Window",   $"Default for {category}");
                default:       return ("Generic",  $"Default for {category}");
            }
        }

        /// <summary>
        /// REQ 21: Get a project-isolated staging directory for temporary files.
        /// Files persist across ESX Read sessions to allow image reuse.
        /// Uses project path hash for isolation (REQ 1 — AP Place).
        /// </summary>
        public static string GetStagingPath(Document doc)
        {
            string projectName = doc.Title ?? "Unknown";
            // Sanitize for use as directory name
            foreach (char c in Path.GetInvalidFileNameChars())
                projectName = projectName.Replace(c, '_');
            if (projectName.Length > 60)
                projectName = projectName.Substring(0, 60);

            // REQ 1: Append path hash for uniqueness when multiple projects share a name
            string hash = GetProjectPathHash(doc);
            string dirName = $"{projectName}_{hash}";

            string path = Path.Combine(Path.GetTempPath(), "EkahauRevitPlugin", dirName);
            try { Directory.CreateDirectory(path); }
            catch { path = Path.GetTempPath(); }
            return path;
        }

        /// <summary>
        /// REQ 1: Compute a short MD5-based hash of the document's file path.
        /// Provides project isolation even when two projects share the same Title.
        /// </summary>
        public static string GetProjectPathHash(Document doc)
        {
            string path = doc.PathName;
            if (string.IsNullOrEmpty(path))
                path = doc.Title ?? "Unknown";
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring(0, 12);
        }

        /// <summary>
        /// REQ 1: Get the full path to the AP staging JSON file.
        /// Written by ESX Read, consumed by AP Place.
        /// </summary>
        public static string GetStagingJsonPath(Document doc)
        {
            return Path.Combine(GetStagingPath(doc), "ap_staging.json");
        }
    }
}
