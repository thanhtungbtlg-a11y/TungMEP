using System;

namespace TungMEP.Modules.Views
{
    internal sealed class ViewSheetAutomationSettings
    {
        public bool CreatePlans { get; set; }
        public bool CreateRcps { get; set; }
        public bool CreateSectionX { get; set; }
        public bool CreateSectionY { get; set; }
        public long PlanTypeId { get; set; }
        public long RcpTypeId { get; set; }
        public long SectionTypeId { get; set; }
        public long PlanTemplateId { get; set; }
        public long RcpTemplateId { get; set; }
        public long SectionTemplateId { get; set; }
        public long ScopeBoxId { get; set; }
        public string ViewNamePattern { get; set; }
        public int ViewScale { get; set; }
        public bool CreateSheets { get; set; }
        public long TitleBlockTypeId { get; set; }
        public string SheetNumberPrefix { get; set; }
        public int SheetNumberStart { get; set; }
        public int SheetNumberPadding { get; set; }
        public string SheetNamePattern { get; set; }
        public int ViewsPerSheet { get; set; }
        public double SectionPaddingMm { get; set; }

        public static ViewSheetAutomationSettings CreateDefault()
        {
            return new ViewSheetAutomationSettings
            {
                CreatePlans = true,
                CreateRcps = true,
                CreateSectionX = false,
                CreateSectionY = false,
                PlanTypeId = -1,
                RcpTypeId = -1,
                SectionTypeId = -1,
                PlanTemplateId = -1,
                RcpTemplateId = -1,
                SectionTemplateId = -1,
                ScopeBoxId = -1,
                ViewNamePattern = "{Level} - {Type} - {Zone}",
                ViewScale = 100,
                CreateSheets = true,
                TitleBlockTypeId = -1,
                SheetNumberPrefix = "MEP-",
                SheetNumberStart = 1,
                SheetNumberPadding = 3,
                SheetNamePattern = "{FirstView}",
                ViewsPerSheet = 1,
                SectionPaddingMm = 500.0
            };
        }

        public ViewSheetAutomationSettings Clone()
        {
            return (ViewSheetAutomationSettings)MemberwiseClone();
        }

        public void Normalize()
        {
            ViewNamePattern = string.IsNullOrWhiteSpace(ViewNamePattern)
                ? "{Level} - {Type} - {Zone}" : ViewNamePattern.Trim();
            SheetNumberPrefix = SheetNumberPrefix ?? string.Empty;
            SheetNamePattern = string.IsNullOrWhiteSpace(SheetNamePattern)
                ? "{FirstView}" : SheetNamePattern.Trim();
            ViewScale = Math.Max(1, Math.Min(10000, ViewScale));
            SheetNumberStart = Math.Max(0, SheetNumberStart);
            SheetNumberPadding = Math.Max(1, Math.Min(10, SheetNumberPadding));
            ViewsPerSheet = Math.Max(1, Math.Min(4, ViewsPerSheet));
            SectionPaddingMm = Math.Max(0.0, Math.Min(10000.0, SectionPaddingMm));
        }

        public bool HasOutput
        {
            get { return CreatePlans || CreateRcps || CreateSectionX || CreateSectionY; }
        }
    }
}
