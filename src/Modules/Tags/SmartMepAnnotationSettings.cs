using System;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.Tags
{
    internal enum SmartMepAnnotationScope
    {
        SelectionOrPick,
        Rectangle,
        ActiveView
    }

    internal enum SmartMepTagKind
    {
        Pipe,
        Duct,
        Equipment
    }

    internal sealed class SmartMepAnnotationSettings
    {
        public SmartMepAnnotationScope Scope { get; set; }
        public bool IncludePipes { get; set; }
        public bool IncludeDucts { get; set; }
        public bool IncludeEquipment { get; set; }
        public long PipeTagTypeId { get; set; }
        public long DuctTagTypeId { get; set; }
        public long EquipmentTagTypeId { get; set; }
        public bool SkipAlreadyTagged { get; set; }
        public bool GroupParallelRuns { get; set; }
        public bool AddLeaders { get; set; }
        public bool ArrangeTwoSides { get; set; }
        public bool AvoidModel { get; set; }
        public bool Preview { get; set; }
        public bool OptimizeDocumentationLayout { get; set; }
        public bool CreateGridReferenceDimensions { get; set; }
        public bool CreateElevationDimensions { get; set; }
        public double GroupToleranceMm { get; set; }
        public double ItemSpacingPaperMm { get; set; }
        public double RailOffsetPaperMm { get; set; }
        public double ClearancePaperMm { get; set; }
        public double DimensionOffsetPaperMm { get; set; }

        public static SmartMepAnnotationSettings CreateDefault()
        {
            return new SmartMepAnnotationSettings
            {
                Scope = SmartMepAnnotationScope.SelectionOrPick,
                IncludePipes = true,
                IncludeDucts = true,
                IncludeEquipment = true,
                PipeTagTypeId = -1,
                DuctTagTypeId = -1,
                EquipmentTagTypeId = -1,
                SkipAlreadyTagged = true,
                GroupParallelRuns = true,
                AddLeaders = true,
                ArrangeTwoSides = true,
                AvoidModel = true,
                Preview = true,
                OptimizeDocumentationLayout = true,
                CreateGridReferenceDimensions = false,
                CreateElevationDimensions = false,
                GroupToleranceMm = 1200.0,
                ItemSpacingPaperMm = 5.0,
                RailOffsetPaperMm = 12.0,
                ClearancePaperMm = 3.0,
                DimensionOffsetPaperMm = 8.0
            };
        }

        public SmartMepAnnotationSettings Clone()
        {
            return (SmartMepAnnotationSettings)MemberwiseClone();
        }

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(SmartMepAnnotationScope), Scope))
            {
                Scope = SmartMepAnnotationScope.SelectionOrPick;
            }
            GroupToleranceMm = Clamp(GroupToleranceMm, 0.0, 10000.0, 1200.0);
            ItemSpacingPaperMm = Clamp(ItemSpacingPaperMm, 0.0, 100.0, 5.0);
            RailOffsetPaperMm = Clamp(RailOffsetPaperMm, 0.0, 300.0, 12.0);
            ClearancePaperMm = Clamp(ClearancePaperMm, 0.0, 100.0, 3.0);
            DimensionOffsetPaperMm = Clamp(DimensionOffsetPaperMm, 1.0, 100.0, 8.0);
        }

        public bool HasCategory
        {
            get { return IncludePipes || IncludeDucts || IncludeEquipment; }
        }

        private static double Clamp(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) { return fallback; }
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class SmartMepTagTypeOption
    {
        public SmartMepTagTypeOption(FamilySymbol symbol, SmartMepTagKind kind)
        {
            Symbol = symbol;
            Kind = kind;
            DisplayName = (symbol.Family == null ? string.Empty : symbol.Family.Name + " : ") +
                symbol.Name;
        }

        public FamilySymbol Symbol { get; private set; }
        public SmartMepTagKind Kind { get; private set; }
        public string DisplayName { get; private set; }
        public override string ToString() { return DisplayName; }
    }
}
