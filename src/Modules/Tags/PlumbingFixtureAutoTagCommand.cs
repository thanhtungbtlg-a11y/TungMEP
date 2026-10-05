using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WinForms = System.Windows.Forms;

namespace TungMEP.Modules.Tags
{
    [Transaction(TransactionMode.Manual)]
    public class UniversalMepAutoTagCommand : IExternalCommand
    {
        private const string DialogTitle = "Tung MEP - Universal Auto Tag MEP";

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
                    DialogTitle,
                    "Open a Floor Plan, Engineering Plan, or Ceiling Plan before running Universal Auto Tag MEP.");
                return Result.Cancelled;
            }

            List<UniversalAutoTagTypeOption> tagTypes = CollectTagTypes(document);
            if (tagTypes.Count == 0)
            {
                TaskDialog.Show(
                    DialogTitle,
                    "No supported native MEP Tag Family Type is loaded in this project.\n\n" +
                    "Load at least one compatible Tag family for Plumbing Fixtures, Pipe Accessories, " +
                    "Mechanical Equipment, Air Terminals, Duct Accessories, Sprinklers, electrical categories, " +
                    "Specialty Equipment, or Generic Models.");
                return Result.Cancelled;
            }
            List<AutoTagArrowheadOption> arrowheadTypes =
                AutoTagArrowheadSupport.Collect(document);

            UniversalMepAutoTagSettings settings;
            using (var form = new UniversalMepAutoTagForm(
                tagTypes,
                arrowheadTypes,
                UniversalMepAutoTagSettingsStore.Load()))
            {
                if (form.ShowDialog() != WinForms.DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                settings = form.Settings;
            }
            settings.Normalize();
            UniversalMepAutoTagSettingsStore.Save(settings);

            List<UniversalTagCategorySelection> enabledSelections =
                settings.EnabledCategories.ToList();
            var definitionsByKey = enabledSelections
                .Select(selection => UniversalTagCategoryDefinition.Find(selection.CategoryKey))
                .Where(definition => definition != null)
                .ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
            var selectedTagTypes = new Dictionary<string, FamilySymbol>(
                StringComparer.OrdinalIgnoreCase);
            foreach (UniversalTagCategorySelection selection in enabledSelections)
            {
                UniversalTagCategoryDefinition definition;
                if (!definitionsByKey.TryGetValue(selection.CategoryKey, out definition))
                {
                    continue;
                }
                FamilySymbol tagType = document.GetElement(
                    new ElementId(selection.TagTypeId)) as FamilySymbol;
                if (!IsTagType(tagType, definition))
                {
                    TaskDialog.Show(
                        DialogTitle,
                        "The selected Tag Family Type for " + definition.DisplayName +
                        " is no longer available or belongs to another tag category.");
                    return Result.Cancelled;
                }
                selectedTagTypes[definition.Key] = tagType;
            }
            if (selectedTagTypes.Count == 0)
            {
                TaskDialog.Show(DialogTitle, "Enable at least one valid MEP tag category.");
                return Result.Cancelled;
            }
            ElementType arrowheadType = null;
            if (settings.AddLeader)
            {
                arrowheadType = document.GetElement(
                    new ElementId(settings.SelectedArrowheadTypeId)) as ElementType;
                if (!AutoTagArrowheadSupport.IsArrowheadType(arrowheadType))
                {
                    TaskDialog.Show(
                        DialogTitle,
                        "The selected Leader Arrowhead Type is no longer available.");
                    return Result.Cancelled;
                }
            }

            HashSet<string> enabledKeys = new HashSet<string>(
                selectedTagTypes.Keys,
                StringComparer.OrdinalIgnoreCase);
            List<FamilyInstance> targets;
            try
            {
                targets = CollectScopeTargets(
                    uiDocument,
                    document,
                    view,
                    settings,
                    enabledKeys);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (targets.Count == 0)
            {
                TaskDialog.Show(
                    DialogTitle,
                    "No host-model element from the enabled categories was found in the selected scope.");
                return Result.Cancelled;
            }

            var statistics = new UniversalMepTagStatistics
            {
                ScopeElements = targets.Count
            };
            foreach (FamilyInstance target in targets)
            {
                UniversalTagCategoryDefinition definition =
                    UniversalTagCategoryDefinition.FromModelCategory(target);
                if (definition != null) statistics.Increment(statistics.ScopeByCategory, definition.Key);
            }
            if (settings.SkipAlreadyTagged)
            {
                HashSet<long> alreadyTagged = CollectAlreadyTaggedTargetIds(
                    document,
                    view,
                    enabledKeys);
                List<FamilyInstance> untagged = targets
                    .Where(target => !alreadyTagged.Contains(target.Id.Value))
                    .ToList();
                statistics.SkippedAlreadyTagged = targets.Count - untagged.Count;
                targets = untagged;
            }
            if (targets.Count == 0)
            {
                TaskDialog.Show(
                    DialogTitle,
                    BuildReport(statistics, settings, definitionsByKey));
                return Result.Succeeded;
            }

            List<FixtureTagItem> items = targets
                .Select(target => TryCreateItem(target, view))
                .Where(item => item != null)
                .OrderByDescending(item => item.Anchor2.Y)
                .ThenBy(item => item.Anchor2.X)
                .ToList();
            statistics.InvalidGeometry = targets.Count - items.Count;
            if (items.Count == 0)
            {
                TaskDialog.Show(
                    DialogTitle,
                    "The selected MEP elements do not have a usable location in this view.");
                return Result.Cancelled;
            }

            double offset = PaperMillimetersToInternal(settings.OffsetPaperMm, view);
            double gap = PaperMillimetersToInternal(settings.TagGapPaperMm, view);
            double clearance = PaperMillimetersToInternal(settings.ClearancePaperMm, view);
            double railBand = Math.Max(
                offset * 2.0,
                UnitUtils.ConvertToInternalUnits(1200.0, UnitTypeId.Millimeters));
            List<Rect2> occupied = CollectOccupiedRectangles(document, view);
            var routes = new List<LeaderRoute2>();
            var createdTagIds = new List<ElementId>();
            var preparedTagTypes = new Dictionary<string, FamilySymbol>(
                StringComparer.OrdinalIgnoreCase);

            using (var transactionGroup = new TransactionGroup(
                document,
                "Tung MEP - Universal Auto Tag MEP"))
            {
                transactionGroup.Start();
                using (var transaction = new Transaction(
                    document,
                    "Tung MEP - Create Universal MEP Tags"))
                {
                    transaction.Start();
                    ConfigureFailureHandling(transaction);
                    foreach (KeyValuePair<string, FamilySymbol> pair in selectedTagTypes)
                    {
                        using (var prepare = new SubTransaction(document))
                        {
                            prepare.Start();
                            try
                            {
                                FamilySymbol prepared = settings.AddLeader
                                    ? AutoTagArrowheadSupport.EnsureTagTypeWithArrowhead(
                                        document,
                                        pair.Value,
                                        arrowheadType)
                                    : pair.Value;
                                if (!prepared.IsActive)
                                {
                                    prepared.Activate();
                                }
                                document.Regenerate();
                                prepare.Commit();
                                preparedTagTypes[pair.Key] = prepared;
                            }
                            catch (Exception exception)
                            {
                                if (prepare.GetStatus() == TransactionStatus.Started)
                                {
                                    prepare.RollBack();
                                }
                                statistics.TagTypePreparationFailed++;
                                if (string.IsNullOrEmpty(statistics.FirstFailure))
                                {
                                    UniversalTagCategoryDefinition definition =
                                        UniversalTagCategoryDefinition.Find(pair.Key);
                                    statistics.FirstFailure = "Could not prepare " +
                                        (definition == null ? pair.Key : definition.DisplayName) +
                                        " Tag Type: " + exception.Message;
                                }
                            }
                        }
                    }

                    foreach (FixtureTagItem item in items)
                    {
                        FamilySymbol tagType;
                        if (!preparedTagTypes.TryGetValue(item.Category.Key, out tagType))
                        {
                            statistics.Failed++;
                            statistics.Increment(statistics.FailedByCategory, item.Category.Key);
                            continue;
                        }
                        using (var subTransaction = new SubTransaction(document))
                        {
                            subTransaction.Start();
                            try
                            {
                                Reference reference = new Reference(item.Fixture);
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
                                    double halfWidth = PaperMillimetersToInternal(18.0, view);
                                    double halfHeight = PaperMillimetersToInternal(4.0, view);
                                    headOffsets = new Rect2(
                                        -halfWidth,
                                        -halfHeight,
                                        halfWidth,
                                        halfHeight);
                                }
                                Placement placement = FindBestPlacement(
                                    item,
                                    items,
                                    headOffsets,
                                    occupied,
                                    routes,
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
                                statistics.Increment(statistics.CreatedByCategory, item.Category.Key);
                                if (placement.HasCollision)
                                {
                                    statistics.CollisionFallbacks++;
                                }
                                occupied.Add(placement.HeadBounds);
                                if (settings.AddLeader)
                                {
                                    routes.Add(placement.Route);
                                }
                            }
                            catch (Exception exception)
                            {
                                if (subTransaction.GetStatus() == TransactionStatus.Started)
                                {
                                    subTransaction.RollBack();
                                }
                                statistics.Failed++;
                                statistics.Increment(statistics.FailedByCategory, item.Category.Key);
                                if (string.IsNullOrEmpty(statistics.FirstFailure))
                                {
                                    statistics.FirstFailure = item.Category.DisplayName + ": " +
                                        exception.Message;
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
                        settings);
                    AnnotationArrangeCommand.ArrangeElements(
                        document,
                        view,
                        createdTagIds,
                        arrange,
                        out arrangementIssue);
                }

                string report = BuildReport(statistics, settings, definitionsByKey);
                if (!string.IsNullOrEmpty(arrangementIssue))
                {
                    report += "\n\nDocumentation arrangement note: " + arrangementIssue;
                }
                if (statistics.Created == 0)
                {
                    transactionGroup.RollBack();
                    TaskDialog.Show(DialogTitle, report);
                    return statistics.SkippedAlreadyTagged > 0
                        ? Result.Succeeded
                        : Result.Cancelled;
                }
                if (settings.PreviewBeforeApply)
                {
                    var preview = new TaskDialog(DialogTitle)
                    {
                        MainInstruction = "Review the Universal Auto Tag MEP result",
                        MainContent = report,
                        CommonButtons = TaskDialogCommonButtons.Ok |
                            TaskDialogCommonButtons.Cancel
                    };
                    if (preview.Show() == TaskDialogResult.Cancel)
                    {
                        transactionGroup.RollBack();
                        return Result.Cancelled;
                    }
                }
                transactionGroup.Assimilate();
                if (!settings.PreviewBeforeApply)
                {
                    TaskDialog.Show(DialogTitle, report);
                }
            }
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
            UniversalMepAutoTagSettings settings)
        {
            AnnotationArrangeSettings arrange = AnnotationArrangeSettings.CreateDefault();
            arrange.Mode = AnnotationArrangeMode.DocumentationRail;
            arrange.Side = settings.SidePreference == AutoTagSidePreference.Left
                ? AnnotationArrangeSide.Left
                : settings.SidePreference == AutoTagSidePreference.Right
                    ? AnnotationArrangeSide.Right
                    : settings.SidePreference == AutoTagSidePreference.Top
                        ? AnnotationArrangeSide.Top
                        : settings.SidePreference == AutoTagSidePreference.Bottom
                            ? AnnotationArrangeSide.Bottom
                            : AnnotationArrangeSide.BothSidesBySource;
            arrange.Alignment = AnnotationRailAlignment.NearModelEdge;
            arrange.LeaderLayout = AnnotationLeaderLayout.OrthogonalElbow;
            arrange.SortMode = AnnotationSortMode.SourcePosition;
            arrange.IncludeIndependentTags = true;
            arrange.IncludeTextNotes = false;
            arrange.PreserveLeaderEnds = true;
            arrange.CreateMissingTagLeaders = settings.AddLeader;
            arrange.AvoidObstacles = true;
            arrange.AutoCreateColumns = true;
            arrange.KeepPinnedFixed = true;
            arrange.PreviewBeforeApply = false;
            arrange.OptimizeLeaderRoutes = true;
            arrange.SpacingPaperMm = settings.TagGapPaperMm;
            arrange.RailOffsetPaperMm = settings.OffsetPaperMm;
            arrange.ClearancePaperMm = settings.ClearancePaperMm;
            arrange.ColumnGapPaperMm = Math.Max(8.0, settings.OffsetPaperMm);
            arrange.MaximumItemsPerColumn = 12;
            arrange.MaximumSearchSteps = Math.Max(6, settings.MaximumLaneSteps);
            return arrange;
        }

        private static List<UniversalAutoTagTypeOption> CollectTagTypes(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Select(symbol => new
                {
                    Symbol = symbol,
                    Category = UniversalTagCategoryDefinition.FromTagSymbol(symbol)
                })
                .Where(item => item.Category != null)
                .Select(item => new UniversalAutoTagTypeOption(item.Symbol, item.Category))
                .ToList();
        }

        private static bool IsTagType(
            FamilySymbol symbol,
            UniversalTagCategoryDefinition definition)
        {
            return symbol != null && symbol.Category != null && definition != null &&
                symbol.Category.Id.Value == definition.TagCategoryId;
        }

        private static bool IsTarget(Element element, ISet<string> enabledCategoryKeys)
        {
            if (!(element is FamilyInstance))
            {
                return false;
            }
            UniversalTagCategoryDefinition definition =
                UniversalTagCategoryDefinition.FromModelCategory(element);
            return definition != null && enabledCategoryKeys != null &&
                enabledCategoryKeys.Contains(definition.Key);
        }

        private static bool IsCatalogTarget(Element element)
        {
            return element is FamilyInstance &&
                UniversalTagCategoryDefinition.FromModelCategory(element) != null;
        }

        private static List<FamilyInstance> CollectScopeTargets(
            UIDocument uiDocument,
            Document document,
            View view,
            UniversalMepAutoTagSettings settings,
            ISet<string> enabledCategoryKeys)
        {
            if (settings.Scope == AutoTagScope.ActiveView)
            {
                return new FilteredElementCollector(document, view.Id)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(target => IsTarget(target, enabledCategoryKeys))
                    .GroupBy(target => target.Id.Value)
                    .Select(group => group.First())
                    .ToList();
            }
            if (settings.Scope == AutoTagScope.SelectionOrPick)
            {
                List<FamilyInstance> selected = uiDocument.Selection.GetElementIds()
                    .Select(document.GetElement)
                    .Where(element => IsTarget(element, enabledCategoryKeys))
                    .Cast<FamilyInstance>()
                    .GroupBy(target => target.Id.Value)
                    .Select(group => group.First())
                    .ToList();
                if (selected.Count > 0)
                {
                    return selected;
                }
                return uiDocument.Selection
                    .PickObjects(
                        ObjectType.Element,
                        new FixtureSelectionFilter(enabledCategoryKeys),
                        "Universal Auto Tag MEP: select enabled MEP elements, then click Finish")
                    .Select(reference => document.GetElement(reference.ElementId))
                    .Where(element => IsTarget(element, enabledCategoryKeys))
                    .Cast<FamilyInstance>()
                    .GroupBy(target => target.Id.Value)
                    .Select(group => group.First())
                    .ToList();
            }
            return uiDocument.Selection
                .PickElementsByRectangle(
                    new FixtureSelectionFilter(enabledCategoryKeys),
                    "Universal Auto Tag MEP: drag a rectangle around enabled MEP elements")
                .Where(element => IsTarget(element, enabledCategoryKeys))
                .Cast<FamilyInstance>()
                .GroupBy(target => target.Id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static HashSet<long> CollectAlreadyTaggedTargetIds(
            Document document,
            View view,
            ISet<string> enabledCategoryKeys)
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
                        if (IsTarget(document.GetElement(id), enabledCategoryKeys))
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

        private static FixtureTagItem TryCreateItem(FamilyInstance fixture, View view)
        {
            if (fixture == null)
            {
                return null;
            }
            UniversalTagCategoryDefinition category =
                UniversalTagCategoryDefinition.FromModelCategory(fixture);
            if (category == null)
            {
                return null;
            }
            XYZ anchor = null;
            LocationPoint point = fixture.Location as LocationPoint;
            if (point != null)
            {
                anchor = point.Point;
            }
            BoundingBoxXYZ box = null;
            try { box = fixture.get_BoundingBox(view); }
            catch { }
            Rect2 bounds;
            bool hasBounds = TryProjectBounds(view, box, out bounds);
            if (anchor == null && box != null)
            {
                Transform transform = box.Transform ?? Transform.Identity;
                anchor = transform.OfPoint((box.Min + box.Max) * 0.5);
            }
            if (anchor == null)
            {
                return null;
            }
            Point2 anchor2 = ToViewPoint(view, anchor);
            if (!hasBounds)
            {
                double half = UnitUtils.ConvertToInternalUnits(
                    100.0,
                    UnitTypeId.Millimeters);
                bounds = new Rect2(
                    anchor2.X - half,
                    anchor2.Y - half,
                    anchor2.X + half,
                    anchor2.Y + half);
            }
            return new FixtureTagItem(fixture, category, anchor, anchor2, bounds);
        }

        private static List<Rect2> CollectOccupiedRectangles(Document document, View view)
        {
            var result = new List<Rect2>();
            foreach (Element element in new FilteredElementCollector(document, view.Id)
                .WhereElementIsNotElementType())
            {
                bool relevant = element is IndependentTag ||
                    element is TextNote ||
                    element is Dimension ||
                    element is Pipe ||
                    element is Duct ||
                    IsCatalogTarget(element) ||
                    IsModelObstacle(element);
                if (!relevant)
                {
                    continue;
                }
                BoundingBoxXYZ box;
                try { box = element.get_BoundingBox(view); }
                catch { box = null; }
                Rect2 rectangle;
                if (TryProjectBounds(view, box, out rectangle))
                {
                    result.Add(rectangle);
                }
            }
            return result;
        }

        private static bool IsModelObstacle(Element element)
        {
            if (element == null || element.Category == null)
            {
                return false;
            }
            long categoryId = element.Category.Id.Value;
            return categoryId == (long)BuiltInCategory.OST_Walls ||
                categoryId == (long)BuiltInCategory.OST_Floors ||
                categoryId == (long)BuiltInCategory.OST_Ceilings ||
                categoryId == (long)BuiltInCategory.OST_Roofs ||
                categoryId == (long)BuiltInCategory.OST_Columns ||
                categoryId == (long)BuiltInCategory.OST_StructuralColumns ||
                categoryId == (long)BuiltInCategory.OST_StructuralFraming ||
                categoryId == (long)BuiltInCategory.OST_PipeFitting ||
                categoryId == (long)BuiltInCategory.OST_PipeAccessory ||
                categoryId == (long)BuiltInCategory.OST_DuctFitting ||
                categoryId == (long)BuiltInCategory.OST_DuctAccessory ||
                categoryId == (long)BuiltInCategory.OST_CableTray ||
                categoryId == (long)BuiltInCategory.OST_Conduit;
        }

        private static bool TryGetTagHeadOffsets(
            IndependentTag tag,
            View view,
            out Rect2 offsets)
        {
            offsets = new Rect2();
            BoundingBoxXYZ box;
            try { box = tag.get_BoundingBox(view); }
            catch { return false; }
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

        private static Placement FindBestPlacement(
            FixtureTagItem item,
            IList<FixtureTagItem> items,
            Rect2 headOffsets,
            IList<Rect2> occupied,
            IList<LeaderRoute2> routes,
            UniversalMepAutoTagSettings settings,
            double offset,
            double gap,
            double clearance,
            double railBand)
        {
            var candidates = new List<Placement>();
            int order = 0;
            foreach (Side side in GetCandidateSides(settings.SidePreference))
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
                double tagStep = Math.Max(
                    side == Side.Left || side == Side.Right
                        ? headOffsets.Height
                        : headOffsets.Width,
                    gap) + gap;
                foreach (int lane in EnumerateLaneOffsets(settings.MaximumLaneSteps))
                {
                    Point2 head = baseHead + along * (lane * tagStep);
                    Rect2 headBounds = headOffsets.Translate(head);
                    Point2 elbow = GetElbow(item.Anchor2, head, side);
                    var route = new LeaderRoute2(item.Anchor2, elbow, head);
                    Rect2 clearanceBounds = headBounds.Inflate(clearance);
                    bool collision = false;
                    double score = order++ + route.Length * 50.0 +
                        Math.Abs(lane) * 100.0;
                    foreach (Rect2 obstacle in occupied)
                    {
                        if (clearanceBounds.Intersects(obstacle))
                        {
                            collision = true;
                            score += 1000000.0 +
                                clearanceBounds.OverlapArea(obstacle) * 100000.0;
                        }
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
                    foreach (LeaderRoute2 existingRoute in routes)
                    {
                        if (route.Crosses(existingRoute))
                        {
                            score += 100000.0;
                        }
                    }
                    candidates.Add(new Placement(
                        head,
                        elbow,
                        headBounds,
                        route,
                        collision,
                        score));
                }
            }
            return candidates.OrderBy(candidate => candidate.Score).First();
        }

        private static Point2 GetRailHead(
            FixtureTagItem item,
            IList<FixtureTagItem> items,
            Side side,
            double offset,
            double railBand)
        {
            bool horizontalSide = side == Side.Left || side == Side.Right;
            List<FixtureTagItem> neighbors = items
                .Where(candidate => Math.Abs(
                    (horizontalSide ? candidate.Anchor2.X : candidate.Anchor2.Y) -
                    (horizontalSide ? item.Anchor2.X : item.Anchor2.Y)) <= railBand)
                .Where(candidate => Math.Abs(
                    (horizontalSide ? candidate.Anchor2.Y : candidate.Anchor2.X) -
                    (horizontalSide ? item.Anchor2.Y : item.Anchor2.X)) <= railBand * 4.0)
                .ToList();
            if (neighbors.Count == 0) { neighbors.Add(item); }
            if (side == Side.Left)
            {
                return new Point2(neighbors.Min(candidate => candidate.Bounds.MinX) - offset, item.Anchor2.Y);
            }
            if (side == Side.Right)
            {
                return new Point2(neighbors.Max(candidate => candidate.Bounds.MaxX) + offset, item.Anchor2.Y);
            }
            if (side == Side.Top)
            {
                return new Point2(item.Anchor2.X, neighbors.Max(candidate => candidate.Bounds.MaxY) + offset);
            }
            return new Point2(item.Anchor2.X, neighbors.Min(candidate => candidate.Bounds.MinY) - offset);
        }

        private static double GetOutwardHeadExtent(
            Rect2 offsets,
            Side side,
            double clearance)
        {
            if (side == Side.Left) { return Math.Max(0.0, offsets.MaxX) + clearance; }
            if (side == Side.Right) { return Math.Max(0.0, -offsets.MinX) + clearance; }
            if (side == Side.Top) { return Math.Max(0.0, -offsets.MinY) + clearance; }
            return Math.Max(0.0, offsets.MaxY) + clearance;
        }

        private static List<Side> GetCandidateSides(AutoTagSidePreference preference)
        {
            if (preference == AutoTagSidePreference.Left) { return new List<Side> { Side.Left }; }
            if (preference == AutoTagSidePreference.Right) { return new List<Side> { Side.Right }; }
            if (preference == AutoTagSidePreference.Top) { return new List<Side> { Side.Top }; }
            if (preference == AutoTagSidePreference.Bottom) { return new List<Side> { Side.Bottom }; }
            return new List<Side> { Side.Left, Side.Right, Side.Top, Side.Bottom };
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

        private static Point2 SideVector(Side side)
        {
            if (side == Side.Left) { return new Point2(-1, 0); }
            if (side == Side.Right) { return new Point2(1, 0); }
            if (side == Side.Top) { return new Point2(0, 1); }
            return new Point2(0, -1);
        }

        private static Point2 GetShoulderDirection(Side side)
        {
            // Keep a visible dogleg even when the first candidate lane is used.
            // A centered head would otherwise make the elbow coincide with the
            // tag head and Revit would display only one long straight segment.
            return side == Side.Left || side == Side.Right
                ? new Point2(0, 1)
                : new Point2(1, 0);
        }

        private static double GetRequiredShoulder(
            Rect2 headOffsets,
            Side side,
            double gap,
            double offset)
        {
            // Put the elbow completely outside the real tag bounding box.
            // Plumbing tags can be much wider than the default shoulder, so a
            // fixed distance lets the leader run through the label text.
            double tagExtentTowardElbow = side == Side.Left || side == Side.Right
                ? Math.Max(0.0, -headOffsets.MinY)
                : Math.Max(0.0, -headOffsets.MinX);
            return Math.Max(
                Math.Max(gap * 2.0, offset * 0.5),
                tagExtentTowardElbow + gap);
        }

        private static Point2 GetElbow(Point2 anchor, Point2 head, Side side)
        {
            return side == Side.Left || side == Side.Right
                ? new Point2(head.X, anchor.Y)
                : new Point2(anchor.X, head.Y);
        }

        private static void ConfigureLeader(
            IndependentTag tag,
            Reference reference,
            View view,
            FixtureTagItem item,
            Placement placement)
        {
            try
            {
                tag.HasLeader = true;
                if (tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
                {
                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                    Point2 target2 = GetFixtureBoundaryTarget(
                        item,
                        placement.Elbow);
                    tag.SetLeaderEnd(
                        reference,
                        FromViewPointAtAnchor(
                            view,
                            item.Anchor3,
                            item.Anchor2,
                            target2));
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
                try { tag.HasLeader = true; }
                catch { }
            }
        }

        private static Point2 GetFixtureBoundaryTarget(
            FixtureTagItem item,
            Point2 toward)
        {
            Point2 direction = toward - item.Anchor2;
            if (direction.Length <= 1e-8)
            {
                return item.Anchor2;
            }
            double factor = double.PositiveInfinity;
            if (Math.Abs(direction.X) > 1e-8)
            {
                double edgeX = direction.X > 0.0
                    ? item.Bounds.MaxX
                    : item.Bounds.MinX;
                double candidate = (edgeX - item.Anchor2.X) / direction.X;
                if (candidate >= 0.0)
                {
                    factor = Math.Min(factor, candidate);
                }
            }
            if (Math.Abs(direction.Y) > 1e-8)
            {
                double edgeY = direction.Y > 0.0
                    ? item.Bounds.MaxY
                    : item.Bounds.MinY;
                double candidate = (edgeY - item.Anchor2.Y) / direction.Y;
                if (candidate >= 0.0)
                {
                    factor = Math.Min(factor, candidate);
                }
            }
            if (double.IsInfinity(factor) || double.IsNaN(factor))
            {
                return item.Anchor2;
            }
            return item.Anchor2 + direction * factor;
        }

        private static bool TryProjectBounds(
            View view,
            BoundingBoxXYZ box,
            out Rect2 rectangle)
        {
            rectangle = new Rect2();
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

        private static void ConfigureFailureHandling(Transaction transaction)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }

        private static string BuildReport(
            UniversalMepTagStatistics statistics,
            UniversalMepAutoTagSettings settings,
            IDictionary<string, UniversalTagCategoryDefinition> definitionsByKey)
        {
            var report = new StringBuilder();
            report.AppendLine("Native MEP tags created: " + statistics.Created + ".");
            report.AppendLine("Enabled-category elements in scope: " + statistics.ScopeElements + ".");
            report.AppendLine("Already tagged elements skipped: " + statistics.SkippedAlreadyTagged + ".");
            report.AppendLine("Elements without usable view geometry: " + statistics.InvalidGeometry + ".");
            report.AppendLine("Tags failed: " + statistics.Failed + ".");
            report.AppendLine("Tag Types that could not be prepared: " +
                statistics.TagTypePreparationFailed + ".");
            report.AppendLine("Crowded placements using best available lane: " +
                statistics.CollisionFallbacks + ".");
            report.AppendLine();
            report.AppendLine("Results by category:");
            foreach (UniversalTagCategorySelection selection in settings.EnabledCategories)
            {
                UniversalTagCategoryDefinition definition;
                if (!definitionsByKey.TryGetValue(selection.CategoryKey, out definition))
                {
                    continue;
                }
                report.AppendLine("- " + definition.DisplayName + ": " +
                    statistics.Get(statistics.CreatedByCategory, definition.Key) + " created / " +
                    statistics.Get(statistics.ScopeByCategory, definition.Key) + " in scope / " +
                    statistics.Get(statistics.FailedByCategory, definition.Key) + " failed.");
                report.AppendLine("  Tag Type: " + selection.TagTypeName);
            }
            report.AppendLine();
            report.AppendLine("Leader: " + (settings.AddLeader ? "on" : "off") + ".");
            if (settings.AddLeader)
            {
                report.AppendLine("Leader arrowhead: " + settings.SelectedArrowheadTypeName + ".");
            }
            report.AppendLine();
            report.AppendLine("Existing manual tags were not deleted or moved. Host-model family instances only.");
            report.AppendLine("Pipe and Duct runs remain handled by Smart MEP Annotation Pack.");
            if (!string.IsNullOrEmpty(statistics.FirstFailure))
            {
                report.AppendLine();
                report.AppendLine("First failure: " + statistics.FirstFailure);
            }
            return report.ToString().TrimEnd();
        }

        private sealed class FixtureSelectionFilter : ISelectionFilter
        {
            private readonly ISet<string> _enabledCategoryKeys;
            public FixtureSelectionFilter(ISet<string> enabledCategoryKeys)
            {
                _enabledCategoryKeys = enabledCategoryKeys == null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(enabledCategoryKeys, StringComparer.OrdinalIgnoreCase);
            }
            public bool AllowElement(Element element)
            {
                return IsTarget(element, _enabledCategoryKeys);
            }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }

        private sealed class FixtureTagItem
        {
            public FixtureTagItem(
                FamilyInstance fixture,
                UniversalTagCategoryDefinition category,
                XYZ anchor3,
                Point2 anchor2,
                Rect2 bounds)
            {
                Fixture = fixture;
                Category = category;
                Anchor3 = anchor3;
                Anchor2 = anchor2;
                Bounds = bounds;
            }
            public FamilyInstance Fixture { get; private set; }
            public UniversalTagCategoryDefinition Category { get; private set; }
            public XYZ Anchor3 { get; private set; }
            public Point2 Anchor2 { get; private set; }
            public Rect2 Bounds { get; private set; }
        }

        private sealed class Placement
        {
            public Placement(
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
            public double OverlapArea(Rect2 other)
            {
                double width = Math.Max(
                    0.0,
                    Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX));
                double height = Math.Max(
                    0.0,
                    Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY));
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

        private sealed class UniversalMepTagStatistics
        {
            public int ScopeElements;
            public int Created;
            public int SkippedAlreadyTagged;
            public int InvalidGeometry;
            public int Failed;
            public int TagTypePreparationFailed;
            public int CollisionFallbacks;
            public string FirstFailure;
            public readonly Dictionary<string, int> ScopeByCategory =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> CreatedByCategory =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> FailedByCategory =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void Increment(IDictionary<string, int> values, string key)
            {
                int value;
                values.TryGetValue(key, out value);
                values[key] = value + 1;
            }

            public int Get(IDictionary<string, int> values, string key)
            {
                int value;
                return values.TryGetValue(key, out value) ? value : 0;
            }
        }

        private enum Side
        {
            Left,
            Right,
            Top,
            Bottom
        }
    }

    // Backward-compatible entry point for payloads or shortcuts created before
    // the Plumbing Fixture command was upgraded to Universal Auto Tag MEP.
    [Transaction(TransactionMode.Manual)]
    public class PlumbingFixtureAutoTagCommand : UniversalMepAutoTagCommand
    {
    }
}
