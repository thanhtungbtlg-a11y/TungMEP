using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WinForms = System.Windows.Forms;

namespace TungMEP.Modules.Dimensions
{
    [Transaction(TransactionMode.Manual)]
    public class AutoDimensionCommand : IExternalCommand
    {
        private const int MaximumLaneAttempts = 10;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            View view = document == null ? null : document.ActiveView;
            if (!IsSupportedView(document, view))
            {
                TaskDialog.Show(
                    "Tung MEP - Auto Dimension",
                    "Open a plan, section, elevation, or detail view before running Auto Dimension.");
                return Result.Cancelled;
            }

            List<DimensionType> dimensionTypes = new FilteredElementCollector(document)
                .OfClass(typeof(DimensionType))
                .Cast<DimensionType>()
                .Where(type => type.StyleType == DimensionStyleType.Linear)
                .OrderBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (dimensionTypes.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Auto Dimension",
                    "The project does not contain a linear Dimension Type.");
                return Result.Cancelled;
            }

            AutoDimensionSettings remembered = AutoDimensionSettingsStore.Load();
            using (var form = new AutoDimensionForm(dimensionTypes, remembered))
            {
                if (form.ShowDialog() != WinForms.DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                AutoDimensionSettings settings = form.Settings;
                settings.Normalize();
                AutoDimensionSettingsStore.Save(settings);

                DimensionType dimensionType = ResolveDimensionType(
                    document,
                    dimensionTypes,
                    settings);
                if (dimensionType == null)
                {
                    TaskDialog.Show(
                        "Tung MEP - Auto Dimension",
                        "The selected Dimension Type is no longer available.");
                    return Result.Cancelled;
                }

                var statistics = new AutoDimensionStatistics();
                Result result = settings.Mode == AutoDimensionMode.SingleSprinklerToWalls
                    ? ExecuteSingleSprinklerToWalls(
                        uiDocument,
                        document,
                        view,
                        dimensionType,
                        settings,
                        statistics)
                    : settings.Mode == AutoDimensionMode.Sprinkler
                    ? ExecuteSprinklerPreset(
                        uiDocument,
                        document,
                        view,
                        dimensionType,
                        settings,
                        statistics)
                    : settings.ByLineInputMode == AutoDimensionByLineInputMode.SelectObjects
                        ? ExecuteBySelection(
                            uiDocument,
                            document,
                            view,
                            dimensionType,
                            settings,
                            statistics)
                        : ExecuteByLine(
                        uiDocument,
                        document,
                        view,
                        dimensionType,
                        settings,
                        statistics);

                if (result == Result.Succeeded || statistics.Created > 0)
                {
                    TaskDialog.Show(
                        "Tung MEP - Auto Dimension",
                        BuildReport(statistics));
                    return Result.Succeeded;
                }
                if (statistics.HasReportableResult)
                {
                    TaskDialog.Show(
                        "Tung MEP - Auto Dimension",
                        BuildReport(statistics));
                }
                return result;
            }
        }

        private static Result ExecuteBySelection(
            UIDocument uiDocument,
            Document document,
            View view,
            DimensionType dimensionType,
            AutoDimensionSettings settings,
            AutoDimensionStatistics statistics)
        {
            var referenceProvider = new AutoDimensionReferenceProvider(document, view);
            var existingReferenceKeys = CollectExistingDimensionReferenceKeys(document, view);
            var lanes = CollectExistingDimensionLanes(document, view);
            List<Element> preselected = CollectPreselectedByLineScope(
                document,
                uiDocument,
                settings);
            bool usePreselection = preselected.Count >= 2;
            bool anyPromptCompleted = usePreselection;

            using (var group = new TransactionGroup(
                document,
                "Tung MEP - Auto Dimension by Selection"))
            {
                group.Start();
                while (true)
                {
                    List<Element> scope;
                    if (usePreselection)
                    {
                        scope = preselected;
                        usePreselection = false;
                    }
                    else
                    {
                        try
                        {
                            scope = uiDocument.Selection.PickElementsByRectangle(
                                    new ByLineElementSelectionFilter(settings),
                                    "Auto Dimension 1/2: drag a rectangle around two or more objects")
                                .Where(element => MatchesSettingsCategory(element, settings))
                                .GroupBy(element => element.Id.Value)
                                .Select(elementGroup => elementGroup.First())
                                .ToList();
                            anyPromptCompleted = true;
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            statistics.UserCancelled = true;
                            break;
                        }
                    }

                    if (scope.Count < 2)
                    {
                        statistics.SkippedChains++;
                        SetFirstFailure(
                            statistics,
                            "Select at least two supported objects for Auto Dimension.");
                        if (!settings.RepeatRun)
                        {
                            break;
                        }
                        continue;
                    }

                    uiDocument.Selection.SetElementIds(scope.Select(element => element.Id).ToList());
                    statistics.ScopeElements += scope.Count;
                    Point2 inferredDirection;
                    if (!TryInferSelectionMeasurementDirection(
                        scope,
                        view,
                        out inferredDirection))
                    {
                        statistics.SkippedChains++;
                        SetFirstFailure(
                            statistics,
                            "Could not infer a common dimension direction from the selected objects.");
                        if (!settings.RepeatRun)
                        {
                            break;
                        }
                        continue;
                    }

                    IList<DimensionReferenceSkip> referenceSkips;
                    IList<DimensionCandidateReference> rawCandidates = referenceProvider.Collect(
                        scope,
                        ToViewVector(view, inferredDirection),
                        null,
                        out referenceSkips);
                    statistics.ReferenceSourceSkipped += referenceSkips.Count;
                    List<DimensionCandidateReference> candidates = FilterCandidates(
                        rawCandidates,
                        settings.ReferenceMode,
                        statistics);
                    statistics.ReferenceCandidates += candidates.Count;
                    IDictionary<string, string> directionKeys = BuildDirectionGroups(candidates, view);
                    List<List<DimensionCandidateReference>> directionGroups = candidates
                        .Where(candidate => directionKeys.ContainsKey(candidate.StableKey) &&
                            !string.Equals(
                                directionKeys[candidate.StableKey],
                                "invalid",
                                StringComparison.Ordinal))
                        .GroupBy(candidate => directionKeys[candidate.StableKey], StringComparer.Ordinal)
                        .Select(candidateGroup => candidateGroup.ToList())
                        .Where(candidateGroup => candidateGroup.Count >= 2)
                        .OrderByDescending(candidateGroup => candidateGroup
                            .Select(candidate => candidate.SourceElementId.Value)
                            .Distinct()
                            .Count())
                        .ToList();
                    if (directionGroups.Count == 0)
                    {
                        statistics.SkippedChains++;
                        SetFirstFailure(
                            statistics,
                            "The selected objects did not provide two compatible parallel Revit References.");
                        if (!settings.RepeatRun)
                        {
                            break;
                        }
                        continue;
                    }

                    XYZ pickedPlacement;
                    Point2 previewDirection = ToViewVector2(
                        view,
                        directionGroups[0][0].Normal);
                    using (DimensionPlacementPreviewOverlay preview =
                        DimensionPlacementPreviewOverlay.TryShow(
                            uiDocument,
                            document,
                            view,
                            directionGroups[0],
                            previewDirection,
                            settings))
                    {
                        try
                        {
                            pickedPlacement = uiDocument.Selection.PickPoint(
                                ObjectSnapTypes.Endpoints |
                                ObjectSnapTypes.Intersections |
                                ObjectSnapTypes.Nearest,
                                "Auto Dimension 2/2: move the preview, then click to place the dimension");
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            statistics.UserCancelled = true;
                            break;
                        }
                    }
                    Point2 placement = ToViewPoint(view, pickedPlacement);
                    double laneStep = PaperMillimetersToInternal(settings.LaneStepPaperMm, view);
                    double tolerance = UnitUtils.ConvertToInternalUnits(
                        settings.ToleranceMm,
                        UnitTypeId.Millimeters);

                    bool transactionCommitFailed = false;
                    using (var transaction = new Transaction(
                        document,
                        "Tung MEP - Auto Dimension by Selection"))
                    {
                        int createdBeforeTransaction = statistics.Created;
                        int lanesBeforeTransaction = lanes.Count;
                        var referenceKeysBeforeTransaction = new HashSet<string>(
                            existingReferenceKeys,
                            StringComparer.Ordinal);
                        transaction.Start();
                        ConfigureFailureHandling(transaction);
                        foreach (List<DimensionCandidateReference> candidateGroup in directionGroups)
                        {
                            Point2 exactDirection = ToViewVector2(
                                view,
                                candidateGroup[0].Normal);
                            if (exactDirection.Length <= 1e-8)
                            {
                                statistics.SkippedChains++;
                                continue;
                            }
                            exactDirection = exactDirection.Normalize();
                            if (Point2.Dot(exactDirection, inferredDirection) < 0.0)
                            {
                                exactDirection = exactDirection * -1.0;
                            }
                            List<DimensionCandidateReference> ordered = SortAndDedupeCandidates(
                                candidateGroup,
                                view,
                                exactDirection,
                                tolerance);
                            if (ordered.Count < 2)
                            {
                                statistics.SkippedChains++;
                                continue;
                            }

                            Point2 origin = new Point2(
                                ordered.Average(candidate => ToViewPoint(view, candidate.Anchor).X),
                                ordered.Average(candidate => ToViewPoint(view, candidate.Anchor).Y));
                            var frame = new ProjectionFrame2(origin, exactDirection);
                            Point2 basePlacement = placement;
                            int before = statistics.Created;
                            if (settings.CreateChain)
                            {
                                TryCreateNativeDimension(
                                    document,
                                    view,
                                    dimensionType,
                                    ordered,
                                    exactDirection,
                                    basePlacement,
                                    laneStep,
                                    settings.AvoidOverlap,
                                    existingReferenceKeys,
                                    lanes,
                                    statistics,
                                    "selected object chain");
                            }
                            if (settings.CreateOverall && (!settings.CreateChain || ordered.Count > 2))
                            {
                                Point2 normal = frame.PerpendicularDirection;
                                double sign = Point2.Dot(
                                    basePlacement - origin,
                                    normal) >= 0.0 ? 1.0 : -1.0;
                                TryCreateNativeDimension(
                                    document,
                                    view,
                                    dimensionType,
                                    new[] { ordered[0], ordered[ordered.Count - 1] },
                                    exactDirection,
                                    basePlacement + (normal * sign * laneStep),
                                    laneStep,
                                    settings.AvoidOverlap,
                                    existingReferenceKeys,
                                    lanes,
                                    statistics,
                                    "overall selected object dimension");
                            }
                            if (statistics.Created == before)
                            {
                                statistics.SkippedChains++;
                            }
                        }

                        if (statistics.Created > createdBeforeTransaction)
                        {
                            transactionCommitFailed = !TryCommitParentTransaction(
                                transaction,
                                createdBeforeTransaction,
                                lanesBeforeTransaction,
                                referenceKeysBeforeTransaction,
                                existingReferenceKeys,
                                lanes,
                                statistics);
                        }
                        else
                        {
                            transaction.RollBack();
                        }
                    }

                    if (transactionCommitFailed || !settings.RepeatRun)
                    {
                        break;
                    }
                }

                if (statistics.Created > 0)
                {
                    try
                    {
                        if (group.Assimilate() == TransactionStatus.Committed)
                        {
                            return Result.Succeeded;
                        }
                        SetFirstFailure(
                            statistics,
                            "Revit did not assimilate the Auto Dimension transaction group.");
                    }
                    catch (Exception exception)
                    {
                        SetFirstFailure(
                            statistics,
                            "Could not finish the Auto Dimension transaction group: " +
                            exception.Message);
                    }
                    statistics.Created = 0;
                    statistics.FailedChains++;
                    return Result.Cancelled;
                }
                group.RollBack();
            }
            return anyPromptCompleted ? Result.Cancelled : Result.Cancelled;
        }

        private static Result ExecuteByLine(
            UIDocument uiDocument,
            Document document,
            View view,
            DimensionType dimensionType,
            AutoDimensionSettings settings,
            AutoDimensionStatistics statistics)
        {
            List<Element> scope = CollectByLineScope(document, view, uiDocument, settings);
            if (scope.Count == 0)
            {
                statistics.FirstFailure =
                    "No supported elements are visible in the active view or selected.";
                return Result.Cancelled;
            }

            var referenceProvider = new AutoDimensionReferenceProvider(document, view);
            var existingReferenceKeys = CollectExistingDimensionReferenceKeys(document, view);
            var lanes = CollectExistingDimensionLanes(document, view);
            bool anyPromptCompleted = false;
            bool fatalPreviewCleanup = false;

            using (var group = new TransactionGroup(document, "Tung MEP - Auto Dimension by Line"))
            {
                group.Start();
                while (true)
                {
                    XYZ pickedStart;
                    XYZ pickedEnd;
                    ObjectSnapTypes pointSnaps = ObjectSnapTypes.Endpoints |
                        ObjectSnapTypes.Intersections |
                        ObjectSnapTypes.Nearest;
                    try
                    {
                        pickedStart = uiDocument.Selection.PickPoint(
                            pointSnaps,
                            "Auto Dimension 1/3: click the first point of a scan line crossing the objects");
                        pickedEnd = uiDocument.Selection.PickPoint(
                            pointSnaps,
                            "Auto Dimension 2/3: click the second point of the scan line");
                        anyPromptCompleted = true;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        statistics.UserCancelled = true;
                        break;
                    }

                    Point2 scanStart = ToViewPoint(view, pickedStart);
                    Point2 scanEnd = ToViewPoint(view, pickedEnd);
                    if ((scanEnd - scanStart).Length <= 1e-8)
                    {
                        statistics.SkippedChains++;
                        SetFirstFailure(statistics, "The two scan-line points are coincident.");
                        if (!settings.RepeatRun)
                        {
                            break;
                        }
                        continue;
                    }

                    ProjectionFrame2 frame = DimensionChainPlanner.CreateFrame(scanStart, scanEnd);
                    XYZ measurementDirection = ToViewVector(view, frame.AlongDirection);
                    double tolerance = UnitUtils.ConvertToInternalUnits(
                        settings.ToleranceMm,
                        UnitTypeId.Millimeters);
                    Point2 providerStart = scanStart - (frame.AlongDirection * tolerance);
                    Point2 providerEnd = scanEnd + (frame.AlongDirection * tolerance);
                    Line scanLine = Line.CreateBound(
                        FromViewPoint(view, providerStart),
                        FromViewPoint(view, providerEnd));
                    string previewFailure;
                    ElementId previewId = TryCreateScanLinePreview(
                        document,
                        view,
                        scanStart,
                        scanEnd,
                        out previewFailure);
                    if (previewId == null || previewId == ElementId.InvalidElementId)
                    {
                        statistics.SkippedChains++;
                        SetFirstFailure(
                            statistics,
                            "Could not display the temporary scan line: " + previewFailure);
                        break;
                    }
                    uiDocument.RefreshActiveView();

                    statistics.ScopeElements += scope.Count;
                    IList<DimensionReferenceSkip> referenceSkips;
                    IList<DimensionCandidateReference> rawCandidates = referenceProvider.Collect(
                        scope,
                        measurementDirection,
                        scanLine,
                        out referenceSkips);
                    statistics.ReferenceSourceSkipped += referenceSkips.Count;
                    List<DimensionCandidateReference> candidates = FilterCandidates(
                        rawCandidates,
                        settings.ReferenceMode,
                        statistics);
                    statistics.ReferenceCandidates += candidates.Count;

                    var byKey = candidates
                        .GroupBy(candidate => candidate.StableKey, StringComparer.Ordinal)
                        .ToDictionary(grouping => grouping.Key, grouping => grouping.First(), StringComparer.Ordinal);
                    IDictionary<string, string> directionGroups = BuildDirectionGroups(
                        candidates,
                        view);
                    var items = candidates
                        .Select(candidate => ProjectedItem.FromPoint(
                            candidate.StableKey,
                            ToViewPoint(view, candidate.Anchor),
                            directionGroups[candidate.StableKey]))
                        .ToList();
                    var options = new DimensionChainPlannerOptions
                    {
                        HitTolerance = tolerance,
                        DedupeTolerance = tolerance,
                        MaximumChainGap = 0.0
                    };
                    IList<DimensionChainPlan> plans = DimensionChainPlanner.PlanChains(
                        items,
                        scanStart,
                        scanEnd,
                        options);
                    if (plans.Count == 0)
                    {
                        statistics.SkippedChains++;
                        SetFirstFailure(
                            statistics,
                            candidates.Count < 2
                                ? "The scan line found fewer than two compatible Revit references."
                                : "Compatible references could not be combined into a dimension chain.");
                        string cleanupFailure;
                        if (!TryDeleteScanLinePreview(document, previewId, out cleanupFailure))
                        {
                            fatalPreviewCleanup = true;
                            statistics.FirstFailure =
                                "Could not remove the temporary scan line: " + cleanupFailure;
                            break;
                        }
                        uiDocument.RefreshActiveView();
                        if (!settings.RepeatRun)
                        {
                            break;
                        }
                        continue;
                    }

                    XYZ pickedPlacement = null;
                    bool placementCancelled = false;
                    bool previewCleanupFailed = false;
                    List<DimensionCandidateReference> placementPreviewCandidates = plans[0].Hits
                        .Select(hit =>
                        {
                            DimensionCandidateReference candidate;
                            return byKey.TryGetValue(hit.Item.ReferenceKey, out candidate)
                                ? candidate
                                : null;
                        })
                        .Where(candidate => candidate != null)
                        .ToList();
                    Point2 placementPreviewDirection = placementPreviewCandidates.Count == 0
                        ? frame.AlongDirection
                        : ToViewVector2(view, placementPreviewCandidates[0].Normal);
                    using (DimensionPlacementPreviewOverlay placementPreview =
                        DimensionPlacementPreviewOverlay.TryShow(
                            uiDocument,
                            document,
                            view,
                            placementPreviewCandidates,
                            placementPreviewDirection,
                            settings))
                    {
                        try
                        {
                            pickedPlacement = uiDocument.Selection.PickPoint(
                                pointSnaps,
                                "Auto Dimension 3/3: move the preview, then click to place the dimension");
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            statistics.UserCancelled = true;
                            placementCancelled = true;
                        }
                        finally
                        {
                            string cleanupFailure;
                            if (!TryDeleteScanLinePreview(document, previewId, out cleanupFailure))
                            {
                                previewCleanupFailed = true;
                                statistics.FirstFailure =
                                    "Could not remove the temporary scan line: " + cleanupFailure;
                            }
                            uiDocument.RefreshActiveView();
                        }
                    }
                    if (previewCleanupFailed)
                    {
                        fatalPreviewCleanup = true;
                        break;
                    }
                    if (placementCancelled || pickedPlacement == null)
                    {
                        break;
                    }
                    Point2 placement = ToViewPoint(view, pickedPlacement);

                    double laneStep = PaperMillimetersToInternal(
                        settings.LaneStepPaperMm,
                        view);
                    bool transactionCommitFailed = false;
                    using (var transaction = new Transaction(
                        document,
                        "Tung MEP - Auto Dimension by Line"))
                    {
                        int createdBeforeTransaction = statistics.Created;
                        int lanesBeforeTransaction = lanes.Count;
                        var referenceKeysBeforeTransaction = new HashSet<string>(
                            existingReferenceKeys,
                            StringComparer.Ordinal);
                        transaction.Start();
                        ConfigureFailureHandling(transaction);
                        foreach (DimensionChainPlan plan in plans)
                        {
                            List<DimensionCandidateReference> ordered = plan.Hits
                                .Select(hit =>
                                {
                                    DimensionCandidateReference candidate;
                                    return byKey.TryGetValue(hit.Item.ReferenceKey, out candidate)
                                        ? candidate
                                        : null;
                                })
                                .Where(candidate => candidate != null)
                                .ToList();
                            if (ordered.Count < 2)
                            {
                                statistics.SkippedChains++;
                                continue;
                            }

                            Point2 exactDirection = ToViewVector2(view, ordered[0].Normal);
                            if (exactDirection.Length <= 1e-8)
                            {
                                statistics.SkippedChains++;
                                SetFirstFailure(
                                    statistics,
                                    "A compatible reference did not expose a valid dimension direction.");
                                continue;
                            }
                            exactDirection = exactDirection.Normalize();
                            if (Point2.Dot(exactDirection, frame.AlongDirection) < 0.0)
                            {
                                exactDirection = exactDirection * -1.0;
                            }
                            var exactFrame = new ProjectionFrame2(scanStart, exactDirection);
                            Point2 basePlacement = placement;

                            int before = statistics.Created;
                            if (settings.CreateChain)
                            {
                                TryCreateNativeDimension(
                                    document,
                                    view,
                                    dimensionType,
                                    ordered,
                                    exactDirection,
                                    basePlacement,
                                    laneStep,
                                    settings.AvoidOverlap,
                                    existingReferenceKeys,
                                    lanes,
                                    statistics,
                                    "scan chain");
                            }
                            if (settings.CreateOverall && (!settings.CreateChain || ordered.Count > 2))
                            {
                                Point2 normal = exactFrame.PerpendicularDirection;
                                double sign = Point2.Dot(
                                    basePlacement - Midpoint(scanStart, scanEnd),
                                    normal) >= 0.0 ? 1.0 : -1.0;
                                var overall = new List<DimensionCandidateReference>
                                {
                                    ordered[0],
                                    ordered[ordered.Count - 1]
                                };
                                TryCreateNativeDimension(
                                    document,
                                    view,
                                    dimensionType,
                                    overall,
                                    exactDirection,
                                    basePlacement + (normal * sign * laneStep),
                                    laneStep,
                                    settings.AvoidOverlap,
                                    existingReferenceKeys,
                                    lanes,
                                    statistics,
                                    "overall scan dimension");
                            }
                            if (statistics.Created == before)
                            {
                                statistics.SkippedChains++;
                            }
                        }
                        if (statistics.Created > createdBeforeTransaction)
                        {
                            transactionCommitFailed = !TryCommitParentTransaction(
                                transaction,
                                createdBeforeTransaction,
                                lanesBeforeTransaction,
                                referenceKeysBeforeTransaction,
                                existingReferenceKeys,
                                lanes,
                                statistics);
                        }
                        else
                        {
                            transaction.RollBack();
                        }
                    }

                    if (transactionCommitFailed)
                    {
                        break;
                    }

                    if (!settings.RepeatRun)
                    {
                        break;
                    }
                }

                if (fatalPreviewCleanup)
                {
                    try
                    {
                        group.RollBack();
                    }
                    catch (Exception exception)
                    {
                        statistics.FirstFailure =
                            "Could not roll back the temporary scan-line preview: " +
                            exception.Message;
                    }
                    statistics.Created = 0;
                    statistics.FailedChains++;
                    return Result.Cancelled;
                }
                if (statistics.Created > 0)
                {
                    try
                    {
                        TransactionStatus groupStatus = group.Assimilate();
                        if (groupStatus == TransactionStatus.Committed)
                        {
                            return Result.Succeeded;
                        }
                        SetFirstFailure(
                            statistics,
                            "Revit did not assimilate the Auto Dimension transaction group.");
                    }
                    catch (Exception exception)
                    {
                        SetFirstFailure(
                            statistics,
                            "Could not finish the Auto Dimension transaction group: " +
                            exception.Message);
                    }
                    statistics.Created = 0;
                    statistics.FailedChains++;
                    return Result.Cancelled;
                }
                group.RollBack();
            }
            return anyPromptCompleted ? Result.Cancelled : Result.Cancelled;
        }

        private static Result ExecuteSingleSprinklerToWalls(
            UIDocument uiDocument,
            Document document,
            View view,
            DimensionType dimensionType,
            AutoDimensionSettings settings,
            AutoDimensionStatistics statistics)
        {
            var provider = new AutoDimensionReferenceProvider(document, view);
            var existingReferenceKeys = CollectExistingDimensionReferenceKeys(document, view);
            var lanes = CollectExistingDimensionLanes(document, view);
            List<FamilyInstance> preselected = CollectSprinklers(document, uiDocument);
            FamilyInstance rememberedSelection = preselected.Count == 1
                ? preselected[0]
                : null;
            bool useRememberedSelection = rememberedSelection != null;

            while (true)
            {
                FamilyInstance sprinkler = null;
                if (useRememberedSelection)
                {
                    sprinkler = rememberedSelection;
                    useRememberedSelection = false;
                }
                else
                {
                    try
                    {
                        Reference picked = uiDocument.Selection.PickObject(
                            ObjectType.Element,
                            new SprinklerSelectionFilter(),
                            "Auto Dimension 1/2: select one sprinkler");
                        sprinkler = document.GetElement(picked.ElementId) as FamilyInstance;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        statistics.UserCancelled = true;
                        break;
                    }
                }
                if (!IsSprinkler(sprinkler))
                {
                    SetFirstFailure(statistics, "Select one sprinkler instance.");
                    break;
                }

                XYZ sprinklerAnchor;
                if (!TryGetElementAnchor(sprinkler, view, out sprinklerAnchor))
                {
                    statistics.ReferenceSourceSkipped++;
                    SetFirstFailure(
                        statistics,
                        "The selected sprinkler does not have a usable location in this view.");
                    break;
                }
                statistics.ScopeElements++;

                Point2 xDirection = new Point2(1.0, 0.0);
                Point2 yDirection = new Point2(0.0, 1.0);
                DimensionCandidateReference sprinklerX =
                    CollectSingleSprinklerCenterReference(
                        provider,
                        sprinkler,
                        sprinklerAnchor,
                        view,
                        xDirection,
                        statistics);
                DimensionCandidateReference sprinklerY =
                    CollectSingleSprinklerCenterReference(
                        provider,
                        sprinkler,
                        sprinklerAnchor,
                        view,
                        yDirection,
                        statistics);
                DimensionCandidateReference wallX = FindNearestWallFaceReference(
                    document,
                    view,
                    sprinklerAnchor,
                    xDirection,
                    settings.ToleranceMm);
                DimensionCandidateReference wallY = FindNearestWallFaceReference(
                    document,
                    view,
                    sprinklerAnchor,
                    yDirection,
                    settings.ToleranceMm);
                DimensionCandidateReference columnX =
                    FindNearestStructuralColumnFaceReference(
                        document,
                        view,
                        sprinklerAnchor,
                        xDirection,
                        settings.ToleranceMm,
                        wallX);
                DimensionCandidateReference columnY =
                    FindNearestStructuralColumnFaceReference(
                        document,
                        view,
                        sprinklerAnchor,
                        yDirection,
                        settings.ToleranceMm,
                        wallY);
                DimensionCandidateReference boundaryX = columnX ?? wallX;
                DimensionCandidateReference boundaryY = columnY ?? wallY;

                var missing = new List<string>();
                if (sprinklerX == null)
                {
                    missing.Add("sprinkler Center (Left/Right) reference for X");
                }
                if (sprinklerY == null)
                {
                    missing.Add("sprinkler Center (Front/Back) reference for Y");
                }
                if (boundaryX == null)
                {
                    missing.Add("an axis-aligned Structural Column or Wall face in X");
                }
                if (boundaryY == null)
                {
                    missing.Add("an axis-aligned Structural Column or Wall face in Y");
                }
                if (missing.Count > 0)
                {
                    SetFirstFailure(
                        statistics,
                        "Could not create the X/Y pair because the tool could not find " +
                        string.Join(", ", missing) + ".");
                    break;
                }
                statistics.ReferenceCandidates += 4;

                XYZ pickedPlacement;
                AutoDimensionSettings previewSettings = settings.Clone();
                previewSettings.CreateChain = true;
                previewSettings.CreateOverall = false;
                using (DimensionPlacementPreviewOverlay previewX =
                    DimensionPlacementPreviewOverlay.TryShow(
                        uiDocument,
                        document,
                        view,
                        new[] { boundaryX, sprinklerX },
                        xDirection,
                        previewSettings))
                using (DimensionPlacementPreviewOverlay previewY =
                    DimensionPlacementPreviewOverlay.TryShow(
                        uiDocument,
                        document,
                        view,
                        new[] { boundaryY, sprinklerY },
                        yDirection,
                        previewSettings))
                {
                    try
                    {
                        pickedPlacement = uiDocument.Selection.PickPoint(
                            ObjectSnapTypes.None,
                            "Auto Dimension 2/2: move the X/Y preview, then click to place both dimensions");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        statistics.UserCancelled = true;
                        break;
                    }
                }

                Point2 placement = ToViewPoint(view, pickedPlacement);
                double laneStep = PaperMillimetersToInternal(
                    settings.LaneStepPaperMm,
                    view);
                using (var transaction = new Transaction(
                    document,
                    "Tung MEP - Dimension Sprinkler to Walls"))
                {
                    int createdBeforeTransaction = statistics.Created;
                    int lanesBeforeTransaction = lanes.Count;
                    var referenceKeysBeforeTransaction = new HashSet<string>(
                        existingReferenceKeys,
                        StringComparer.Ordinal);
                    transaction.Start();
                    ConfigureFailureHandling(transaction);
                    TryCreateNativeDimension(
                        document,
                        view,
                        dimensionType,
                        new[] { boundaryX, sprinklerX },
                        xDirection,
                        placement,
                        laneStep,
                        settings.AvoidOverlap,
                        existingReferenceKeys,
                        lanes,
                        statistics,
                        "sprinkler-to-boundary X dimension");
                    TryCreateNativeDimension(
                        document,
                        view,
                        dimensionType,
                        new[] { boundaryY, sprinklerY },
                        yDirection,
                        placement,
                        laneStep,
                        settings.AvoidOverlap,
                        existingReferenceKeys,
                        lanes,
                        statistics,
                        "sprinkler-to-boundary Y dimension");
                    if (statistics.Created > createdBeforeTransaction)
                    {
                        if (!TryCommitParentTransaction(
                            transaction,
                            createdBeforeTransaction,
                            lanesBeforeTransaction,
                            referenceKeysBeforeTransaction,
                            existingReferenceKeys,
                            lanes,
                            statistics))
                        {
                            break;
                        }
                    }
                    else
                    {
                        transaction.RollBack();
                        break;
                    }
                }

                if (!settings.RepeatRun)
                {
                    break;
                }
            }
            return statistics.Created > 0 ? Result.Succeeded : Result.Cancelled;
        }

        private static DimensionCandidateReference CollectSingleSprinklerCenterReference(
            AutoDimensionReferenceProvider provider,
            FamilyInstance sprinkler,
            XYZ sprinklerAnchor,
            View view,
            Point2 measurementDirection,
            AutoDimensionStatistics statistics)
        {
            IList<DimensionCandidateReference> candidates;
            string reason;
            if (!provider.TryCollect(
                sprinkler,
                ToViewVector(view, measurementDirection),
                null,
                out candidates,
                out reason))
            {
                statistics.ReferenceSourceSkipped++;
                SetFirstFailure(statistics, reason);
                return null;
            }
            Point2 anchor = ToViewPoint(view, sprinklerAnchor);
            List<DimensionCandidateReference> centers = candidates
                .Where(candidate => candidate != null &&
                    candidate.Kind == DimensionReferenceKind.Center)
                .OrderBy(candidate => Math.Abs(Point2.Dot(
                    ToViewPoint(view, candidate.Anchor) - anchor,
                    measurementDirection)))
                .ToList();
            return centers.FirstOrDefault();
        }

        private static DimensionCandidateReference FindNearestWallFaceReference(
            Document document,
            View view,
            XYZ sprinklerAnchor,
            Point2 measurementDirection,
            double toleranceMm)
        {
            XYZ axis = ToViewVector(view, measurementDirection);
            Point2 perpendicular2 = new Point2(
                -measurementDirection.Y,
                measurementDirection.X);
            XYZ perpendicular = ToViewVector(view, perpendicular2);
            XYZ viewNormal = view.ViewDirection.Normalize();
            double alignmentTolerance = UnitUtils.ConvertToInternalUnits(
                Math.Max(25.0, toleranceMm),
                UnitTypeId.Millimeters);
            double minimumDistance = UnitUtils.ConvertToInternalUnits(
                0.5,
                UnitTypeId.Millimeters);
            DimensionCandidateReference nearest = null;
            double nearestDistance = double.MaxValue;
            IEnumerable<Wall> walls = new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(Wall))
                .Cast<Wall>();
            foreach (Wall wall in walls)
            {
                EvaluateWallFaces(
                    document,
                    wall,
                    null,
                    Transform.Identity,
                    sprinklerAnchor,
                    axis,
                    perpendicular,
                    viewNormal,
                    alignmentTolerance,
                    minimumDistance,
                    ref nearest,
                    ref nearestDistance);
            }

            IEnumerable<RevitLinkInstance> links =
                new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>();
            foreach (RevitLinkInstance link in links)
            {
                Document linkDocument;
                Transform linkTransform;
                try
                {
                    linkDocument = link.GetLinkDocument();
                    linkTransform = link.GetTotalTransform();
                }
                catch
                {
                    continue;
                }
                if (linkDocument == null || linkTransform == null)
                {
                    continue;
                }
                foreach (Wall wall in new FilteredElementCollector(linkDocument)
                    .OfClass(typeof(Wall))
                    .Cast<Wall>())
                {
                    EvaluateWallFaces(
                        document,
                        wall,
                        link,
                        linkTransform,
                        sprinklerAnchor,
                        axis,
                        perpendicular,
                        viewNormal,
                        alignmentTolerance,
                        minimumDistance,
                        ref nearest,
                        ref nearestDistance);
                }
            }
            return nearest;
        }

        private static DimensionCandidateReference
            FindNearestStructuralColumnFaceReference(
                Document document,
                View view,
                XYZ sprinklerAnchor,
                Point2 measurementDirection,
                double toleranceMm,
                DimensionCandidateReference nearestWall)
        {
            XYZ axis = ToViewVector(view, measurementDirection);
            Point2 perpendicular2 = new Point2(
                -measurementDirection.Y,
                measurementDirection.X);
            XYZ perpendicular = ToViewVector(view, perpendicular2);
            XYZ viewNormal = view.ViewDirection.Normalize();
            double alignmentTolerance = UnitUtils.ConvertToInternalUnits(
                Math.Max(500.0, toleranceMm),
                UnitTypeId.Millimeters);
            double minimumDistance = UnitUtils.ConvertToInternalUnits(
                0.5,
                UnitTypeId.Millimeters);
            double requiredSign = 0.0;
            double nearestDistance = double.MaxValue;
            if (nearestWall != null && nearestWall.Anchor != null)
            {
                double signedWallDistance = (nearestWall.Anchor - sprinklerAnchor)
                    .DotProduct(axis);
                if (Math.Abs(signedWallDistance) > minimumDistance)
                {
                    nearestDistance = Math.Abs(signedWallDistance) + minimumDistance;
                }
            }

            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine
            };
            DimensionCandidateReference nearest = null;
            var columnCategories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_Columns
            };
            var columnFilter = new ElementMulticategoryFilter(columnCategories);
            IEnumerable<FamilyInstance> hostColumns =
                new FilteredElementCollector(document, view.Id)
                    .WherePasses(columnFilter)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>();
            foreach (FamilyInstance column in hostColumns)
            {
                EvaluateStructuralColumnGeometry(
                    document,
                    column,
                    null,
                    Transform.Identity,
                    options,
                    sprinklerAnchor,
                    axis,
                    perpendicular,
                    viewNormal,
                    requiredSign,
                    alignmentTolerance,
                    minimumDistance,
                    ref nearest,
                    ref nearestDistance);
            }

            IEnumerable<RevitLinkInstance> links =
                new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>();
            foreach (RevitLinkInstance link in links)
            {
                Document linkDocument;
                Transform linkTransform;
                try
                {
                    linkDocument = link.GetLinkDocument();
                    linkTransform = link.GetTotalTransform();
                }
                catch
                {
                    continue;
                }
                if (linkDocument == null || linkTransform == null)
                {
                    continue;
                }
                foreach (FamilyInstance column in
                    new FilteredElementCollector(linkDocument)
                        .WherePasses(columnFilter)
                        .WhereElementIsNotElementType()
                        .OfType<FamilyInstance>())
                {
                    EvaluateStructuralColumnGeometry(
                        document,
                        column,
                        link,
                        linkTransform,
                        options,
                        sprinklerAnchor,
                        axis,
                        perpendicular,
                        viewNormal,
                        requiredSign,
                        alignmentTolerance,
                        minimumDistance,
                        ref nearest,
                        ref nearestDistance);
                }
            }
            return nearest;
        }

        private static void EvaluateStructuralColumnGeometry(
            Document hostDocument,
            FamilyInstance column,
            RevitLinkInstance link,
            Transform documentTransform,
            Options options,
            XYZ sprinklerAnchor,
            XYZ axis,
            XYZ perpendicular,
            XYZ viewNormal,
            double requiredSign,
            double alignmentTolerance,
            double minimumDistance,
            ref DimensionCandidateReference nearest,
            ref double nearestDistance)
        {
            GeometryElement geometry;
            try
            {
                geometry = column.get_Geometry(options);
            }
            catch
            {
                return;
            }
            if (geometry == null)
            {
                return;
            }
            EvaluateStructuralColumnGeometryObjects(
                hostDocument,
                column,
                link,
                geometry,
                documentTransform,
                sprinklerAnchor,
                axis,
                perpendicular,
                viewNormal,
                requiredSign,
                alignmentTolerance,
                minimumDistance,
                ref nearest,
                ref nearestDistance);
        }

        private static void EvaluateStructuralColumnGeometryObjects(
            Document hostDocument,
            FamilyInstance column,
            RevitLinkInstance link,
            GeometryElement geometry,
            Transform transform,
            XYZ sprinklerAnchor,
            XYZ axis,
            XYZ perpendicular,
            XYZ viewNormal,
            double requiredSign,
            double alignmentTolerance,
            double minimumDistance,
            ref DimensionCandidateReference nearest,
            ref double nearestDistance)
        {
            foreach (GeometryObject geometryObject in geometry)
            {
                Solid solid = geometryObject as Solid;
                if (solid != null && solid.Faces != null && solid.Volume > 1e-9)
                {
                    foreach (Face rawFace in solid.Faces)
                    {
                        PlanarFace face = rawFace as PlanarFace;
                        if (face == null || face.Reference == null)
                        {
                            continue;
                        }
                        EvaluateStructuralColumnFace(
                            hostDocument,
                            column,
                            link,
                            face,
                            transform,
                            sprinklerAnchor,
                            axis,
                            perpendicular,
                            viewNormal,
                            requiredSign,
                            alignmentTolerance,
                            minimumDistance,
                            ref nearest,
                            ref nearestDistance);
                    }
                    continue;
                }

                GeometryInstance instance = geometryObject as GeometryInstance;
                if (instance == null)
                {
                    continue;
                }
                GeometryElement symbolGeometry;
                try
                {
                    symbolGeometry = instance.GetSymbolGeometry();
                }
                catch
                {
                    continue;
                }
                if (symbolGeometry == null)
                {
                    continue;
                }
                EvaluateStructuralColumnGeometryObjects(
                    hostDocument,
                    column,
                    link,
                    symbolGeometry,
                    transform.Multiply(instance.Transform),
                    sprinklerAnchor,
                    axis,
                    perpendicular,
                    viewNormal,
                    requiredSign,
                    alignmentTolerance,
                    minimumDistance,
                    ref nearest,
                    ref nearestDistance);
            }
        }

        private static void EvaluateStructuralColumnFace(
            Document hostDocument,
            FamilyInstance column,
            RevitLinkInstance link,
            PlanarFace face,
            Transform transform,
            XYZ sprinklerAnchor,
            XYZ axis,
            XYZ perpendicular,
            XYZ viewNormal,
            double requiredSign,
            double alignmentTolerance,
            double minimumDistance,
            ref DimensionCandidateReference nearest,
            ref double nearestDistance)
        {
            XYZ localAnchor;
            XYZ normal;
            XYZ worldProjection;
            XYZ worldBoundedProjection;
            try
            {
                localAnchor = transform.Inverse.OfPoint(sprinklerAnchor);
                XYZ localNormal = face.FaceNormal.Normalize();
                double planeOffset = (localAnchor - face.Origin)
                    .DotProduct(localNormal);
                XYZ localPlaneProjection = localAnchor -
                    (localNormal * planeOffset);
                XYZ fromOrigin = localPlaneProjection - face.Origin;
                double xLengthSquared = face.XVector.DotProduct(face.XVector);
                double yLengthSquared = face.YVector.DotProduct(face.YVector);
                if (xLengthSquared <= 1e-12 || yLengthSquared <= 1e-12)
                {
                    return;
                }
                double u = fromOrigin.DotProduct(face.XVector) / xLengthSquared;
                double v = fromOrigin.DotProduct(face.YVector) / yLengthSquared;
                BoundingBoxUV uvBounds = face.GetBoundingBox();
                if (uvBounds == null)
                {
                    return;
                }
                var boundedUv = new UV(
                    Math.Max(uvBounds.Min.U, Math.Min(uvBounds.Max.U, u)),
                    Math.Max(uvBounds.Min.V, Math.Min(uvBounds.Max.V, v)));
                normal = transform.OfVector(face.FaceNormal).Normalize();
                worldProjection = transform.OfPoint(localPlaneProjection);
                worldBoundedProjection = transform.OfPoint(face.Evaluate(boundedUv));
            }
            catch
            {
                return;
            }
            if (Math.Abs(normal.DotProduct(axis)) < 0.999999999999 ||
                Math.Abs(normal.DotProduct(viewNormal)) > 0.000001)
            {
                return;
            }
            XYZ delta = worldProjection - sprinklerAnchor;
            double signedDistance = delta.DotProduct(axis);
            double distance = Math.Abs(signedDistance);
            XYZ boundaryGap = worldBoundedProjection - worldProjection;
            double transverse = Math.Abs(boundaryGap.DotProduct(perpendicular));
            double depth = Math.Abs(boundaryGap.DotProduct(viewNormal));
            double proximity = Math.Sqrt(
                (distance * distance) +
                (transverse * transverse) +
                (depth * depth));
            if (distance <= minimumDistance ||
                proximity > nearestDistance ||
                transverse > alignmentTolerance ||
                depth > UnitUtils.ConvertToInternalUnits(
                    100.0,
                    UnitTypeId.Millimeters) ||
                (requiredSign != 0.0 && signedDistance * requiredSign <= 0.0))
            {
                return;
            }

            Reference dimensionReference;
            try
            {
                dimensionReference = link == null
                    ? face.Reference
                    : face.Reference.CreateLinkReference(link);
                if (link != null)
                {
                    dimensionReference = MakeLinkedReferenceForLinearDimension(
                        hostDocument,
                        dimensionReference);
                }
            }
            catch
            {
                return;
            }
            if (dimensionReference == null)
            {
                return;
            }
            string stableKey;
            try
            {
                stableKey = dimensionReference.ConvertToStableRepresentation(
                    hostDocument);
            }
            catch
            {
                return;
            }
            XYZ faceDirection = viewNormal.CrossProduct(normal);
            if (faceDirection.GetLength() <= 1e-9)
            {
                faceDirection = perpendicular;
            }
            Category category = column.Category;
            nearest = new DimensionCandidateReference(
                dimensionReference,
                worldProjection,
                faceDirection.Normalize(),
                normal,
                link == null ? column.Id : link.Id,
                category == null ? ElementId.InvalidElementId : category.Id,
                category == null ? string.Empty : category.Name,
                stableKey,
                DimensionReferenceKind.Face);
            nearestDistance = proximity;
        }

        private static void EvaluateWallFaces(
            Document hostDocument,
            Wall wall,
            RevitLinkInstance link,
            Transform transform,
            XYZ sprinklerAnchor,
            XYZ axis,
            XYZ perpendicular,
            XYZ viewNormal,
            double alignmentTolerance,
            double minimumDistance,
            ref DimensionCandidateReference nearest,
            ref double nearestDistance)
        {
            var sideReferences = new List<Reference>();
            try
            {
                sideReferences.AddRange(HostObjectUtils.GetSideFaces(
                    wall,
                    ShellLayerType.Exterior));
                sideReferences.AddRange(HostObjectUtils.GetSideFaces(
                    wall,
                    ShellLayerType.Interior));
            }
            catch
            {
                return;
            }
            XYZ localAnchor;
            try
            {
                localAnchor = transform.Inverse.OfPoint(sprinklerAnchor);
            }
            catch
            {
                return;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Reference sourceReference in sideReferences)
            {
                if (sourceReference == null)
                {
                    continue;
                }
                Reference dimensionReference;
                try
                {
                    dimensionReference = link == null
                        ? sourceReference
                        : sourceReference.CreateLinkReference(link);
                    if (link != null)
                    {
                        dimensionReference = MakeLinkedReferenceForLinearDimension(
                            hostDocument,
                            dimensionReference);
                    }
                }
                catch
                {
                    continue;
                }
                if (dimensionReference == null)
                {
                    continue;
                }
                string stableKey;
                try
                {
                    stableKey = dimensionReference.ConvertToStableRepresentation(
                        hostDocument);
                }
                catch
                {
                    continue;
                }
                if (!seen.Add(stableKey))
                {
                    continue;
                }
                Face face;
                try
                {
                    face = wall.GetGeometryObjectFromReference(sourceReference) as Face;
                }
                catch
                {
                    continue;
                }
                if (face == null)
                {
                    continue;
                }
                IntersectionResult projection;
                try
                {
                    projection = face.Project(localAnchor);
                }
                catch
                {
                    continue;
                }
                if (projection == null || projection.XYZPoint == null)
                {
                    continue;
                }
                XYZ normal;
                XYZ worldProjection;
                try
                {
                    normal = transform.OfVector(
                        face.ComputeNormal(projection.UVPoint)).Normalize();
                    worldProjection = transform.OfPoint(projection.XYZPoint);
                }
                catch
                {
                    continue;
                }
                if (Math.Abs(normal.DotProduct(axis)) < 0.999999999999 ||
                    Math.Abs(normal.DotProduct(viewNormal)) > 0.000001)
                {
                    continue;
                }
                XYZ delta = worldProjection - sprinklerAnchor;
                double distance = Math.Abs(delta.DotProduct(axis));
                double transverse = Math.Abs(delta.DotProduct(perpendicular));
                double depth = Math.Abs(delta.DotProduct(viewNormal));
                if (distance <= minimumDistance || distance >= nearestDistance ||
                    transverse > alignmentTolerance || depth > alignmentTolerance)
                {
                    continue;
                }
                XYZ faceDirection = viewNormal.CrossProduct(normal);
                if (faceDirection.GetLength() <= 1e-9)
                {
                    faceDirection = perpendicular;
                }
                Category category = wall.Category;
                nearest = new DimensionCandidateReference(
                    dimensionReference,
                    worldProjection,
                    faceDirection.Normalize(),
                    normal,
                    link == null ? wall.Id : link.Id,
                    category == null ? ElementId.InvalidElementId : category.Id,
                    category == null ? string.Empty : category.Name,
                    stableKey,
                    DimensionReferenceKind.Face);
                nearestDistance = distance;
            }
        }

        private static Reference MakeLinkedReferenceForLinearDimension(
            Document document,
            Reference reference)
        {
            if (document == null || reference == null ||
                reference.LinkedElementId == ElementId.InvalidElementId)
            {
                return reference;
            }
            try
            {
                string[] parts = reference.ConvertToStableRepresentation(document)
                    .Split(':');
                string rewritten = string.Empty;
                bool first = true;
                foreach (string part in parts)
                {
                    string token = part;
                    if (part.IndexOf("RVTLINK", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        token = rewritten.EndsWith(":0", StringComparison.Ordinal)
                            ? "RVTLINK"
                            : "0:RVTLINK";
                    }
                    rewritten = first
                        ? token
                        : string.Concat(rewritten, ":", token);
                    first = false;
                }
                rewritten += ":0:LINEAR";
                return Reference.ParseFromStableRepresentation(document, rewritten);
            }
            catch
            {
                return reference;
            }
        }

        private static Result ExecuteSprinklerPreset(
            UIDocument uiDocument,
            Document document,
            View view,
            DimensionType dimensionType,
            AutoDimensionSettings settings,
            AutoDimensionStatistics statistics)
        {
            List<FamilyInstance> sprinklers = CollectSprinklers(document, uiDocument);
            if (sprinklers.Count < 2)
            {
                try
                {
                    sprinklers = uiDocument.Selection
                        .PickElementsByRectangle(
                            new SprinklerSelectionFilter(),
                            "Auto Dimension: drag a rectangle around two or more sprinklers")
                        .OfType<FamilyInstance>()
                        .Where(IsSprinkler)
                        .GroupBy(instance => instance.Id.Value)
                        .Select(group => group.First())
                        .ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    statistics.UserCancelled = true;
                    return Result.Cancelled;
                }
            }
            if (sprinklers.Count < 2)
            {
                statistics.FirstFailure = "Select at least two sprinklers.";
                return Result.Cancelled;
            }

            var sprinklerPoints = new List<SprinklerPoint>();
            foreach (FamilyInstance sprinkler in sprinklers)
            {
                XYZ anchor;
                if (TryGetElementAnchor(sprinkler, view, out anchor))
                {
                    sprinklerPoints.Add(new SprinklerPoint(
                        sprinkler,
                        anchor,
                        ToViewPoint(view, anchor)));
                }
                else
                {
                    statistics.ReferenceSourceSkipped++;
                }
            }
            if (sprinklerPoints.Count < 2)
            {
                statistics.FirstFailure =
                    "Fewer than two selected sprinklers have a usable location in this view.";
                return Result.Cancelled;
            }

            double modelTolerance = UnitUtils.ConvertToInternalUnits(
                settings.ToleranceMm,
                UnitTypeId.Millimeters);
            IList<IList<SprinklerPoint>> proximityRegions =
                ClusterSprinklerRegionsByProximity(
                    sprinklerPoints,
                    modelTolerance);
            int directionRegionCount = proximityRegions.Count(region => region.Count >= 2);
            int directionRegionIndex = 0;
            var regionPlans = new List<SprinklerRegionPlan>();
            foreach (IList<SprinklerPoint> proximityRegion in proximityRegions)
            {
                if (proximityRegion.Count < 2)
                {
                    statistics.SkippedChains++;
                    continue;
                }
                directionRegionIndex++;
                Point2 branchDirection = DetectSprinklerBranchDirection(
                    document,
                    view,
                    proximityRegion,
                    modelTolerance);
                if (branchDirection.Length <= 1e-8)
                {
                    string regionSuffix = directionRegionCount > 1
                        ? " for region " + directionRegionIndex + " of " + directionRegionCount
                        : string.Empty;
                    try
                    {
                        XYZ directionStart = uiDocument.Selection.PickPoint(
                            ObjectSnapTypes.None,
                            "Auto Dimension: pick the first point along a sprinkler branch" +
                            regionSuffix);
                        XYZ directionEnd = uiDocument.Selection.PickPoint(
                            ObjectSnapTypes.None,
                            "Auto Dimension: pick the second point along the same branch direction" +
                            regionSuffix);
                        branchDirection = ToViewPoint(view, directionEnd) -
                            ToViewPoint(view, directionStart);
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        statistics.UserCancelled = true;
                        return Result.Cancelled;
                    }
                    if (branchDirection.Length <= 1e-8)
                    {
                        statistics.FirstFailure =
                            "The two branch-direction points are coincident.";
                        return Result.Cancelled;
                    }
                }
                branchDirection = branchDirection.Normalize();
                foreach (IList<SprinklerPoint> alignedRegion in ClusterSprinklerRegions(
                    proximityRegion,
                    branchDirection,
                    modelTolerance))
                {
                    if (alignedRegion.Count < 2)
                    {
                        statistics.SkippedChains++;
                        continue;
                    }
                    regionPlans.Add(new SprinklerRegionPlan(
                        alignedRegion,
                        branchDirection));
                }
            }
            if (regionPlans.Count == 0)
            {
                statistics.FirstFailure =
                    "No sprinkler region contains at least two aligned heads.";
                return Result.Cancelled;
            }
            var provider = new AutoDimensionReferenceProvider(document, view);
            var existingReferenceKeys = CollectExistingDimensionReferenceKeys(document, view);
            var lanes = CollectExistingDimensionLanes(document, view);
            double offset = PaperMillimetersToInternal(settings.OffsetPaperMm, view);
            double laneStep = PaperMillimetersToInternal(settings.LaneStepPaperMm, view);

            using (var transaction = new Transaction(
                document,
                "Tung MEP - Auto Dimension Sprinklers"))
            {
                int createdBeforeTransaction = statistics.Created;
                int lanesBeforeTransaction = lanes.Count;
                var referenceKeysBeforeTransaction = new HashSet<string>(
                    existingReferenceKeys,
                    StringComparer.Ordinal);
                transaction.Start();
                ConfigureFailureHandling(transaction);
                foreach (SprinklerRegionPlan region in regionPlans)
                {
                    CreateSprinklerRegionDimensions(
                        document,
                        view,
                        dimensionType,
                        provider,
                        region.Points,
                        region.BranchDirection,
                        region.CrossDirection,
                        offset,
                        laneStep,
                        modelTolerance,
                        settings,
                        existingReferenceKeys,
                        lanes,
                        statistics);
                }

                if (statistics.Created > createdBeforeTransaction)
                {
                    bool committed = TryCommitParentTransaction(
                        transaction,
                        createdBeforeTransaction,
                        lanesBeforeTransaction,
                        referenceKeysBeforeTransaction,
                        existingReferenceKeys,
                        lanes,
                        statistics);
                    return committed ? Result.Succeeded : Result.Cancelled;
                }
                transaction.RollBack();
            }
            SetFirstFailure(
                statistics,
                "No native dimension was created. Check the sprinkler family's Center reference planes.");
            return Result.Cancelled;
        }

        private static void CreateSprinklerRegionDimensions(
            Document document,
            View view,
            DimensionType dimensionType,
            AutoDimensionReferenceProvider provider,
            IList<SprinklerPoint> sprinklerPoints,
            Point2 branchDirection,
            Point2 crossDirection,
            double offset,
            double laneStep,
            double modelTolerance,
            AutoDimensionSettings settings,
            ISet<string> existingReferenceKeys,
            IList<DimensionLane> lanes,
            AutoDimensionStatistics statistics)
        {
            if (sprinklerPoints == null || sprinklerPoints.Count < 2)
            {
                statistics.SkippedChains++;
                return;
            }
            var frame = new ProjectionFrame2(new Point2(0.0, 0.0), branchDirection);
            var pointByKey = sprinklerPoints.ToDictionary(
                point => point.Element.Id.Value.ToString(),
                point => point,
                StringComparer.Ordinal);
            List<ProjectedItem> projected = sprinklerPoints
                .Select(point => ProjectedItem.FromPoint(
                    point.Element.Id.Value.ToString(),
                    point.Point,
                    string.Empty))
                .ToList();
            IList<ProjectedItemGroup> branchGroups = DimensionChainPlanner.GroupPointsIntoRows(
                projected,
                frame,
                modelTolerance)
                .Where(group => group.Items.Count > 0)
                .OrderBy(group => group.Coordinate)
                .ToList();
            if (branchGroups.Count == 0)
            {
                statistics.SkippedChains++;
                return;
            }

            double minimumAlong = sprinklerPoints.Min(point => frame.Project(point.Point).Along);
            double maximumAlong = sprinklerPoints.Max(point => frame.Project(point.Point).Along);
            double minimumAcross = sprinklerPoints.Min(point => frame.Project(point.Point).Perpendicular);
            double maximumAcross = sprinklerPoints.Max(point => frame.Project(point.Point).Perpendicular);

            if (settings.SprinklerDimensionAlongRows)
            {
                foreach (ProjectedItemGroup group in branchGroups)
                {
                    List<FamilyInstance> rowElements = group.Items
                        .Select(item =>
                        {
                            SprinklerPoint point;
                            return pointByKey.TryGetValue(item.ReferenceKey, out point)
                                ? point.Element
                                : null;
                        })
                        .Where(instance => instance != null)
                        .ToList();
                    List<DimensionCandidateReference> references = CollectSprinklerReferences(
                        provider,
                        rowElements,
                        view,
                        branchDirection,
                        AutoDimensionReferenceMode.Center,
                        statistics);
                    references = SortAndDedupeCandidates(
                        references,
                        view,
                        branchDirection,
                        modelTolerance);
                    if (references.Count < 2)
                    {
                        statistics.SkippedChains++;
                        continue;
                    }
                    Point2 placement = frame.Unproject(
                        (minimumAlong + maximumAlong) * 0.5,
                        group.Coordinate + offset);
                    TryCreateNativeDimension(
                        document,
                        view,
                        dimensionType,
                        references,
                        branchDirection,
                        placement,
                        laneStep,
                        settings.AvoidOverlap,
                        existingReferenceKeys,
                        lanes,
                        statistics,
                        "sprinkler row");
                }
            }

            if (settings.SprinklerDimensionBetweenRows && branchGroups.Count >= 2)
            {
                var betweenReferences = new List<DimensionCandidateReference>();
                foreach (ProjectedItemGroup group in branchGroups)
                {
                    SprinklerPoint representative = group.Items
                        .Select(item =>
                        {
                            SprinklerPoint point;
                            return pointByKey.TryGetValue(item.ReferenceKey, out point)
                                ? point
                                : null;
                        })
                        .Where(point => point != null)
                        .OrderBy(point => frame.Project(point.Point).Along)
                        .FirstOrDefault();
                    if (representative == null)
                    {
                        continue;
                    }
                    DimensionCandidateReference reference = CollectOneSprinklerReference(
                        provider,
                        representative.Element,
                        view,
                        crossDirection,
                        group.Coordinate,
                        frame,
                        statistics);
                    if (reference != null)
                    {
                        betweenReferences.Add(reference);
                    }
                }
                betweenReferences = SortAndDedupeCandidates(
                    betweenReferences,
                    view,
                    crossDirection,
                    modelTolerance);
                if (betweenReferences.Count >= 2)
                {
                    Point2 placement = frame.Unproject(
                        maximumAlong + offset,
                        (minimumAcross + maximumAcross) * 0.5);
                    TryCreateNativeDimension(
                        document,
                        view,
                        dimensionType,
                        betweenReferences,
                        crossDirection,
                        placement,
                        laneStep,
                        settings.AvoidOverlap,
                        existingReferenceKeys,
                        lanes,
                        statistics,
                        "between sprinkler rows");
                }
                else
                {
                    statistics.SkippedChains++;
                }
            }

            if (settings.SprinklerDimensionOverall)
            {
                CreateSprinklerOverallDimensions(
                    document,
                    view,
                    dimensionType,
                    provider,
                    sprinklerPoints,
                    frame,
                    branchDirection,
                    crossDirection,
                    minimumAlong,
                    maximumAlong,
                    minimumAcross,
                    maximumAcross,
                    offset,
                    laneStep,
                    modelTolerance,
                    settings,
                    existingReferenceKeys,
                    lanes,
                    statistics);
            }
        }

        private static IList<IList<SprinklerPoint>> ClusterSprinklerRegionsByProximity(
            IList<SprinklerPoint> points,
            double modelTolerance)
        {
            if (points == null || points.Count == 0)
            {
                return new List<IList<SprinklerPoint>>();
            }
            if (points.Count < 3)
            {
                return new List<IList<SprinklerPoint>> { points.ToList() };
            }

            var nearestDistances = new List<double>();
            for (int first = 0; first < points.Count; first++)
            {
                double nearest = double.MaxValue;
                for (int second = 0; second < points.Count; second++)
                {
                    if (first == second)
                    {
                        continue;
                    }
                    nearest = Math.Min(
                        nearest,
                        (points[first].Point - points[second].Point).Length);
                }
                if (nearest < double.MaxValue)
                {
                    nearestDistances.Add(nearest);
                }
            }
            double median = Median(nearestDistances);
            double minimumConnection = UnitUtils.ConvertToInternalUnits(
                3000.0,
                UnitTypeId.Millimeters);
            double maximumConnection = UnitUtils.ConvertToInternalUnits(
                6000.0,
                UnitTypeId.Millimeters);
            double connectionLimit = Math.Max(
                Math.Max(minimumConnection, modelTolerance * 10.0),
                Math.Min(maximumConnection, median * 1.75));

            var visited = new bool[points.Count];
            var regions = new List<IList<SprinklerPoint>>();
            for (int start = 0; start < points.Count; start++)
            {
                if (visited[start])
                {
                    continue;
                }
                var region = new List<SprinklerPoint>();
                var queue = new Queue<int>();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    region.Add(points[current]);
                    for (int candidate = 0; candidate < points.Count; candidate++)
                    {
                        if (visited[candidate] ||
                            (points[current].Point - points[candidate].Point).Length >
                                connectionLimit)
                        {
                            continue;
                        }
                        visited[candidate] = true;
                        queue.Enqueue(candidate);
                    }
                }
                regions.Add(region);
            }
            return regions.All(region => region.Count == 1)
                ? new List<IList<SprinklerPoint>> { points.ToList() }
                : regions;
        }

        private static IList<IList<SprinklerPoint>> ClusterSprinklerRegions(
            IList<SprinklerPoint> points,
            Point2 branchDirection,
            double modelTolerance)
        {
            if (points == null || points.Count == 0)
            {
                return new List<IList<SprinklerPoint>>();
            }
            if (points.Count < 3)
            {
                return new List<IList<SprinklerPoint>> { points.ToList() };
            }
            var frame = new ProjectionFrame2(new Point2(0.0, 0.0), branchDirection);
            List<Projection2> projected = points
                .Select(point => frame.Project(point.Point))
                .ToList();
            double alignmentTolerance = Math.Max(
                modelTolerance,
                UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters));
            var nearestAligned = new List<double>();
            for (int first = 0; first < projected.Count; first++)
            {
                double nearest = double.MaxValue;
                for (int second = 0; second < projected.Count; second++)
                {
                    if (first == second)
                    {
                        continue;
                    }
                    double along = Math.Abs(projected[first].Along - projected[second].Along);
                    double across = Math.Abs(
                        projected[first].Perpendicular - projected[second].Perpendicular);
                    if (along <= alignmentTolerance || across <= alignmentTolerance)
                    {
                        double distance = Math.Max(along, across);
                        if (distance > alignmentTolerance)
                        {
                            nearest = Math.Min(nearest, distance);
                        }
                    }
                }
                if (nearest < double.MaxValue)
                {
                    nearestAligned.Add(nearest);
                }
            }
            double median = nearestAligned.Count == 0
                ? 0.0
                : Median(nearestAligned);
            double minimumConnection = UnitUtils.ConvertToInternalUnits(
                3000.0,
                UnitTypeId.Millimeters);
            double maximumConnection = UnitUtils.ConvertToInternalUnits(
                6000.0,
                UnitTypeId.Millimeters);
            double connectionLimit = Math.Max(
                minimumConnection,
                Math.Min(maximumConnection, median * 1.75));

            var visited = new bool[points.Count];
            var regions = new List<IList<SprinklerPoint>>();
            for (int start = 0; start < points.Count; start++)
            {
                if (visited[start])
                {
                    continue;
                }
                var region = new List<SprinklerPoint>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                visited[start] = true;
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    region.Add(points[current]);
                    for (int candidate = 0; candidate < points.Count; candidate++)
                    {
                        if (visited[candidate])
                        {
                            continue;
                        }
                        double along = Math.Abs(
                            projected[current].Along - projected[candidate].Along);
                        double across = Math.Abs(
                            projected[current].Perpendicular -
                            projected[candidate].Perpendicular);
                        bool connected =
                            (along <= alignmentTolerance && across <= connectionLimit) ||
                            (across <= alignmentTolerance && along <= connectionLimit);
                        if (connected)
                        {
                            visited[candidate] = true;
                            queue.Enqueue(candidate);
                        }
                    }
                }
                regions.Add(region);
            }
            return regions;
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> ordered = values
                .Where(value => value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value))
                .OrderBy(value => value)
                .ToList();
            if (ordered.Count == 0)
            {
                return 0.0;
            }
            int middle = ordered.Count / 2;
            return ordered.Count % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) * 0.5
                : ordered[middle];
        }

        private static void CreateSprinklerOverallDimensions(
            Document document,
            View view,
            DimensionType dimensionType,
            AutoDimensionReferenceProvider provider,
            IList<SprinklerPoint> points,
            ProjectionFrame2 frame,
            Point2 branchDirection,
            Point2 crossDirection,
            double minimumAlong,
            double maximumAlong,
            double minimumAcross,
            double maximumAcross,
            double offset,
            double laneStep,
            double tolerance,
            AutoDimensionSettings settings,
            ISet<string> existingReferenceKeys,
            IList<DimensionLane> lanes,
            AutoDimensionStatistics statistics)
        {
            SprinklerPoint firstAlong = points
                .OrderBy(point => frame.Project(point.Point).Along)
                .First();
            SprinklerPoint lastAlong = points
                .OrderByDescending(point => frame.Project(point.Point).Along)
                .First();
            var alongReferences = new List<DimensionCandidateReference>();
            DimensionCandidateReference firstAlongReference = CollectOneSprinklerReference(
                provider,
                firstAlong.Element,
                view,
                branchDirection,
                minimumAlong,
                frame,
                statistics);
            DimensionCandidateReference lastAlongReference = CollectOneSprinklerReference(
                provider,
                lastAlong.Element,
                view,
                branchDirection,
                maximumAlong,
                frame,
                statistics);
            if (firstAlongReference != null)
            {
                alongReferences.Add(firstAlongReference);
            }
            if (lastAlongReference != null)
            {
                alongReferences.Add(lastAlongReference);
            }
            alongReferences = SortAndDedupeCandidates(
                alongReferences,
                view,
                branchDirection,
                tolerance);
            if (alongReferences.Count >= 2)
            {
                Point2 placement = frame.Unproject(
                    (minimumAlong + maximumAlong) * 0.5,
                    maximumAcross + offset + laneStep);
                TryCreateNativeDimension(
                    document,
                    view,
                    dimensionType,
                    alongReferences,
                    branchDirection,
                    placement,
                    laneStep,
                    settings.AvoidOverlap,
                    existingReferenceKeys,
                    lanes,
                    statistics,
                    "overall sprinkler length");
            }
            else
            {
                statistics.SkippedChains++;
            }

            SprinklerPoint firstAcross = points
                .OrderBy(point => frame.Project(point.Point).Perpendicular)
                .First();
            SprinklerPoint lastAcross = points
                .OrderByDescending(point => frame.Project(point.Point).Perpendicular)
                .First();
            var acrossReferences = new List<DimensionCandidateReference>();
            DimensionCandidateReference firstAcrossReference = CollectOneSprinklerReference(
                provider,
                firstAcross.Element,
                view,
                crossDirection,
                minimumAcross,
                frame,
                statistics);
            DimensionCandidateReference lastAcrossReference = CollectOneSprinklerReference(
                provider,
                lastAcross.Element,
                view,
                crossDirection,
                maximumAcross,
                frame,
                statistics);
            if (firstAcrossReference != null)
            {
                acrossReferences.Add(firstAcrossReference);
            }
            if (lastAcrossReference != null)
            {
                acrossReferences.Add(lastAcrossReference);
            }
            acrossReferences = SortAndDedupeCandidates(
                acrossReferences,
                view,
                crossDirection,
                tolerance);
            if (acrossReferences.Count >= 2)
            {
                Point2 placement = frame.Unproject(
                    maximumAlong + offset + laneStep,
                    (minimumAcross + maximumAcross) * 0.5);
                TryCreateNativeDimension(
                    document,
                    view,
                    dimensionType,
                    acrossReferences,
                    crossDirection,
                    placement,
                    laneStep,
                    settings.AvoidOverlap,
                    existingReferenceKeys,
                    lanes,
                    statistics,
                    "overall sprinkler width");
            }
            else
            {
                statistics.SkippedChains++;
            }
        }

        private static List<DimensionCandidateReference> CollectSprinklerReferences(
            AutoDimensionReferenceProvider provider,
            IEnumerable<FamilyInstance> sprinklers,
            View view,
            Point2 measurementDirection,
            AutoDimensionReferenceMode referenceMode,
            AutoDimensionStatistics statistics)
        {
            XYZ direction = ToViewVector(view, measurementDirection);
            var result = new List<DimensionCandidateReference>();
            foreach (FamilyInstance sprinkler in sprinklers)
            {
                IList<DimensionCandidateReference> candidates;
                string reason;
                if (!provider.TryCollect(
                    sprinkler,
                    direction,
                    null,
                    out candidates,
                    out reason))
                {
                    statistics.ReferenceSourceSkipped++;
                    SetFirstFailure(statistics, reason);
                    continue;
                }
                List<DimensionCandidateReference> filtered = FilterCandidates(
                    candidates,
                    referenceMode);
                statistics.ReferenceCandidates += filtered.Count;
                result.AddRange(filtered);
            }
            return result;
        }

        private static DimensionCandidateReference CollectOneSprinklerReference(
            AutoDimensionReferenceProvider provider,
            FamilyInstance sprinkler,
            View view,
            Point2 measurementDirection,
            double targetCoordinate,
            ProjectionFrame2 branchFrame,
            AutoDimensionStatistics statistics)
        {
            IList<DimensionCandidateReference> candidates;
            string reason;
            if (!provider.TryCollect(
                sprinkler,
                ToViewVector(view, measurementDirection),
                null,
                out candidates,
                out reason))
            {
                statistics.ReferenceSourceSkipped++;
                SetFirstFailure(statistics, reason);
                return null;
            }
            List<DimensionCandidateReference> centers = candidates
                .Where(candidate => candidate.Kind == DimensionReferenceKind.Center)
                .ToList();
            List<DimensionCandidateReference> usable = centers.Count > 0
                ? centers
                : candidates.ToList();
            statistics.ReferenceCandidates += usable.Count;
            if (usable.Count == 0)
            {
                return null;
            }
            bool alongBranch = Math.Abs(Point2.Dot(
                measurementDirection.Normalize(),
                branchFrame.AlongDirection)) >= 0.98;
            return usable
                .OrderBy(candidate =>
                {
                    Projection2 projected = branchFrame.Project(
                        ToViewPoint(view, candidate.Anchor));
                    double coordinate = alongBranch
                        ? projected.Along
                        : projected.Perpendicular;
                    return Math.Abs(coordinate - targetCoordinate);
                })
                .First();
        }

        private static bool TryCreateNativeDimension(
            Document document,
            View view,
            DimensionType dimensionType,
            IList<DimensionCandidateReference> rawCandidates,
            Point2 direction,
            Point2 basePlacement,
            double laneStep,
            bool avoidOverlap,
            ISet<string> existingReferenceKeys,
            IList<DimensionLane> lanes,
            AutoDimensionStatistics statistics,
            string label)
        {
            if (rawCandidates == null || rawCandidates.Count < 2 || direction.Length <= 1e-8)
            {
                statistics.SkippedChains++;
                return false;
            }
            direction = CanonicalDirection(direction.Normalize());
            List<DimensionCandidateReference> candidates = SortAndDedupeCandidates(
                rawCandidates,
                view,
                direction,
                UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters));
            if (candidates.Count < 2)
            {
                statistics.SkippedChains++;
                return false;
            }
            Point2 firstNormal = ToViewVector2(view, candidates[0].Normal);
            if (firstNormal.Length <= 1e-8 || candidates.Any(candidate =>
            {
                Point2 normal = ToViewVector2(view, candidate.Normal);
                return normal.Length <= 1e-8 || Math.Abs(Point2.Dot(
                    firstNormal.Normalize(),
                    normal.Normalize())) < 0.999999999999;
            }))
            {
                statistics.SkippedChains++;
                SetFirstFailure(
                    statistics,
                    "A dimension chain contained references that are not exactly parallel.");
                return false;
            }

            string referenceKey = BuildReferenceSetKey(
                document,
                candidates.Select(candidate => candidate.Reference));
            if (!string.IsNullOrEmpty(referenceKey) && existingReferenceKeys.Contains(referenceKey))
            {
                statistics.DuplicateDimensionsSkipped++;
                return false;
            }

            Point2 average = new Point2(
                candidates.Average(candidate => ToViewPoint(view, candidate.Anchor).X),
                candidates.Average(candidate => ToViewPoint(view, candidate.Anchor).Y));
            double minimumAlong = candidates.Min(candidate => Point2.Dot(
                ToViewPoint(view, candidate.Anchor),
                direction));
            double maximumAlong = candidates.Max(candidate => Point2.Dot(
                ToViewPoint(view, candidate.Anchor),
                direction));
            Point2 outward = new Point2(-direction.Y, direction.X);
            if (Point2.Dot(basePlacement - average, outward) < 0.0)
            {
                outward = outward * -1.0;
            }
            Point2 placement = basePlacement;
            bool foundLane = !avoidOverlap;
            if (avoidOverlap)
            {
                double baseDistance = Math.Max(
                    laneStep,
                    Math.Abs(Point2.Dot(basePlacement - average, outward)));
                Point2 oppositeBase = average - (outward * baseDistance);
                for (int side = 0; side < 2 && !foundLane; side++)
                {
                    Point2 sideBase = side == 0 ? basePlacement : oppositeBase;
                    Point2 sideDirection = side == 0 ? outward : outward * -1.0;
                    for (int rank = 0; rank < MaximumLaneAttempts; rank++)
                    {
                        Point2 proposed = sideBase + (sideDirection * laneStep * rank);
                        if (!IsLaneOccupied(
                            lanes,
                            direction,
                            proposed,
                            laneStep * 0.75,
                            minimumAlong,
                            maximumAlong))
                        {
                            placement = proposed;
                            foundLane = true;
                            break;
                        }
                    }
                }
            }
            if (!foundLane)
            {
                statistics.SkippedChains++;
                SetFirstFailure(
                    statistics,
                    "No free dimension lane was found on either side of the references.");
                return false;
            }

            using (var subTransaction = new SubTransaction(document))
            {
                subTransaction.Start();
                try
                {
                    var references = new ReferenceArray();
                    foreach (DimensionCandidateReference candidate in candidates)
                    {
                        Reference dimensionReference = candidate.Reference;
                        try
                        {
                            string stable = dimensionReference
                                .ConvertToStableRepresentation(document);
                            Reference reparsed = Reference.ParseFromStableRepresentation(
                                document,
                                stable);
                            if (reparsed != null)
                            {
                                dimensionReference = reparsed;
                            }
                        }
                        catch
                        {
                            // Some native datum references are valid without a
                            // round-trip stable representation.
                        }
                        references.Append(dimensionReference);
                    }
                    Line dimensionLine = Line.CreateUnbound(
                        FromViewPoint(view, placement),
                        ToViewVector(view, direction));
                    Dimension dimension = document.Create.NewDimension(
                        view,
                        dimensionLine,
                        references,
                        dimensionType);
                    document.Regenerate();
                    if (dimension == null || !dimension.IsValidObject || !dimension.AreReferencesAvailable)
                    {
                        throw new InvalidOperationException(
                            "Revit did not retain the dimension references.");
                    }
                    TransactionStatus subStatus = subTransaction.Commit();
                    if (subStatus != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException(
                            "Revit did not commit the dimension subtransaction.");
                    }
                    statistics.Created++;
                    lanes.Add(new DimensionLane(
                        direction,
                        placement,
                        minimumAlong,
                        maximumAlong));
                    if (!string.IsNullOrEmpty(referenceKey))
                    {
                        existingReferenceKeys.Add(referenceKey);
                    }
                    return true;
                }
                catch (Exception exception)
                {
                    try
                    {
                        subTransaction.RollBack();
                    }
                    catch
                    {
                        // The parent transaction remains responsible for final rollback.
                    }
                    statistics.FailedChains++;
                    SetFirstFailure(
                        statistics,
                        "Could not create " + label + ": " + exception.Message);
                    return false;
                }
            }
        }

        private static bool TryCommitParentTransaction(
            Transaction transaction,
            int createdBeforeTransaction,
            int lanesBeforeTransaction,
            ISet<string> referenceKeysBeforeTransaction,
            ISet<string> existingReferenceKeys,
            IList<DimensionLane> lanes,
            AutoDimensionStatistics statistics)
        {
            string failure = string.Empty;
            try
            {
                TransactionStatus status = transaction.Commit();
                if (status == TransactionStatus.Committed)
                {
                    return true;
                }
                if (status == TransactionStatus.Pending)
                {
                    SetFirstFailure(
                        statistics,
                        "Revit is still processing a dimension failure dialog.");
                    return false;
                }
                failure = "Revit returned transaction status " + status + ".";
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }

            try
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                {
                    transaction.RollBack();
                }
            }
            catch
            {
                // Revit may already have rolled the transaction back while handling failures.
            }
            statistics.Created = createdBeforeTransaction;
            while (lanes.Count > lanesBeforeTransaction)
            {
                lanes.RemoveAt(lanes.Count - 1);
            }
            existingReferenceKeys.Clear();
            foreach (string key in referenceKeysBeforeTransaction)
            {
                existingReferenceKeys.Add(key);
            }
            statistics.FailedChains++;
            SetFirstFailure(
                statistics,
                "The Revit transaction was not committed: " + failure);
            return false;
        }

        private static void ConfigureFailureHandling(Transaction transaction)
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetForcedModalHandling(true);
            options.SetClearAfterRollback(true);
            options.SetDelayedMiniWarnings(false);
            options.SetFailuresPreprocessor(new AutoDimensionFailurePreprocessor());
            transaction.SetFailureHandlingOptions(options);
        }

        private static List<Element> CollectByLineScope(
            Document document,
            View view,
            UIDocument uiDocument,
            AutoDimensionSettings settings)
        {
            List<Element> selected = uiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .Where(IsSupportedByLineElement)
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
            // One accidentally selected element must not silently reduce a view-wide
            // scan to an unusable scope. Two or more supported preselected elements
            // are treated as an intentional scope override.
            if (selected.Count >= 2)
            {
                return selected;
            }

            var categories = new List<BuiltInCategory>();
            if (settings.IncludeGrids)
            {
                categories.Add(BuiltInCategory.OST_Grids);
            }
            if (settings.IncludeWalls)
            {
                categories.Add(BuiltInCategory.OST_Walls);
            }
            if (settings.IncludeColumns)
            {
                categories.Add(BuiltInCategory.OST_Columns);
                categories.Add(BuiltInCategory.OST_StructuralColumns);
            }
            if (settings.IncludePipes)
            {
                categories.Add(BuiltInCategory.OST_PipeCurves);
            }
            if (settings.IncludeDucts)
            {
                categories.Add(BuiltInCategory.OST_DuctCurves);
            }
            if (settings.IncludeSprinklers)
            {
                categories.Add(BuiltInCategory.OST_Sprinklers);
            }
            if (settings.IncludeMechanicalEquipment)
            {
                categories.Add(BuiltInCategory.OST_MechanicalEquipment);
            }
            if (categories.Count == 0)
            {
                return new List<Element>();
            }
            var filter = new ElementMulticategoryFilter(categories.Distinct().ToList());
            return new FilteredElementCollector(document, view.Id)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .Where(element => MatchesSettingsCategory(element, settings))
                .ToList();
        }

        private static List<Element> CollectPreselectedByLineScope(
            Document document,
            UIDocument uiDocument,
            AutoDimensionSettings settings)
        {
            return uiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .Where(element => MatchesSettingsCategory(element, settings))
                .GroupBy(element => element.Id.Value)
                .Select(elementGroup => elementGroup.First())
                .ToList();
        }

        private static bool IsSupportedByLineElement(Element element)
        {
            if (element == null || element.Category == null)
            {
                return false;
            }
            long category = element.Category.Id.Value;
            return category == (long)BuiltInCategory.OST_Grids ||
                category == (long)BuiltInCategory.OST_Walls ||
                category == (long)BuiltInCategory.OST_Columns ||
                category == (long)BuiltInCategory.OST_StructuralColumns ||
                category == (long)BuiltInCategory.OST_PipeCurves ||
                category == (long)BuiltInCategory.OST_DuctCurves ||
                category == (long)BuiltInCategory.OST_Sprinklers ||
                category == (long)BuiltInCategory.OST_MechanicalEquipment;
        }

        private static bool MatchesSettingsCategory(
            Element element,
            AutoDimensionSettings settings)
        {
            if (element == null || element.Category == null)
            {
                return false;
            }
            long category = element.Category.Id.Value;
            return (settings.IncludeGrids && category == (long)BuiltInCategory.OST_Grids) ||
                (settings.IncludeWalls && category == (long)BuiltInCategory.OST_Walls) ||
                (settings.IncludeColumns &&
                    (category == (long)BuiltInCategory.OST_Columns ||
                     category == (long)BuiltInCategory.OST_StructuralColumns)) ||
                (settings.IncludePipes && category == (long)BuiltInCategory.OST_PipeCurves) ||
                (settings.IncludeDucts && category == (long)BuiltInCategory.OST_DuctCurves) ||
                (settings.IncludeSprinklers && category == (long)BuiltInCategory.OST_Sprinklers) ||
                (settings.IncludeMechanicalEquipment &&
                    category == (long)BuiltInCategory.OST_MechanicalEquipment);
        }

        private static List<FamilyInstance> CollectSprinklers(
            Document document,
            UIDocument uiDocument)
        {
            return uiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .OfType<FamilyInstance>()
                .Where(IsSprinkler)
                .GroupBy(instance => instance.Id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static bool IsSprinkler(FamilyInstance instance)
        {
            return instance != null && instance.Category != null &&
                instance.Category.Id.Value == (long)BuiltInCategory.OST_Sprinklers;
        }

        private static List<DimensionCandidateReference> FilterCandidates(
            IEnumerable<DimensionCandidateReference> candidates,
            AutoDimensionReferenceMode mode,
            AutoDimensionStatistics statistics = null)
        {
            List<DimensionCandidateReference> usable = (candidates ??
                Enumerable.Empty<DimensionCandidateReference>())
                .Where(candidate => candidate.Reference != null && candidate.Anchor != null)
                .ToList();
            IEnumerable<DimensionCandidateReference> filtered = usable;
            if (mode == AutoDimensionReferenceMode.Center)
            {
                filtered = filtered.Where(candidate => candidate.Kind == DimensionReferenceKind.Center);
            }
            else if (mode == AutoDimensionReferenceMode.OuterFaces)
            {
                var selected = new List<DimensionCandidateReference>();
                foreach (IGrouping<long, DimensionCandidateReference> source in usable.GroupBy(
                    candidate => candidate.SourceElementId == null
                        ? long.MinValue
                        : candidate.SourceElementId.Value))
                {
                    List<DimensionCandidateReference> faces = source
                        .Where(candidate => candidate.Kind == DimensionReferenceKind.Face)
                        .ToList();
                    if (faces.Count > 0)
                    {
                        selected.AddRange(faces);
                        continue;
                    }

                    DimensionCandidateReference first = source.FirstOrDefault();
                    if (!IsMepCurveCandidate(first))
                    {
                        continue;
                    }
                    List<DimensionCandidateReference> centers = source
                        .Where(candidate => candidate.Kind == DimensionReferenceKind.Center)
                        .ToList();
                    if (centers.Count == 0)
                    {
                        continue;
                    }
                    selected.AddRange(centers);
                    if (statistics != null)
                    {
                        statistics.MepOuterFaceCenterFallbacks++;
                    }
                }
                filtered = selected;
            }
            return filtered
                .GroupBy(candidate => candidate.StableKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        private static bool IsMepCurveCandidate(DimensionCandidateReference candidate)
        {
            if (candidate == null || candidate.SourceCategoryId == null)
            {
                return false;
            }
            long category = candidate.SourceCategoryId.Value;
            return category == (long)BuiltInCategory.OST_PipeCurves ||
                category == (long)BuiltInCategory.OST_DuctCurves;
        }

        private static IDictionary<string, string> BuildDirectionGroups(
            IEnumerable<DimensionCandidateReference> candidates,
            View view)
        {
            var groups = new List<Point2>();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DimensionCandidateReference candidate in candidates)
            {
                Point2 normal = ToViewVector2(view, candidate.Normal);
                if (normal.Length <= 1e-8)
                {
                    result[candidate.StableKey] = "invalid";
                    continue;
                }
                normal = CanonicalDirection(normal);
                int groupIndex = -1;
                for (int index = 0; index < groups.Count; index++)
                {
                    if (Math.Abs(Point2.Dot(groups[index], normal)) >= 0.999999999999)
                    {
                        groupIndex = index;
                        break;
                    }
                }
                if (groupIndex < 0)
                {
                    groupIndex = groups.Count;
                    groups.Add(normal);
                }
                result[candidate.StableKey] = "direction-" + groupIndex;
            }
            return result;
        }

        private static List<DimensionCandidateReference> SortAndDedupeCandidates(
            IEnumerable<DimensionCandidateReference> candidates,
            View view,
            Point2 direction,
            double tolerance)
        {
            direction = CanonicalDirection(direction.Normalize());
            List<CandidateCoordinate> ordered = (candidates ??
                Enumerable.Empty<DimensionCandidateReference>())
                .Where(candidate => candidate != null && candidate.Reference != null && candidate.Anchor != null)
                .Select(candidate => new CandidateCoordinate
                {
                    Candidate = candidate,
                    Coordinate = Point2.Dot(ToViewPoint(view, candidate.Anchor), direction)
                })
                .OrderBy(item => item.Coordinate)
                .ThenBy(item => item.Candidate.StableKey, StringComparer.Ordinal)
                .ToList();
            var result = new List<DimensionCandidateReference>();
            int index = 0;
            while (index < ordered.Count)
            {
                CandidateCoordinate selected = ordered[index];
                double start = selected.Coordinate;
                index++;
                while (index < ordered.Count &&
                    ordered[index].Coordinate - start <= tolerance + 1e-9)
                {
                    if (selected.Candidate.Kind == DimensionReferenceKind.Face &&
                        ordered[index].Candidate.Kind == DimensionReferenceKind.Center)
                    {
                        selected = ordered[index];
                    }
                    index++;
                }
                result.Add(selected.Candidate);
            }
            return result;
        }

        private static HashSet<string> CollectExistingDimensionReferenceKeys(
            Document document,
            View view)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dimension dimension in new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>())
            {
                try
                {
                    string key = BuildReferenceSetKey(
                        document,
                        dimension.References.Cast<Reference>());
                    if (!string.IsNullOrEmpty(key))
                    {
                        keys.Add(key);
                    }
                }
                catch
                {
                    // A broken pre-existing dimension must not block the command.
                }
            }
            return keys;
        }

        private static string BuildReferenceSetKey(
            Document document,
            IEnumerable<Reference> references)
        {
            var keys = new List<string>();
            foreach (Reference reference in references ?? Enumerable.Empty<Reference>())
            {
                if (reference == null)
                {
                    continue;
                }
                try
                {
                    keys.Add(reference.ConvertToStableRepresentation(document));
                }
                catch
                {
                    keys.Add(reference.ElementId.Value + ":" + reference.ElementReferenceType);
                }
            }
            return keys.Count < 2
                ? string.Empty
                : string.Join("|", keys.OrderBy(key => key, StringComparer.Ordinal));
        }

        private static List<DimensionLane> CollectExistingDimensionLanes(
            Document document,
            View view)
        {
            var result = new List<DimensionLane>();
            foreach (Dimension dimension in new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>())
            {
                Line line = dimension.Curve as Line;
                if (line == null)
                {
                    continue;
                }
                XYZ origin;
                XYZ vector;
                try
                {
                    origin = line.Origin;
                    vector = line.Direction;
                }
                catch
                {
                    continue;
                }
                Point2 direction = ToViewVector2(view, vector);
                if (direction.Length > 1e-8)
                {
                    direction = CanonicalDirection(direction.Normalize());
                    double minimumAlong;
                    double maximumAlong;
                    if (!TryGetViewBoundingInterval(
                        dimension,
                        view,
                        direction,
                        out minimumAlong,
                        out maximumAlong))
                    {
                        minimumAlong = double.NegativeInfinity;
                        maximumAlong = double.PositiveInfinity;
                    }
                    result.Add(new DimensionLane(
                        direction,
                        ToViewPoint(view, origin),
                        minimumAlong,
                        maximumAlong));
                }
            }
            return result;
        }

        private static bool IsLaneOccupied(
            IEnumerable<DimensionLane> lanes,
            Point2 direction,
            Point2 point,
            double clearance,
            double minimumAlong,
            double maximumAlong)
        {
            Point2 normalized = CanonicalDirection(direction.Normalize());
            Point2 normal = new Point2(-normalized.Y, normalized.X);
            foreach (DimensionLane lane in lanes)
            {
                if (Math.Abs(Point2.Dot(normalized, lane.Direction)) < 0.995)
                {
                    continue;
                }
                if (maximumAlong < lane.MinimumAlong - clearance ||
                    minimumAlong > lane.MaximumAlong + clearance)
                {
                    continue;
                }
                double distance = Math.Abs(Point2.Dot(point - lane.Point, normal));
                if (distance < clearance)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetViewBoundingInterval(
            Element element,
            View view,
            Point2 direction,
            out double minimumAlong,
            out double maximumAlong)
        {
            minimumAlong = double.MaxValue;
            maximumAlong = double.MinValue;
            BoundingBoxXYZ bounds = null;
            try
            {
                bounds = element.get_BoundingBox(view);
            }
            catch
            {
                return false;
            }
            if (bounds == null)
            {
                return false;
            }
            foreach (XYZ rawCorner in CreateBoundingBoxCorners(bounds))
            {
                Point2 point = ToViewPoint(view, bounds.Transform.OfPoint(rawCorner));
                double coordinate = Point2.Dot(point, direction);
                minimumAlong = Math.Min(minimumAlong, coordinate);
                maximumAlong = Math.Max(maximumAlong, coordinate);
            }
            return minimumAlong <= maximumAlong;
        }

        private static IEnumerable<XYZ> CreateBoundingBoxCorners(BoundingBoxXYZ bounds)
        {
            XYZ minimum = bounds.Min;
            XYZ maximum = bounds.Max;
            return new[]
            {
                new XYZ(minimum.X, minimum.Y, minimum.Z),
                new XYZ(minimum.X, minimum.Y, maximum.Z),
                new XYZ(minimum.X, maximum.Y, minimum.Z),
                new XYZ(minimum.X, maximum.Y, maximum.Z),
                new XYZ(maximum.X, minimum.Y, minimum.Z),
                new XYZ(maximum.X, minimum.Y, maximum.Z),
                new XYZ(maximum.X, maximum.Y, minimum.Z),
                new XYZ(maximum.X, maximum.Y, maximum.Z)
            };
        }

        private static bool TryInferSelectionMeasurementDirection(
            IEnumerable<Element> elements,
            View view,
            out Point2 direction)
        {
            direction = new Point2(0.0, 0.0);
            var exactDirections = new List<Point2>();
            foreach (Element element in elements ?? Enumerable.Empty<Element>())
            {
                Point2 candidate;
                if (TryGetElementMeasurementDirection(element, view, out candidate))
                {
                    exactDirections.Add(CanonicalDirection(candidate));
                }
            }
            if (exactDirections.Count >= 2)
            {
                var groups = new List<List<Point2>>();
                foreach (Point2 candidate in exactDirections)
                {
                    List<Point2> group = groups.FirstOrDefault(existing =>
                        Math.Abs(Point2.Dot(existing[0], candidate)) >= 0.996194698092);
                    if (group == null)
                    {
                        group = new List<Point2>();
                        groups.Add(group);
                    }
                    if (group.Count > 0 && Point2.Dot(group[0], candidate) < 0.0)
                    {
                        group.Add(candidate * -1.0);
                    }
                    else
                    {
                        group.Add(candidate);
                    }
                }
                List<Point2> dominant = groups
                    .OrderByDescending(group => group.Count)
                    .First();
                direction = new Point2(
                    dominant.Average(item => item.X),
                    dominant.Average(item => item.Y));
                if (direction.Length > 1e-8)
                {
                    direction = direction.Normalize();
                    return true;
                }
            }

            List<Point2> anchors = (elements ?? Enumerable.Empty<Element>())
                .Select(element =>
                {
                    XYZ anchor;
                    return TryGetElementAnchor(element, view, out anchor)
                        ? (Point2?)ToViewPoint(view, anchor)
                        : null;
                })
                .Where(anchor => anchor.HasValue)
                .Select(anchor => anchor.Value)
                .ToList();
            double maximumDistance = 0.0;
            for (int first = 0; first < anchors.Count; first++)
            {
                for (int second = first + 1; second < anchors.Count; second++)
                {
                    Point2 vector = anchors[second] - anchors[first];
                    double distance = vector.Length;
                    if (distance > maximumDistance)
                    {
                        maximumDistance = distance;
                        direction = vector;
                    }
                }
            }
            if (direction.Length <= 1e-8)
            {
                return false;
            }
            direction = direction.Normalize();
            return true;
        }

        private static bool TryGetElementMeasurementDirection(
            Element element,
            View view,
            out Point2 measurementDirection)
        {
            measurementDirection = new Point2(0.0, 0.0);
            Curve curve = null;
            Grid grid = element as Grid;
            if (grid != null)
            {
                try
                {
                    curve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view)
                        .FirstOrDefault(candidate => candidate is Line);
                }
                catch
                {
                }
                if (curve == null)
                {
                    try
                    {
                        curve = grid.Curve;
                    }
                    catch
                    {
                    }
                }
            }
            else
            {
                LocationCurve location = element == null ? null : element.Location as LocationCurve;
                curve = location == null ? null : location.Curve;
            }
            Line line = curve as Line;
            if (line == null)
            {
                return false;
            }
            Point2 along = ToViewPoint(view, line.GetEndPoint(1)) -
                ToViewPoint(view, line.GetEndPoint(0));
            if (along.Length <= 1e-8)
            {
                return false;
            }
            along = along.Normalize();
            measurementDirection = new Point2(-along.Y, along.X);
            return true;
        }

        private static ElementId TryCreateScanLinePreview(
            Document document,
            View view,
            Point2 start,
            Point2 end,
            out string failure)
        {
            failure = string.Empty;
            using (var transaction = new Transaction(
                document,
                "Tung MEP - Show Auto Dimension scan line"))
            {
                try
                {
                    transaction.Start();
                    DetailCurve preview = document.Create.NewDetailCurve(
                        view,
                        Line.CreateBound(
                            FromViewPoint(view, start),
                            FromViewPoint(view, end)));
                    if (preview == null || !preview.IsValidObject)
                    {
                        throw new InvalidOperationException(
                            "Revit did not create the view-specific preview curve.");
                    }
                    var overrides = new OverrideGraphicSettings();
                    overrides.SetProjectionLineColor(new Autodesk.Revit.DB.Color(0, 255, 255));
                    overrides.SetProjectionLineWeight(8);
                    view.SetElementOverrides(preview.Id, overrides);
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException(
                            "Revit did not commit the preview transaction.");
                    }
                    return preview.Id;
                }
                catch (Exception exception)
                {
                    try
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started)
                        {
                            transaction.RollBack();
                        }
                    }
                    catch
                    {
                    }
                    failure = exception.Message;
                    return ElementId.InvalidElementId;
                }
            }
        }

        private static bool TryDeleteScanLinePreview(
            Document document,
            ElementId previewId,
            out string failure)
        {
            failure = string.Empty;
            if (previewId == null || previewId == ElementId.InvalidElementId ||
                document.GetElement(previewId) == null)
            {
                return true;
            }
            using (var transaction = new Transaction(
                document,
                "Tung MEP - Hide Auto Dimension scan line"))
            {
                try
                {
                    transaction.Start();
                    document.Delete(previewId);
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException(
                            "Revit did not commit the preview cleanup transaction.");
                    }
                    return true;
                }
                catch (Exception exception)
                {
                    try
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started)
                        {
                            transaction.RollBack();
                        }
                    }
                    catch
                    {
                    }
                    failure = exception.Message;
                    return false;
                }
            }
        }

        private static Point2 DetectSprinklerBranchDirection(
            Document document,
            View view,
            IList<SprinklerPoint> sprinklers,
            double tolerance)
        {
            double pipeTolerance = Math.Max(
                UnitUtils.ConvertToInternalUnits(250.0, UnitTypeId.Millimeters),
                tolerance * 10.0);
            var pipeLines = new List<ProjectedPipeLine>();
            foreach (Pipe pipe in new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>())
            {
                LocationCurve location = pipe.Location as LocationCurve;
                Line line = location == null ? null : location.Curve as Line;
                if (line == null)
                {
                    continue;
                }
                Point2 start = ToViewPoint(view, line.GetEndPoint(0));
                Point2 end = ToViewPoint(view, line.GetEndPoint(1));
                Point2 vector = end - start;
                double length = vector.Length;
                if (length <= 1e-8)
                {
                    continue;
                }
                pipeLines.Add(new ProjectedPipeLine(
                    start,
                    end,
                    CanonicalDirection(vector)));
            }

            var directionGroups = new List<ProjectedPipeDirectionGroup>();
            foreach (ProjectedPipeLine pipeLine in pipeLines)
            {
                ProjectedPipeDirectionGroup group = directionGroups
                    .FirstOrDefault(candidate => Math.Abs(Point2.Dot(
                        candidate.Direction,
                        pipeLine.Direction)) >= 0.999999);
                if (group == null)
                {
                    group = new ProjectedPipeDirectionGroup(pipeLine.Direction);
                    directionGroups.Add(group);
                }
                group.Lines.Add(pipeLine);
            }

            List<ProjectedPipeDirectionGroup> scoredGroups = directionGroups
                .Select(group =>
                {
                    group.Lanes.AddRange(BuildDistinctPipeLanes(
                        group.Lines,
                        group.Direction,
                        pipeTolerance));
                    group.HeadHits = sprinklers.Count(point => group.Lines.Any(line =>
                        DistanceToSegment(point.Point, line.Start, line.End) <= pipeTolerance));
                    group.MultiHeadRuns = group.Lanes.Count(lane => sprinklers.Count(point =>
                        lane.Lines.Any(line => DistanceToSegment(
                            point.Point,
                            line.Start,
                            line.End) <= pipeTolerance)) >= 2);
                    return group;
                })
                .OrderByDescending(group => group.HeadHits)
                .ThenByDescending(group => group.MultiHeadRuns)
                .ThenByDescending(group => group.Lanes.Count)
                .ToList();
            ProjectedPipeDirectionGroup bestGroup = scoredGroups.FirstOrDefault();
            int requiredHits = Math.Max(
                2,
                (int)Math.Ceiling(sprinklers.Count * 0.5));
            int requiredRuns = sprinklers.Count <= 4 ? 1 : 2;
            bool ambiguous = scoredGroups.Count > 1 && bestGroup != null &&
                scoredGroups[1].HeadHits == bestGroup.HeadHits &&
                scoredGroups[1].MultiHeadRuns == bestGroup.MultiHeadRuns;
            if (bestGroup != null && bestGroup.HeadHits >= requiredHits &&
                bestGroup.MultiHeadRuns >= requiredRuns && !ambiguous)
            {
                return bestGroup.Direction;
            }

            // Without a trustworthy pipe direction, the caller asks the user for two
            // direction points.  This keeps rotated and pipe-less layouts deterministic.
            return new Point2(0.0, 0.0);
        }

        private static IList<ProjectedPipeLane> BuildDistinctPipeLanes(
            IEnumerable<ProjectedPipeLine> lines,
            Point2 direction,
            double offsetTolerance)
        {
            direction = CanonicalDirection(direction);
            Point2 normal = new Point2(-direction.Y, direction.X);
            var lanes = new List<ProjectedPipeLane>();
            foreach (ProjectedPipeLine line in lines ?? Enumerable.Empty<ProjectedPipeLine>())
            {
                double offset = (Point2.Dot(line.Start, normal) +
                    Point2.Dot(line.End, normal)) * 0.5;
                ProjectedPipeLane lane = lanes
                    .OrderBy(candidate => Math.Abs(candidate.Offset - offset))
                    .FirstOrDefault(candidate =>
                        Math.Abs(candidate.Offset - offset) <= offsetTolerance);
                if (lane == null)
                {
                    lane = new ProjectedPipeLane(offset);
                    lanes.Add(lane);
                }
                lane.Add(line, offset);
            }
            return lanes;
        }

        private static Point2 CanonicalDirection(Point2 direction)
        {
            direction = direction.Normalize();
            if (direction.Y < -1e-9 ||
                (Math.Abs(direction.Y) <= 1e-9 && direction.X < 0.0))
            {
                return direction * -1.0;
            }
            return direction;
        }

        private static double DistanceToSegment(Point2 point, Point2 start, Point2 end)
        {
            Point2 vector = end - start;
            double lengthSquared = Point2.Dot(vector, vector);
            if (lengthSquared <= 1e-16)
            {
                return point.DistanceTo(start);
            }
            double parameter = Point2.Dot(point - start, vector) / lengthSquared;
            parameter = Math.Max(0.0, Math.Min(1.0, parameter));
            return point.DistanceTo(start + (vector * parameter));
        }

        private static bool TryGetElementAnchor(
            Element element,
            View view,
            out XYZ anchor)
        {
            anchor = null;
            LocationPoint pointLocation = element.Location as LocationPoint;
            if (pointLocation != null && pointLocation.Point != null)
            {
                anchor = pointLocation.Point;
                return true;
            }
            LocationCurve curveLocation = element.Location as LocationCurve;
            if (curveLocation != null && curveLocation.Curve != null)
            {
                try
                {
                    anchor = curveLocation.Curve.Evaluate(0.5, true);
                    if (anchor != null)
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
            BoundingBoxXYZ bounds = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bounds == null)
            {
                return false;
            }
            anchor = (bounds.Min + bounds.Max) * 0.5;
            return true;
        }

        private static DimensionType ResolveDimensionType(
            Document document,
            IEnumerable<DimensionType> dimensionTypes,
            AutoDimensionSettings settings)
        {
            DimensionType selected = document.GetElement(
                new ElementId(settings.SelectedDimensionTypeId)) as DimensionType;
            if (selected != null && selected.StyleType == DimensionStyleType.Linear)
            {
                return selected;
            }
            selected = dimensionTypes.FirstOrDefault(type => string.Equals(
                type.Name,
                settings.SelectedDimensionTypeName,
                StringComparison.CurrentCultureIgnoreCase));
            return selected ?? dimensionTypes.FirstOrDefault();
        }

        private static bool IsSupportedView(Document document, View view)
        {
            if (document == null || document.IsFamilyDocument || view == null || view.IsTemplate)
            {
                return false;
            }
            return view.ViewType == ViewType.FloorPlan ||
                view.ViewType == ViewType.CeilingPlan ||
                view.ViewType == ViewType.EngineeringPlan ||
                view.ViewType == ViewType.Section ||
                view.ViewType == ViewType.Elevation ||
                view.ViewType == ViewType.Detail;
        }

        private static double PaperMillimetersToInternal(double paperMillimeters, View view)
        {
            int scale = view == null || view.Scale < 1 ? 1 : view.Scale;
            return UnitUtils.ConvertToInternalUnits(
                paperMillimeters * scale,
                UnitTypeId.Millimeters);
        }

        private static Point2 ToViewPoint(View view, XYZ point)
        {
            XYZ origin = GetViewOrigin(view);
            XYZ delta = point - origin;
            return new Point2(
                delta.DotProduct(view.RightDirection),
                delta.DotProduct(view.UpDirection));
        }

        private static XYZ FromViewPoint(View view, Point2 point)
        {
            return GetViewOrigin(view) +
                (view.RightDirection * point.X) +
                (view.UpDirection * point.Y);
        }

        private static XYZ ToViewVector(View view, Point2 vector)
        {
            return ((view.RightDirection * vector.X) +
                (view.UpDirection * vector.Y)).Normalize();
        }

        private static Point2 ToViewVector2(View view, XYZ vector)
        {
            return new Point2(
                vector.DotProduct(view.RightDirection),
                vector.DotProduct(view.UpDirection));
        }

        private static XYZ GetViewOrigin(View view)
        {
            try
            {
                return view.Origin ?? XYZ.Zero;
            }
            catch
            {
                return XYZ.Zero;
            }
        }

        private static Point2 Midpoint(Point2 first, Point2 second)
        {
            return (first + second) * 0.5;
        }

        private static void SetFirstFailure(
            AutoDimensionStatistics statistics,
            string failure)
        {
            if (statistics != null && string.IsNullOrEmpty(statistics.FirstFailure) &&
                !string.IsNullOrWhiteSpace(failure))
            {
                statistics.FirstFailure = failure.Trim();
            }
        }

        private static string BuildReport(AutoDimensionStatistics statistics)
        {
            string report =
                "Native dimensions created: " + statistics.Created + "." +
                "\nDimension chains skipped: " + statistics.SkippedChains + "." +
                "\nDimension chains failed: " + statistics.FailedChains + "." +
                "\nDuplicate dimensions skipped: " + statistics.DuplicateDimensionsSkipped + "." +
                "\nScope elements checked: " + statistics.ScopeElements + "." +
                "\nCompatible reference candidates evaluated: " + statistics.ReferenceCandidates + "." +
                "\nScope elements without a usable Revit Reference: " +
                statistics.ReferenceSourceSkipped + ".";
            if (statistics.MepOuterFaceCenterFallbacks > 0)
            {
                report += "\nRound/unsupported MEP outer faces using centerline: " +
                    statistics.MepOuterFaceCenterFallbacks + ".";
            }
            report += "\n\nExisting dimensions were not deleted or modified.";
            if (!string.IsNullOrEmpty(statistics.FirstFailure))
            {
                report += "\n\nFirst issue: " + statistics.FirstFailure;
            }
            return report;
        }

        private sealed class SprinklerSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return IsSprinkler(element as FamilyInstance);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class ByLineElementSelectionFilter : ISelectionFilter
        {
            private readonly AutoDimensionSettings _settings;

            public ByLineElementSelectionFilter(AutoDimensionSettings settings)
            {
                _settings = settings;
            }

            public bool AllowElement(Element element)
            {
                return MatchesSettingsCategory(element, _settings);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class DimensionPlacementPreviewOverlay : IDisposable
        {
            private readonly System.Drawing.Rectangle _bounds;
            private readonly List<System.Drawing.PointF> _anchors;
            private readonly System.Drawing.PointF _direction;
            private readonly List<string> _segmentLabels;
            private readonly string _overallLabel;
            private readonly bool _showChain;
            private readonly bool _showOverall;
            private readonly float _overallOffsetPixels;
            private readonly System.Drawing.Color _previewColor;
            private readonly System.Threading.ManualResetEvent _ready;
            private readonly System.Threading.Thread _thread;
            private volatile DimensionPreviewForm _form;
            private volatile bool _available;
            private volatile bool _disposed;

            private DimensionPlacementPreviewOverlay(
                System.Drawing.Rectangle bounds,
                IEnumerable<System.Drawing.PointF> anchors,
                System.Drawing.PointF direction,
                IEnumerable<string> segmentLabels,
                string overallLabel,
                bool showChain,
                bool showOverall,
                float overallOffsetPixels,
                System.Drawing.Color previewColor)
            {
                _bounds = bounds;
                _anchors = anchors.ToList();
                _direction = direction;
                _segmentLabels = segmentLabels.ToList();
                _overallLabel = overallLabel ?? string.Empty;
                _showChain = showChain;
                _showOverall = showOverall;
                _overallOffsetPixels = overallOffsetPixels;
                _previewColor = previewColor;
                _ready = new System.Threading.ManualResetEvent(false);
                _thread = new System.Threading.Thread(RunOverlay)
                {
                    IsBackground = true,
                    Name = "TungMEP Dimension Placement Preview"
                };
                _thread.SetApartmentState(System.Threading.ApartmentState.STA);
                _thread.Start();
                _ready.WaitOne(1500);
            }

            public static DimensionPlacementPreviewOverlay TryShow(
                UIDocument uiDocument,
                Document document,
                View view,
                IEnumerable<DimensionCandidateReference> candidates,
                Point2 modelDirection,
                AutoDimensionSettings settings)
            {
                try
                {
                    if (uiDocument == null || document == null || view == null ||
                        candidates == null || settings == null ||
                        modelDirection.Length <= 1e-8)
                    {
                        return null;
                    }
                    UIView uiView = uiDocument.GetOpenUIViews()
                        .FirstOrDefault(candidate => candidate.ViewId == view.Id);
                    if (uiView == null)
                    {
                        return null;
                    }
                    var window = uiView.GetWindowRectangle();
                    int left = Math.Min(window.Left, window.Right);
                    int top = Math.Min(window.Top, window.Bottom);
                    int width = Math.Abs(window.Right - window.Left);
                    int height = Math.Abs(window.Bottom - window.Top);
                    if (width < 20 || height < 20)
                    {
                        return null;
                    }
                    IList<XYZ> zoomCorners = uiView.GetZoomCorners();
                    if (zoomCorners == null || zoomCorners.Count < 2)
                    {
                        return null;
                    }
                    List<Point2> projectedCorners = zoomCorners
                        .Select(corner => ToViewPoint(view, corner))
                        .ToList();
                    double minimumX = projectedCorners.Min(corner => corner.X);
                    double maximumX = projectedCorners.Max(corner => corner.X);
                    double minimumY = projectedCorners.Min(corner => corner.Y);
                    double maximumY = projectedCorners.Max(corner => corner.Y);
                    if (maximumX - minimumX <= 1e-8 || maximumY - minimumY <= 1e-8)
                    {
                        return null;
                    }

                    Func<Point2, System.Drawing.PointF> map = point =>
                        new System.Drawing.PointF(
                            (float)((point.X - minimumX) / (maximumX - minimumX) * width),
                            (float)(height -
                                ((point.Y - minimumY) / (maximumY - minimumY) * height)));
                    Point2 normalizedModelDirection = modelDirection.Normalize();
                    double previewDedupeTolerance = UnitUtils.ConvertToInternalUnits(
                        0.5,
                        UnitTypeId.Millimeters);
                    List<Point2> orderedModelAnchors = candidates
                        .Where(candidate => candidate != null && candidate.Anchor != null)
                        .Select(candidate => ToViewPoint(view, candidate.Anchor))
                        .OrderBy(anchor => Point2.Dot(anchor, normalizedModelDirection))
                        .ToList();
                    var dedupedModelAnchors = new List<Point2>();
                    foreach (Point2 anchor in orderedModelAnchors)
                    {
                        if (dedupedModelAnchors.Count == 0 || Math.Abs(
                            Point2.Dot(anchor, normalizedModelDirection) -
                            Point2.Dot(
                                dedupedModelAnchors[dedupedModelAnchors.Count - 1],
                                normalizedModelDirection)) > previewDedupeTolerance)
                        {
                            dedupedModelAnchors.Add(anchor);
                        }
                    }
                    List<System.Drawing.PointF> screenAnchors = dedupedModelAnchors
                        .Select(map)
                        .ToList();
                    if (screenAnchors.Count < 2)
                    {
                        return null;
                    }
                    System.Drawing.PointF directionStart = map(new Point2(0.0, 0.0));
                    System.Drawing.PointF directionEnd = map(normalizedModelDirection);
                    float directionX = directionEnd.X - directionStart.X;
                    float directionY = directionEnd.Y - directionStart.Y;
                    float directionLength = (float)Math.Sqrt(
                        (directionX * directionX) + (directionY * directionY));
                    if (directionLength <= 0.001F)
                    {
                        return null;
                    }
                    var segmentLabels = new List<string>();
                    for (int index = 1; index < dedupedModelAnchors.Count; index++)
                    {
                        double length = Math.Abs(
                            Point2.Dot(
                                dedupedModelAnchors[index],
                                normalizedModelDirection) -
                            Point2.Dot(
                                dedupedModelAnchors[index - 1],
                                normalizedModelDirection));
                        segmentLabels.Add(FormatPreviewLength(document, length));
                    }
                    double overallLength = Math.Abs(
                        Point2.Dot(
                            dedupedModelAnchors[dedupedModelAnchors.Count - 1],
                            normalizedModelDirection) -
                        Point2.Dot(
                            dedupedModelAnchors[0],
                            normalizedModelDirection));
                    bool showChain = settings.CreateChain;
                    bool showOverall = settings.CreateOverall &&
                        (!settings.CreateChain || dedupedModelAnchors.Count > 2);
                    Point2 modelNormal = new Point2(
                        -normalizedModelDirection.Y,
                        normalizedModelDirection.X);
                    double laneStep = PaperMillimetersToInternal(
                        settings.LaneStepPaperMm,
                        view);
                    System.Drawing.PointF laneStart = map(new Point2(0.0, 0.0));
                    System.Drawing.PointF laneEnd = map(modelNormal * laneStep);
                    float overallOffsetPixels = (float)Math.Sqrt(
                        Math.Pow(laneEnd.X - laneStart.X, 2.0) +
                        Math.Pow(laneEnd.Y - laneStart.Y, 2.0));
                    var overlay = new DimensionPlacementPreviewOverlay(
                        new System.Drawing.Rectangle(left, top, width, height),
                        screenAnchors,
                        new System.Drawing.PointF(
                            directionX / directionLength,
                            directionY / directionLength),
                        segmentLabels,
                        FormatPreviewLength(document, overallLength),
                        showChain,
                        showOverall,
                        Math.Max(18.0F, overallOffsetPixels),
                        DetectPreviewColor(new System.Drawing.Rectangle(
                            left,
                            top,
                            width,
                            height)));
                    if (!overlay._available)
                    {
                        overlay.Dispose();
                        return null;
                    }
                    return overlay;
                }
                catch
                {
                    return null;
                }
            }

            private static string FormatPreviewLength(
                Document document,
                double internalLength)
            {
                try
                {
                    string formatted = UnitFormatUtils.Format(
                        document.GetUnits(),
                        SpecTypeId.Length,
                        internalLength,
                        false);
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        return formatted;
                    }
                }
                catch
                {
                }
                return Math.Round(UnitUtils.ConvertFromInternalUnits(
                    internalLength,
                    UnitTypeId.Millimeters)).ToString("0") + " mm";
            }

            private static System.Drawing.Color DetectPreviewColor(
                System.Drawing.Rectangle bounds)
            {
                try
                {
                    int lightSamples = 0;
                    int sampleCount = 0;
                    float[] fractions = { 0.15F, 0.35F, 0.50F, 0.65F, 0.85F };
                    using (var pixel = new System.Drawing.Bitmap(1, 1))
                    using (System.Drawing.Graphics graphics =
                        System.Drawing.Graphics.FromImage(pixel))
                    {
                        foreach (float horizontal in fractions)
                        {
                            foreach (float vertical in fractions)
                            {
                                int x = bounds.Left + (int)(bounds.Width * horizontal);
                                int y = bounds.Top + (int)(bounds.Height * vertical);
                                graphics.CopyFromScreen(x, y, 0, 0,
                                    new System.Drawing.Size(1, 1));
                                System.Drawing.Color color = pixel.GetPixel(0, 0);
                                double luminance = (0.2126 * color.R) +
                                    (0.7152 * color.G) +
                                    (0.0722 * color.B);
                                if (luminance >= 145.0)
                                {
                                    lightSamples++;
                                }
                                sampleCount++;
                            }
                        }
                    }
                    return lightSamples * 2 >= sampleCount
                        ? System.Drawing.Color.FromArgb(92, 92, 92)
                        : System.Drawing.Color.FromArgb(195, 195, 195);
                }
                catch
                {
                    return System.Drawing.Color.FromArgb(150, 150, 150);
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                DimensionPreviewForm form = _form;
                if (form != null)
                {
                    try
                    {
                        form.BeginInvoke(new Action(form.Close));
                    }
                    catch
                    {
                    }
                }
                try
                {
                    if (_thread.IsAlive &&
                        System.Threading.Thread.CurrentThread != _thread)
                    {
                        _thread.Join(1500);
                    }
                }
                catch
                {
                }
            }

            private void RunOverlay()
            {
                try
                {
                    var form = new DimensionPreviewForm(
                        _bounds,
                        _anchors,
                        _direction,
                        _segmentLabels,
                        _overallLabel,
                        _showChain,
                        _showOverall,
                        _overallOffsetPixels,
                        _previewColor);
                    _form = form;
                    form.HandleCreated += delegate
                    {
                        if (_disposed)
                        {
                            form.BeginInvoke(new Action(form.Close));
                            _ready.Set();
                            return;
                        }
                        _available = true;
                        _ready.Set();
                    };
                    form.FormClosed += delegate { _available = false; };
                    if (form.IsHandleCreated)
                    {
                        _available = !_disposed;
                        _ready.Set();
                    }
                    if (_disposed)
                    {
                        form.Dispose();
                        return;
                    }
                    WinForms.Application.Run(form);
                }
                catch
                {
                    _available = false;
                }
                finally
                {
                    _ready.Set();
                    _form = null;
                }
            }

            private sealed class DimensionPreviewForm : WinForms.Form
            {
                private const int ExtendedStyleTransparent = 0x00000020;
                private const int ExtendedStyleToolWindow = 0x00000080;
                private const int ExtendedStyleLayered = 0x00080000;
                private const int ExtendedStyleNoActivate = 0x08000000;
                private const int WindowMessageNonClientHitTest = 0x0084;
                private static readonly IntPtr HitTestTransparent = new IntPtr(-1);

                private readonly List<System.Drawing.PointF> _anchors;
                private readonly System.Drawing.PointF _direction;
                private readonly List<string> _segmentLabels;
                private readonly string _overallLabel;
                private readonly bool _showChain;
                private readonly bool _showOverall;
                private readonly float _overallOffsetPixels;
                private readonly System.Drawing.Color _previewColor;
                private readonly WinForms.Timer _timer;

                public DimensionPreviewForm(
                    System.Drawing.Rectangle bounds,
                    IEnumerable<System.Drawing.PointF> anchors,
                    System.Drawing.PointF direction,
                    IEnumerable<string> segmentLabels,
                    string overallLabel,
                    bool showChain,
                    bool showOverall,
                    float overallOffsetPixels,
                    System.Drawing.Color previewColor)
                {
                    _anchors = anchors.ToList();
                    _direction = direction;
                    _segmentLabels = segmentLabels.ToList();
                    _overallLabel = overallLabel ?? string.Empty;
                    _showChain = showChain;
                    _showOverall = showOverall;
                    _overallOffsetPixels = overallOffsetPixels;
                    _previewColor = previewColor;
                    FormBorderStyle = WinForms.FormBorderStyle.None;
                    ShowInTaskbar = false;
                    StartPosition = WinForms.FormStartPosition.Manual;
                    Bounds = bounds;
                    TopMost = true;
                    BackColor = System.Drawing.Color.Magenta;
                    TransparencyKey = System.Drawing.Color.Magenta;
                    Opacity = 1.0;
                    DoubleBuffered = true;
                    _timer = new WinForms.Timer { Interval = 20 };
                    _timer.Tick += delegate { Invalidate(); };
                    _timer.Start();
                }

                protected override WinForms.CreateParams CreateParams
                {
                    get
                    {
                        WinForms.CreateParams parameters = base.CreateParams;
                        parameters.ExStyle |= ExtendedStyleTransparent |
                            ExtendedStyleToolWindow |
                            ExtendedStyleLayered |
                            ExtendedStyleNoActivate;
                        return parameters;
                    }
                }

                protected override bool ShowWithoutActivation
                {
                    get { return true; }
                }

                protected override void WndProc(ref WinForms.Message message)
                {
                    if (message.Msg == WindowMessageNonClientHitTest)
                    {
                        message.Result = HitTestTransparent;
                        return;
                    }
                    base.WndProc(ref message);
                }

                protected override void OnPaint(WinForms.PaintEventArgs args)
                {
                    base.OnPaint(args);
                    if (_anchors.Count < 2)
                    {
                        return;
                    }
                    args.Graphics.SmoothingMode =
                        System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    System.Drawing.Point cursor = WinForms.Cursor.Position;
                    var cursorPoint = new System.Drawing.PointF(
                        cursor.X - Left,
                        cursor.Y - Top);
                    var normal = new System.Drawing.PointF(-_direction.Y, _direction.X);
                    float lineNormalCoordinate = Dot(cursorPoint, normal);
                    if (_showChain)
                    {
                        DrawGhostDimension(
                            args.Graphics,
                            _anchors,
                            _segmentLabels,
                            lineNormalCoordinate,
                            _previewColor,
                            _previewColor,
                            normal);
                    }
                    if (_showOverall)
                    {
                        float averageAnchorCoordinate = _anchors.Average(anchor =>
                            Dot(anchor, normal));
                        float sign = lineNormalCoordinate >= averageAnchorCoordinate
                            ? 1.0F
                            : -1.0F;
                        float overallCoordinate = _showChain
                            ? lineNormalCoordinate + (sign * _overallOffsetPixels)
                            : lineNormalCoordinate;
                        DrawGhostDimension(
                            args.Graphics,
                            new[] { _anchors[0], _anchors[_anchors.Count - 1] },
                            new[] { _overallLabel },
                            overallCoordinate,
                            _previewColor,
                            _previewColor,
                            normal);
                    }
                }

                private void DrawGhostDimension(
                    System.Drawing.Graphics graphics,
                    IList<System.Drawing.PointF> anchors,
                    IList<string> labels,
                    float lineNormalCoordinate,
                    System.Drawing.Color lineColor,
                    System.Drawing.Color textColor,
                    System.Drawing.PointF normal)
                {
                    if (anchors == null || anchors.Count < 2)
                    {
                        return;
                    }
                    float minimumAlong = anchors.Min(anchor => Dot(anchor, _direction));
                    float maximumAlong = anchors.Max(anchor => Dot(anchor, _direction));
                    const float padding = 8.0F;
                    System.Drawing.PointF lineStart = Add(
                        Scale(_direction, minimumAlong - padding),
                        Scale(normal, lineNormalCoordinate));
                    System.Drawing.PointF lineEnd = Add(
                        Scale(_direction, maximumAlong + padding),
                        Scale(normal, lineNormalCoordinate));
                    using (var dimensionPen = new System.Drawing.Pen(lineColor, 1.35F))
                    using (var witnessPen = new System.Drawing.Pen(lineColor, 1.0F))
                    using (var markerBrush = new System.Drawing.SolidBrush(lineColor))
                    {
                        dimensionPen.DashStyle =
                            System.Drawing.Drawing2D.DashStyle.Solid;
                        witnessPen.DashStyle =
                            System.Drawing.Drawing2D.DashStyle.Solid;
                        graphics.DrawLine(dimensionPen, lineStart, lineEnd);
                        foreach (System.Drawing.PointF anchor in anchors)
                        {
                            float delta = lineNormalCoordinate - Dot(anchor, normal);
                            System.Drawing.PointF witnessEnd = Add(
                                anchor,
                                Scale(normal, delta));
                            graphics.DrawLine(witnessPen, anchor, witnessEnd);
                            const float markerRadius = 3.0F;
                            graphics.FillEllipse(
                                markerBrush,
                                witnessEnd.X - markerRadius,
                                witnessEnd.Y - markerRadius,
                                markerRadius * 2.0F,
                                markerRadius * 2.0F);
                        }
                    }
                    float averageAnchorNormal = anchors.Average(anchor =>
                        Dot(anchor, normal));
                    float textSide = lineNormalCoordinate >= averageAnchorNormal
                        ? 1.0F
                        : -1.0F;
                    float textNormalCoordinate = lineNormalCoordinate +
                        (textSide * 13.0F);
                    for (int index = 1; index < anchors.Count; index++)
                    {
                        string label = labels != null && index - 1 < labels.Count
                            ? labels[index - 1]
                            : string.Empty;
                        if (string.IsNullOrEmpty(label))
                        {
                            continue;
                        }
                        float midpointAlong = (Dot(anchors[index - 1], _direction) +
                            Dot(anchors[index], _direction)) * 0.5F;
                        System.Drawing.PointF textPoint = Add(
                            Scale(_direction, midpointAlong),
                            Scale(normal, textNormalCoordinate));
                        DrawRotatedText(graphics, label, textPoint, textColor);
                    }
                }

                private void DrawRotatedText(
                    System.Drawing.Graphics graphics,
                    string text,
                    System.Drawing.PointF point,
                    System.Drawing.Color color)
                {
                    float angle = (float)(Math.Atan2(
                        _direction.Y,
                        _direction.X) * 180.0 / Math.PI);
                    if (angle > 90.0F)
                    {
                        angle -= 180.0F;
                    }
                    else if (angle < -90.0F)
                    {
                        angle += 180.0F;
                    }
                    System.Drawing.Drawing2D.GraphicsState state = graphics.Save();
                    try
                    {
                        graphics.TranslateTransform(point.X, point.Y);
                        graphics.RotateTransform(angle);
                        using (var font = new System.Drawing.Font(
                            "Segoe UI",
                            10.5F,
                            System.Drawing.FontStyle.Regular))
                        using (var textBrush = new System.Drawing.SolidBrush(color))
                        {
                            System.Drawing.SizeF size = graphics.MeasureString(text, font);
                            graphics.DrawString(
                                text,
                                font,
                                textBrush,
                                -size.Width * 0.5F,
                                -size.Height * 0.5F);
                        }
                    }
                    finally
                    {
                        graphics.Restore(state);
                    }
                }

                protected override void Dispose(bool disposing)
                {
                    if (disposing && _timer != null)
                    {
                        _timer.Stop();
                        _timer.Dispose();
                    }
                    base.Dispose(disposing);
                }

                private static float Dot(
                    System.Drawing.PointF first,
                    System.Drawing.PointF second)
                {
                    return (first.X * second.X) + (first.Y * second.Y);
                }

                private static System.Drawing.PointF Add(
                    System.Drawing.PointF first,
                    System.Drawing.PointF second)
                {
                    return new System.Drawing.PointF(
                        first.X + second.X,
                        first.Y + second.Y);
                }

                private static System.Drawing.PointF Scale(
                    System.Drawing.PointF point,
                    float scale)
                {
                    return new System.Drawing.PointF(
                        point.X * scale,
                        point.Y * scale);
                }
            }
        }

        private sealed class SprinklerPoint
        {
            public SprinklerPoint(FamilyInstance element, XYZ anchor, Point2 point)
            {
                Element = element;
                Anchor = anchor;
                Point = point;
            }

            public FamilyInstance Element { get; private set; }
            public XYZ Anchor { get; private set; }
            public Point2 Point { get; private set; }
        }

        private sealed class SprinklerRegionPlan
        {
            public SprinklerRegionPlan(
                IList<SprinklerPoint> points,
                Point2 branchDirection)
            {
                Points = points;
                BranchDirection = branchDirection.Normalize();
                CrossDirection = new Point2(
                    -BranchDirection.Y,
                    BranchDirection.X);
            }

            public IList<SprinklerPoint> Points { get; private set; }
            public Point2 BranchDirection { get; private set; }
            public Point2 CrossDirection { get; private set; }
        }

        private sealed class DimensionLane
        {
            public DimensionLane(
                Point2 direction,
                Point2 point,
                double minimumAlong,
                double maximumAlong)
            {
                Direction = direction;
                Point = point;
                MinimumAlong = minimumAlong;
                MaximumAlong = maximumAlong;
            }

            public Point2 Direction { get; private set; }
            public Point2 Point { get; private set; }
            public double MinimumAlong { get; private set; }
            public double MaximumAlong { get; private set; }
        }

        private sealed class CandidateCoordinate
        {
            public DimensionCandidateReference Candidate { get; set; }
            public double Coordinate { get; set; }
        }

        private sealed class ProjectedPipeLine
        {
            public ProjectedPipeLine(Point2 start, Point2 end, Point2 direction)
            {
                Start = start;
                End = end;
                Direction = direction;
            }

            public Point2 Start { get; private set; }
            public Point2 End { get; private set; }
            public Point2 Direction { get; private set; }
        }

        private sealed class ProjectedPipeDirectionGroup
        {
            public ProjectedPipeDirectionGroup(Point2 direction)
            {
                Direction = direction;
                Lines = new List<ProjectedPipeLine>();
                Lanes = new List<ProjectedPipeLane>();
            }

            public Point2 Direction { get; private set; }
            public List<ProjectedPipeLine> Lines { get; private set; }
            public List<ProjectedPipeLane> Lanes { get; private set; }
            public int HeadHits { get; set; }
            public int MultiHeadRuns { get; set; }
        }

        private sealed class ProjectedPipeLane
        {
            private double _offsetTotal;

            public ProjectedPipeLane(double offset)
            {
                Offset = offset;
                _offsetTotal = offset;
                Lines = new List<ProjectedPipeLine>();
            }

            public double Offset { get; private set; }
            public List<ProjectedPipeLine> Lines { get; private set; }

            public void Add(ProjectedPipeLine line, double offset)
            {
                Lines.Add(line);
                if (Lines.Count == 1)
                {
                    Offset = offset;
                    _offsetTotal = offset;
                    return;
                }
                _offsetTotal += offset;
                Offset = _offsetTotal / Lines.Count;
            }
        }

        private sealed class AutoDimensionFailurePreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                if (failures.Any(failure =>
                    failure.GetSeverity() == FailureSeverity.Error ||
                    failure.GetSeverity() == FailureSeverity.DocumentCorruption))
                {
                    return FailureProcessingResult.ProceedWithRollBack;
                }
                return FailureProcessingResult.Continue;
            }
        }

        private sealed class AutoDimensionStatistics
        {
            public int Created { get; set; }
            public int SkippedChains { get; set; }
            public int FailedChains { get; set; }
            public int DuplicateDimensionsSkipped { get; set; }
            public int ScopeElements { get; set; }
            public int ReferenceCandidates { get; set; }
            public int ReferenceSourceSkipped { get; set; }
            public int MepOuterFaceCenterFallbacks { get; set; }
            public bool UserCancelled { get; set; }
            public string FirstFailure { get; set; }

            public bool HasReportableResult
            {
                get
                {
                    return Created > 0 || SkippedChains > 0 || FailedChains > 0 ||
                        DuplicateDimensionsSkipped > 0 || ReferenceCandidates > 0 ||
                        ScopeElements > 0 || ReferenceSourceSkipped > 0 ||
                        MepOuterFaceCenterFallbacks > 0 ||
                        !string.IsNullOrEmpty(FirstFailure);
                }
            }
        }
    }
}
