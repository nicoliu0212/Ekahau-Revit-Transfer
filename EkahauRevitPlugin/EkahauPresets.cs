using System.Collections.Generic;

namespace EkahauRevitPlugin
{
    /// <summary>
    /// Ekahau RF wall-type preset definitions.
    /// Keys are short identifiers stored in the Ekahau_WallType shared parameter.
    /// </summary>
    public class EkahauPreset
    {
        public string Name { get; set; }
        public string Color { get; set; }
        public double AttenuationTwoGHz { get; set; }
        public double AttenuationFiveGHz { get; set; }
        public double AttenuationSixGHz { get; set; }
        public double ReflectionCoefficient { get; set; }
        public double DiffractionCoefficient { get; set; }
        public double DefaultThicknessMeters { get; set; }
    }

    public static class EkahauPresets
    {
        public static readonly Dictionary<string, EkahauPreset> All =
            new Dictionary<string, EkahauPreset>
            {
                ["Concrete"] = new EkahauPreset
                {
                    Name = "Wall, Concrete", Color = "#8E8E8E",
                    AttenuationTwoGHz = 30.0, AttenuationFiveGHz = 50.0, AttenuationSixGHz = 58.0,
                    ReflectionCoefficient = 0.50, DiffractionCoefficient = 17.0, DefaultThicknessMeters = 0.20
                },
                ["Brick"] = new EkahauPreset
                {
                    Name = "Wall, Brick", Color = "#B85C3A",
                    AttenuationTwoGHz = 12.0, AttenuationFiveGHz = 25.0, AttenuationSixGHz = 30.0,
                    ReflectionCoefficient = 0.40, DiffractionCoefficient = 15.0, DefaultThicknessMeters = 0.20
                },
                ["Drywall"] = new EkahauPreset
                {
                    Name = "Wall, Drywall", Color = "#D2C4A0",
                    AttenuationTwoGHz = 3.0, AttenuationFiveGHz = 4.0, AttenuationSixGHz = 5.0,
                    ReflectionCoefficient = 0.20, DiffractionCoefficient = 10.0, DefaultThicknessMeters = 0.10
                },
                ["Glass"] = new EkahauPreset
                {
                    Name = "Wall, Glass", Color = "#88CCEE",
                    AttenuationTwoGHz = 4.0, AttenuationFiveGHz = 8.0, AttenuationSixGHz = 10.0,
                    ReflectionCoefficient = 0.30, DiffractionCoefficient = 12.0, DefaultThicknessMeters = 0.01
                },
                ["CurtainWall"] = new EkahauPreset
                {
                    Name = "Wall, Curtain/Glass", Color = "#66BBDD",
                    AttenuationTwoGHz = 4.0, AttenuationFiveGHz = 8.0, AttenuationSixGHz = 10.0,
                    ReflectionCoefficient = 0.30, DiffractionCoefficient = 12.0, DefaultThicknessMeters = 0.02
                },
                ["Wood"] = new EkahauPreset
                {
                    Name = "Wall, Wood", Color = "#C4A35A",
                    AttenuationTwoGHz = 4.0, AttenuationFiveGHz = 7.0, AttenuationSixGHz = 9.0,
                    ReflectionCoefficient = 0.25, DiffractionCoefficient = 12.0, DefaultThicknessMeters = 0.10
                },
                ["Metal"] = new EkahauPreset
                {
                    Name = "Wall, Metal/Steel", Color = "#707080",
                    AttenuationTwoGHz = 15.0, AttenuationFiveGHz = 25.0, AttenuationSixGHz = 30.0,
                    ReflectionCoefficient = 0.60, DiffractionCoefficient = 18.0, DefaultThicknessMeters = 0.003
                },
                ["WoodDoor"] = new EkahauPreset
                {
                    Name = "Door, Wood", Color = "#C49A6C",
                    AttenuationTwoGHz = 4.0, AttenuationFiveGHz = 6.0, AttenuationSixGHz = 8.0,
                    ReflectionCoefficient = 0.25, DiffractionCoefficient = 10.0, DefaultThicknessMeters = 0.04
                },
                ["MetalDoor"] = new EkahauPreset
                {
                    Name = "Door, Metal", Color = "#808090",
                    AttenuationTwoGHz = 15.0, AttenuationFiveGHz = 22.0, AttenuationSixGHz = 28.0,
                    ReflectionCoefficient = 0.55, DiffractionCoefficient = 15.0, DefaultThicknessMeters = 0.05
                },
                ["GlassDoor"] = new EkahauPreset
                {
                    Name = "Door, Glass", Color = "#88CCEE",
                    AttenuationTwoGHz = 4.0, AttenuationFiveGHz = 8.0, AttenuationSixGHz = 10.0,
                    ReflectionCoefficient = 0.30, DiffractionCoefficient = 12.0, DefaultThicknessMeters = 0.01
                },
                ["Window"] = new EkahauPreset
                {
                    Name = "Window, Standard", Color = "#AADDFF",
                    AttenuationTwoGHz = 4.0, AttenuationFiveGHz = 8.0, AttenuationSixGHz = 10.0,
                    ReflectionCoefficient = 0.30, DiffractionCoefficient = 12.0, DefaultThicknessMeters = 0.006
                },
                ["Elevator"] = new EkahauPreset
                {
                    Name = "Elevator Shaft", Color = "#505050",
                    AttenuationTwoGHz = 30.0, AttenuationFiveGHz = 40.0, AttenuationSixGHz = 50.0,
                    ReflectionCoefficient = 0.60, DiffractionCoefficient = 18.0, DefaultThicknessMeters = 0.20
                },
                ["Generic"] = new EkahauPreset
                {
                    Name = "Wall, Generic", Color = "#AAAAAA",
                    AttenuationTwoGHz = 8.0, AttenuationFiveGHz = 15.0, AttenuationSixGHz = 18.0,
                    ReflectionCoefficient = 0.35, DiffractionCoefficient = 14.0, DefaultThicknessMeters = 0.15
                },
            };

        public static readonly List<string> WallPresets = new List<string>
        {
            "Concrete", "Brick", "Drywall", "Glass", "CurtainWall",
            "Wood", "Metal", "Elevator", "Generic"
        };

        public static readonly List<string> DoorPresets = new List<string>
        {
            "WoodDoor", "MetalDoor", "GlassDoor", "Generic"
        };

        public static readonly List<string> WindowPresets = new List<string>
        {
            "Window", "Glass", "Generic"
        };

        /// <summary>
        /// Req 11: Short label — no dB values, keeps ComboBox width small.
        /// </summary>
        public static string DisplayLabel(string presetKey)
        {
            if (All.TryGetValue(presetKey, out var p))
                return $"{presetKey} - {p.Name}";
            return presetKey;
        }

        public static List<string> GetPresetsForCategory(string category)
        {
            switch (category)
            {
                case "wall": return WallPresets;
                case "door": return DoorPresets;
                case "window": return WindowPresets;
                default: return new List<string>(All.Keys);
            }
        }
    }
}
