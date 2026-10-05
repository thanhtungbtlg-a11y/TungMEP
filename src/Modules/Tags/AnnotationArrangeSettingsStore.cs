using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TungMEP.Modules.Tags
{
    internal static class AnnotationArrangeSettingsStore
    {
        private const string FileName = "AnnotationArrange.last-used.txt";

        public static AnnotationArrangeSettings Load()
        {
            AnnotationArrangeSettings settings = AnnotationArrangeSettings.CreateDefault();
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
                settings.Scope = GetEnum(values, "Scope", settings.Scope);
                settings.Side = GetEnum(values, "Side", settings.Side);
                settings.Alignment = GetEnum(values, "Alignment", settings.Alignment);
                settings.LeaderLayout = GetEnum(values, "LeaderLayout", settings.LeaderLayout);
                settings.SortMode = GetEnum(values, "SortMode", settings.SortMode);
                settings.IncludeIndependentTags = GetBool(values, "IncludeIndependentTags", settings.IncludeIndependentTags);
                settings.IncludeTextNotes = GetBool(values, "IncludeTextNotes", settings.IncludeTextNotes);
                settings.IncludeTextNotesWithoutLeaders = GetBool(values, "IncludeTextNotesWithoutLeaders", settings.IncludeTextNotesWithoutLeaders);
                settings.PreserveLeaderEnds = GetBool(values, "PreserveLeaderEnds", settings.PreserveLeaderEnds);
                settings.CreateMissingTagLeaders = GetBool(values, "CreateMissingTagLeaders", settings.CreateMissingTagLeaders);
                settings.AvoidObstacles = GetBool(values, "AvoidObstacles", settings.AvoidObstacles);
                settings.AutoCreateColumns = GetBool(values, "AutoCreateColumns", settings.AutoCreateColumns);
                settings.KeepPinnedFixed = GetBool(values, "KeepPinnedFixed", settings.KeepPinnedFixed);
                settings.ReverseOrder = GetBool(values, "ReverseOrder", settings.ReverseOrder);
                settings.PreviewBeforeApply = GetBool(values, "PreviewBeforeApply", settings.PreviewBeforeApply);
                settings.OptimizeLeaderRoutes = GetBool(values, "OptimizeLeaderRoutes", settings.OptimizeLeaderRoutes);
                settings.SpacingPaperMm = GetDouble(values, "SpacingPaperMm", settings.SpacingPaperMm);
                settings.RailOffsetPaperMm = GetDouble(values, "RailOffsetPaperMm", settings.RailOffsetPaperMm);
                settings.ClearancePaperMm = GetDouble(values, "ClearancePaperMm", settings.ClearancePaperMm);
                settings.ColumnGapPaperMm = GetDouble(values, "ColumnGapPaperMm", settings.ColumnGapPaperMm);
                settings.MaximumItemsPerColumn = (int)GetLong(values, "MaximumItemsPerColumn", settings.MaximumItemsPerColumn);
                settings.MaximumSearchSteps = (int)GetLong(values, "MaximumSearchSteps", settings.MaximumSearchSteps);
                settings.Normalize();
            }
            catch
            {
                settings = AnnotationArrangeSettings.CreateDefault();
            }
            return settings;
        }

        public static void Save(AnnotationArrangeSettings settings)
        {
            if (settings == null) { return; }
            string temporary = null;
            try
            {
                AnnotationArrangeSettings normalized = settings.Clone();
                normalized.Normalize();
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = path + ".tmp";
                File.WriteAllLines(temporary, new[]
                {
                    "Version=2",
                    "Mode=" + normalized.Mode,
                    "Scope=" + normalized.Scope,
                    "Side=" + normalized.Side,
                    "Alignment=" + normalized.Alignment,
                    "LeaderLayout=" + normalized.LeaderLayout,
                    "SortMode=" + normalized.SortMode,
                    "IncludeIndependentTags=" + normalized.IncludeIndependentTags,
                    "IncludeTextNotes=" + normalized.IncludeTextNotes,
                    "IncludeTextNotesWithoutLeaders=" + normalized.IncludeTextNotesWithoutLeaders,
                    "PreserveLeaderEnds=" + normalized.PreserveLeaderEnds,
                    "CreateMissingTagLeaders=" + normalized.CreateMissingTagLeaders,
                    "AvoidObstacles=" + normalized.AvoidObstacles,
                    "AutoCreateColumns=" + normalized.AutoCreateColumns,
                    "KeepPinnedFixed=" + normalized.KeepPinnedFixed,
                    "ReverseOrder=" + normalized.ReverseOrder,
                    "PreviewBeforeApply=" + normalized.PreviewBeforeApply,
                    "OptimizeLeaderRoutes=" + normalized.OptimizeLeaderRoutes,
                    "SpacingPaperMm=" + Format(normalized.SpacingPaperMm),
                    "RailOffsetPaperMm=" + Format(normalized.RailOffsetPaperMm),
                    "ClearancePaperMm=" + Format(normalized.ClearancePaperMm),
                    "ColumnGapPaperMm=" + Format(normalized.ColumnGapPaperMm),
                    "MaximumItemsPerColumn=" + normalized.MaximumItemsPerColumn.ToString(CultureInfo.InvariantCulture),
                    "MaximumSearchSteps=" + normalized.MaximumSearchSteps.ToString(CultureInfo.InvariantCulture)
                }, Encoding.UTF8);
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(temporary, path);
                temporary = null;
            }
            catch
            {
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch { }
            }
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TungMEP",
                FileName);
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

        private static bool GetBool(IDictionary<string, string> values, string key, bool fallback)
        {
            string text;
            bool parsed;
            return values.TryGetValue(key, out text) && bool.TryParse(text, out parsed)
                ? parsed
                : fallback;
        }

        private static long GetLong(IDictionary<string, string> values, string key, long fallback)
        {
            string text;
            long parsed;
            return values.TryGetValue(key, out text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        private static double GetDouble(IDictionary<string, string> values, string key, double fallback)
        {
            string text;
            double parsed;
            return values.TryGetValue(key, out text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : fallback;
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
