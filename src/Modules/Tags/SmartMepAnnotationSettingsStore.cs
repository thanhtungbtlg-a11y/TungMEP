using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TungMEP.Modules.Tags
{
    internal static class SmartMepAnnotationSettingsStore
    {
        private const string FileName = "SmartMepAnnotation.last-used.txt";

        public static SmartMepAnnotationSettings Load()
        {
            SmartMepAnnotationSettings settings = SmartMepAnnotationSettings.CreateDefault();
            try
            {
                string path = GetPath();
                if (!File.Exists(path)) { return settings; }
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
                settings.IncludePipes = GetBool(values, "IncludePipes", settings.IncludePipes);
                settings.IncludeDucts = GetBool(values, "IncludeDucts", settings.IncludeDucts);
                settings.IncludeEquipment = GetBool(values, "IncludeEquipment", settings.IncludeEquipment);
                settings.PipeTagTypeId = GetLong(values, "PipeTagTypeId", settings.PipeTagTypeId);
                settings.DuctTagTypeId = GetLong(values, "DuctTagTypeId", settings.DuctTagTypeId);
                settings.EquipmentTagTypeId = GetLong(values, "EquipmentTagTypeId", settings.EquipmentTagTypeId);
                settings.SkipAlreadyTagged = GetBool(values, "SkipAlreadyTagged", settings.SkipAlreadyTagged);
                settings.GroupParallelRuns = GetBool(values, "GroupParallelRuns", settings.GroupParallelRuns);
                settings.AddLeaders = GetBool(values, "AddLeaders", settings.AddLeaders);
                settings.ArrangeTwoSides = GetBool(values, "ArrangeTwoSides", settings.ArrangeTwoSides);
                settings.AvoidModel = GetBool(values, "AvoidModel", settings.AvoidModel);
                settings.Preview = GetBool(values, "Preview", settings.Preview);
                settings.OptimizeDocumentationLayout = GetBool(values, "OptimizeDocumentationLayout", settings.OptimizeDocumentationLayout);
                settings.CreateGridReferenceDimensions = GetBool(values, "CreateGridReferenceDimensions", settings.CreateGridReferenceDimensions);
                settings.CreateElevationDimensions = GetBool(values, "CreateElevationDimensions", settings.CreateElevationDimensions);
                settings.GroupToleranceMm = GetDouble(values, "GroupToleranceMm", settings.GroupToleranceMm);
                settings.ItemSpacingPaperMm = GetDouble(values, "ItemSpacingPaperMm", settings.ItemSpacingPaperMm);
                settings.RailOffsetPaperMm = GetDouble(values, "RailOffsetPaperMm", settings.RailOffsetPaperMm);
                settings.ClearancePaperMm = GetDouble(values, "ClearancePaperMm", settings.ClearancePaperMm);
                settings.DimensionOffsetPaperMm = GetDouble(values, "DimensionOffsetPaperMm", settings.DimensionOffsetPaperMm);
                settings.Normalize();
            }
            catch { settings = SmartMepAnnotationSettings.CreateDefault(); }
            return settings;
        }

        public static void Save(SmartMepAnnotationSettings settings)
        {
            if (settings == null) { return; }
            try
            {
                SmartMepAnnotationSettings value = settings.Clone();
                value.Normalize();
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "Version=2",
                    "Scope=" + value.Scope,
                    "IncludePipes=" + value.IncludePipes,
                    "IncludeDucts=" + value.IncludeDucts,
                    "IncludeEquipment=" + value.IncludeEquipment,
                    "PipeTagTypeId=" + value.PipeTagTypeId,
                    "DuctTagTypeId=" + value.DuctTagTypeId,
                    "EquipmentTagTypeId=" + value.EquipmentTagTypeId,
                    "SkipAlreadyTagged=" + value.SkipAlreadyTagged,
                    "GroupParallelRuns=" + value.GroupParallelRuns,
                    "AddLeaders=" + value.AddLeaders,
                    "ArrangeTwoSides=" + value.ArrangeTwoSides,
                    "AvoidModel=" + value.AvoidModel,
                    "Preview=" + value.Preview,
                    "OptimizeDocumentationLayout=" + value.OptimizeDocumentationLayout,
                    "CreateGridReferenceDimensions=" + value.CreateGridReferenceDimensions,
                    "CreateElevationDimensions=" + value.CreateElevationDimensions,
                    "GroupToleranceMm=" + Format(value.GroupToleranceMm),
                    "ItemSpacingPaperMm=" + Format(value.ItemSpacingPaperMm),
                    "RailOffsetPaperMm=" + Format(value.RailOffsetPaperMm),
                    "ClearancePaperMm=" + Format(value.ClearancePaperMm),
                    "DimensionOffsetPaperMm=" + Format(value.DimensionOffsetPaperMm)
                }, Encoding.UTF8);
            }
            catch { }
        }

        private static string GetPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TungMEP", FileName);
        }

        private static T GetEnum<T>(IDictionary<string, string> values, string key, T fallback)
            where T : struct
        {
            string text; T result;
            return values.TryGetValue(key, out text) && Enum.TryParse(text, true, out result)
                ? result : fallback;
        }

        private static bool GetBool(IDictionary<string, string> values, string key, bool fallback)
        {
            string text; bool result;
            return values.TryGetValue(key, out text) && bool.TryParse(text, out result)
                ? result : fallback;
        }

        private static long GetLong(IDictionary<string, string> values, string key, long fallback)
        {
            string text; long result;
            return values.TryGetValue(key, out text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? result : fallback;
        }

        private static double GetDouble(IDictionary<string, string> values, string key, double fallback)
        {
            string text; double result;
            return values.TryGetValue(key, out text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                ? result : fallback;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
