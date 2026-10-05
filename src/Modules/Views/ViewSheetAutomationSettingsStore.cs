using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TungMEP.Modules.Views
{
    internal static class ViewSheetAutomationSettingsStore
    {
        private const string FileName = "ViewSheetAutomation.last-used.txt";

        public static ViewSheetAutomationSettings Load()
        {
            ViewSheetAutomationSettings value = ViewSheetAutomationSettings.CreateDefault();
            try
            {
                string path = GetPath();
                if (!File.Exists(path)) return value;
                var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator > 0) data[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
                value.CreatePlans = Bool(data, "CreatePlans", value.CreatePlans);
                value.CreateRcps = Bool(data, "CreateRcps", value.CreateRcps);
                value.CreateSectionX = Bool(data, "CreateSectionX", value.CreateSectionX);
                value.CreateSectionY = Bool(data, "CreateSectionY", value.CreateSectionY);
                value.PlanTypeId = Long(data, "PlanTypeId", value.PlanTypeId);
                value.RcpTypeId = Long(data, "RcpTypeId", value.RcpTypeId);
                value.SectionTypeId = Long(data, "SectionTypeId", value.SectionTypeId);
                value.PlanTemplateId = Long(data, "PlanTemplateId", value.PlanTemplateId);
                value.RcpTemplateId = Long(data, "RcpTemplateId", value.RcpTemplateId);
                value.SectionTemplateId = Long(data, "SectionTemplateId", value.SectionTemplateId);
                value.ScopeBoxId = Long(data, "ScopeBoxId", value.ScopeBoxId);
                value.ViewNamePattern = Text(data, "ViewNamePattern", value.ViewNamePattern);
                value.ViewScale = (int)Long(data, "ViewScale", value.ViewScale);
                value.CreateSheets = Bool(data, "CreateSheets", value.CreateSheets);
                value.TitleBlockTypeId = Long(data, "TitleBlockTypeId", value.TitleBlockTypeId);
                value.SheetNumberPrefix = Text(data, "SheetNumberPrefix", value.SheetNumberPrefix);
                value.SheetNumberStart = (int)Long(data, "SheetNumberStart", value.SheetNumberStart);
                value.SheetNumberPadding = (int)Long(data, "SheetNumberPadding", value.SheetNumberPadding);
                value.SheetNamePattern = Text(data, "SheetNamePattern", value.SheetNamePattern);
                value.ViewsPerSheet = (int)Long(data, "ViewsPerSheet", value.ViewsPerSheet);
                value.SectionPaddingMm = Double(data, "SectionPaddingMm", value.SectionPaddingMm);
                value.Normalize();
            }
            catch { value = ViewSheetAutomationSettings.CreateDefault(); }
            return value;
        }

        public static void Save(ViewSheetAutomationSettings settings)
        {
            if (settings == null) return;
            try
            {
                ViewSheetAutomationSettings value = settings.Clone(); value.Normalize();
                string path = GetPath(); Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[]
                {
                    "Version=1",
                    "CreatePlans=" + value.CreatePlans,
                    "CreateRcps=" + value.CreateRcps,
                    "CreateSectionX=" + value.CreateSectionX,
                    "CreateSectionY=" + value.CreateSectionY,
                    "PlanTypeId=" + value.PlanTypeId,
                    "RcpTypeId=" + value.RcpTypeId,
                    "SectionTypeId=" + value.SectionTypeId,
                    "PlanTemplateId=" + value.PlanTemplateId,
                    "RcpTemplateId=" + value.RcpTemplateId,
                    "SectionTemplateId=" + value.SectionTemplateId,
                    "ScopeBoxId=" + value.ScopeBoxId,
                    "ViewNamePattern=" + value.ViewNamePattern,
                    "ViewScale=" + value.ViewScale,
                    "CreateSheets=" + value.CreateSheets,
                    "TitleBlockTypeId=" + value.TitleBlockTypeId,
                    "SheetNumberPrefix=" + value.SheetNumberPrefix,
                    "SheetNumberStart=" + value.SheetNumberStart,
                    "SheetNumberPadding=" + value.SheetNumberPadding,
                    "SheetNamePattern=" + value.SheetNamePattern,
                    "ViewsPerSheet=" + value.ViewsPerSheet,
                    "SectionPaddingMm=" + value.SectionPaddingMm.ToString("0.###", CultureInfo.InvariantCulture)
                }, Encoding.UTF8);
            }
            catch { }
        }

        private static string GetPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TungMEP", FileName); }
        private static string Text(IDictionary<string, string> data, string key, string fallback) { string value; return data.TryGetValue(key, out value) ? value : fallback; }
        private static bool Bool(IDictionary<string, string> data, string key, bool fallback) { string text; bool value; return data.TryGetValue(key, out text) && bool.TryParse(text, out value) ? value : fallback; }
        private static long Long(IDictionary<string, string> data, string key, long fallback) { string text; long value; return data.TryGetValue(key, out text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback; }
        private static double Double(IDictionary<string, string> data, string key, double fallback) { string text; double value; return data.TryGetValue(key, out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback; }
    }
}
