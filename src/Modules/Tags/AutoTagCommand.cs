using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WinForms = System.Windows.Forms;

namespace TungMEP.Modules.Tags
{
    [Transaction(TransactionMode.Manual)]
    public class AutoTagCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            View view = document == null ? null : document.ActiveView;
            if (!IsSupportedView(view))
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    "Open a Floor Plan, Engineering Plan, or Ceiling Plan before running Smart Auto Tag MEP.");
                return Result.Cancelled;
            }

            List<AutoTagTypeOption> tagTypes = CollectPipeTagTypes(document);
            if (tagTypes.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    "No Pipe Tag Type is loaded in this project.\n\n" +
                    "Load a Revit Pipe Tag family first. The family labels control whether the tag shows System, DN, Slope, and BOP-FFL.");
                return Result.Cancelled;
            }
            List<AutoTagArrowheadOption> arrowheadTypes =
                AutoTagArrowheadSupport.Collect(document);
            if (arrowheadTypes.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    "No Revit Arrowhead Type was found in this project.");
                return Result.Cancelled;
            }

            AutoTagSettings settings;
            using (var form = new AutoTagForm(
                tagTypes,
                arrowheadTypes,
                AutoTagSettingsStore.Load()))
            {
                if (form.ShowDialog() != WinForms.DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                settings = form.Settings;
            }
            settings.Normalize();
            AutoTagSettingsStore.Save(settings);

            FamilySymbol tagType = document.GetElement(
                new ElementId(settings.SelectedTagTypeId)) as FamilySymbol;
            if (tagType == null || !IsPipeTagType(tagType))
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    "The selected Pipe Tag Type is no longer available.");
                return Result.Cancelled;
            }
            ElementType arrowheadType = document.GetElement(
                new ElementId(settings.SelectedArrowheadTypeId)) as ElementType;
            if (!AutoTagArrowheadSupport.IsArrowheadType(arrowheadType))
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    "The selected Leader Arrowhead Type is no longer available.");
                return Result.Cancelled;
            }

            List<Pipe> pipes;
            try
            {
                pipes = CollectScopePipes(uiDocument, document, view, settings);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (pipes.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    "No host-model Pipe was found in the selected scope.");
                return Result.Cancelled;
            }

            HashSet<long> alreadyTagged = CollectAlreadyTaggedPipeIds(document, view);
            var statistics = new AutoTagStatistics { ScopePipes = pipes.Count };
            if (settings.SkipAlreadyTagged)
            {
                List<Pipe> untagged = pipes
                    .Where(pipe => !alreadyTagged.Contains(pipe.Id.Value))
                    .ToList();
                statistics.SkippedAlreadyTagged = pipes.Count - untagged.Count;
                pipes = untagged;
            }
            if (pipes.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    BuildReport(statistics, settings, tagType));
                return Result.Succeeded;
            }

            List<PipeTagItem> items = pipes
                .Select(pipe => TryCreatePipeTagItem(pipe, view))
                .Where(item => item != null)
                .OrderBy(item => item.IsMostlyVertical ? 0 : 1)
                .ThenBy(item => item.Anchor2.X)
                .ThenByDescending(item => item.Anchor2.Y)
                .ToList();
            statistics.InvalidGeometry = pipes.Count - items.Count;
            if (items.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Auto Tag MEP",
                    "The selected pipes do not have usable curves in this view.");
                return Result.Cancelled;
            }

            double offset = PaperMillimetersToInternal(settings.OffsetPaperMm, view);
            double gap = PaperMillimetersToInternal(settings.TagGapPaperMm, view);
            double clearance = PaperMillimetersToInternal(settings.ClearancePaperMm, view);
            double railBand = Math.Max(
                offset * 2.0,
                UnitUtils.ConvertToInternalUnits(1200.0, UnitTypeId.Millimeters));
            List<Rect2> occupied = CollectOccupiedRectangles(document, view);
            List<LeaderRoute2> leaderRoutes = new List<LeaderRoute2>();
            var createdTagIds = new List<ElementId>();

            using (var transaction = new Transaction(document, "Tung MEP - Smart Auto Tag Pipes"))
            {
                transaction.Start();
                ConfigureFailureHandling(transaction);
                try
                {
                    tagType = AutoTagArrowheadSupport.EnsureTagTypeWithArrowhead(
                        document,
                        tagType,
                        arrowheadType);
                }
                catch (Exception exception)
                {
                    transaction.RollBack();
                    TaskDialog.Show(
                        "Tung MEP - Smart Auto Tag MEP",
                        "Could not prepare the Tag Type with the selected arrowhead.\n\n" +
                        exception.Message);
                    return Result.Cancelled;
                }
                if (!tagType.IsActive)
                {
                    tagType.Activate();
                    document.Regenerate();
                }

                foreach (PipeTagItem item in items)
                {
                    using (var subTransaction = new SubTransaction(document))
                    {
                        subTransaction.Start();
                        try
                        {
                            BopUpdateResult bopResult = settings.UpdateBopParameter
                                ? TryUpdateBopParameter(item.Pipe, document, settings.BopParameterName)
                                : BopUpdateResult.NotRequested;
                            Reference reference = new Reference(item.Pipe);
                            IndependentTag tag = IndependentTag.Create(
                                document,
                                tagType.Id,
                                view.Id,
                                reference,
                                false,
                                TagOrientation.Horizontal,
                                item.Anchor3);
                            document.Regenerate();

                            Rect2 headOffsets;
                            if (!TryGetTagHeadOffsets(tag, view, out headOffsets))
                            {
                                double fallbackHalfWidth = PaperMillimetersToInternal(18.0, view);
                                double fallbackHalfHeight = PaperMillimetersToInternal(4.0, view);
                                headOffsets = new Rect2(
                                    -fallbackHalfWidth,
                                    -fallbackHalfHeight,
                                    fallbackHalfWidth,
                                    fallbackHalfHeight);
                            }

                            TagPlacementCandidate placement = FindBestPlacement(
                                item,
                                items,
                                headOffsets,
                                occupied,
                                leaderRoutes,
                                settings,
                                offset,
                                gap,
                                clearance,
                                railBand);
                            tag.TagHeadPosition = FromViewPointAtAnchor(
                                view,
                                item.Anchor3,
                                item.Anchor2,
                                placement.Head);
                            document.Regenerate();

                            if (settings.AddLeader)
                            {
                                ConfigureLeader(tag, reference, view, item, placement);
                                document.Regenerate();
                            }
                            subTransaction.Commit();

                            statistics.Created++;
                            createdTagIds.Add(tag.Id);
                            if (placement.HasCollision)
                            {
                                statistics.CollisionFallbacks++;
                            }
                            if (bopResult == BopUpdateResult.Updated)
                            {
                                statistics.BopUpdated++;
                            }
                            else if (bopResult == BopUpdateResult.Missing)
                            {
                                statistics.BopMissing++;
                            }
                            else if (bopResult == BopUpdateResult.ReadOnlyOrInvalid)
                            {
                                statistics.BopReadOnlyOrInvalid++;
                            }
                            occupied.Add(placement.HeadBounds);
                            if (settings.AddLeader)
                            {
                                leaderRoutes.Add(placement.Route);
                            }
                        }
                        catch (Exception exception)
                        {
                            if (subTransaction.GetStatus() == TransactionStatus.Started)
                            {
                                subTransaction.RollBack();
                            }
                            statistics.Failed++;
                            if (string.IsNullOrEmpty(statistics.FirstFailure))
                            {
                                statistics.FirstFailure = exception.Message;
                            }
                        }
                    }
                }
                transaction.Commit();
            }

            string arrangementIssue = null;
            if (settings.OptimizeDocumentationLayout && createdTagIds.Count > 1)
            {
                AnnotationArrangeSettings arrange = CreateDocumentationArrangeSettings(
                    settings.SidePreference,
                    settings.TagGapPaperMm,
                    settings.OffsetPaperMm,
                    settings.ClearancePaperMm,
                    settings.MaximumLaneSteps,
                    settings.AddLeader);
                AnnotationArrangeCommand.ArrangeElements(
                    document,
                    view,
                    createdTagIds,
                    arrange,
                    out arrangementIssue);
            }

            TaskDialog.Show(
                "Tung MEP - Smart Auto Tag MEP",
                BuildReport(statistics, settings, tagType) +
                (string.IsNullOrEmpty(arrangementIssue)
                    ? string.Empty
                    : "\n\nDocumentation arrangement note: " + arrangementIssue));
            return statistics.Created > 0 || statistics.SkippedAlreadyTagged > 0
                ? Result.Succeeded
                : Result.Cancelled;
        }

        private static bool IsSupportedView(View view)
        {
            return view != null && !view.IsTemplate &&
                (view.ViewType == ViewType.FloorPlan ||
                 view.ViewType == ViewType.CeilingPlan ||
                 view.ViewType == ViewType.EngineeringPlan);
        }

        private static AnnotationArrangeSettings CreateDocumentationArrangeSettings(
            AutoTagSidePreference preference,
            double spacing,
            double offset,
            double clearance,
            int searchSteps,
            bool createLeaders)
        {
            AnnotationArrangeSettings arrange = AnnotationArrangeSettings.CreateDefault();
            arrange.Mode = AnnotationArrangeMode.DocumentationRail;
            arrange.Side = preference == AutoTagSidePreference.Left
                ? AnnotationArrangeSide.Left
                : preference == AutoTagSidePreference.Right
                    ? AnnotationArrangeSide.Right
                    : preference == AutoTagSidePreference.Top
                        ? AnnotationArrangeSide.Top
                        : preference == AutoTagSidePreference.Bottom
                            ? AnnotationArrangeSide.Bottom
                            : AnnotationArrangeSide.BothSidesBySource;
            arrange.Alignment = AnnotationRailAlignment.NearModelEdge;
            arrange.LeaderLayout = AnnotationLeaderLayout.OrthogonalElbow;
            arrange.SortMode = AnnotationSortMode.SourcePosition;
            arrange.IncludeIndependentTags = true;
            arrange.IncludeTextNotes = false;
            arrange.PreserveLeaderEnds = true;
            arrange.CreateMissingTagLeaders = createLeaders;
            arrange.AvoidObstacles = true;
            arrange.AutoCreateColumns = true;
            arrange.KeepPinnedFixed = true;
            arrange.PreviewBeforeApply = false;
            arrange.OptimizeLeaderRoutes = true;
            arrange.SpacingPaperMm = spacing;
            arrange.RailOffsetPaperMm = offset;
            arrange.ClearancePaperMm = clearance;
            arrange.ColumnGapPaperMm = Math.Max(8.0, offset);
            arrange.MaximumItemsPerColumn = 12;
            arrange.MaximumSearchSteps = Math.Max(6, searchSteps);
            return arrange;
        }

        private static List<AutoTagTypeOption> CollectPipeTagTypes(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(IsPipeTagType)
                .Select(symbol => new AutoTagTypeOption(symbol))
                .ToList();
        }

        private static bool IsPipeTagType(FamilySymbol symbol)
        {
            return symbol != null && symbol.Category != null &&
                symbol.Category.Id.Value == (long)BuiltInCategory.OST_PipeTags;
        }

        private static List<Pipe> CollectScopePipes(
            UIDocument uiDocument,
            Document document,
            View view,
            AutoTagSettings settings)
        {
            if (settings.Scope == AutoTagScope.ActiveView)
            {
                return new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(Pipe))
                    .Cast<Pipe>()
                    .Where(pipe => pipe != null)
                    .GroupBy(pipe => pipe.Id.Value)
                    .Select(group => group.First())
                    .ToList();
            }

            if (settings.Scope == AutoTagScope.SelectionOrPick)
            {
                List<Pipe> selected = uiDocument.Selection.GetElementIds()
                    .Select(document.GetElement)
                    .OfType<Pipe>()
                    .GroupBy(pipe => pipe.Id.Value)
                    .Select(group => group.First())
                    .ToList();
                if (selected.Count > 0)
                {
                    return selected;
                }
                return uiDocument.Selection
                    .PickObjects(
                        ObjectType.Element,
                        new PipeSelectionFilter(),
                        "Smart Auto Tag MEP: select host-model pipes, then click Finish")
                    .Select(reference => document.GetElement(reference.ElementId))
                    .OfType<Pipe>()
                    .GroupBy(pipe => pipe.Id.Value)
                    .Select(group => group.First())
                    .ToList();
            }

            return uiDocument.Selection
                .PickElementsByRectangle(
                    new PipeSelectionFilter(),
                    "Smart Auto Tag MEP: drag a rectangle around host-model pipes")
                .OfType<Pipe>()
                .GroupBy(pipe => pipe.Id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static HashSet<long> CollectAlreadyTaggedPipeIds(Document document, View view)
        {
            var result = new HashSet<long>();
            foreach (IndependentTag tag in new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>())
            {
                try
                {
                    foreach (ElementId id in tag.GetTaggedLocalElementIds())
                    {
                        if (document.GetElement(id) is Pipe)
                        {
                            result.Add(id.Value);
                        }
                    }
                }
                catch
                {
                }
            }
            return result;
        }

        private static PipeTagItem TryCreatePipeTagItem(Pipe pipe, View view)
        {
            LocationCurve location = pipe == null ? null : pipe.Location as LocationCurve;
            Curve curve = location == null ? null : location.Curve;
            if (curve == null || !curve.IsBound || curve.Length <= 1e-8)
            {
                return null;
            }
            XYZ start = curve.GetEndPoint(0);
            XYZ end = curve.GetEndPoint(1);
            XYZ anchor = curve.Evaluate(0.5, true);
            Point2 start2 = ToViewPoint(view, start);
            Point2 end2 = ToViewPoint(view, end);
            Point2 direction = end2 - start2;
            if (direction.Length <= 1e-8)
            {
                return null;
            }
            direction = direction.Normalize();
            return new PipeTagItem(
                pipe,
                anchor,
                ToViewPoint(view, anchor),
                start2,
                end2,
                direction);
        }

        private static List<Rect2> CollectOccupiedRectangles(Document document, View view)
        {
            var result = new List<Rect2>();
            foreach (Element element in new FilteredElementCollector(document, view.Id)
                .WhereElementIsNotElementType())
            {
                bool annotation = element is IndependentTag ||
                    element is TextNote ||
                    element is Dimension;
                bool pipe = element is Pipe;
                if (!annotation && !pipe)
                {
                    continue;
                }
                BoundingBoxXYZ box;
                try
                {
                    box = element.get_BoundingBox(view);
                }
                catch
                {
                    box = null;
                }
                Rect2 rectangle;
                if (TryProjectBounds(view, box, out rectangle))
                {
                    result.Add(rectangle);
                }
            }
            return result;
        }

        private static bool TryGetTagHeadOffsets(
            IndependentTag tag,
            View view,
            out Rect2 offsets)
        {
            offsets = new Rect2();
            BoundingBoxXYZ box;
            try
            {
                box = tag.get_BoundingBox(view);
            }
            catch
            {
                return false;
            }
            Rect2 bounds;
            if (!TryProjectBounds(view, box, out bounds))
            {
                return false;
            }
            Point2 head = ToViewPoint(view, tag.TagHeadPosition);
            offsets = new Rect2(
                bounds.MinX - head.X,
                bounds.MinY - head.Y,
                bounds.MaxX - head.X,
                bounds.MaxY - head.Y);
            return offsets.Width > 1e-8 && offsets.Height > 1e-8;
        }

        private static TagPlacementCandidate FindBestPlacement(
            PipeTagItem item,
            IList<PipeTagItem> items,
            Rect2 headOffsets,
            IList<Rect2> occupied,
            IList<LeaderRoute2> leaderRoutes,
            AutoTagSettings settings,
            double offset,
            double gap,
            double clearance,
            double railBand)
        {
            List<CardinalSide> sides = GetCandidateSides(item, settings.SidePreference);
            double tagStep = Math.Max(
                item.IsMostlyVertical ? headOffsets.Height : headOffsets.Width,
                gap) + gap;
            var candidates = new List<TagPlacementCandidate>();
            int order = 0;
            foreach (CardinalSide side in sides)
            {
                Point2 outward = SideVector(side);
                Point2 along = new Point2(-outward.Y, outward.X);
                Point2 baseHead = GetRailHead(item, items, side, offset, railBand);
                baseHead = baseHead + outward * GetOutwardHeadExtent(
                    headOffsets,
                    side,
                    clearance);
                if (settings.AddLeader)
                {
                    double shoulder = GetRequiredShoulder(
                        headOffsets,
                        side,
                        gap,
                        offset);
                    baseHead = baseHead + GetShoulderDirection(side) * shoulder;
                }
                foreach (int lane in EnumerateLaneOffsets(settings.MaximumLaneSteps))
                {
                    Point2 head = baseHead + along * (lane * tagStep);
                    Rect2 headBounds = headOffsets.Translate(head);
                    Point2 elbow = GetLeaderElbow(item.Anchor2, head, side);
                    var route = new LeaderRoute2(item.Anchor2, elbow, head);
                    double score = order++;
                    bool collision = false;
                    Rect2 clearanceBounds = headBounds.Inflate(clearance);
                    foreach (Rect2 obstacle in occupied)
                    {
                        if (!clearanceBounds.Intersects(obstacle))
                        {
                            continue;
                        }
                        collision = true;
                        score += 1000000.0 +
                            clearanceBounds.OverlapArea(obstacle) * 100000.0;
                    }
                    foreach (Rect2 obstacle in occupied)
                    {
                        Rect2 expanded = obstacle.Inflate(clearance * 0.35);
                        if (!expanded.Contains(route.Start) && route.Crosses(expanded))
                        {
                            collision = true;
                            score += 350000.0;
                        }
                    }
                    foreach (LeaderRoute2 existingRoute in leaderRoutes)
                    {
                        if (route.Crosses(existingRoute))
                        {
                            score += 100000.0;
                        }
                    }
                    score += route.Length * 50.0;
                    score += Math.Abs(lane) * 100.0;
                    candidates.Add(new TagPlacementCandidate(
                        head,
                        elbow,
                        headBounds,
                        route,
                        collision,
                        score));
                }
            }
            return candidates
                .OrderBy(candidate => candidate.Score)
                .First();
        }

        private static double GetOutwardHeadExtent(
            Rect2 headOffsets,
            CardinalSide side,
            double clearance)
        {
            if (side == CardinalSide.Left)
            {
                return Math.Max(0.0, headOffsets.MaxX) + clearance;
            }
            if (side == CardinalSide.Right)
            {
                return Math.Max(0.0, -headOffsets.MinX) + clearance;
            }
            if (side == CardinalSide.Top)
            {
                return Math.Max(0.0, -headOffsets.MinY) + clearance;
            }
            return Math.Max(0.0, headOffsets.MaxY) + clearance;
        }

        private static Point2 GetRailHead(
            PipeTagItem item,
            IList<PipeTagItem> items,
            CardinalSide side,
            double offset,
            double railBand)
        {
            bool horizontalSide = side == CardinalSide.Left || side == CardinalSide.Right;
            double itemCoordinate = horizontalSide ? item.Anchor2.X : item.Anchor2.Y;
            List<PipeTagItem> neighbors = items
                .Where(candidate => candidate.IsMostlyVertical == item.IsMostlyVertical)
                .Where(candidate => Math.Abs(
                    (horizontalSide ? candidate.Anchor2.X : candidate.Anchor2.Y) -
                    itemCoordinate) <= railBand)
                .Where(candidate => Math.Abs(
                    (horizontalSide ? candidate.Anchor2.Y : candidate.Anchor2.X) -
                    (horizontalSide ? item.Anchor2.Y : item.Anchor2.X)) <= railBand * 4.0)
                .ToList();
            if (neighbors.Count == 0)
            {
                neighbors.Add(item);
            }
            if (side == CardinalSide.Left)
            {
                return new Point2(neighbors.Min(candidate => candidate.MinX) - offset, item.Anchor2.Y);
            }
            if (side == CardinalSide.Right)
            {
                return new Point2(neighbors.Max(candidate => candidate.MaxX) + offset, item.Anchor2.Y);
            }
            if (side == CardinalSide.Top)
            {
                return new Point2(item.Anchor2.X, neighbors.Max(candidate => candidate.MaxY) + offset);
            }
            return new Point2(item.Anchor2.X, neighbors.Min(candidate => candidate.MinY) - offset);
        }

        private static List<CardinalSide> GetCandidateSides(
            PipeTagItem item,
            AutoTagSidePreference preference)
        {
            if (preference == AutoTagSidePreference.Left)
            {
                return new List<CardinalSide> { CardinalSide.Left };
            }
            if (preference == AutoTagSidePreference.Right)
            {
                return new List<CardinalSide> { CardinalSide.Right };
            }
            if (preference == AutoTagSidePreference.Top)
            {
                return new List<CardinalSide> { CardinalSide.Top };
            }
            if (preference == AutoTagSidePreference.Bottom)
            {
                return new List<CardinalSide> { CardinalSide.Bottom };
            }
            return item.IsMostlyVertical
                ? new List<CardinalSide> { CardinalSide.Left, CardinalSide.Right }
                : new List<CardinalSide> { CardinalSide.Top, CardinalSide.Bottom };
        }

        private static IEnumerable<int> EnumerateLaneOffsets(int maximumSteps)
        {
            yield return 0;
            for (int step = 1; step <= maximumSteps; step++)
            {
                yield return step;
                yield return -step;
            }
        }

        private static Point2 SideVector(CardinalSide side)
        {
            if (side == CardinalSide.Left)
            {
                return new Point2(-1.0, 0.0);
            }
            if (side == CardinalSide.Right)
            {
                return new Point2(1.0, 0.0);
            }
            if (side == CardinalSide.Top)
            {
                return new Point2(0.0, 1.0);
            }
            return new Point2(0.0, -1.0);
        }

        private static Point2 GetShoulderDirection(CardinalSide side)
        {
            return side == CardinalSide.Left || side == CardinalSide.Right
                ? new Point2(0.0, 1.0)
                : new Point2(1.0, 0.0);
        }

        private static double GetRequiredShoulder(
            Rect2 headOffsets,
            CardinalSide side,
            double gap,
            double offset)
        {
            double tagExtentTowardElbow =
                side == CardinalSide.Left || side == CardinalSide.Right
                    ? Math.Max(0.0, -headOffsets.MinY)
                    : Math.Max(0.0, -headOffsets.MinX);
            return Math.Max(
                Math.Max(gap * 2.0, offset * 0.5),
                tagExtentTowardElbow + gap);
        }

        private static Point2 GetLeaderElbow(Point2 anchor, Point2 head, CardinalSide side)
        {
            return side == CardinalSide.Left || side == CardinalSide.Right
                ? new Point2(head.X, anchor.Y)
                : new Point2(anchor.X, head.Y);
        }

        private static void ConfigureLeader(
            IndependentTag tag,
            Reference reference,
            View view,
            PipeTagItem item,
            TagPlacementCandidate placement)
        {
            try
            {
                tag.HasLeader = true;
                if (tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
                {
                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                    tag.SetLeaderEnd(reference, item.Anchor3);
                }
                tag.SetLeaderElbow(
                    reference,
                    FromViewPointAtAnchor(
                        view,
                        item.Anchor3,
                        item.Anchor2,
                        placement.Elbow));
            }
            catch
            {
                // Some tag families only support an attached straight leader.
                try
                {
                    tag.HasLeader = true;
                }
                catch
                {
                }
            }
        }

        private static BopUpdateResult TryUpdateBopParameter(
            Pipe pipe,
            Document document,
            string parameterName)
        {
            Parameter parameter = pipe.LookupParameter(parameterName);
            if (parameter == null)
            {
                return BopUpdateResult.Missing;
            }
            if (parameter.IsReadOnly)
            {
                return BopUpdateResult.ReadOnlyOrInvalid;
            }
            LocationCurve location = pipe.Location as LocationCurve;
            Curve curve = location == null ? null : location.Curve;
            Level level = document.GetElement(pipe.LevelId) as Level;
            if (curve == null || level == null)
            {
                return BopUpdateResult.ReadOnlyOrInvalid;
            }
            double centerElevation = curve.Evaluate(0.5, true).Z;
            Parameter outsideDiameter = pipe.get_Parameter(
                BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            Parameter nominalDiameter = pipe.get_Parameter(
                BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            double diameter = outsideDiameter != null && outsideDiameter.HasValue
                ? outsideDiameter.AsDouble()
                : nominalDiameter != null && nominalDiameter.HasValue
                    ? nominalDiameter.AsDouble()
                    : 0.0;
            double relativeBop = centerElevation - diameter * 0.5 - level.Elevation;
            bool updated;
            if (parameter.StorageType == StorageType.Double)
            {
                updated = parameter.Set(relativeBop);
            }
            else if (parameter.StorageType == StorageType.String)
            {
                double millimeters = UnitUtils.ConvertFromInternalUnits(
                    relativeBop,
                    UnitTypeId.Millimeters);
                string sign = millimeters >= 0.0 ? "+" : "-";
                updated = parameter.Set(
                    "BOP-FFL" + sign + Math.Abs(Math.Round(millimeters)).ToString("0"));
            }
            else
            {
                updated = false;
            }
            return updated
                ? BopUpdateResult.Updated
                : BopUpdateResult.ReadOnlyOrInvalid;
        }

        private static bool TryProjectBounds(
            View view,
            BoundingBoxXYZ box,
            out Rect2 rectangle)
        {
            rectangle = new Rect2();
            if (box == null)
            {
                return false;
            }
            Transform transform = box.Transform ?? Transform.Identity;
            var points = new List<Point2>();
            for (int x = 0; x <= 1; x++)
            {
                for (int y = 0; y <= 1; y++)
                {
                    for (int z = 0; z <= 1; z++)
                    {
                        XYZ local = new XYZ(
                            x == 0 ? box.Min.X : box.Max.X,
                            y == 0 ? box.Min.Y : box.Max.Y,
                            z == 0 ? box.Min.Z : box.Max.Z);
                        points.Add(ToViewPoint(view, transform.OfPoint(local)));
                    }
                }
            }
            rectangle = new Rect2(
                points.Min(point => point.X),
                points.Min(point => point.Y),
                points.Max(point => point.X),
                points.Max(point => point.Y));
            return rectangle.Width > 1e-8 || rectangle.Height > 1e-8;
        }

        private static Point2 ToViewPoint(View view, XYZ point)
        {
            XYZ delta = point - view.Origin;
            return new Point2(
                delta.DotProduct(view.RightDirection),
                delta.DotProduct(view.UpDirection));
        }

        private static XYZ FromViewPointAtAnchor(
            View view,
            XYZ anchor3,
            Point2 anchor2,
            Point2 target)
        {
            Point2 delta = target - anchor2;
            return anchor3 + view.RightDirection * delta.X + view.UpDirection * delta.Y;
        }

        private static double PaperMillimetersToInternal(double millimeters, View view)
        {
            return UnitUtils.ConvertToInternalUnits(
                millimeters * Math.Max(1, view.Scale),
                UnitTypeId.Millimeters);
        }

        private static void ConfigureFailureHandling(Transaction transaction)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }

        private static string BuildReport(
            AutoTagStatistics statistics,
            AutoTagSettings settings,
            FamilySymbol tagType)
        {
            var report = new StringBuilder();
            report.AppendLine("Pipe tags created: " + statistics.Created + ".");
            report.AppendLine("Pipes in scope: " + statistics.ScopePipes + ".");
            report.AppendLine("Already tagged pipes skipped: " + statistics.SkippedAlreadyTagged + ".");
            report.AppendLine("Pipes with invalid view geometry: " + statistics.InvalidGeometry + ".");
            report.AppendLine("Tags failed: " + statistics.Failed + ".");
            report.AppendLine("Crowded placements using best available lane: " +
                statistics.CollisionFallbacks + ".");
            report.AppendLine();
            report.AppendLine("Pipe Tag Type: " +
                (tagType.Family == null ? tagType.Name : tagType.Family.Name + " : " + tagType.Name));
            report.AppendLine("Leader: " + (settings.AddLeader ? "on" : "off") + ".");
            report.AppendLine("Leader arrowhead: " + settings.SelectedArrowheadTypeName + ".");
            if (settings.UpdateBopParameter)
            {
                report.AppendLine("BOP-FFL parameter updated: " + statistics.BopUpdated + ".");
                report.AppendLine("BOP-FFL parameter missing: " + statistics.BopMissing + ".");
                report.AppendLine("BOP-FFL parameter read-only/invalid: " +
                    statistics.BopReadOnlyOrInvalid + ".");
                report.AppendLine("Parameter name: " + settings.BopParameterName + ".");
            }
            report.AppendLine();
            report.AppendLine("Existing manual tags were not deleted or moved.");
            if (!string.IsNullOrEmpty(statistics.FirstFailure))
            {
                report.AppendLine();
                report.AppendLine("First failure: " + statistics.FirstFailure);
            }
            return report.ToString().TrimEnd();
        }

        private sealed class PipeSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element) { return element is Pipe; }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }

        private sealed class PipeTagItem
        {
            public PipeTagItem(
                Pipe pipe,
                XYZ anchor3,
                Point2 anchor2,
                Point2 start2,
                Point2 end2,
                Point2 direction)
            {
                Pipe = pipe;
                Anchor3 = anchor3;
                Anchor2 = anchor2;
                Start2 = start2;
                End2 = end2;
                Direction = direction;
            }
            public Pipe Pipe { get; private set; }
            public XYZ Anchor3 { get; private set; }
            public Point2 Anchor2 { get; private set; }
            public Point2 Start2 { get; private set; }
            public Point2 End2 { get; private set; }
            public Point2 Direction { get; private set; }
            public bool IsMostlyVertical { get { return Math.Abs(Direction.Y) >= Math.Abs(Direction.X); } }
            public double MinX { get { return Math.Min(Start2.X, End2.X); } }
            public double MaxX { get { return Math.Max(Start2.X, End2.X); } }
            public double MinY { get { return Math.Min(Start2.Y, End2.Y); } }
            public double MaxY { get { return Math.Max(Start2.Y, End2.Y); } }
        }

        private sealed class TagPlacementCandidate
        {
            public TagPlacementCandidate(
                Point2 head,
                Point2 elbow,
                Rect2 headBounds,
                LeaderRoute2 route,
                bool hasCollision,
                double score)
            {
                Head = head;
                Elbow = elbow;
                HeadBounds = headBounds;
                Route = route;
                HasCollision = hasCollision;
                Score = score;
            }
            public Point2 Head { get; private set; }
            public Point2 Elbow { get; private set; }
            public Rect2 HeadBounds { get; private set; }
            public LeaderRoute2 Route { get; private set; }
            public bool HasCollision { get; private set; }
            public double Score { get; private set; }
        }

        private struct Point2
        {
            public Point2(double x, double y) { X = x; Y = y; }
            public double X;
            public double Y;
            public double Length { get { return Math.Sqrt(X * X + Y * Y); } }
            public Point2 Normalize()
            {
                double length = Length;
                return length <= 1e-12 ? new Point2() : new Point2(X / length, Y / length);
            }
            public static Point2 operator +(Point2 left, Point2 right)
            {
                return new Point2(left.X + right.X, left.Y + right.Y);
            }
            public static Point2 operator -(Point2 left, Point2 right)
            {
                return new Point2(left.X - right.X, left.Y - right.Y);
            }
            public static Point2 operator *(Point2 value, double factor)
            {
                return new Point2(value.X * factor, value.Y * factor);
            }
        }

        private struct Rect2
        {
            public Rect2(double minX, double minY, double maxX, double maxY)
            {
                MinX = Math.Min(minX, maxX);
                MinY = Math.Min(minY, maxY);
                MaxX = Math.Max(minX, maxX);
                MaxY = Math.Max(minY, maxY);
            }
            public double MinX;
            public double MinY;
            public double MaxX;
            public double MaxY;
            public double Width { get { return MaxX - MinX; } }
            public double Height { get { return MaxY - MinY; } }
            public Rect2 Translate(Point2 point)
            {
                return new Rect2(MinX + point.X, MinY + point.Y, MaxX + point.X, MaxY + point.Y);
            }
            public Rect2 Inflate(double distance)
            {
                return new Rect2(
                    MinX - distance,
                    MinY - distance,
                    MaxX + distance,
                    MaxY + distance);
            }
            public bool Intersects(Rect2 other)
            {
                return MinX < other.MaxX && MaxX > other.MinX &&
                    MinY < other.MaxY && MaxY > other.MinY;
            }
            public double OverlapArea(Rect2 other)
            {
                double width = Math.Max(0.0, Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX));
                double height = Math.Max(0.0, Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY));
                return width * height;
            }
            public bool Contains(Point2 point)
            {
                return point.X >= MinX && point.X <= MaxX &&
                    point.Y >= MinY && point.Y <= MaxY;
            }
        }

        private struct LeaderRoute2
        {
            public LeaderRoute2(Point2 start, Point2 elbow, Point2 end)
            {
                Start = start;
                Elbow = elbow;
                End = end;
            }
            public Point2 Start;
            public Point2 Elbow;
            public Point2 End;
            public double Length
            {
                get { return (Elbow - Start).Length + (End - Elbow).Length; }
            }
            public bool Crosses(LeaderRoute2 other)
            {
                return ToAnnotationRoute().ConflictsWith(
                    other.ToAnnotationRoute());
            }
            private AnnotationRoute2 ToAnnotationRoute()
            {
                return new AnnotationRoute2(
                    new AnnotationRoutePoint2(Start.X, Start.Y),
                    new AnnotationRoutePoint2(Elbow.X, Elbow.Y),
                    new AnnotationRoutePoint2(End.X, End.Y));
            }
            public bool Crosses(Rect2 rectangle)
            {
                return SegmentCrossesRect(Start, Elbow, rectangle) ||
                    SegmentCrossesRect(Elbow, End, rectangle);
            }
            private static bool SegmentCrossesRect(Point2 start, Point2 end, Rect2 rectangle)
            {
                if (rectangle.Contains(start) || rectangle.Contains(end))
                {
                    return true;
                }
                Point2 bottomLeft = new Point2(rectangle.MinX, rectangle.MinY);
                Point2 bottomRight = new Point2(rectangle.MaxX, rectangle.MinY);
                Point2 topRight = new Point2(rectangle.MaxX, rectangle.MaxY);
                Point2 topLeft = new Point2(rectangle.MinX, rectangle.MaxY);
                return SegmentsCross(start, end, bottomLeft, bottomRight) ||
                    SegmentsCross(start, end, bottomRight, topRight) ||
                    SegmentsCross(start, end, topRight, topLeft) ||
                    SegmentsCross(start, end, topLeft, bottomLeft);
            }
            private static bool SegmentsCross(Point2 a, Point2 b, Point2 c, Point2 d)
            {
                const double tolerance = 1e-8;
                if ((a - b).Length <= tolerance || (c - d).Length <= tolerance)
                {
                    return false;
                }
                double o1 = Cross(b - a, c - a);
                double o2 = Cross(b - a, d - a);
                double o3 = Cross(d - c, a - c);
                double o4 = Cross(d - c, b - c);
                return o1 * o2 < -tolerance && o3 * o4 < -tolerance;
            }
            private static double Cross(Point2 left, Point2 right)
            {
                return left.X * right.Y - left.Y * right.X;
            }
        }

        private sealed class AutoTagStatistics
        {
            public int ScopePipes;
            public int Created;
            public int SkippedAlreadyTagged;
            public int InvalidGeometry;
            public int Failed;
            public int CollisionFallbacks;
            public int BopUpdated;
            public int BopMissing;
            public int BopReadOnlyOrInvalid;
            public string FirstFailure;
        }

        private enum CardinalSide
        {
            Left,
            Right,
            Top,
            Bottom
        }

        private enum BopUpdateResult
        {
            NotRequested,
            Updated,
            Missing,
            ReadOnlyOrInvalid
        }
    }
}
