using System;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.Tags
{
    internal enum AutoTagScope
    {
        SelectionOrPick,
        Rectangle,
        ActiveView
    }

    internal enum AutoTagSidePreference
    {
        Auto,
        Left,
        Right,
        Top,
        Bottom
    }

    internal sealed class AutoTagSettings
    {
        public AutoTagScope Scope { get; set; }
        public AutoTagSidePreference SidePreference { get; set; }
        public long SelectedTagTypeId { get; set; }
        public string SelectedTagTypeName { get; set; }
        public long SelectedArrowheadTypeId { get; set; }
        public string SelectedArrowheadTypeName { get; set; }
        public bool AddLeader { get; set; }
        public bool SkipAlreadyTagged { get; set; }
        public bool OptimizeDocumentationLayout { get; set; }
        public bool UpdateBopParameter { get; set; }
        public string BopParameterName { get; set; }
        public double OffsetPaperMm { get; set; }
        public double TagGapPaperMm { get; set; }
        public double ClearancePaperMm { get; set; }
        public int MaximumLaneSteps { get; set; }

        public static AutoTagSettings CreateDefault()
        {
            return new AutoTagSettings
            {
                Scope = AutoTagScope.SelectionOrPick,
                SidePreference = AutoTagSidePreference.Auto,
                SelectedTagTypeId = ElementId.InvalidElementId.Value,
                SelectedTagTypeName = string.Empty,
                SelectedArrowheadTypeId = ElementId.InvalidElementId.Value,
                SelectedArrowheadTypeName = string.Empty,
                AddLeader = true,
                SkipAlreadyTagged = true,
                OptimizeDocumentationLayout = true,
                UpdateBopParameter = true,
                BopParameterName = "TungMEP_BOP_FFL",
                OffsetPaperMm = 12.0,
                TagGapPaperMm = 4.0,
                ClearancePaperMm = 2.0,
                MaximumLaneSteps = 8
            };
        }

        public AutoTagSettings Clone()
        {
            return new AutoTagSettings
            {
                Scope = Scope,
                SidePreference = SidePreference,
                SelectedTagTypeId = SelectedTagTypeId,
                SelectedTagTypeName = SelectedTagTypeName ?? string.Empty,
                SelectedArrowheadTypeId = SelectedArrowheadTypeId,
                SelectedArrowheadTypeName = SelectedArrowheadTypeName ?? string.Empty,
                AddLeader = AddLeader,
                SkipAlreadyTagged = SkipAlreadyTagged,
                OptimizeDocumentationLayout = OptimizeDocumentationLayout,
                UpdateBopParameter = UpdateBopParameter,
                BopParameterName = BopParameterName ?? string.Empty,
                OffsetPaperMm = OffsetPaperMm,
                TagGapPaperMm = TagGapPaperMm,
                ClearancePaperMm = ClearancePaperMm,
                MaximumLaneSteps = MaximumLaneSteps
            };
        }

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(AutoTagScope), Scope))
            {
                Scope = AutoTagScope.SelectionOrPick;
            }
            if (!Enum.IsDefined(typeof(AutoTagSidePreference), SidePreference))
            {
                SidePreference = AutoTagSidePreference.Auto;
            }
            SelectedTagTypeName = SelectedTagTypeName ?? string.Empty;
            SelectedArrowheadTypeName = SelectedArrowheadTypeName ?? string.Empty;
            BopParameterName = string.IsNullOrWhiteSpace(BopParameterName)
                ? "TungMEP_BOP_FFL"
                : BopParameterName.Trim();
            OffsetPaperMm = ClampFinite(OffsetPaperMm, 1.0, 200.0, 12.0);
            TagGapPaperMm = ClampFinite(TagGapPaperMm, 0.5, 100.0, 4.0);
            ClearancePaperMm = ClampFinite(ClearancePaperMm, 0.0, 100.0, 2.0);
            MaximumLaneSteps = Math.Max(1, Math.Min(30, MaximumLaneSteps));
        }

        private static double ClampFinite(
            double value,
            double minimum,
            double maximum,
            double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return fallback;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class AutoTagTypeOption
    {
        public AutoTagTypeOption(FamilySymbol symbol)
        {
            IdValue = symbol == null ? ElementId.InvalidElementId.Value : symbol.Id.Value;
            FamilyName = symbol == null || symbol.Family == null
                ? string.Empty
                : symbol.Family.Name;
            TypeName = symbol == null ? string.Empty : symbol.Name;
        }

        public long IdValue { get; private set; }
        public string FamilyName { get; private set; }
        public string TypeName { get; private set; }
        public string DisplayName
        {
            get
            {
                return string.IsNullOrEmpty(FamilyName)
                    ? TypeName
                    : FamilyName + " : " + TypeName;
            }
        }
        public ElementId ElementId { get { return new ElementId(IdValue); } }
        public override string ToString() { return DisplayName; }
    }

    internal sealed class AutoTagArrowheadOption
    {
        public AutoTagArrowheadOption(ElementType type)
        {
            IdValue = type == null ? ElementId.InvalidElementId.Value : type.Id.Value;
            Name = type == null ? string.Empty : type.Name;
        }

        public long IdValue { get; private set; }
        public string Name { get; private set; }
        public ElementId ElementId { get { return new ElementId(IdValue); } }
        public override string ToString() { return Name; }
    }
}
