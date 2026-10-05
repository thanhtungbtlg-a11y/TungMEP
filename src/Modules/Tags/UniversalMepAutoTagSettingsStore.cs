using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TungMEP.Modules.Tags
{
    internal static class UniversalMepAutoTagSettingsStore
    {
        private const string SettingsDirectoryName = "TungMEP";
        private const string SettingsFileName = "UniversalMepAutoTag.last-used.txt";

        public static UniversalMepAutoTagSettings Load()
        {
            UniversalMepAutoTagSettings settings = UniversalMepAutoTagSettings.CreateDefault();
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
                settings.Scope = GetEnum(values, "Scope", settings.Scope);
                settings.SidePreference = GetEnum(
                    values,
                    "SidePreference",
                    settings.SidePreference);
                settings.SelectedArrowheadTypeId = GetLong(
                    values,
                    "SelectedArrowheadTypeId",
                    settings.SelectedArrowheadTypeId);
                settings.SelectedArrowheadTypeName = GetBase64Text(
                    values,
                    "SelectedArrowheadTypeName",
                    settings.SelectedArrowheadTypeName);
                settings.AddLeader = GetBoolean(values, "AddLeader", settings.AddLeader);
                settings.SkipAlreadyTagged = GetBoolean(
                    values,
                    "SkipAlreadyTagged",
                    settings.SkipAlreadyTagged);
                settings.PreviewBeforeApply = GetBoolean(
                    values,
                    "PreviewBeforeApply",
                    settings.PreviewBeforeApply);
                settings.OptimizeDocumentationLayout = GetBoolean(
                    values,
                    "OptimizeDocumentationLayout",
                    settings.OptimizeDocumentationLayout);
                settings.OffsetPaperMm = GetDouble(
                    values,
                    "OffsetPaperMm",
                    settings.OffsetPaperMm);
                settings.TagGapPaperMm = GetDouble(
                    values,
                    "TagGapPaperMm",
                    settings.TagGapPaperMm);
                settings.ClearancePaperMm = GetDouble(
                    values,
                    "ClearancePaperMm",
                    settings.ClearancePaperMm);
                settings.MaximumLaneSteps = (int)GetLong(
                    values,
                    "MaximumLaneSteps",
                    settings.MaximumLaneSteps);

                foreach (UniversalTagCategorySelection selection in settings.Categories)
                {
                    string prefix = "Category." + selection.CategoryKey + ".";
                    selection.Enabled = GetBoolean(
                        values,
                        prefix + "Enabled",
                        selection.Enabled);
                    selection.TagTypeId = GetLong(
                        values,
                        prefix + "TagTypeId",
                        selection.TagTypeId);
                    selection.TagTypeName = GetBase64Text(
                        values,
                        prefix + "TagTypeName",
                        selection.TagTypeName);
                }
                settings.Normalize();
                return settings;
            }
            catch
            {
                settings = UniversalMepAutoTagSettings.CreateDefault();
                settings.Normalize();
                return settings;
            }
        }

        public static void Save(UniversalMepAutoTagSettings settings)
        {
            UniversalMepAutoTagSettings normalized = settings == null
                ? UniversalMepAutoTagSettings.CreateDefault()
                : settings.Clone();
            normalized.Normalize();
            string temporaryPath = null;
            try
            {
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporaryPath = path + ".tmp";
                var lines = new List<string>
                {
                    "Scope=" + normalized.Scope,
                    "SidePreference=" + normalized.SidePreference,
                    "SelectedArrowheadTypeId=" + normalized.SelectedArrowheadTypeId.ToString(CultureInfo.InvariantCulture),
                    "SelectedArrowheadTypeName=" + ToBase64(normalized.SelectedArrowheadTypeName),
                    "AddLeader=" + normalized.AddLeader,
                    "SkipAlreadyTagged=" + normalized.SkipAlreadyTagged,
                    "PreviewBeforeApply=" + normalized.PreviewBeforeApply,
                    "OptimizeDocumentationLayout=" + normalized.OptimizeDocumentationLayout,
                    "OffsetPaperMm=" + Format(normalized.OffsetPaperMm),
                    "TagGapPaperMm=" + Format(normalized.TagGapPaperMm),
                    "ClearancePaperMm=" + Format(normalized.ClearancePaperMm),
                    "MaximumLaneSteps=" + normalized.MaximumLaneSteps.ToString(CultureInfo.InvariantCulture)
                };
                foreach (UniversalTagCategorySelection selection in normalized.Categories)
                {
                    string prefix = "Category." + selection.CategoryKey + ".";
                    lines.Add(prefix + "Enabled=" + selection.Enabled);
                    lines.Add(prefix + "TagTypeId=" + selection.TagTypeId.ToString(CultureInfo.InvariantCulture));
                    lines.Add(prefix + "TagTypeName=" + ToBase64(selection.TagTypeName));
                }
                File.WriteAllLines(temporaryPath, lines, Encoding.UTF8);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temporaryPath, path);
                temporaryPath = null;
            }
            catch
            {
                // Per-user preferences must never block the command.
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }

        private static string GetPath()
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
            T value;
            return values.TryGetValue(key, out text) && Enum.TryParse(text, true, out value)
                ? value
                : fallback;
        }

        private static bool GetBoolean(
            IDictionary<string, string> values,
            string key,
            bool fallback)
        {
            string text;
            bool value;
            return values.TryGetValue(key, out text) && bool.TryParse(text, out value)
                ? value
                : fallback;
        }

        private static long GetLong(
            IDictionary<string, string> values,
            string key,
            long fallback)
        {
            string text;
            long value;
            return values.TryGetValue(key, out text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private static double GetDouble(
            IDictionary<string, string> values,
            string key,
            double fallback)
        {
            string text;
            double value;
            return values.TryGetValue(key, out text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
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
