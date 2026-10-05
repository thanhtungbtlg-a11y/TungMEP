using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TungMEP.Modules.AutoSprinkler
{
    internal class LastUsedSelections
    {
        public string SprinklerFamilyName { get; set; }
        public string SprinklerTypeName { get; set; }
        public string PipeTypeName { get; set; }
        public string PipingSystemTypeName { get; set; }
        public double? CoverageRadiusMm { get; set; }
        public double? MinimumSpacingMm { get; set; }
        public double? MaximumSpacingMm { get; set; }
        public double? BoundaryClearanceMm { get; set; }
        public double? ObstacleClearanceMm { get; set; }
        public double? HeightMm { get; set; }
        public double? BranchOffsetMm { get; set; }
        public bool? DrawBranchGuides { get; set; }
    }

    internal static class LastUsedSelectionStore
    {
        private const string SettingsDirectoryName = "TungMEP";
        private const string SettingsFileName = "AutoSprinkler.last-used.txt";

        public static LastUsedSelections Load()
        {
            var selections = new LastUsedSelections();
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                {
                    return selections;
                }
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path))
                {
                    int separator = line.IndexOf('=');
                    if (separator > 0)
                    {
                        values[line.Substring(0, separator)] = line.Substring(separator + 1);
                    }
                }
                selections.SprinklerFamilyName = GetValue(values, "SprinklerFamilyName");
                selections.SprinklerTypeName = GetValue(values, "SprinklerTypeName");
                selections.PipeTypeName = GetValue(values, "PipeTypeName");
                selections.PipingSystemTypeName = GetValue(values, "PipingSystemTypeName");
                selections.CoverageRadiusMm = GetDouble(values, "CoverageRadiusMm");
                selections.MinimumSpacingMm = GetDouble(values, "MinimumSpacingMm");
                selections.MaximumSpacingMm = GetDouble(values, "MaximumSpacingMm");
                selections.BoundaryClearanceMm = GetDouble(values, "BoundaryClearanceMm");
                selections.ObstacleClearanceMm = GetDouble(values, "ObstacleClearanceMm");
                selections.HeightMm = GetDouble(values, "HeightMm");
                selections.BranchOffsetMm = GetDouble(values, "BranchOffsetMm");
                selections.DrawBranchGuides = GetBoolean(values, "DrawBranchGuides");
            }
            catch
            {
                // A missing or malformed per-user preference file must never block Revit.
            }
            return selections;
        }

        public static void Save(SprinklerSettings settings)
        {
            if (settings == null || settings.Symbol == null || settings.PipeType == null || settings.PipingSystemType == null)
            {
                return;
            }
            try
            {
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "SprinklerFamilyName=" + settings.Symbol.FamilyName,
                    "SprinklerTypeName=" + settings.Symbol.Name,
                    "PipeTypeName=" + settings.PipeType.Name,
                    "PipingSystemTypeName=" + settings.PipingSystemType.Name,
                    "CoverageRadiusMm=" + Format(settings.CoverageRadiusMm),
                    "MinimumSpacingMm=" + Format(settings.MinimumSpacingMm),
                    "MaximumSpacingMm=" + Format(settings.MaximumSpacingMm),
                    "BoundaryClearanceMm=" + Format(settings.BoundaryClearanceMm),
                    "ObstacleClearanceMm=" + Format(settings.ObstacleClearanceMm),
                    "HeightMm=" + Format(settings.HeightMm),
                    "BranchOffsetMm=" + Format(settings.BranchOffsetMm),
                    "DrawBranchGuides=" + settings.DrawBranchGuides
                });
            }
            catch
            {
                // Preferences are optional; placement must continue if storage is unavailable.
            }
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SettingsDirectoryName,
                SettingsFileName);
        }

        private static string GetValue(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static double? GetDouble(IDictionary<string, string> values, string key)
        {
            string value;
            double parsed;
            if (values.TryGetValue(key, out value) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return null;
        }

        private static bool? GetBoolean(IDictionary<string, string> values, string key)
        {
            string value;
            bool parsed;
            if (values.TryGetValue(key, out value) && bool.TryParse(value, out parsed))
            {
                return parsed;
            }
            return null;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
