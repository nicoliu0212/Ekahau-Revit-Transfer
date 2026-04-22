using System.Collections.Generic;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// Keyword-based matching of Revit type/material names to Ekahau preset keys.
    /// Order matters: first match wins.  Case-insensitive.
    /// </summary>
    public static class KeywordMatcher
    {
        private static readonly List<(string[] Keywords, string PresetKey)> KeywordMap =
            new List<(string[], string)>
            {
                (new[] { "concrete", "conc", "\u6df7\u51dd\u571f", "beton", "rc " }, "Concrete"),
                (new[] { "brick", "\u7816", "masonry", "cmu", "block" }, "Brick"),
                (new[] { "curtain wall", "curtain_wall", "\u5e55\u5899" }, "CurtainWall"),
                (new[] { "glass", "\u7535\u7483", "glazing", "glazed" }, "Glass"),
                (new[] { "drywall", "gypsum", "\u77f3\u818f", "plasterboard", "plaster", "gyp", "partition" }, "Drywall"),
                (new[] { "metal", "steel", "\u94a2", "alumin", "iron" }, "Metal"),
                (new[] { "wood", "\u6728", "timber", "lumber" }, "Wood"),
                (new[] { "elevator", "\u7535\u68af", "lift" }, "Elevator"),
                (new[] { "metal door", "fire door", "hollow metal", "\u94a2\u95e8", "\u9632\u706b\u95e8" }, "MetalDoor"),
                (new[] { "glass door", "\u7535\u7483\u95e8", "glazed door" }, "GlassDoor"),
                (new[] { "door", "\u95e8" }, "WoodDoor"),
                (new[] { "window", "\u7a97" }, "Window"),
            };

        /// <summary>Returns the matched preset key, or null if no match.</summary>
        public static string Match(string text)
        {
            var result = MatchWithKeyword(text);
            return result?.PresetKey;
        }

        /// <summary>
        /// Req 7: Returns (PresetKey, MatchedKeyword) or null — caller uses MatchedKeyword
        /// to build a human-readable suggestion source description.
        /// </summary>
        public static (string PresetKey, string MatchedKeyword)? MatchWithKeyword(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string lower = text.ToLowerInvariant();
            foreach (var (keywords, presetKey) in KeywordMap)
            {
                foreach (string kw in keywords)
                {
                    if (lower.Contains(kw.ToLowerInvariant()))
                        return (presetKey, kw);
                }
            }
            return null;
        }
    }
}
