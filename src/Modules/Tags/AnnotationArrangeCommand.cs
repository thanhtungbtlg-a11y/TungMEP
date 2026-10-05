using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WinForms = System.Windows.Forms;

namespace TungMEP.Modules.Tags
{
    [Transaction(TransactionMode.Manual)]
    public class AnnotationArrangeCommand : IExternalCommand
    {
        private static readonly ISet<long> ModelObstacleCategoryIds =
            new HashSet<long>
            {
                (long)BuiltInCategory.OST_Walls,
                (long)BuiltInCategory.OST_Floors,
                (long)BuiltInCategory.OST_Ceilings,
                (long)BuiltInCategory.OST_Roofs,
                (long)BuiltInCategory.OST_Columns,
                (long)BuiltInCategory.OST_StructuralColumns,
                (long)BuiltInCategory.OST_StructuralFraming,
                (long)BuiltInCategory.OST_PipeCurves,
                (long)BuiltInCategory.OST_PipeFitting,
                (long)BuiltInCategory.OST_PipeAccessory,
                (long)BuiltInCategory.OST_DuctCurves,
                (long)BuiltInCategory.OST_DuctFitting,
                (long)BuiltInCategory.OST_DuctAccessory,
                (long)BuiltInCategory.OST_MechanicalEquipment,
                (long)BuiltInCategory.OST_PlumbingFixtures,
                (long)BuiltInCategory.OST_Sprinklers,
                (long)BuiltInCategory.OST_ElectricalEquipment,
                (long)BuiltInCategory.OST_ElectricalFixtures,
                (long)BuiltInCategory.OST_LightingFixtures,
                (long)BuiltInCategory.OST_Casework,
                (long)BuiltInCategory.OST_GenericModel
            };

        internal static bool ArrangeElements(
            Document document,
            View view,
            IList<ElementId> elementIds,
            AnnotationArrangeSettings requestedSettings,
            out string issue)
        {
            issue = null;
            if (document == null || !IsSupportedView(view) ||
                elementIds == null || elementIds.Count == 0)
            {
                issue = "No supported annotations were supplied for arrangement.";
                return false;
            }

            AnnotationArrangeSettings settings = requestedSettings == null
                ? AnnotationArrangeSettings.CreateDefault()
                : requestedSettings.Clone();
            settings.Normalize();
            List<Element> scope = elementIds
                .Select(document.GetElement)
                .Where(element => Matches(element, settings))
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
            var statistics = new AnnotationArrangeStatistics
            {
                ScopeElements = scope.Count
            };
            List<AnnotationItem> items = CaptureItems(
                document,
                view,
                scope,
                settings,
                statistics);
            List<AnnotationItem> movable = items
                .Where(item => !item.Pinned || !settings.KeepPinnedFixed)
                .ToList();
            if (movable.Count == 0)
            {
                issue = "No movable annotation was available.";
                return false;
            }

            var selectedIds = new HashSet<long>(items.Select(item => item.IdValue));
            List<Rect2> obstacles = settings.AvoidObstacles
                ? CollectObstacleRectangles(document, view, selectedIds)
                : new List<Rect2>();
            foreach (AnnotationItem locked in items.Where(
                item => item.Pinned && settings.KeepPinnedFixed))
            {
                obstacles.Add(locked.CurrentBounds);
            }
            ArrangePlan plan = BuildPlan(
                movable,
                obstacles,
                view,
                settings,
                null,
                statistics);
            if (plan == null || plan.Destinations.Count == 0 ||
                !ApplyPlan(document, view, plan, settings, statistics))
            {
                issue = string.IsNullOrEmpty(statistics.FirstFailure)
                    ? "Could not calculate a valid annotation arrangement."
                    : statistics.FirstFailure;
                return false;
            }
            issue = statistics.Failed > 0
                ? statistics.FirstFailure
                : null;
            return statistics.Arranged > 0;
        }

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
                    "Tung MEP - Smart Annotation Arrange",
                    "Open a plan, RCP, section, elevation, detail, or drafting view before running this tool.");
                return Result.Cancelled;
            }

            AnnotationArrangeSettings settings;
            using (var form = new AnnotationArrangeForm(
                AnnotationArrangeSettingsStore.Load()))
            {
                if (form.ShowDialog() != WinForms.DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                settings = form.Settings;
            }
            settings.Normalize();
            AnnotationArrangeSettingsStore.Save(settings);

            List<Element> scope;
            try
            {
                scope = CollectScope(uiDocument, document, view, settings);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (scope.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Annotation Arrange",
                    "No supported Independent Tag or Text Note was found in the selected scope.");
                return Result.Cancelled;
            }

            var statistics = new AnnotationArrangeStatistics
            {
                ScopeElements = scope.Count
            };
            List<AnnotationItem> items = CaptureItems(document, view, scope, settings, statistics);
            if (items.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Annotation Arrange",
                    BuildReport(statistics, settings, false));
                return Result.Cancelled;
            }

            PickedRail pickedRail = null;
            if (settings.Side == AnnotationArrangeSide.PickRail)
            {
                try
                {
                    XYZ first = uiDocument.Selection.PickPoint(
                        ObjectSnapTypes.None,
                        "Smart Annotation Arrange: pick the first rail point");
                    XYZ second = uiDocument.Selection.PickPoint(
                        ObjectSnapTypes.None,
                        "Smart Annotation Arrange: pick the second rail point");
                    pickedRail = CreatePickedRail(view, first, second, items);
                    if (pickedRail == null)
                    {
                        TaskDialog.Show(
                            "Tung MEP - Smart Annotation Arrange",
                            "The two rail points are too close together.");
                        return Result.Cancelled;
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }

            HashSet<long> selectedIds = new HashSet<long>(items.Select(item => item.IdValue));
            List<Rect2> obstacles = settings.AvoidObstacles
                ? CollectObstacleRectangles(document, view, selectedIds)
                : new List<Rect2>();
            foreach (AnnotationItem locked in items.Where(item => item.Pinned && settings.KeepPinnedFixed))
            {
                obstacles.Add(locked.CurrentBounds);
                statistics.LockedPinned++;
            }
            List<AnnotationItem> movable = items
                .Where(item => !item.Pinned || !settings.KeepPinnedFixed)
                .ToList();
            if (movable.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Annotation Arrange",
                    BuildReport(statistics, settings, false));
                return Result.Cancelled;
            }

            ArrangePlan plan = BuildPlan(
                movable,
                obstacles,
                view,
                settings,
                pickedRail,
                statistics);
            if (plan == null || plan.Destinations.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Smart Annotation Arrange",
                    "Could not calculate a valid annotation arrangement.");
                return Result.Cancelled;
            }

            using (var group = new TransactionGroup(
                document,
                "Tung MEP - Smart Annotation Arrange"))
            {
                group.Start();
                bool applied = ApplyPlan(
                    document,
                    view,
                    plan,
                    settings,
                    statistics);
                if (!applied)
                {
                    group.RollBack();
                    TaskDialog.Show(
                        "Tung MEP - Smart Annotation Arrange",
                        BuildReport(statistics, settings, false));
                    return Result.Cancelled;
                }
                uiDocument.RefreshActiveView();

                if (settings.PreviewBeforeApply)
                {
                    var preview = new TaskDialog("Tung MEP - Annotation Preview")
                    {
                        MainInstruction = "Keep this annotation arrangement?",
                        MainContent =
                            "Review the result visible in the active view. Choose OK to keep it or Cancel to restore every original position.",
                        CommonButtons = TaskDialogCommonButtons.Ok |
                            TaskDialogCommonButtons.Cancel,
                        DefaultButton = TaskDialogResult.Ok
                    };
                    if (preview.Show() != TaskDialogResult.Ok)
                    {
                        group.RollBack();
                        uiDocument.RefreshActiveView();
                        return Result.Cancelled;
                    }
                }
                group.Assimilate();
            }

            TaskDialog.Show(
                "Tung MEP - Smart Annotation Arrange",
                BuildReport(statistics, settings, true));
            return Result.Succeeded;
        }

        private static bool IsSupportedView(View view)
        {
            if (view == null || view.IsTemplate)
            {
                return false;
            }
            return view.ViewType == ViewType.FloorPlan ||
                view.ViewType == ViewType.CeilingPlan ||
                view.ViewType == ViewType.EngineeringPlan ||
                view.ViewType == ViewType.Section ||
                view.ViewType == ViewType.Elevation ||
                view.ViewType == ViewType.Detail ||
                view.ViewType == ViewType.DraftingView;
        }

        private static List<Element> CollectScope(
            UIDocument uiDocument,
            Document document,
            View view,
            AnnotationArrangeSettings settings)
        {
            if (settings.Scope == AnnotationArrangeScope.ActiveView)
            {
                return new FilteredElementCollector(document, view.Id)
                    .WhereElementIsNotElementType()
                    .Where(element => Matches(element, settings))
                    .GroupBy(element => element.Id.Value)
                    .Select(group => group.First())
                    .ToList();
            }

            if (settings.Scope == AnnotationArrangeScope.SelectionOrPick)
            {
                List<Element> selected = uiDocument.Selection.GetElementIds()
                    .Select(document.GetElement)
                    .Where(element => Matches(element, settings))
                    .GroupBy(element => element.Id.Value)
                    .Select(group => group.First())
                    .ToList();
                if (selected.Count > 0)
                {
                    return selected;
                }
                return uiDocument.Selection
                    .PickObjects(
                        ObjectType.Element,
                        new AnnotationSelectionFilter(settings),
                        "Smart Annotation Arrange: select Tags/Text Notes, then click Finish")
                    .Select(reference => document.GetElement(reference.ElementId))
                    .Where(element => Matches(element, settings))
                    .GroupBy(element => element.Id.Value)
                    .Select(group => group.First())
                    .ToList();
            }

            return uiDocument.Selection
                .PickElementsByRectangle(
                    new AnnotationSelectionFilter(settings),
                    "Smart Annotation Arrange: drag a rectangle around Tags/Text Notes")
                .Where(element => Matches(element, settings))
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static bool Matches(Element element, AnnotationArrangeSettings settings)
        {
            IndependentTag tag = element as IndependentTag;
            if (tag != null)
            {
                return settings.IncludeIndependentTags;
            }
            TextNote note = element as TextNote;
            if (note == null || !settings.IncludeTextNotes)
            {
                return false;
            }
            return note.LeaderCount > 0 || settings.IncludeTextNotesWithoutLeaders;
        }

        private static List<AnnotationItem> CaptureItems(
            Document document,
            View view,
            IList<Element> scope,
            AnnotationArrangeSettings settings,
            AnnotationArrangeStatistics statistics)
        {
            var items = new List<AnnotationItem>();
            foreach (Element element in scope)
            {
                AnnotationItem item = CaptureBasicItem(document, view, element);
                if (item != null)
                {
                    items.Add(item);
                }
                else
                {
                    statistics.UnsupportedOrInvalid++;
                }
            }
            if (items.Count == 0)
            {
                return items;
            }

            using (var transaction = new Transaction(
                document,
                "Tung MEP - Measure Annotation Heads"))
            {
                transaction.Start();
                try
                {
                    foreach (AnnotationItem item in items)
                    {
                        if (item.Element.Pinned)
                        {
                            item.Element.Pinned = false;
                        }
                        IndependentTag tag = item.Element as IndependentTag;
                        if (tag != null && tag.HasLeader)
                        {
                            tag.HasLeader = false;
                        }
                        TextNote note = item.Element as TextNote;
                        if (note != null && note.LeaderCount > 0)
                        {
                            note.RemoveLeaders();
                        }
                    }
                    document.Regenerate();
                    foreach (AnnotationItem item in items)
                    {
                        Rect2 bounds;
                        if (TryGetBounds(item.Element, view, out bounds))
                        {
                            item.HeadOffsets = new Rect2(
                                bounds.MinX - item.Head2.X,
                                bounds.MinY - item.Head2.Y,
                                bounds.MaxX - item.Head2.X,
                                bounds.MaxY - item.Head2.Y);
                            item.CurrentBounds = item.HeadOffsets.Translate(item.Head2);
                        }
                    }
                }
                catch
                {
                }
                transaction.RollBack();
            }

            double fallbackHalfWidth = PaperMillimetersToInternal(20.0, view);
            double fallbackHalfHeight = PaperMillimetersToInternal(4.0, view);
            foreach (AnnotationItem item in items)
            {
                if (item.HeadOffsets.Width <= 1e-8 || item.HeadOffsets.Height <= 1e-8)
                {
                    item.HeadOffsets = new Rect2(
                        -fallbackHalfWidth,
                        -fallbackHalfHeight,
                        fallbackHalfWidth,
                        fallbackHalfHeight);
                    item.CurrentBounds = item.HeadOffsets.Translate(item.Head2);
                }
            }
            return items;
        }

        private static AnnotationItem CaptureBasicItem(
            Document document,
            View view,
            Element element)
        {
            IndependentTag tag = element as IndependentTag;
            if (tag != null)
            {
                XYZ head = tag.TagHeadPosition;
                var item = new AnnotationItem(
                    element,
                    head,
                    ToViewPoint(view, head),
                    tag.Pinned,
                    AnnotationItemKind.IndependentTag);
                CaptureTagLeaders(document, view, tag, item);
                if (item.LeaderStates.Count > 0)
                {
                    item.Source2 = Average(item.LeaderStates.Select(state => state.End2));
                }
                else
                {
                    item.Source2 = TryGetTaggedSource(document, view, tag, item.Head2);
                }
                return item;
            }

            TextNote note = element as TextNote;
            if (note != null)
            {
                XYZ head = note.Coord;
                var item = new AnnotationItem(
                    element,
                    head,
                    ToViewPoint(view, head),
                    note.Pinned,
                    AnnotationItemKind.TextNote);
                int index = 0;
                foreach (Leader leader in note.GetLeaders())
                {
                    var state = new AnnotationLeaderState
                    {
                        Index = index++,
                        HasEnd = true,
                        End3 = leader.End,
                        End2 = ToViewPoint(view, leader.End),
                        HasElbow = true,
                        Elbow3 = leader.Elbow,
                        Elbow2 = ToViewPoint(view, leader.Elbow)
                    };
                    item.LeaderStates.Add(state);
                }
                item.Source2 = item.LeaderStates.Count > 0
                    ? Average(item.LeaderStates.Select(state => state.End2))
                    : item.Head2;
                return item;
            }
            return null;
        }

        private static void CaptureTagLeaders(
            Document document,
            View view,
            IndependentTag tag,
            AnnotationItem item)
        {
            IList<Reference> references;
            try { references = tag.GetTaggedReferences(); }
            catch { references = new List<Reference>(); }
            int index = 0;
            foreach (Reference reference in references)
            {
                var state = new AnnotationLeaderState
                {
                    Index = index++,
                    Reference = reference
                };
                if (tag.HasLeader)
                {
                    try
                    {
                        state.End3 = tag.GetLeaderEnd(reference);
                        state.End2 = ToViewPoint(view, state.End3);
                        state.HasEnd = true;
                    }
                    catch { }
                    try
                    {
                        if (tag.HasLeaderElbow(reference))
                        {
                            state.Elbow3 = tag.GetLeaderElbow(reference);
                            state.Elbow2 = ToViewPoint(view, state.Elbow3);
                            state.HasElbow = true;
                        }
                    }
                    catch { }
                }
                if (!state.HasEnd)
                {
                    Element source = null;
                    try { source = document.GetElement(reference.ElementId); }
                    catch { }
                    Rect2 sourceBounds;
                    if (source != null && TryGetBounds(source, view, out sourceBounds))
                    {
                        state.End2 = sourceBounds.Center;
                        state.End3 = FromViewPointAtAnchor(
                            view,
                            item.Head3,
                            item.Head2,
                            state.End2);
                    }
                    else
                    {
                        state.End2 = item.Head2;
                        state.End3 = item.Head3;
                    }
                }
                item.LeaderStates.Add(state);
            }
        }

        private static Point2 TryGetTaggedSource(
            Document document,
            View view,
            IndependentTag tag,
            Point2 fallback)
        {
            var points = new List<Point2>();
            try
            {
                foreach (Element element in tag.GetTaggedLocalElements())
                {
                    Rect2 bounds;
                    if (TryGetBounds(element, view, out bounds))
                    {
                        points.Add(bounds.Center);
                    }
                }
            }
            catch { }
            return points.Count == 0 ? fallback : Average(points);
        }

        private static PickedRail CreatePickedRail(
            View view,
            XYZ first,
            XYZ second,
            IList<AnnotationItem> items)
        {
            Point2 first2 = ToViewPoint(view, first);
            Point2 second2 = ToViewPoint(view, second);
            Point2 delta = second2 - first2;
            if (delta.Length <= 1e-8)
            {
                return null;
            }
            Point2 sourceCenter = new Point2(
                items.Average(item => item.Source2.X),
                items.Average(item => item.Source2.Y));
            if (Math.Abs(delta.Y) >= Math.Abs(delta.X))
            {
                double x = (first2.X + second2.X) * 0.5;
                return new PickedRail
                {
                    Side = x <= sourceCenter.X
                        ? AnnotationArrangeSide.Left
                        : AnnotationArrangeSide.Right,
                    Coordinate = x
                };
            }
            double y = (first2.Y + second2.Y) * 0.5;
            return new PickedRail
            {
                Side = y >= sourceCenter.Y
                    ? AnnotationArrangeSide.Top
                    : AnnotationArrangeSide.Bottom,
                Coordinate = y
            };
        }

        private static ArrangePlan BuildPlan(
            IList<AnnotationItem> items,
            IList<Rect2> obstacles,
            View view,
            AnnotationArrangeSettings settings,
            PickedRail pickedRail,
            AnnotationArrangeStatistics statistics)
        {
            if (settings.Side != AnnotationArrangeSide.BothSidesBySource &&
                settings.Side != AnnotationArrangeSide.BothSidesKeepCurrent)
            {
                return BuildSingleSidePlan(
                    items,
                    obstacles,
                    view,
                    settings,
                    pickedRail,
                    statistics,
                    null);
            }

            IDictionary<AnnotationArrangeSide, List<AnnotationItem>> partitions =
                SplitBothSides(items, settings.Side);
            var combined = new ArrangePlan { Side = settings.Side };
            var workingObstacles = new List<Rect2>(obstacles);
            var workingRoutes = new List<LeaderRoute2>();
            int columns = 0;
            int remainingCollisions = 0;
            foreach (AnnotationArrangeSide side in new[]
            {
                AnnotationArrangeSide.Left,
                AnnotationArrangeSide.Right
            })
            {
                List<AnnotationItem> sideItems;
                if (!partitions.TryGetValue(side, out sideItems) || sideItems.Count == 0)
                {
                    continue;
                }
                AnnotationArrangeSettings sideSettings = settings.Clone();
                sideSettings.Side = side;
                var localStatistics = new AnnotationArrangeStatistics();
                ArrangePlan sidePlan = BuildSingleSidePlan(
                    sideItems,
                    workingObstacles,
                    view,
                    sideSettings,
                    null,
                    localStatistics,
                    workingRoutes);
                foreach (KeyValuePair<AnnotationItem, Point2> entry in sidePlan.Destinations)
                {
                    combined.Destinations[entry.Key] = entry.Value;
                    combined.ItemSides[entry.Key] = side;
                    workingObstacles.Add(entry.Key.HeadOffsets.Translate(entry.Value));
                }
                workingRoutes.AddRange(BuildLeaderRoutes(
                    sideItems,
                    sidePlan.Destinations,
                    side,
                    sideSettings));
                columns += localStatistics.ColumnsCreated;
                remainingCollisions += localStatistics.RemainingCollisionCandidates;
            }
            statistics.ColumnsCreated = columns;
            statistics.RemainingCollisionCandidates += remainingCollisions;
            statistics.ResolvedSide = "Left + Right";
            return combined;
        }

        private static ArrangePlan BuildSingleSidePlan(
            IList<AnnotationItem> items,
            IList<Rect2> obstacles,
            View view,
            AnnotationArrangeSettings settings,
            PickedRail pickedRail,
            AnnotationArrangeStatistics statistics,
            IList<LeaderRoute2> initialRoutes)
        {
            AnnotationArrangeSide side = pickedRail == null
                ? ResolveSide(items, obstacles, view, settings)
                : pickedRail.Side;
            bool verticalStack = side == AnnotationArrangeSide.Left ||
                side == AnnotationArrangeSide.Right;
            double spacing = PaperMillimetersToInternal(settings.SpacingPaperMm, view);
            double railOffset = PaperMillimetersToInternal(settings.RailOffsetPaperMm, view);
            double clearance = PaperMillimetersToInternal(settings.ClearancePaperMm, view);
            double columnGap = PaperMillimetersToInternal(settings.ColumnGapPaperMm, view);

            List<AnnotationItem> ordered = OrderItems(
                items,
                verticalStack,
                settings.SortMode,
                settings.ReverseOrder,
                side);
            int maximum = settings.AutoCreateColumns &&
                (settings.Mode == AnnotationArrangeMode.SmartRail ||
                 settings.Mode == AnnotationArrangeMode.DocumentationRail ||
                 settings.Mode == AnnotationArrangeMode.CompactStack ||
                 settings.Mode == AnnotationArrangeMode.ReflowAfterEdit)
                ? settings.MaximumItemsPerColumn
                : ordered.Count;
            List<List<AnnotationItem>> groups = ordered
                .Select((item, index) => new { Item = item, Index = index })
                .GroupBy(entry => entry.Index / Math.Max(1, maximum))
                .Select(group => group.Select(entry => entry.Item).ToList())
                .ToList();

            double baseRail = pickedRail == null
                ? CalculateBaseRail(items, side, railOffset, settings.Mode)
                : pickedRail.Coordinate;
            double maximumNormalSize = verticalStack
                ? items.Max(item => item.HeadOffsets.Width)
                : items.Max(item => item.HeadOffsets.Height);
            var plan = new ArrangePlan { Side = side };
            var occupied = new List<Rect2>(obstacles);
            var plannedRoutes = initialRoutes == null
                ? new List<LeaderRoute2>()
                : new List<LeaderRoute2>(initialRoutes);
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                List<AnnotationItem> group = groups[groupIndex];
                double outwardSign = side == AnnotationArrangeSide.Left ||
                    side == AnnotationArrangeSide.Bottom
                    ? -1.0
                    : 1.0;
                double groupRail = baseRail + outwardSign * groupIndex *
                    (maximumNormalSize + columnGap);
                Dictionary<AnnotationItem, Point2> groupHeads = BuildGroupHeads(
                    group,
                    side,
                    groupRail,
                    spacing,
                    settings);

                if (settings.OptimizeLeaderRoutes &&
                    settings.Mode != AnnotationArrangeMode.AlignOnly)
                {
                    Dictionary<AnnotationItem, Point2> best = groupHeads;
                    double bestScore = double.PositiveInfinity;
                    int bestConflicts = int.MaxValue;
                    int searchSteps = Math.Max(1, settings.MaximumSearchSteps);
                    double stackStep = Math.Max(
                        spacing,
                        verticalStack
                            ? group.Max(item => item.HeadOffsets.Height) + spacing
                            : group.Max(item => item.HeadOffsets.Width) + spacing);
                    foreach (List<AnnotationItem> candidateOrder in BuildCandidateOrders(
                        group,
                        verticalStack,
                        side,
                        settings))
                    {
                        for (int outwardLane = 0; outwardLane <= searchSteps; outwardLane++)
                        {
                            double candidateRail = groupRail + outwardSign * outwardLane *
                                (maximumNormalSize + columnGap);
                            List<AnnotationItem> crossingAwareOrder =
                                OptimizeOrderForRouteCrossings(
                                    candidateOrder,
                                    side,
                                    candidateRail,
                                    spacing,
                                    settings,
                                    plannedRoutes);
                            Dictionary<AnnotationItem, Point2> baseCandidate = BuildGroupHeads(
                                crossingAwareOrder,
                                side,
                                candidateRail,
                                spacing,
                                settings);
                            foreach (int stackLane in EnumerateLaneOffsets(
                                Math.Min(6, Math.Max(2, searchSteps / 2))))
                            {
                                Dictionary<AnnotationItem, Point2> candidate = TranslateHeads(
                                    baseCandidate,
                                    verticalStack,
                                    stackLane * stackStep);
                                int conflicts;
                                double score = CalculateLayoutScore(
                                    group,
                                    candidate,
                                    occupied,
                                    plannedRoutes,
                                    clearance,
                                    side,
                                    settings,
                                    out conflicts);
                                score += outwardLane * 250.0 + Math.Abs(stackLane) * 150.0;
                                if (score < bestScore)
                                {
                                    best = candidate;
                                    bestScore = score;
                                    bestConflicts = conflicts;
                                }
                            }
                        }
                    }
                    groupHeads = best;
                    statistics.RemainingCollisionCandidates += Math.Max(0, bestConflicts);
                }
                else if (settings.AvoidObstacles && settings.Mode != AnnotationArrangeMode.AlignOnly)
                {
                    Dictionary<AnnotationItem, Point2> best = groupHeads;
                    int bestCollisions = CountCollisions(
                        group,
                        best,
                        occupied,
                        clearance);
                    for (int lane = 1; lane <= 8 && bestCollisions > 0; lane++)
                    {
                        double shiftedRail = groupRail + outwardSign * lane *
                            (maximumNormalSize + columnGap);
                        Dictionary<AnnotationItem, Point2> candidate = BuildGroupHeads(
                            group,
                            side,
                            shiftedRail,
                            spacing,
                            settings);
                        int collisions = CountCollisions(
                            group,
                            candidate,
                            occupied,
                            clearance);
                        if (collisions < bestCollisions)
                        {
                            best = candidate;
                            bestCollisions = collisions;
                        }
                    }
                    groupHeads = best;
                    statistics.RemainingCollisionCandidates += bestCollisions;
                }

                foreach (KeyValuePair<AnnotationItem, Point2> entry in groupHeads)
                {
                    plan.Destinations[entry.Key] = entry.Value;
                    plan.ItemSides[entry.Key] = side;
                    occupied.Add(entry.Key.HeadOffsets.Translate(entry.Value));
                }
                plannedRoutes.AddRange(BuildLeaderRoutes(
                    group,
                    groupHeads,
                    side,
                    settings));
            }
            statistics.ColumnsCreated = groups.Count;
            statistics.ResolvedSide = side.ToString();
            return plan;
        }

        private static IDictionary<AnnotationArrangeSide, List<AnnotationItem>> SplitBothSides(
            IList<AnnotationItem> items,
            AnnotationArrangeSide splitMode)
        {
            if (splitMode == AnnotationArrangeSide.BothSidesKeepCurrent)
            {
                List<AnnotationItem> currentLeft = items
                    .Where(item => item.Head2.X <= item.Source2.X)
                    .ToList();
                List<AnnotationItem> currentRight = items
                    .Where(item => item.Head2.X > item.Source2.X)
                    .ToList();
                if (currentLeft.Count > 0 && currentRight.Count > 0)
                {
                    return new Dictionary<AnnotationArrangeSide, List<AnnotationItem>>
                    {
                        { AnnotationArrangeSide.Left, currentLeft },
                        { AnnotationArrangeSide.Right, currentRight }
                    };
                }
            }

            List<AnnotationItem> ordered = items
                .OrderBy(item => item.Source2.X)
                .ToList();
            if (ordered.Count <= 1)
            {
                AnnotationArrangeSide side = ordered.Count == 0 ||
                    ordered[0].Head2.X <= ordered[0].Source2.X
                    ? AnnotationArrangeSide.Left
                    : AnnotationArrangeSide.Right;
                return new Dictionary<AnnotationArrangeSide, List<AnnotationItem>>
                {
                    { side, ordered }
                };
            }

            int splitIndex = ordered.Count / 2;
            double largestGap = double.NegativeInfinity;
            for (int index = 1; index < ordered.Count; index++)
            {
                double gap = ordered[index].Source2.X - ordered[index - 1].Source2.X;
                if (gap > largestGap)
                {
                    largestGap = gap;
                    splitIndex = index;
                }
            }
            splitIndex = Math.Max(1, Math.Min(ordered.Count - 1, splitIndex));
            return new Dictionary<AnnotationArrangeSide, List<AnnotationItem>>
            {
                { AnnotationArrangeSide.Left, ordered.Take(splitIndex).ToList() },
                { AnnotationArrangeSide.Right, ordered.Skip(splitIndex).ToList() }
            };
        }

        private static AnnotationArrangeSide ResolveSide(
            IList<AnnotationItem> items,
            IList<Rect2> obstacles,
            View view,
            AnnotationArrangeSettings settings)
        {
            if (settings.Side != AnnotationArrangeSide.Auto &&
                settings.Side != AnnotationArrangeSide.PickRail)
            {
                return settings.Side;
            }
            if (settings.Mode == AnnotationArrangeMode.AlignOnly ||
                settings.Mode == AnnotationArrangeMode.DistributeEvenly ||
                settings.Mode == AnnotationArrangeMode.CompactStack ||
                settings.Mode == AnnotationArrangeMode.ReflowAfterEdit)
            {
                double dx = items.Average(item => item.Head2.X - item.Source2.X);
                double dy = items.Average(item => item.Head2.Y - item.Source2.Y);
                if (Math.Abs(dx) >= Math.Abs(dy))
                {
                    return dx <= 0.0
                        ? AnnotationArrangeSide.Left
                        : AnnotationArrangeSide.Right;
                }
                return dy >= 0.0
                    ? AnnotationArrangeSide.Top
                    : AnnotationArrangeSide.Bottom;
            }

            AnnotationArrangeSide[] candidates =
            {
                AnnotationArrangeSide.Left,
                AnnotationArrangeSide.Right,
                AnnotationArrangeSide.Top,
                AnnotationArrangeSide.Bottom
            };
            double offset = PaperMillimetersToInternal(settings.RailOffsetPaperMm, view);
            double clearance = PaperMillimetersToInternal(settings.ClearancePaperMm, view);
            AnnotationArrangeSide bestSide = AnnotationArrangeSide.Left;
            double bestScore = double.PositiveInfinity;
            foreach (AnnotationArrangeSide side in candidates)
            {
                double rail = CalculateBaseRail(
                    items,
                    side,
                    offset,
                    AnnotationArrangeMode.SmartRail);
                Dictionary<AnnotationItem, Point2> heads = BuildGroupHeads(
                    OrderItems(
                        items,
                        IsVerticalStack(side),
                        settings.SortMode,
                        settings.ReverseOrder,
                        side),
                    side,
                    rail,
                    PaperMillimetersToInternal(settings.SpacingPaperMm, view),
                    settings);
                int collisions = CountCollisions(items, heads, obstacles, clearance);
                int routeConflicts;
                double score = CalculateLayoutScore(
                    items,
                    heads,
                    obstacles,
                    new List<LeaderRoute2>(),
                    clearance,
                    side,
                    settings,
                    out routeConflicts);
                score += collisions * 1000000.0;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestSide = side;
                }
            }
            return bestSide;
        }

        private static double CalculateBaseRail(
            IList<AnnotationItem> items,
            AnnotationArrangeSide side,
            double offset,
            AnnotationArrangeMode mode)
        {
            bool preserveCurrentRail = mode == AnnotationArrangeMode.AlignOnly ||
                mode == AnnotationArrangeMode.DistributeEvenly ||
                mode == AnnotationArrangeMode.ReflowAfterEdit;
            if (preserveCurrentRail)
            {
                List<double> currentEdges = items.Select(item => GetCurrentNearEdge(item, side))
                    .OrderBy(value => value)
                    .ToList();
                return currentEdges[currentEdges.Count / 2];
            }
            if (side == AnnotationArrangeSide.Left)
            {
                return items.Min(item => item.Source2.X) - offset;
            }
            if (side == AnnotationArrangeSide.Right)
            {
                return items.Max(item => item.Source2.X) + offset;
            }
            if (side == AnnotationArrangeSide.Top)
            {
                return items.Max(item => item.Source2.Y) + offset;
            }
            return items.Min(item => item.Source2.Y) - offset;
        }

        private static double GetCurrentNearEdge(
            AnnotationItem item,
            AnnotationArrangeSide side)
        {
            if (side == AnnotationArrangeSide.Left) { return item.CurrentBounds.MaxX; }
            if (side == AnnotationArrangeSide.Right) { return item.CurrentBounds.MinX; }
            if (side == AnnotationArrangeSide.Top) { return item.CurrentBounds.MinY; }
            return item.CurrentBounds.MaxY;
        }

        private static Dictionary<AnnotationItem, Point2> BuildGroupHeads(
            IList<AnnotationItem> group,
            AnnotationArrangeSide side,
            double rail,
            double spacing,
            AnnotationArrangeSettings settings)
        {
            var result = new Dictionary<AnnotationItem, Point2>();
            bool vertical = IsVerticalStack(side);
            List<double> stackCoordinates = CalculateStackCoordinates(
                group,
                vertical,
                spacing,
                settings.Mode);
            for (int index = 0; index < group.Count; index++)
            {
                AnnotationItem item = group[index];
                double normal = CalculateHeadNormal(
                    item.HeadOffsets,
                    side,
                    rail,
                    settings.Alignment);
                result[item] = vertical
                    ? new Point2(normal, stackCoordinates[index])
                    : new Point2(stackCoordinates[index], normal);
            }
            return result;
        }

        private static List<double> CalculateStackCoordinates(
            IList<AnnotationItem> group,
            bool vertical,
            double spacing,
            AnnotationArrangeMode mode)
        {
            if (mode == AnnotationArrangeMode.AlignOnly)
            {
                return group.Select(item => vertical ? item.Head2.Y : item.Head2.X).ToList();
            }
            if (mode == AnnotationArrangeMode.DistributeEvenly)
            {
                if (group.Count == 1)
                {
                    return new List<double> { vertical ? group[0].Head2.Y : group[0].Head2.X };
                }
                List<double> existing = group
                    .Select(item => vertical ? item.Head2.Y : item.Head2.X)
                    .OrderBy(value => value)
                    .ToList();
                double minimum = existing.First();
                double maximum = existing.Last();
                double step = (maximum - minimum) / (group.Count - 1);
                var evenly = new List<double>();
                for (int index = 0; index < group.Count; index++)
                {
                    evenly.Add(vertical
                        ? maximum - step * index
                        : minimum + step * index);
                }
                return evenly;
            }

            double total = group.Sum(item => vertical
                ? item.HeadOffsets.Height
                : item.HeadOffsets.Width) +
                spacing * Math.Max(0, group.Count - 1);
            double center = mode == AnnotationArrangeMode.ReflowAfterEdit
                ? group.Average(item => vertical ? item.Head2.Y : item.Head2.X)
                : group.Average(item => vertical ? item.Source2.Y : item.Source2.X);
            var packed = new List<double>();
            if (vertical)
            {
                double cursor = center + total * 0.5;
                foreach (AnnotationItem item in group)
                {
                    double head = cursor - item.HeadOffsets.MaxY;
                    packed.Add(head);
                    cursor -= item.HeadOffsets.Height + spacing;
                }
            }
            else
            {
                double cursor = center - total * 0.5;
                foreach (AnnotationItem item in group)
                {
                    double head = cursor - item.HeadOffsets.MinX;
                    packed.Add(head);
                    cursor += item.HeadOffsets.Width + spacing;
                }
            }
            return packed;
        }

        private static double CalculateHeadNormal(
            Rect2 offsets,
            AnnotationArrangeSide side,
            double rail,
            AnnotationRailAlignment alignment)
        {
            double attachmentOffset;
            if (alignment == AnnotationRailAlignment.Center)
            {
                attachmentOffset = side == AnnotationArrangeSide.Left ||
                    side == AnnotationArrangeSide.Right
                    ? (offsets.MinX + offsets.MaxX) * 0.5
                    : (offsets.MinY + offsets.MaxY) * 0.5;
            }
            else
            {
                bool near = alignment == AnnotationRailAlignment.NearModelEdge;
                if (side == AnnotationArrangeSide.Left)
                {
                    attachmentOffset = near ? offsets.MaxX : offsets.MinX;
                }
                else if (side == AnnotationArrangeSide.Right)
                {
                    attachmentOffset = near ? offsets.MinX : offsets.MaxX;
                }
                else if (side == AnnotationArrangeSide.Top)
                {
                    attachmentOffset = near ? offsets.MinY : offsets.MaxY;
                }
                else
                {
                    attachmentOffset = near ? offsets.MaxY : offsets.MinY;
                }
            }
            return rail - attachmentOffset;
        }

        private static List<AnnotationItem> OrderItems(
            IEnumerable<AnnotationItem> items,
            bool vertical,
            AnnotationSortMode sortMode,
            bool reverse,
            AnnotationArrangeSide side)
        {
            IEnumerable<AnnotationItem> ordered;
            if (sortMode == AnnotationSortMode.ElementId)
            {
                ordered = items.OrderBy(item => item.IdValue);
            }
            else if (sortMode == AnnotationSortMode.CurrentAnnotationPosition)
            {
                ordered = vertical
                    ? items.OrderByDescending(item => item.Head2.Y).ThenBy(item => item.Head2.X)
                    : items.OrderBy(item => item.Head2.X).ThenByDescending(item => item.Head2.Y);
            }
            else if (sortMode == AnnotationSortMode.NestedLeaderFan)
            {
                // A section/elevation equipment rail normally has sources at nearly
                // the same height. Sorting those sources by Y produces arbitrary
                // leaders. Order each vertical rail from the model centre outwards
                // so the orthogonal elbows form a clean nested fan.
                if (side == AnnotationArrangeSide.Left)
                {
                    ordered = items
                        .OrderByDescending(item => item.Source2.X)
                        .ThenByDescending(item => item.Source2.Y);
                }
                else if (side == AnnotationArrangeSide.Right)
                {
                    ordered = items
                        .OrderBy(item => item.Source2.X)
                        .ThenByDescending(item => item.Source2.Y);
                }
                else if (side == AnnotationArrangeSide.Top)
                {
                    ordered = items
                        .OrderBy(item => item.Source2.Y)
                        .ThenBy(item => item.Source2.X);
                }
                else
                {
                    ordered = items
                        .OrderByDescending(item => item.Source2.Y)
                        .ThenBy(item => item.Source2.X);
                }
            }
            else
            {
                ordered = vertical
                    ? items.OrderByDescending(item => item.Source2.Y).ThenBy(item => item.Source2.X)
                    : items.OrderBy(item => item.Source2.X).ThenByDescending(item => item.Source2.Y);
            }
            List<AnnotationItem> result = ordered.ToList();
            if (reverse) { result.Reverse(); }
            return result;
        }

        private static bool IsVerticalStack(AnnotationArrangeSide side)
        {
            return side == AnnotationArrangeSide.Left ||
                side == AnnotationArrangeSide.Right;
        }

        private static int CountCollisions(
            IEnumerable<AnnotationItem> items,
            IDictionary<AnnotationItem, Point2> heads,
            IEnumerable<Rect2> obstacles,
            double clearance)
        {
            int count = 0;
            foreach (AnnotationItem item in items)
            {
                Point2 head;
                if (!heads.TryGetValue(item, out head)) { continue; }
                Rect2 bounds = item.HeadOffsets.Translate(head).Inflate(clearance);
                count += obstacles.Count(obstacle => bounds.Intersects(obstacle));
            }
            return count;
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

        private static List<List<AnnotationItem>> BuildCandidateOrders(
            IList<AnnotationItem> group,
            bool vertical,
            AnnotationArrangeSide side,
            AnnotationArrangeSettings settings)
        {
            var result = new List<List<AnnotationItem>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            AnnotationSortMode[] modes = settings.Mode == AnnotationArrangeMode.DocumentationRail
                ? new[]
                {
                    settings.SortMode,
                    AnnotationSortMode.SourcePosition,
                    AnnotationSortMode.NestedLeaderFan,
                    AnnotationSortMode.CurrentAnnotationPosition
                }
                : new[] { settings.SortMode };
            foreach (AnnotationSortMode mode in modes)
            {
                foreach (bool reverse in new[] { settings.ReverseOrder, !settings.ReverseOrder })
                {
                    List<AnnotationItem> ordered = OrderItems(
                        group,
                        vertical,
                        mode,
                        reverse,
                        side);
                    string key = string.Join(",", ordered.Select(item => item.IdValue));
                    if (keys.Add(key))
                    {
                        result.Add(ordered);
                    }
                }
            }
            return result;
        }

        private static List<AnnotationItem> OptimizeOrderForRouteCrossings(
            IList<AnnotationItem> requestedOrder,
            AnnotationArrangeSide side,
            double rail,
            double spacing,
            AnnotationArrangeSettings settings,
            IList<LeaderRoute2> plannedRoutes)
        {
            List<AnnotationItem> current = requestedOrder.ToList();
            if (current.Count < 2 ||
                settings.LeaderLayout != AnnotationLeaderLayout.OrthogonalElbow)
            {
                return current;
            }

            bool verticalStack = IsVerticalStack(side);
            for (int pass = 0; pass < 3; pass++)
            {
                Dictionary<AnnotationItem, Point2> heads = BuildGroupHeads(
                    current,
                    side,
                    rail,
                    spacing,
                    settings);
                List<AnnotationRoutePoint2> sources = current
                    .Select(item => new AnnotationRoutePoint2(
                        item.Source2.X,
                        item.Source2.Y))
                    .ToList();
                List<AnnotationRoutePoint2> headSlots = current
                    .Select(item => new AnnotationRoutePoint2(
                        heads[item].X,
                        heads[item].Y))
                    .ToList();
                int[] assignment = AnnotationRouteAssignmentOptimizer.Optimize(
                    sources,
                    headSlots,
                    verticalStack,
                    plannedRoutes == null
                        ? null
                        : plannedRoutes
                            .Select(route => route.ToAnnotationRoute())
                            .ToList());
                bool changed = assignment
                    .Select((sourceIndex, headIndex) =>
                        sourceIndex != headIndex)
                    .Any(value => value);
                if (!changed)
                {
                    break;
                }

                List<AnnotationItem> candidate = assignment
                    .Select(sourceIndex => current[sourceIndex])
                    .ToList();
                int currentCrossings = CountRouteCrossings(
                    current,
                    heads,
                    side,
                    settings,
                    plannedRoutes);
                Dictionary<AnnotationItem, Point2> candidateHeads = BuildGroupHeads(
                    candidate,
                    side,
                    rail,
                    spacing,
                    settings);
                int candidateCrossings = CountRouteCrossings(
                    candidate,
                    candidateHeads,
                    side,
                    settings,
                    plannedRoutes);
                if (candidateCrossings >= currentCrossings)
                {
                    break;
                }
                current = candidate;
            }
            return current;
        }

        private static int CountRouteCrossings(
            IList<AnnotationItem> items,
            IDictionary<AnnotationItem, Point2> heads,
            AnnotationArrangeSide side,
            AnnotationArrangeSettings settings,
            IList<LeaderRoute2> plannedRoutes)
        {
            List<LeaderRoute2> routes = BuildLeaderRoutes(
                items,
                heads,
                side,
                settings);
            int crossings = 0;
            for (int first = 0; first < routes.Count; first++)
            {
                for (int second = first + 1;
                    second < routes.Count;
                    second++)
                {
                    if (routes[first].Crosses(routes[second]))
                    {
                        crossings++;
                    }
                }
            }
            if (plannedRoutes != null)
            {
                foreach (LeaderRoute2 route in routes)
                {
                    foreach (LeaderRoute2 plannedRoute in plannedRoutes)
                    {
                        if (route.Crosses(plannedRoute))
                        {
                            crossings++;
                        }
                    }
                }
            }
            return crossings;
        }

        private static Dictionary<AnnotationItem, Point2> TranslateHeads(
            IDictionary<AnnotationItem, Point2> heads,
            bool vertical,
            double distance)
        {
            return heads.ToDictionary(
                entry => entry.Key,
                entry => vertical
                    ? new Point2(entry.Value.X, entry.Value.Y + distance)
                    : new Point2(entry.Value.X + distance, entry.Value.Y));
        }

        private static double CalculateLayoutScore(
            IEnumerable<AnnotationItem> items,
            IDictionary<AnnotationItem, Point2> heads,
            IEnumerable<Rect2> obstacles,
            IList<LeaderRoute2> existingRoutes,
            double clearance,
            AnnotationArrangeSide side,
            AnnotationArrangeSettings settings,
            out int conflicts)
        {
            List<AnnotationItem> itemList = items.ToList();
            List<Rect2> obstacleList = obstacles.ToList();
            List<LeaderRoute2> routes = BuildLeaderRoutes(
                itemList,
                heads,
                side,
                settings);
            int headConflicts = CountCollisions(
                itemList,
                heads,
                obstacleList,
                clearance);
            int routeObstacleConflicts = 0;
            foreach (LeaderRoute2 route in routes)
            {
                foreach (Rect2 obstacle in obstacleList)
                {
                    Rect2 expanded = obstacle.Inflate(clearance * 0.35);
                    if (!expanded.Contains(route.Start) && route.Crosses(expanded))
                    {
                        routeObstacleConflicts++;
                    }
                }
            }
            int routeCrossings = 0;
            for (int first = 0; first < routes.Count; first++)
            {
                for (int second = first + 1; second < routes.Count; second++)
                {
                    if (routes[first].Crosses(routes[second]))
                    {
                        routeCrossings++;
                    }
                }
                foreach (LeaderRoute2 existing in existingRoutes)
                {
                    if (routes[first].Crosses(existing))
                    {
                        routeCrossings++;
                    }
                }
            }
            double routeLength = routes.Sum(route => route.Length);
            double movement = heads.Sum(entry =>
                (entry.Value - entry.Key.Head2).Length);
            conflicts = headConflicts + routeObstacleConflicts + routeCrossings;
            return headConflicts * 1000000000.0 +
                routeObstacleConflicts * 10000000.0 +
                routeCrossings * 1000000.0 +
                routeLength * 50.0 +
                movement * 2.0;
        }

        private static List<LeaderRoute2> BuildLeaderRoutes(
            IEnumerable<AnnotationItem> items,
            IDictionary<AnnotationItem, Point2> heads,
            AnnotationArrangeSide side,
            AnnotationArrangeSettings settings)
        {
            var result = new List<LeaderRoute2>();
            foreach (AnnotationItem item in items)
            {
                Point2 head;
                if (!heads.TryGetValue(item, out head))
                {
                    continue;
                }
                List<AnnotationLeaderState> states = item.LeaderStates
                    .Where(state => state != null)
                    .ToList();
                if (states.Count == 0)
                {
                    Point2 source = item.Source2;
                    Point2 elbow = GetDesiredElbow(
                        head,
                        source,
                        null,
                        side,
                        settings.LeaderLayout);
                    result.Add(new LeaderRoute2(source, elbow, head));
                    continue;
                }
                foreach (AnnotationLeaderState state in states)
                {
                    Point2 source = state.HasEnd ? state.End2 : item.Source2;
                    Point2 elbow = GetDesiredElbow(
                        head,
                        source,
                        state,
                        side,
                        settings.LeaderLayout);
                    result.Add(new LeaderRoute2(source, elbow, head));
                }
            }
            return result;
        }

        private static bool ApplyPlan(
            Document document,
            View view,
            ArrangePlan plan,
            AnnotationArrangeSettings settings,
            AnnotationArrangeStatistics statistics)
        {
            using (var transaction = new Transaction(
                document,
                "Tung MEP - Arrange Tags and Text Notes"))
            {
                transaction.Start();
                foreach (KeyValuePair<AnnotationItem, Point2> entry in plan.Destinations)
                {
                    AnnotationItem item = entry.Key;
                    using (var subTransaction = new SubTransaction(document))
                    {
                        subTransaction.Start();
                        try
                        {
                            bool repin = item.Pinned && !settings.KeepPinnedFixed;
                            if (repin) { item.Element.Pinned = false; }
                            MoveItem(
                                document,
                                view,
                                item,
                                entry.Value,
                                plan.ItemSides.ContainsKey(item)
                                    ? plan.ItemSides[item]
                                    : plan.Side,
                                settings);
                            if (repin) { item.Element.Pinned = true; }
                            subTransaction.Commit();
                            statistics.Arranged++;
                            if (item.Kind == AnnotationItemKind.IndependentTag)
                            {
                                statistics.TagsArranged++;
                            }
                            else
                            {
                                statistics.TextNotesArranged++;
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
                if (statistics.Arranged == 0)
                {
                    transaction.RollBack();
                    return false;
                }
                transaction.Commit();
            }
            return true;
        }

        private static void MoveItem(
            Document document,
            View view,
            AnnotationItem item,
            Point2 destination,
            AnnotationArrangeSide side,
            AnnotationArrangeSettings settings)
        {
            XYZ destination3 = FromViewPointAtAnchor(
                view,
                item.Head3,
                item.Head2,
                destination);
            IndependentTag tag = item.Element as IndependentTag;
            if (tag != null)
            {
                tag.TagHeadPosition = destination3;
                if (!tag.HasLeader && settings.CreateMissingTagLeaders &&
                    item.LeaderStates.Count > 0)
                {
                    tag.HasLeader = true;
                }
                document.Regenerate();
                if (tag.HasLeader)
                {
                    ApplyTagLeaders(tag, view, item, destination, side, settings);
                }
                return;
            }

            TextNote note = item.Element as TextNote;
            if (note != null)
            {
                note.Coord = destination3;
                document.Regenerate();
                ApplyTextNoteLeaders(note, view, item, destination, side, settings);
            }
        }

        private static void ApplyTagLeaders(
            IndependentTag tag,
            View view,
            AnnotationItem item,
            Point2 destination,
            AnnotationArrangeSide side,
            AnnotationArrangeSettings settings)
        {
            foreach (AnnotationLeaderState state in item.LeaderStates)
            {
                if (state.Reference == null) { continue; }
                if (settings.PreserveLeaderEnds && state.HasEnd &&
                    tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
                {
                    try
                    {
                        tag.LeaderEndCondition = LeaderEndCondition.Free;
                        tag.SetLeaderEnd(state.Reference, state.End3);
                    }
                    catch { }
                }
                Point2 source = state.HasEnd ? state.End2 : item.Source2;
                Point2 elbow = GetDesiredElbow(
                    destination,
                    source,
                    state,
                    side,
                    settings.LeaderLayout);
                try
                {
                    tag.SetLeaderElbow(
                        state.Reference,
                        FromViewPointAtAnchor(
                            view,
                            item.Head3,
                            item.Head2,
                            elbow));
                }
                catch { }
            }
        }

        private static void ApplyTextNoteLeaders(
            TextNote note,
            View view,
            AnnotationItem item,
            Point2 destination,
            AnnotationArrangeSide side,
            AnnotationArrangeSettings settings)
        {
            IList<Leader> leaders = note.GetLeaders();
            for (int index = 0; index < leaders.Count; index++)
            {
                Leader leader = leaders[index];
                AnnotationLeaderState state = index < item.LeaderStates.Count
                    ? item.LeaderStates[index]
                    : null;
                if (state == null) { continue; }
                if (settings.PreserveLeaderEnds && state.HasEnd)
                {
                    try { leader.End = state.End3; }
                    catch { }
                }
                Point2 source = state.HasEnd ? state.End2 : item.Source2;
                Point2 elbow = GetDesiredElbow(
                    destination,
                    source,
                    state,
                    side,
                    settings.LeaderLayout);
                try
                {
                    leader.Elbow = FromViewPointAtAnchor(
                        view,
                        item.Head3,
                        item.Head2,
                        elbow);
                }
                catch { }
            }
        }

        private static Point2 GetDesiredElbow(
            Point2 head,
            Point2 source,
            AnnotationLeaderState state,
            AnnotationArrangeSide side,
            AnnotationLeaderLayout layout)
        {
            if (layout == AnnotationLeaderLayout.Preserve && state != null && state.HasElbow)
            {
                return state.Elbow2;
            }
            if (layout == AnnotationLeaderLayout.Straight)
            {
                return new Point2(
                    (head.X + source.X) * 0.5,
                    (head.Y + source.Y) * 0.5);
            }
            return IsVerticalStack(side)
                ? new Point2(source.X, head.Y)
                : new Point2(head.X, source.Y);
        }

        private static List<Rect2> CollectObstacleRectangles(
            Document document,
            View view,
            ISet<long> selectedIds)
        {
            var result = new List<Rect2>();
            foreach (Element element in new FilteredElementCollector(document, view.Id)
                .WhereElementIsNotElementType())
            {
                if (selectedIds.Contains(element.Id.Value)) { continue; }
                if (!IsLayoutObstacle(element))
                {
                    continue;
                }
                Rect2 bounds;
                if (TryGetBounds(element, view, out bounds))
                {
                    result.Add(bounds);
                }
            }
            return result;
        }

        private static bool IsLayoutObstacle(Element element)
        {
            if (element is IndependentTag ||
                element is TextNote ||
                element is Dimension)
            {
                return true;
            }
            Category category = element == null ? null : element.Category;
            return category != null && ModelObstacleCategoryIds.Contains(category.Id.Value);
        }

        private static bool TryGetBounds(Element element, View view, out Rect2 rectangle)
        {
            rectangle = new Rect2();
            if (element == null) { return false; }
            BoundingBoxXYZ box;
            try { box = element.get_BoundingBox(view); }
            catch { return false; }
            if (box == null) { return false; }
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
            return anchor3 + view.RightDirection * delta.X +
                view.UpDirection * delta.Y;
        }

        private static double PaperMillimetersToInternal(double millimeters, View view)
        {
            return UnitUtils.ConvertToInternalUnits(
                millimeters * Math.Max(1, view.Scale),
                UnitTypeId.Millimeters);
        }

        private static Point2 Average(IEnumerable<Point2> points)
        {
            List<Point2> values = points.ToList();
            return values.Count == 0
                ? new Point2()
                : new Point2(
                    values.Average(point => point.X),
                    values.Average(point => point.Y));
        }

        private static string BuildReport(
            AnnotationArrangeStatistics statistics,
            AnnotationArrangeSettings settings,
            bool applied)
        {
            var report = new StringBuilder();
            report.AppendLine(applied
                ? "Annotation arrangement applied."
                : "No annotation arrangement was applied.");
            report.AppendLine();
            report.AppendLine("Mode: " + settings.Mode + ".");
            if (!string.IsNullOrEmpty(statistics.ResolvedSide))
            {
                report.AppendLine("Resolved side: " + statistics.ResolvedSide + ".");
            }
            report.AppendLine("Scope elements: " + statistics.ScopeElements + ".");
            report.AppendLine("Independent Tags arranged: " + statistics.TagsArranged + ".");
            report.AppendLine("Text Notes arranged: " + statistics.TextNotesArranged + ".");
            report.AppendLine("Pinned annotations kept fixed: " + statistics.LockedPinned + ".");
            report.AppendLine("Unsupported/invalid annotations: " + statistics.UnsupportedOrInvalid + ".");
            report.AppendLine("Additional columns/rows used: " + Math.Max(0, statistics.ColumnsCreated - 1) + ".");
            report.AppendLine("Remaining obstacle conflicts: " + statistics.RemainingCollisionCandidates + ".");
            report.AppendLine("Failed annotations: " + statistics.Failed + ".");
            report.AppendLine();
            report.AppendLine("Existing tagged model elements and preserved leader endpoints were not moved.");
            if (!string.IsNullOrEmpty(statistics.FirstFailure))
            {
                report.AppendLine();
                report.AppendLine("First failure: " + statistics.FirstFailure);
            }
            return report.ToString().TrimEnd();
        }

        private sealed class AnnotationSelectionFilter : ISelectionFilter
        {
            private readonly AnnotationArrangeSettings _settings;
            public AnnotationSelectionFilter(AnnotationArrangeSettings settings)
            {
                _settings = settings;
            }
            public bool AllowElement(Element element) { return Matches(element, _settings); }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }

        private sealed class AnnotationItem
        {
            public AnnotationItem(
                Element element,
                XYZ head3,
                Point2 head2,
                bool pinned,
                AnnotationItemKind kind)
            {
                Element = element;
                Head3 = head3;
                Head2 = head2;
                Source2 = head2;
                Pinned = pinned;
                Kind = kind;
                LeaderStates = new List<AnnotationLeaderState>();
            }
            public Element Element { get; private set; }
            public long IdValue { get { return Element.Id.Value; } }
            public XYZ Head3 { get; private set; }
            public Point2 Head2 { get; private set; }
            public Point2 Source2 { get; set; }
            public bool Pinned { get; private set; }
            public AnnotationItemKind Kind { get; private set; }
            public Rect2 HeadOffsets { get; set; }
            public Rect2 CurrentBounds { get; set; }
            public List<AnnotationLeaderState> LeaderStates { get; private set; }
        }

        private sealed class AnnotationLeaderState
        {
            public int Index;
            public Reference Reference;
            public bool HasEnd;
            public XYZ End3;
            public Point2 End2;
            public bool HasElbow;
            public XYZ Elbow3;
            public Point2 Elbow2;
        }

        private sealed class ArrangePlan
        {
            public ArrangePlan()
            {
                Destinations = new Dictionary<AnnotationItem, Point2>();
                ItemSides = new Dictionary<AnnotationItem, AnnotationArrangeSide>();
            }
            public AnnotationArrangeSide Side;
            public Dictionary<AnnotationItem, Point2> Destinations { get; private set; }
            public Dictionary<AnnotationItem, AnnotationArrangeSide> ItemSides { get; private set; }
        }

        private sealed class PickedRail
        {
            public AnnotationArrangeSide Side;
            public double Coordinate;
        }

        private sealed class AnnotationArrangeStatistics
        {
            public int ScopeElements;
            public int Arranged;
            public int TagsArranged;
            public int TextNotesArranged;
            public int LockedPinned;
            public int UnsupportedOrInvalid;
            public int Failed;
            public int ColumnsCreated;
            public int RemainingCollisionCandidates;
            public string ResolvedSide;
            public string FirstFailure;
        }

        private enum AnnotationItemKind
        {
            IndependentTag,
            TextNote
        }

        private struct Point2
        {
            public Point2(double x, double y) { X = x; Y = y; }
            public double X;
            public double Y;
            public double Length { get { return Math.Sqrt(X * X + Y * Y); } }
            public static Point2 operator +(Point2 left, Point2 right)
            {
                return new Point2(left.X + right.X, left.Y + right.Y);
            }
            public static Point2 operator -(Point2 left, Point2 right)
            {
                return new Point2(left.X - right.X, left.Y - right.Y);
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
            public Point2 Center { get { return new Point2((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5); } }
            public Rect2 Translate(Point2 point)
            {
                return new Rect2(
                    MinX + point.X,
                    MinY + point.Y,
                    MaxX + point.X,
                    MaxY + point.Y);
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
            public AnnotationRoute2 ToAnnotationRoute()
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
    }
}
