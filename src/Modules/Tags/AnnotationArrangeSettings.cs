using System;

namespace TungMEP.Modules.Tags
{
    internal enum AnnotationArrangeMode
    {
        AlignOnly,
        DistributeEvenly,
        CompactStack,
        SmartRail,
        DocumentationRail,
        ReflowAfterEdit
    }

    internal enum AnnotationArrangeScope
    {
        SelectionOrPick,
        Rectangle,
        ActiveView
    }

    internal enum AnnotationArrangeSide
    {
        Auto,
        Left,
        Right,
        Top,
        Bottom,
        BothSidesBySource,
        BothSidesKeepCurrent,
        PickRail
    }

    internal enum AnnotationRailAlignment
    {
        NearModelEdge,
        Center,
        FarModelEdge
    }

    internal enum AnnotationLeaderLayout
    {
        Preserve,
        Straight,
        OrthogonalElbow
    }

    internal enum AnnotationSortMode
    {
        SourcePosition,
        NestedLeaderFan,
        CurrentAnnotationPosition,
        ElementId
    }

    internal sealed class AnnotationArrangeSettings
    {
        public AnnotationArrangeMode Mode { get; set; }
        public AnnotationArrangeScope Scope { get; set; }
        public AnnotationArrangeSide Side { get; set; }
        public AnnotationRailAlignment Alignment { get; set; }
        public AnnotationLeaderLayout LeaderLayout { get; set; }
        public AnnotationSortMode SortMode { get; set; }
        public bool IncludeIndependentTags { get; set; }
        public bool IncludeTextNotes { get; set; }
        public bool IncludeTextNotesWithoutLeaders { get; set; }
        public bool PreserveLeaderEnds { get; set; }
        public bool CreateMissingTagLeaders { get; set; }
        public bool AvoidObstacles { get; set; }
        public bool AutoCreateColumns { get; set; }
        public bool KeepPinnedFixed { get; set; }
        public bool ReverseOrder { get; set; }
        public bool PreviewBeforeApply { get; set; }
        public bool OptimizeLeaderRoutes { get; set; }
        public double SpacingPaperMm { get; set; }
        public double RailOffsetPaperMm { get; set; }
        public double ClearancePaperMm { get; set; }
        public double ColumnGapPaperMm { get; set; }
        public int MaximumItemsPerColumn { get; set; }
        public int MaximumSearchSteps { get; set; }

        public static AnnotationArrangeSettings CreateDefault()
        {
            return new AnnotationArrangeSettings
            {
                Mode = AnnotationArrangeMode.DocumentationRail,
                Scope = AnnotationArrangeScope.SelectionOrPick,
                Side = AnnotationArrangeSide.Auto,
                Alignment = AnnotationRailAlignment.NearModelEdge,
                LeaderLayout = AnnotationLeaderLayout.OrthogonalElbow,
                SortMode = AnnotationSortMode.SourcePosition,
                IncludeIndependentTags = true,
                IncludeTextNotes = true,
                IncludeTextNotesWithoutLeaders = false,
                PreserveLeaderEnds = true,
                CreateMissingTagLeaders = false,
                AvoidObstacles = true,
                AutoCreateColumns = true,
                KeepPinnedFixed = true,
                ReverseOrder = false,
                PreviewBeforeApply = true,
                OptimizeLeaderRoutes = true,
                SpacingPaperMm = 4.0,
                RailOffsetPaperMm = 12.0,
                ClearancePaperMm = 2.0,
                ColumnGapPaperMm = 10.0,
                MaximumItemsPerColumn = 12,
                MaximumSearchSteps = 12
            };
        }

        public AnnotationArrangeSettings Clone()
        {
            return new AnnotationArrangeSettings
            {
                Mode = Mode,
                Scope = Scope,
                Side = Side,
                Alignment = Alignment,
                LeaderLayout = LeaderLayout,
                SortMode = SortMode,
                IncludeIndependentTags = IncludeIndependentTags,
                IncludeTextNotes = IncludeTextNotes,
                IncludeTextNotesWithoutLeaders = IncludeTextNotesWithoutLeaders,
                PreserveLeaderEnds = PreserveLeaderEnds,
                CreateMissingTagLeaders = CreateMissingTagLeaders,
                AvoidObstacles = AvoidObstacles,
                AutoCreateColumns = AutoCreateColumns,
                KeepPinnedFixed = KeepPinnedFixed,
                ReverseOrder = ReverseOrder,
                PreviewBeforeApply = PreviewBeforeApply,
                OptimizeLeaderRoutes = OptimizeLeaderRoutes,
                SpacingPaperMm = SpacingPaperMm,
                RailOffsetPaperMm = RailOffsetPaperMm,
                ClearancePaperMm = ClearancePaperMm,
                ColumnGapPaperMm = ColumnGapPaperMm,
                MaximumItemsPerColumn = MaximumItemsPerColumn,
                MaximumSearchSteps = MaximumSearchSteps
            };
        }

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(AnnotationArrangeMode), Mode))
            {
                Mode = AnnotationArrangeMode.DocumentationRail;
            }
            if (!Enum.IsDefined(typeof(AnnotationArrangeScope), Scope))
            {
                Scope = AnnotationArrangeScope.SelectionOrPick;
            }
            if (!Enum.IsDefined(typeof(AnnotationArrangeSide), Side))
            {
                Side = AnnotationArrangeSide.Auto;
            }
            if (!Enum.IsDefined(typeof(AnnotationRailAlignment), Alignment))
            {
                Alignment = AnnotationRailAlignment.NearModelEdge;
            }
            if (!Enum.IsDefined(typeof(AnnotationLeaderLayout), LeaderLayout))
            {
                LeaderLayout = AnnotationLeaderLayout.OrthogonalElbow;
            }
            if (!Enum.IsDefined(typeof(AnnotationSortMode), SortMode))
            {
                SortMode = AnnotationSortMode.SourcePosition;
            }
            SpacingPaperMm = Clamp(SpacingPaperMm, 0.0, 100.0, 4.0);
            RailOffsetPaperMm = Clamp(RailOffsetPaperMm, 0.0, 300.0, 12.0);
            ClearancePaperMm = Clamp(ClearancePaperMm, 0.0, 100.0, 2.0);
            ColumnGapPaperMm = Clamp(ColumnGapPaperMm, 0.0, 200.0, 10.0);
            MaximumItemsPerColumn = Math.Max(1, Math.Min(100, MaximumItemsPerColumn));
            MaximumSearchSteps = Math.Max(1, Math.Min(30, MaximumSearchSteps));
        }

        public bool HasAnyCategory
        {
            get { return IncludeIndependentTags || IncludeTextNotes; }
        }

        private static double Clamp(
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
}
