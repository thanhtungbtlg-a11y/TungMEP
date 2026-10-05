using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TungMEP.Modules.Grids
{
    [Transaction(TransactionMode.Manual)]
    public class GridModeCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null || uiDocument.Document == null)
            {
                message = "No active Revit document was found.";
                return Result.Failed;
            }

            Document document = uiDocument.Document;
            View view = document.ActiveView;
            if (document.IsFamilyDocument || view == null || view.IsTemplate ||
                view.ViewType == ViewType.ThreeD ||
                view.ViewType == ViewType.Schedule ||
                view.ViewType == ViewType.DrawingSheet)
            {
                TaskDialog.Show(
                    "Tung MEP - Grid Mode",
                    "Open a plan, section, or elevation view before switching Grid 2D/3D mode.");
                return Result.Cancelled;
            }

            List<Grid> selected = CollectSelectedGrids(document, uiDocument);
            bool selectionMode = selected.Count > 0;
            List<Grid> grids = selectionMode
                ? selected
                : CollectVisibleGrids(document, view);
            if (grids.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Grid Mode",
                    selectionMode
                        ? "The current selection contains no grids."
                        : "No grids are visible in the active view.");
                return Result.Succeeded;
            }

            DatumExtentType target;
            if (!TryChooseTarget(grids, view, selectionMode, out target))
            {
                return Result.Cancelled;
            }
            List<DimensionViewState> dimensionStates = CaptureReferencedDimensions(document, view, grids);
            int changed = 0;
            int unchanged = 0;
            int failed = 0;
            int dimensionsPreserved = 0;
            try
            {
                using (var transaction = new Transaction(
                    document,
                    target == DatumExtentType.Model
                        ? "Tung MEP - Grids to 3D"
                        : "Tung MEP - Grids to 2D"))
                {
                    transaction.Start();
                    foreach (Grid grid in grids)
                    {
                        if (HasTargetMode(grid, view, target))
                        {
                            unchanged++;
                            continue;
                        }

                        using (var subTransaction = new SubTransaction(document))
                        {
                            subTransaction.Start();
                            try
                            {
                                GridViewState state = CaptureViewState(grid, view);
                                ApplyModeWithoutChangingVisibleExtent(grid, view, target, state);
                                subTransaction.Commit();
                                changed++;
                            }
                            catch
                            {
                                try
                                {
                                    subTransaction.RollBack();
                                }
                                catch
                                {
                                    // The parent transaction will still handle the remaining grids.
                                }
                                failed++;
                            }
                        }
                    }
                    document.Regenerate();
                    dimensionsPreserved = RestoreDimensionPositions(document, dimensionStates);
                    document.Regenerate();
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                message = string.Empty;
                TaskDialog.Show(
                    "Tung MEP - Grid Mode",
                    "Grid mode was not changed because an existing dimension could not be kept in its original position.\n\n" +
                    ex.Message);
                return Result.Cancelled;
            }

            string mode = target == DatumExtentType.Model ? "3D" : "2D";
            string scope = selectionMode ? "selected grids" : "all grids visible in this view";
            string result = "Switched " + changed + " " + scope + " to " + mode + " mode.";
            if (unchanged > 0)
            {
                result += "\nAlready in " + mode + " mode: " + unchanged + ".";
            }
            if (failed > 0)
            {
                result += "\nSkipped grids whose visible extents could not be preserved: " + failed + ".";
            }
            if (dimensionsPreserved > 0)
            {
                result += "\nDimensions kept in place: " + dimensionsPreserved + ".";
            }
            TaskDialog.Show("Tung MEP - Grid Mode", result);
            return Result.Succeeded;
        }

        private static bool TryChooseTarget(
            IList<Grid> grids,
            View view,
            bool selectionMode,
            out DatumExtentType target)
        {
            target = DatumExtentType.ViewSpecific;
            int mode2D = 0;
            int mode3D = 0;
            int mixed = 0;
            foreach (Grid grid in grids)
            {
                try
                {
                    DatumExtentType end0 = grid.GetDatumExtentTypeInView(DatumEnds.End0, view);
                    DatumExtentType end1 = grid.GetDatumExtentTypeInView(DatumEnds.End1, view);
                    if (end0 == DatumExtentType.ViewSpecific && end1 == DatumExtentType.ViewSpecific)
                    {
                        mode2D++;
                    }
                    else if (end0 == DatumExtentType.Model && end1 == DatumExtentType.Model)
                    {
                        mode3D++;
                    }
                    else
                    {
                        mixed++;
                    }
                }
                catch
                {
                    mixed++;
                }
            }

            var dialog = new TaskDialog("Tung MEP - Grid Mode")
            {
                MainInstruction = "Choose the target Grid extent mode",
                MainContent = (selectionMode ? "Selected grids" : "Visible grids in this view") + ": " + grids.Count +
                    "\n2D: " + mode2D + "   3D: " + mode3D + "   Mixed: " + mixed +
                    "\n\nThe current positions, lengths, and bubble visibility will be preserved in this view.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel,
                AllowCancellation = true
            };
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink1,
                "Switch to 2D",
                "Use view-specific extents while keeping the current visible Grid geometry.");
            dialog.AddCommandLink(
                TaskDialogCommandLinkId.CommandLink2,
                "Switch to 3D",
                "Use model extents while keeping the current visible Grid geometry.");
            TaskDialogResult choice = dialog.Show();
            if (choice == TaskDialogResult.CommandLink1)
            {
                target = DatumExtentType.ViewSpecific;
                return true;
            }
            if (choice == TaskDialogResult.CommandLink2)
            {
                target = DatumExtentType.Model;
                return true;
            }
            return false;
        }

        private static bool HasTargetMode(Grid grid, View view, DatumExtentType target)
        {
            try
            {
                return grid.GetDatumExtentTypeInView(DatumEnds.End0, view) == target &&
                       grid.GetDatumExtentTypeInView(DatumEnds.End1, view) == target;
            }
            catch
            {
                return false;
            }
        }

        private static GridViewState CaptureViewState(Grid grid, View view)
        {
            DatumExtentType currentMode = DatumExtentType.Model;
            try
            {
                if (grid.GetDatumExtentTypeInView(DatumEnds.End0, view) == DatumExtentType.ViewSpecific ||
                    grid.GetDatumExtentTypeInView(DatumEnds.End1, view) == DatumExtentType.ViewSpecific)
                {
                    currentMode = DatumExtentType.ViewSpecific;
                }
            }
            catch
            {
                // Model curves are the safest fallback for a datum that cannot report one end.
            }

            Curve visibleCurve = GetLongestBoundCurve(grid, view, currentMode) ??
                                 GetLongestBoundCurve(
                                     grid,
                                     view,
                                     currentMode == DatumExtentType.Model
                                         ? DatumExtentType.ViewSpecific
                                         : DatumExtentType.Model);
            if (visibleCurve == null)
            {
                throw new InvalidOperationException("The visible Grid curve could not be read.");
            }

            var state = new GridViewState
            {
                Curve = visibleCurve.CreateTransformed(Transform.Identity)
            };
            state.BubbleEnd0 = TryGetBubbleVisibility(grid, view, DatumEnds.End0);
            state.BubbleEnd1 = TryGetBubbleVisibility(grid, view, DatumEnds.End1);
            return state;
        }

        private static Curve GetLongestBoundCurve(
            Grid grid,
            View view,
            DatumExtentType extentType)
        {
            try
            {
                return grid.GetCurvesInView(extentType, view)
                    .Where(curve => curve != null && curve.IsBound)
                    .OrderByDescending(curve => curve.Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static bool? TryGetBubbleVisibility(Grid grid, View view, DatumEnds end)
        {
            try
            {
                return grid.IsBubbleVisibleInView(end, view);
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyModeWithoutChangingVisibleExtent(
            Grid grid,
            View view,
            DatumExtentType target,
            GridViewState state)
        {
            grid.SetDatumExtentType(DatumEnds.End0, view, target);
            grid.SetDatumExtentType(DatumEnds.End1, view, target);
            grid.Document.Regenerate();
            grid.SetCurveInView(target, view, state.Curve);
            grid.Document.Regenerate();

            Curve actual = GetLongestBoundCurve(grid, view, target);
            if (!CurvesMatchInView(state.Curve, actual))
            {
                grid.SetCurveInView(target, view, state.Curve);
                grid.Document.Regenerate();
                actual = GetLongestBoundCurve(grid, view, target);
            }
            if (!CurvesMatchInView(state.Curve, actual))
            {
                throw new InvalidOperationException(
                    "Revit did not keep Grid " + grid.Name + " at its original visible extents.");
            }
            RestoreBubbleVisibility(grid, view, DatumEnds.End0, state.BubbleEnd0);
            RestoreBubbleVisibility(grid, view, DatumEnds.End1, state.BubbleEnd1);
        }

        private static bool CurvesMatchInView(Curve expected, Curve actual)
        {
            if (expected == null || actual == null || !expected.IsBound || !actual.IsBound)
            {
                return false;
            }
            const double tolerance = 1e-5;
            XYZ expectedStart = expected.GetEndPoint(0);
            XYZ expectedEnd = expected.GetEndPoint(1);
            XYZ actualStart = actual.GetEndPoint(0);
            XYZ actualEnd = actual.GetEndPoint(1);
            double direct = expectedStart.DistanceTo(actualStart) + expectedEnd.DistanceTo(actualEnd);
            double reversed = expectedStart.DistanceTo(actualEnd) + expectedEnd.DistanceTo(actualStart);
            return Math.Min(direct, reversed) <= tolerance &&
                   Math.Abs(expected.Length - actual.Length) <= tolerance;
        }

        private static void RestoreBubbleVisibility(
            Grid grid,
            View view,
            DatumEnds end,
            bool? visible)
        {
            if (!visible.HasValue)
            {
                return;
            }
            if (visible.Value)
            {
                grid.ShowBubbleInView(end, view);
            }
            else
            {
                grid.HideBubbleInView(end, view);
            }
        }

        private static List<DimensionViewState> CaptureReferencedDimensions(
            Document document,
            View view,
            IList<Grid> grids)
        {
            var gridIds = new HashSet<long>(grids.Select(grid => grid.Id.Value));
            var states = new List<DimensionViewState>();
            foreach (Dimension dimension in new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>())
            {
                if (!ReferencesAnyGrid(dimension, gridIds))
                {
                    continue;
                }
                XYZ anchor;
                if (!TryGetDimensionAnchor(dimension, out anchor))
                {
                    continue;
                }
                var state = new DimensionViewState
                {
                    Id = dimension.Id,
                    Anchor = anchor
                };
                try
                {
                    state.TextPosition = dimension.TextPosition;
                }
                catch
                {
                    state.TextPosition = null;
                }
                try
                {
                    state.LeaderEndPosition = dimension.LeaderEndPosition;
                }
                catch
                {
                    state.LeaderEndPosition = null;
                }

                DimensionSegmentArray segments = dimension.Segments;
                for (int index = 0; index < segments.Size; index++)
                {
                    DimensionSegment segment = segments.get_Item(index);
                    var segmentState = new DimensionSegmentViewState();
                    try
                    {
                        segmentState.TextPosition = segment.TextPosition;
                    }
                    catch
                    {
                        segmentState.TextPosition = null;
                    }
                    try
                    {
                        segmentState.LeaderEndPosition = segment.LeaderEndPosition;
                    }
                    catch
                    {
                        segmentState.LeaderEndPosition = null;
                    }
                    state.Segments.Add(segmentState);
                }
                states.Add(state);
            }
            return states;
        }

        private static bool ReferencesAnyGrid(Dimension dimension, ISet<long> gridIds)
        {
            try
            {
                foreach (Reference reference in dimension.References)
                {
                    if (reference != null && gridIds.Contains(reference.ElementId.Value))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static bool TryGetDimensionAnchor(Dimension dimension, out XYZ anchor)
        {
            anchor = XYZ.Zero;
            try
            {
                Curve curve = dimension.Curve;
                if (curve == null)
                {
                    return false;
                }
                anchor = curve.IsBound
                    ? curve.Evaluate(0.5, true)
                    : curve.GetEndPoint(0);
                return anchor != null;
            }
            catch
            {
                return false;
            }
        }

        private static int RestoreDimensionPositions(
            Document document,
            IList<DimensionViewState> states)
        {
            int restored = 0;
            foreach (DimensionViewState state in states)
            {
                Dimension dimension = document.GetElement(state.Id) as Dimension;
                if (dimension == null || !dimension.IsValidObject)
                {
                    throw new InvalidOperationException("A Grid dimension became invalid.");
                }

                XYZ currentAnchor;
                if (!TryGetDimensionAnchor(dimension, out currentAnchor))
                {
                    throw new InvalidOperationException("The position of a Grid dimension could not be read.");
                }
                XYZ translation = state.Anchor - currentAnchor;
                if (translation.GetLength() > 1e-8)
                {
                    ElementTransformUtils.MoveElement(document, dimension.Id, translation);
                    document.Regenerate();
                }

                if (state.TextPosition != null)
                {
                    dimension.TextPosition = state.TextPosition;
                }
                if (state.LeaderEndPosition != null)
                {
                    dimension.LeaderEndPosition = state.LeaderEndPosition;
                }
                DimensionSegmentArray segments = dimension.Segments;
                if (segments.Size != state.Segments.Count)
                {
                    throw new InvalidOperationException("A Grid dimension segment count changed.");
                }
                for (int index = 0; index < segments.Size; index++)
                {
                    DimensionSegment segment = segments.get_Item(index);
                    DimensionSegmentViewState segmentState = state.Segments[index];
                    if (segmentState.TextPosition != null)
                    {
                        segment.TextPosition = segmentState.TextPosition;
                    }
                    if (segmentState.LeaderEndPosition != null)
                    {
                        segment.LeaderEndPosition = segmentState.LeaderEndPosition;
                    }
                }
                restored++;
            }
            return restored;
        }

        private static List<Grid> CollectSelectedGrids(
            Document document,
            UIDocument uiDocument)
        {
            var ids = new HashSet<ElementId>();
            foreach (ElementId selectedId in uiDocument.Selection.GetElementIds())
            {
                Grid grid = document.GetElement(selectedId) as Grid;
                if (grid != null)
                {
                    ids.Add(grid.Id);
                    continue;
                }
                MultiSegmentGrid multiGrid = document.GetElement(selectedId) as MultiSegmentGrid;
                if (multiGrid != null)
                {
                    foreach (ElementId gridId in multiGrid.GetGridIds())
                    {
                        ids.Add(gridId);
                    }
                }
            }
            return ids
                .Select(id => document.GetElement(id) as Grid)
                .Where(grid => grid != null)
                .ToList();
        }

        private static List<Grid> CollectVisibleGrids(Document document, View view)
        {
            var ids = new HashSet<ElementId>(
                new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Select(grid => grid.Id));
            foreach (MultiSegmentGrid multiGrid in new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(MultiSegmentGrid))
                .Cast<MultiSegmentGrid>())
            {
                foreach (ElementId gridId in multiGrid.GetGridIds())
                {
                    ids.Add(gridId);
                }
            }
            return ids
                .Select(id => document.GetElement(id) as Grid)
                .Where(grid => grid != null && grid.CanBeVisibleInView(view))
                .OrderBy(grid => grid.Name)
                .ToList();
        }

        private sealed class GridViewState
        {
            public Curve Curve { get; set; }
            public bool? BubbleEnd0 { get; set; }
            public bool? BubbleEnd1 { get; set; }
        }

        private sealed class DimensionViewState
        {
            public DimensionViewState()
            {
                Segments = new List<DimensionSegmentViewState>();
            }

            public ElementId Id { get; set; }
            public XYZ Anchor { get; set; }
            public XYZ TextPosition { get; set; }
            public XYZ LeaderEndPosition { get; set; }
            public List<DimensionSegmentViewState> Segments { get; private set; }
        }

        private sealed class DimensionSegmentViewState
        {
            public XYZ TextPosition { get; set; }
            public XYZ LeaderEndPosition { get; set; }
        }
    }
}
