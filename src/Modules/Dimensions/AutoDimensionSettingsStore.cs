using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TungMEP.Modules.Dimensions
{
    internal static class AutoDimensionSettingsStore
    {
        private const string SettingsDirectoryName = "TungMEP";
        private const string SettingsFileName = "AutoDimension.last-used.txt";

        public static AutoDimensionSettings Load()
        {
            AutoDimensionSettings settings = AutoDimensionSettings.CreateDefault();
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                {
                    return settings;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator > 0)
                    {
                        values[line.Substring(0, separator).Trim()] =
                            line.Substring(separator + 1).Trim();
                    }
                }

                settings.Mode = GetEnum(values, "Mode", settings.Mode);
                settings.ByLineInputMode = GetEnum(
                    values,
                    "ByLineInputMode",
                    settings.ByLineInputMode);
                settings.IncludeGrids = GetBoolean(values, "IncludeGrids", settings.IncludeGrids);
                settings.IncludeWalls = GetBoolean(values, "IncludeWalls", settings.IncludeWalls);
                settings.IncludeColumns = GetBoolean(values, "IncludeColumns", settings.IncludeColumns);
                settings.IncludePipes = GetBoolean(values, "IncludePipes", settings.IncludePipes);
                settings.IncludeDucts = GetBoolean(values, "IncludeDucts", settings.IncludeDucts);
                settings.IncludeSprinklers = GetBoolean(values, "IncludeSprinklers", settings.IncludeSprinklers);
                settings.IncludeMechanicalEquipment = GetBoolean(
                    values,
                    "IncludeMechanicalEquipment",
                    settings.IncludeMechanicalEquipment);
                settings.ReferenceMode = GetEnum(values, "ReferenceMode", settings.ReferenceMode);
                settings.SelectedDimensionTypeId = GetLong(
                    values,
                    "SelectedDimensionTypeId",
                    settings.SelectedDimensionTypeId);
                settings.SelectedDimensionTypeName = GetBase64Text(
                    values,
                    "SelectedDimensionTypeName",
                    settings.SelectedDimensionTypeName);
                settings.OffsetPaperMm = GetDouble(values, "OffsetPaperMm", settings.OffsetPaperMm);
                settings.LaneStepPaperMm = GetDouble(values, "LaneStepPaperMm", settings.LaneStepPaperMm);
                settings.ToleranceMm = GetDouble(values, "ToleranceMm", settings.ToleranceMm);
                settings.CreateChain = GetBoolean(values, "CreateChain", settings.CreateChain);
                settings.CreateOverall = GetBoolean(values, "CreateOverall", settings.CreateOverall);
                settings.AvoidOverlap = GetBoolean(values, "AvoidOverlap", settings.AvoidOverlap);
                settings.RepeatRun = GetBoolean(values, "RepeatRun", settings.RepeatRun);
                settings.SprinklerDimensionAlongRows = GetBoolean(
                    values,
                    "SprinklerDimensionAlongRows",
                    settings.SprinklerDimensionAlongRows);
                settings.SprinklerDimensionBetweenRows = GetBoolean(
                    values,
                    "SprinklerDimensionBetweenRows",
                    settings.SprinklerDimensionBetweenRows);
                settings.SprinklerDimensionOverall = GetBoolean(
                    values,
                    "SprinklerDimensionOverall",
                    settings.SprinklerDimensionOverall);
                settings.Normalize();
            }
            catch
            {
                // Invalid or unavailable per-user preferences must never block the command.
                settings = AutoDimensionSettings.CreateDefault();
            }
            return settings;
        }

        public static void Save(AutoDimensionSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            string temporaryPath = null;
            try
            {
                AutoDimensionSettings normalized = settings.Clone();
                normalized.Normalize();
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporaryPath = path + ".tmp";
                File.WriteAllLines(temporaryPath, new[]
                {
                    "Version=1",
                    "Mode=" + normalized.Mode,
                    "ByLineInputMode=" + normalized.ByLineInputMode,
                    "IncludeGrids=" + normalized.IncludeGrids,
                    "IncludeWalls=" + normalized.IncludeWalls,
                    "IncludeColumns=" + normalized.IncludeColumns,
                    "IncludePipes=" + normalized.IncludePipes,
                    "IncludeDucts=" + normalized.IncludeDucts,
                    "IncludeSprinklers=" + normalized.IncludeSprinklers,
                    "IncludeMechanicalEquipment=" + normalized.IncludeMechanicalEquipment,
                    "ReferenceMode=" + normalized.ReferenceMode,
                    "SelectedDimensionTypeId=" + normalized.SelectedDimensionTypeId.ToString(CultureInfo.InvariantCulture),
                    "SelectedDimensionTypeName=" + ToBase64(normalized.SelectedDimensionTypeName),
                    "OffsetPaperMm=" + Format(normalized.OffsetPaperMm),
                    "LaneStepPaperMm=" + Format(normalized.LaneStepPaperMm),
                    "ToleranceMm=" + Format(normalized.ToleranceMm),
                    "CreateChain=" + normalized.CreateChain,
                    "CreateOverall=" + normalized.CreateOverall,
                    "AvoidOverlap=" + normalized.AvoidOverlap,
                    "RepeatRun=" + normalized.RepeatRun,
                    "SprinklerDimensionAlongRows=" + normalized.SprinklerDimensionAlongRows,
                    "SprinklerDimensionBetweenRows=" + normalized.SprinklerDimensionBetweenRows,
                    "SprinklerDimensionOverall=" + normalized.SprinklerDimensionOverall
                }, Encoding.UTF8);

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
                temporaryPath = null;
            }
            catch
            {
                // Preferences are optional; execution must continue when storage is unavailable.
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath))
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                        // Ignore cleanup failures for the optional preference file.
                    }
                }
            }
        }

        internal static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SettingsDirectoryName,
                SettingsFileName);
        }

        private static T GetEnum<T>(IDictionary<string, string> values, string key, T fallback)
            where T : struct
        {
            string text;
            T parsed;
            return values.TryGetValue(key, out text) && Enum.TryParse(text, true, out parsed)
                ? parsed
                : fallback;
        }

        private static bool GetBoolean(
            IDictionary<string, string> values,
            string key,
            bool fallback)
        {
            string text;
            bool parsed;
            return values.TryGetValue(key, out text) && bool.TryParse(text, out parsed)
                ? parsed
                : fallback;
        }

        private static long GetLong(
            IDictionary<string, string> values,
            string key,
            long fallback)
        {
            string text;
            long parsed;
            return values.TryGetValue(key, out text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        private static double GetDouble(
            IDictionary<string, string> values,
            string key,
            double fallback)
        {
            string text;
            double parsed;
            return values.TryGetValue(key, out text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        private static string GetBase64Text(
            IDictionary<string, string> values,
            string key,
            string fallback)
        {
            string encoded;
            if (!values.TryGetValue(key, out encoded) || string.IsNullOrEmpty(encoded))
            {
                return fallback ?? string.Empty;
            }
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch
            {
                return fallback ?? string.Empty;
            }
        }

        private static string ToBase64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
