using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TungMEP.Modules.Tags
{
    internal static class AutoTagSettingsStore
    {
        private const string SettingsDirectoryName = "TungMEP";
        private const string SettingsFileName = "AutoTag.last-used.txt";
        private const string PlumbingFixtureSettingsFileName =
            "AutoTagPlumbingFixture.last-used.txt";

        public static AutoTagSettings Load()
        {
            return Load(SettingsFileName, true);
        }

        public static AutoTagSettings LoadPlumbingFixture()
        {
            return Load(PlumbingFixtureSettingsFileName, false);
        }

        private static AutoTagSettings Load(string fileName, bool updateBopByDefault)
        {
            AutoTagSettings settings = AutoTagSettings.CreateDefault();
            settings.UpdateBopParameter = updateBopByDefault;
            try
            {
                string path = GetPath(fileName);
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
                settings.SelectedTagTypeId = GetLong(
                    values,
                    "SelectedTagTypeId",
                    settings.SelectedTagTypeId);
                settings.SelectedTagTypeName = GetBase64Text(
                    values,
                    "SelectedTagTypeName",
                    settings.SelectedTagTypeName);
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
                settings.OptimizeDocumentationLayout = GetBoolean(
                    values,
                    "OptimizeDocumentationLayout",
                    settings.OptimizeDocumentationLayout);
                settings.UpdateBopParameter = GetBoolean(
                    values,
                    "UpdateBopParameter",
                    settings.UpdateBopParameter);
                settings.BopParameterName = GetBase64Text(
                    values,
                    "BopParameterName",
                    settings.BopParameterName);
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
                settings.Normalize();
            }
            catch
            {
                settings = AutoTagSettings.CreateDefault();
                settings.UpdateBopParameter = updateBopByDefault;
            }
            return settings;
        }

        public static void Save(AutoTagSettings settings)
        {
            Save(settings, SettingsFileName);
        }

        public static void SavePlumbingFixture(AutoTagSettings settings)
        {
            Save(settings, PlumbingFixtureSettingsFileName);
        }

        private static void Save(AutoTagSettings settings, string fileName)
        {
            if (settings == null)
            {
                return;
            }
            string temporaryPath = null;
            try
            {
                AutoTagSettings normalized = settings.Clone();
                normalized.Normalize();
                string path = GetPath(fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporaryPath = path + ".tmp";
                File.WriteAllLines(temporaryPath, new[]
                {
                    "Version=2",
                    "Scope=" + normalized.Scope,
                    "SidePreference=" + normalized.SidePreference,
                    "SelectedTagTypeId=" + normalized.SelectedTagTypeId.ToString(CultureInfo.InvariantCulture),
                    "SelectedTagTypeName=" + ToBase64(normalized.SelectedTagTypeName),
                    "SelectedArrowheadTypeId=" + normalized.SelectedArrowheadTypeId.ToString(CultureInfo.InvariantCulture),
                    "SelectedArrowheadTypeName=" + ToBase64(normalized.SelectedArrowheadTypeName),
                    "AddLeader=" + normalized.AddLeader,
                    "SkipAlreadyTagged=" + normalized.SkipAlreadyTagged,
                    "OptimizeDocumentationLayout=" + normalized.OptimizeDocumentationLayout,
                    "UpdateBopParameter=" + normalized.UpdateBopParameter,
                    "BopParameterName=" + ToBase64(normalized.BopParameterName),
                    "OffsetPaperMm=" + Format(normalized.OffsetPaperMm),
                    "TagGapPaperMm=" + Format(normalized.TagGapPaperMm),
                    "ClearancePaperMm=" + Format(normalized.ClearancePaperMm),
                    "MaximumLaneSteps=" + normalized.MaximumLaneSteps.ToString(CultureInfo.InvariantCulture)
                }, Encoding.UTF8);
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

        private static string GetPath(string fileName)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SettingsDirectoryName,
                fileName);
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
