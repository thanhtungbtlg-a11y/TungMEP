using System;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.Dimensions
{
    internal enum AutoDimensionMode
    {
        ByLine,
        Sprinkler,
        SingleSprinklerToWalls
    }

    internal enum AutoDimensionReferenceMode
    {
        Center,
        OuterFaces,
        CenterAndOuterFaces
    }

    internal enum AutoDimensionByLineInputMode
    {
        SelectObjects,
        DrawScanLine
    }

    internal sealed class AutoDimensionSettings
    {
        public AutoDimensionMode Mode { get; set; }
        public AutoDimensionByLineInputMode ByLineInputMode { get; set; }

        public bool IncludeGrids { get; set; }
        public bool IncludeWalls { get; set; }
        public bool IncludeColumns { get; set; }
        public bool IncludePipes { get; set; }
        public bool IncludeDucts { get; set; }
        public bool IncludeSprinklers { get; set; }
        public bool IncludeMechanicalEquipment { get; set; }

        public AutoDimensionReferenceMode ReferenceMode { get; set; }
        public long SelectedDimensionTypeId { get; set; }
        public string SelectedDimensionTypeName { get; set; }

        public double OffsetPaperMm { get; set; }
        public double LaneStepPaperMm { get; set; }
        public double ToleranceMm { get; set; }

        public bool CreateChain { get; set; }
        public bool CreateOverall { get; set; }
        public bool AvoidOverlap { get; set; }
        public bool RepeatRun { get; set; }

        public bool SprinklerDimensionAlongRows { get; set; }
        public bool SprinklerDimensionBetweenRows { get; set; }
        public bool SprinklerDimensionOverall { get; set; }

        public bool HasAnyByLineCategory
        {
            get
            {
                return IncludeGrids || IncludeWalls || IncludeColumns ||
                    IncludePipes || IncludeDucts || IncludeSprinklers ||
                    IncludeMechanicalEquipment;
            }
        }

        public bool HasAnyByLineOutput
        {
            get { return CreateChain || CreateOverall; }
        }

        public bool HasAnySprinklerOutput
        {
            get
            {
                return SprinklerDimensionAlongRows ||
                    SprinklerDimensionBetweenRows ||
                    SprinklerDimensionOverall;
            }
        }

        public static AutoDimensionSettings CreateDefault()
        {
            return new AutoDimensionSettings
            {
                Mode = AutoDimensionMode.ByLine,
                ByLineInputMode = AutoDimensionByLineInputMode.SelectObjects,
                IncludeGrids = true,
                IncludeWalls = true,
                IncludeColumns = true,
                IncludePipes = false,
                IncludeDucts = false,
                IncludeSprinklers = false,
                IncludeMechanicalEquipment = false,
                ReferenceMode = AutoDimensionReferenceMode.Center,
                SelectedDimensionTypeId = ElementId.InvalidElementId.Value,
                SelectedDimensionTypeName = string.Empty,
                OffsetPaperMm = 10.0,
                LaneStepPaperMm = 6.0,
                ToleranceMm = 5.0,
                CreateChain = true,
                CreateOverall = true,
                AvoidOverlap = true,
                RepeatRun = true,
                SprinklerDimensionAlongRows = true,
                SprinklerDimensionBetweenRows = true,
                SprinklerDimensionOverall = true
            };
        }

        public AutoDimensionSettings Clone()
        {
            return new AutoDimensionSettings
            {
                Mode = Mode,
                ByLineInputMode = ByLineInputMode,
                IncludeGrids = IncludeGrids,
                IncludeWalls = IncludeWalls,
                IncludeColumns = IncludeColumns,
                IncludePipes = IncludePipes,
                IncludeDucts = IncludeDucts,
                IncludeSprinklers = IncludeSprinklers,
                IncludeMechanicalEquipment = IncludeMechanicalEquipment,
                ReferenceMode = ReferenceMode,
                SelectedDimensionTypeId = SelectedDimensionTypeId,
                SelectedDimensionTypeName = SelectedDimensionTypeName ?? string.Empty,
                OffsetPaperMm = OffsetPaperMm,
                LaneStepPaperMm = LaneStepPaperMm,
                ToleranceMm = ToleranceMm,
                CreateChain = CreateChain,
                CreateOverall = CreateOverall,
                AvoidOverlap = AvoidOverlap,
                RepeatRun = RepeatRun,
                SprinklerDimensionAlongRows = SprinklerDimensionAlongRows,
                SprinklerDimensionBetweenRows = SprinklerDimensionBetweenRows,
                SprinklerDimensionOverall = SprinklerDimensionOverall
            };
        }

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(AutoDimensionMode), Mode))
            {
                Mode = AutoDimensionMode.ByLine;
            }
            if (!Enum.IsDefined(typeof(AutoDimensionByLineInputMode), ByLineInputMode))
            {
                ByLineInputMode = AutoDimensionByLineInputMode.SelectObjects;
            }
            if (!Enum.IsDefined(typeof(AutoDimensionReferenceMode), ReferenceMode))
            {
                ReferenceMode = AutoDimensionReferenceMode.Center;
            }
            SelectedDimensionTypeName = SelectedDimensionTypeName ?? string.Empty;
            OffsetPaperMm = ClampFinite(OffsetPaperMm, 0.0, 500.0, 10.0);
            LaneStepPaperMm = ClampFinite(LaneStepPaperMm, 0.1, 500.0, 6.0);
            ToleranceMm = ClampFinite(ToleranceMm, 0.1, 5000.0, 5.0);
        }

        private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return fallback;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class AutoDimensionTypeOption
    {
        public AutoDimensionTypeOption(ElementId id, string name)
        {
            IdValue = id == null ? ElementId.InvalidElementId.Value : id.Value;
            Name = name ?? string.Empty;
        }

        public AutoDimensionTypeOption(long idValue, string name)
        {
            IdValue = idValue;
            Name = name ?? string.Empty;
        }

        public AutoDimensionTypeOption(DimensionType type)
        {
            IdValue = type == null ? ElementId.InvalidElementId.Value : type.Id.Value;
            Name = type == null ? string.Empty : type.Name;
        }

        public long IdValue { get; private set; }
        public string Name { get; private set; }

        public ElementId ElementId
        {
            get { return new ElementId(IdValue); }
        }

        public override string ToString()
        {
            return Name;
        }
    }
}
