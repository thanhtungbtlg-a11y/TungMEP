using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;

namespace TungMEP.Modules.MepTools
{
    [Transaction(TransactionMode.Manual)]
    public class MepSplitCommand : IExternalCommand
    {
        // Retained for binary/source compatibility with the legacy dialog that is no longer launched.
        internal const int MaximumPiecesPerCurve = 500;
        private const string ToolTitle = "Tung MEP - MEP SLIPT BY LEVEL";

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            if (document == null || document.IsFamilyDocument)
            {
                TaskDialog.Show(ToolTitle, "Open a Revit project before running MEP SLIPT BY LEVEL.");
                return Result.Cancelled;
            }

            List<ElementId> sourceIds = CollectPreselectedSources(document, uiDocument);
            if (sourceIds.Count == 0)
            {
                try
                {
                    sourceIds = uiDocument.Selection
                        .PickObjects(
                            ObjectType.Element,
                            new SplitByLevelSelectionFilter(),
                            "Select straight, unpinned Pipe, Duct, Conduit, or Cable Tray elements")
                        .Select(reference => reference.ElementId)
                        .GroupBy(id => id.Value)
                        .Select(group => group.First())
                        .ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }

            if (sourceIds.Count == 0)
            {
                TaskDialog.Show(ToolTitle, "No supported MEP curves were selected.");
                return Result.Cancelled;
            }

            double minimumSegmentLength = Math.Max(
                UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters),
                commandData.Application.Application.ShortCurveTolerance * 2.0);
            double pointTolerance = Math.Max(
                UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters),
                commandData.Application.Application.ShortCurveTolerance * 2.0);

            List<SplitLevelOption> candidateLevels = CollectCandidateLevels(
                document,
                sourceIds,
                minimumSegmentLength,
                pointTolerance);
            if (candidateLevels.Count == 0)
            {
                TaskDialog.Show(
                    ToolTitle,
                    "None of the selected straight MEP curves crosses an interior Level plane.\n\n" +
                    "Horizontal curves and curves that only end at a Level do not require a split.");
                return Result.Cancelled;
            }

            using (var dialog = new MepSplitByLevelForm(candidateLevels))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                List<SplitLevelOption> selectedLevels = dialog.SelectedLevels;
                if (selectedLevels.Count == 0)
                {
                    return Result.Cancelled;
                }

                double fittingClearance = UnitUtils.ConvertToInternalUnits(
                    dialog.FittingClearanceMm,
                    UnitTypeId.Millimeters);
                double connectorTolerance = Math.Max(
                    UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters),
                    commandData.Application.Application.ShortCurveTolerance * 4.0);
                List<FittingEnvelope> fittingEnvelopes = CollectFittingEnvelopes(document);

                int splitSources = 0;
                int unchangedSources = 0;
                int failedSources = 0;
                int insulatedSourcesSkipped = 0;
                int plannedIntersections = 0;
                int splitIntersections = 0;
                int fittingIntersectionsSkipped = 0;
                int createdUnions = 0;
                int failedUnions = 0;
                int alreadyConnectedBreaks = 0;
                var skippedFittingIds = new HashSet<long>();
                var levelsActuallySplit = new HashSet<long>();
                string firstFailure = string.Empty;

                using (var transaction = new Transaction(document, ToolTitle))
                {
                    transaction.Start();
                    foreach (ElementId sourceId in sourceIds)
                    {
                        Element source = document.GetElement(sourceId);
                        Line sourceLine;
                        if (!TryGetSupportedLine(source, out sourceLine))
                        {
                            unchangedSources++;
                            continue;
                        }
                        if (HasInsulationOrLining(document, source.Id))
                        {
                            unchangedSources++;
                            insulatedSourcesSkipped++;
                            continue;
                        }

                        List<PlannedLevelBreak> plannedBreaks = BuildLevelBreaks(
                            sourceLine,
                            selectedLevels,
                            minimumSegmentLength,
                            pointTolerance);
                        plannedIntersections += plannedBreaks.Count;
                        if (plannedBreaks.Count == 0)
                        {
                            unchangedSources++;
                            continue;
                        }

                        var allowedBreaks = new List<PlannedLevelBreak>();
                        foreach (PlannedLevelBreak plannedBreak in plannedBreaks)
                        {
                            FittingEnvelope blocker = fittingEnvelopes.FirstOrDefault(
                                envelope => envelope.Blocks(
                                    plannedBreak.Point,
                                    plannedBreak.ProjectElevation,
                                    fittingClearance));
                            if (blocker != null)
                            {
                                fittingIntersectionsSkipped++;
                                skippedFittingIds.Add(blocker.ElementId.Value);
                                continue;
                            }
                            allowedBreaks.Add(plannedBreak);
                        }
                        if (allowedBreaks.Count == 0)
                        {
                            unchangedSources++;
                            continue;
                        }

                        using (var sourceTransaction = new SubTransaction(document))
                        {
                            sourceTransaction.Start();
                            try
                            {
                                int sourceCreatedUnions = 0;
                                int sourceFailedUnions = 0;
                                int sourceAlreadyConnectedBreaks = 0;
                                var liveSegmentIds = new List<ElementId> { sourceId };
                                var actualBreaks = new List<PlannedLevelBreak>();

                                foreach (PlannedLevelBreak plannedBreak in allowedBreaks)
                                {
                                    ElementId segmentId;
                                    XYZ pointOnSegment;
                                    if (!TryFindSegmentAtPoint(
                                        document,
                                        liveSegmentIds,
                                        plannedBreak.Point,
                                        minimumSegmentLength,
                                        pointTolerance,
                                        out segmentId,
                                        out pointOnSegment))
                                    {
                                        throw new InvalidOperationException(
                                            "A live MEP segment could not be found at Level " +
                                            plannedBreak.LevelNames + ".");
                                    }

                                    Element segment = document.GetElement(segmentId);
                                    ElementId newSegmentId = BreakCurve(
                                        document,
                                        segment,
                                        pointOnSegment,
                                        connectorTolerance);
                                    if (newSegmentId == null ||
                                        newSegmentId == ElementId.InvalidElementId ||
                                        document.GetElement(newSegmentId) == null)
                                    {
                                        throw new InvalidOperationException(
                                            "Revit did not return a new segment at Level " +
                                            plannedBreak.LevelNames + ".");
                                    }
                                    if (!liveSegmentIds.Any(id => id.Value == newSegmentId.Value))
                                    {
                                        liveSegmentIds.Add(newSegmentId);
                                    }
                                    actualBreaks.Add(new PlannedLevelBreak(
                                        pointOnSegment,
                                        plannedBreak.Station,
                                        plannedBreak.ProjectElevation,
                                        plannedBreak.LevelNames,
                                        plannedBreak.LevelIds));
                                    document.Regenerate();
                                }

                                if (dialog.InsertUnions)
                                {
                                    foreach (PlannedLevelBreak actualBreak in actualBreaks)
                                    {
                                        Connector first;
                                        Connector second;
                                        bool alreadyConnected;
                                        if (!TryFindConnectorPair(
                                            document,
                                            liveSegmentIds,
                                            actualBreak.Point,
                                            connectorTolerance,
                                            out first,
                                            out second,
                                            out alreadyConnected))
                                        {
                                            if (alreadyConnected)
                                            {
                                                sourceAlreadyConnectedBreaks++;
                                            }
                                            else
                                            {
                                                sourceFailedUnions++;
                                            }
                                            continue;
                                        }

                                        using (var unionTransaction = new SubTransaction(document))
                                        {
                                            unionTransaction.Start();
                                            try
                                            {
                                                FamilyInstance union = document.Create.NewUnionFitting(first, second);
                                                if (union == null || !union.IsValidObject)
                                                {
                                                    throw new InvalidOperationException(
                                                        "Revit did not create a union fitting.");
                                                }
                                                unionTransaction.Commit();
                                                sourceCreatedUnions++;
                                                document.Regenerate();
                                            }
                                            catch
                                            {
                                                try { unionTransaction.RollBack(); }
                                                catch { }
                                                sourceFailedUnions++;
                                            }
                                        }
                                    }
                                }

                                sourceTransaction.Commit();
                                splitSources++;
                                splitIntersections += actualBreaks.Count;
                                createdUnions += sourceCreatedUnions;
                                failedUnions += sourceFailedUnions;
                                alreadyConnectedBreaks += sourceAlreadyConnectedBreaks;
                                foreach (PlannedLevelBreak actualBreak in actualBreaks)
                                {
                                    foreach (long levelId in actualBreak.LevelIds)
                                    {
                                        levelsActuallySplit.Add(levelId);
                                    }
                                }
                            }
                            catch (Exception exception)
                            {
                                try { sourceTransaction.RollBack(); }
                                catch { }
                                failedSources++;
                                SetFirstFailure(ref firstFailure, source, exception.Message);
                            }
                        }
                    }
                    transaction.Commit();
                }

                var result = new System.Text.StringBuilder();
                result.AppendLine("MEP curves split by Level: " + splitSources + ".");
                result.AppendLine("New MEP segments: " + splitIntersections + ".");
                result.AppendLine("Selected Levels used: " + levelsActuallySplit.Count +
                    " / " + selectedLevels.SelectMany(level => level.LevelIds).Distinct().Count() + ".");
                result.AppendLine("Level intersections evaluated: " + plannedIntersections + ".");
                result.AppendLine("Level intersections skipped at fittings/accessories: " +
                    fittingIntersectionsSkipped + ".");
                if (skippedFittingIds.Count > 0)
                {
                    result.AppendLine("Touching fittings/accessories protected: " +
                        skippedFittingIds.Count + ".");
                }
                if (unchangedSources > 0)
                {
                    result.AppendLine("Curves unchanged: " + unchangedSources + ".");
                }
                if (failedSources > 0)
                {
                    result.AppendLine("Curves not split because of errors: " + failedSources + ".");
                }
                if (insulatedSourcesSkipped > 0)
                {
                    result.AppendLine("Insulated/lined Pipe or Duct curves skipped: " +
                        insulatedSourcesSkipped + ".");
                }
                if (dialog.InsertUnions)
                {
                    result.AppendLine("Union fittings created: " + createdUnions + ".");
                    if (alreadyConnectedBreaks > 0)
                    {
                        result.AppendLine("Breaks already connected: " + alreadyConnectedBreaks + ".");
                    }
                    if (failedUnions > 0)
                    {
                        result.AppendLine("Union fittings not created: " + failedUnions +
                            " (check the selected MEP type routing preferences)." );
                    }
                }
                if (!string.IsNullOrEmpty(firstFailure))
                {
                    result.AppendLine();
                    result.AppendLine("First failure: " + firstFailure);
                }
                TaskDialog.Show(ToolTitle, result.ToString());
                return splitSources > 0 ? Result.Succeeded : Result.Cancelled;
            }
        }

        private static List<ElementId> CollectPreselectedSources(
            Document document,
            UIDocument uiDocument)
        {
            return uiDocument.Selection.GetElementIds()
                .Where(id => IsSupportedSource(document.GetElement(id)))
                .GroupBy(id => id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static bool IsSupportedSource(Element element)
        {
            Line line;
            return TryGetSupportedLine(element, out line);
        }

        private static bool TryGetSupportedLine(Element element, out Line line)
        {
            line = null;
            if (!(element is Pipe) &&
                !(element is Duct) &&
                !(element is Conduit) &&
                !(element is CableTray))
            {
                return false;
            }
            if (element.Pinned || element.GroupId != ElementId.InvalidElementId)
            {
                return false;
            }
            LocationCurve location = element.Location as LocationCurve;
            line = location == null ? null : location.Curve as Line;
            return line != null && line.IsBound && line.Length > 1e-8;
        }

        private static bool HasInsulationOrLining(Document document, ElementId elementId)
        {
            try
            {
                return InsulationLiningBase.GetInsulationIds(document, elementId).Count > 0 ||
                    InsulationLiningBase.GetLiningIds(document, elementId).Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static List<SplitLevelOption> CollectCandidateLevels(
            Document document,
            IList<ElementId> sourceIds,
            double minimumEndDistance,
            double zTolerance)
        {
            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderByDescending(level => level.ProjectElevation)
                .ThenBy(level => level.Name)
                .ToList();
            var result = new List<SplitLevelOption>();
            foreach (Level level in levels)
            {
                bool crosses = sourceIds.Any(sourceId =>
                {
                    Line line;
                    double station;
                    return TryGetSupportedLine(document.GetElement(sourceId), out line) &&
                        TryGetStationAtElevation(
                            line,
                            level.ProjectElevation,
                            minimumEndDistance,
                            zTolerance,
                            out station);
                });
                if (crosses)
                {
                    result.Add(new SplitLevelOption(level));
                }
            }
            return result;
        }

        private static bool TryGetStationAtElevation(
            Line line,
            double projectElevation,
            double minimumEndDistance,
            double zTolerance,
            out double station)
        {
            station = 0.0;
            XYZ start = line.GetEndPoint(0);
            XYZ end = line.GetEndPoint(1);
            double deltaZ = end.Z - start.Z;
            if (Math.Abs(deltaZ) <= zTolerance)
            {
                return false;
            }
            double parameter = (projectElevation - start.Z) / deltaZ;
            if (parameter <= 0.0 || parameter >= 1.0)
            {
                return false;
            }
            station = line.Length * parameter;
            return station >= minimumEndDistance &&
                station <= line.Length - minimumEndDistance;
        }

        private static List<PlannedLevelBreak> BuildLevelBreaks(
            Line line,
            IList<SplitLevelOption> selectedLevels,
            double minimumEndDistance,
            double zTolerance)
        {
            var raw = new List<PlannedLevelBreak>();
            XYZ start = line.GetEndPoint(0);
            XYZ direction = (line.GetEndPoint(1) - start).Normalize();
            foreach (SplitLevelOption level in selectedLevels)
            {
                double station;
                if (!TryGetStationAtElevation(
                    line,
                    level.ProjectElevation,
                    minimumEndDistance,
                    zTolerance,
                    out station))
                {
                    continue;
                }
                raw.Add(new PlannedLevelBreak(
                    start + direction * station,
                    station,
                    level.ProjectElevation,
                    level.LevelNames,
                    level.LevelIds));
            }

            var grouped = new List<PlannedLevelBreak>();
            foreach (PlannedLevelBreak candidate in raw.OrderByDescending(item => item.Station))
            {
                PlannedLevelBreak existing = grouped.FirstOrDefault(
                    item => Math.Abs(item.ProjectElevation - candidate.ProjectElevation) <= zTolerance);
                if (existing == null)
                {
                    grouped.Add(candidate);
                }
                else
                {
                    existing.Merge(candidate);
                }
            }
            return grouped.OrderByDescending(item => item.Station).ToList();
        }

        private static bool TryFindSegmentAtPoint(
            Document document,
            IList<ElementId> liveSegmentIds,
            XYZ plannedPoint,
            double minimumEndDistance,
            double pointTolerance,
            out ElementId segmentId,
            out XYZ pointOnSegment)
        {
            segmentId = ElementId.InvalidElementId;
            pointOnSegment = null;
            double bestDistance = double.MaxValue;
            foreach (ElementId candidateId in liveSegmentIds)
            {
                Element candidate = document.GetElement(candidateId);
                Line line;
                if (!TryGetSupportedLine(candidate, out line))
                {
                    continue;
                }
                IntersectionResult projection = line.Project(plannedPoint);
                if (projection == null || projection.XYZPoint == null)
                {
                    continue;
                }
                XYZ projected = projection.XYZPoint;
                double distance = projected.DistanceTo(plannedPoint);
                if (distance > pointTolerance ||
                    projected.DistanceTo(line.GetEndPoint(0)) < minimumEndDistance ||
                    projected.DistanceTo(line.GetEndPoint(1)) < minimumEndDistance)
                {
                    continue;
                }
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    segmentId = candidateId;
                    pointOnSegment = projected;
                }
            }
            return segmentId != ElementId.InvalidElementId && pointOnSegment != null;
        }

        private static ElementId BreakCurve(
            Document document,
            Element segment,
            XYZ point,
            double connectorTolerance)
        {
            if (segment is Pipe)
            {
                return PlumbingUtils.BreakCurve(document, segment.Id, point);
            }
            if (segment is Duct)
            {
                return MechanicalUtils.BreakCurve(document, segment.Id, point);
            }
            if (segment is Conduit || segment is CableTray)
            {
                return BreakElectricalCurve(document, segment as MEPCurve, point, connectorTolerance);
            }
            throw new InvalidOperationException("Unsupported MEP curve type.");
        }

        private static ElementId BreakElectricalCurve(
            Document document,
            MEPCurve segment,
            XYZ point,
            double connectorTolerance)
        {
            if (segment == null)
            {
                throw new InvalidOperationException("The electrical MEP curve is unavailable.");
            }
            LocationCurve sourceLocation = segment.Location as LocationCurve;
            Line sourceLine = sourceLocation == null ? null : sourceLocation.Curve as Line;
            if (sourceLine == null)
            {
                throw new InvalidOperationException("Only straight Conduit and Cable Tray are supported.");
            }
            XYZ start = sourceLine.GetEndPoint(0);
            XYZ end = sourceLine.GetEndPoint(1);
            Connector originalEnd = FindEndConnector(segment, end, connectorTolerance);
            List<ExternalConnectorTarget> farTargets = CaptureExternalTargets(
                originalEnd,
                segment.Id);
            DisconnectTargets(originalEnd, farTargets);

            ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElement(
                document,
                segment.Id,
                XYZ.Zero);
            ElementId copiedId = copiedIds.FirstOrDefault();
            MEPCurve copied = copiedId == null ? null : document.GetElement(copiedId) as MEPCurve;
            LocationCurve copiedLocation = copied == null ? null : copied.Location as LocationCurve;
            if (copied == null || copiedLocation == null)
            {
                throw new InvalidOperationException("Revit could not duplicate the electrical MEP curve.");
            }

            sourceLocation.Curve = Line.CreateBound(start, point);
            copiedLocation.Curve = Line.CreateBound(point, end);
            document.Regenerate();

            if (farTargets.Count > 0)
            {
                Connector copiedEnd = FindEndConnector(copied, end, connectorTolerance);
                if (copiedEnd == null)
                {
                    throw new InvalidOperationException(
                        "The copied electrical segment has no connector at its original far end.");
                }
                RestoreExternalTargets(document, copiedEnd, farTargets, connectorTolerance);
            }
            return copied.Id;
        }

        private static Connector FindEndConnector(
            MEPCurve curve,
            XYZ point,
            double tolerance)
        {
            if (curve == null || curve.ConnectorManager == null)
            {
                return null;
            }
            Connector best = null;
            double bestDistance = double.MaxValue;
            foreach (Connector connector in curve.ConnectorManager.Connectors)
            {
                if (connector == null || connector.ConnectorType != ConnectorType.End)
                {
                    continue;
                }
                double distance = connector.Origin.DistanceTo(point);
                if (distance <= tolerance && distance < bestDistance)
                {
                    best = connector;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static List<ExternalConnectorTarget> CaptureExternalTargets(
            Connector connector,
            ElementId ownerId)
        {
            var result = new List<ExternalConnectorTarget>();
            if (connector == null)
            {
                return result;
            }
            try
            {
                foreach (Connector reference in connector.AllRefs)
                {
                    if (reference == null || reference.Owner == null ||
                        reference.Owner.Id.Value == ownerId.Value ||
                        reference.ConnectorType == ConnectorType.Logical)
                    {
                        continue;
                    }
                    result.Add(new ExternalConnectorTarget(
                        reference.Owner.Id,
                        reference.Origin,
                        reference.Domain,
                        reference.Shape));
                }
            }
            catch
            {
                // The curve can still be split when the far end is open.
            }
            return result;
        }

        private static void DisconnectTargets(
            Connector connector,
            IList<ExternalConnectorTarget> targets)
        {
            if (connector == null || targets.Count == 0)
            {
                return;
            }
            var references = new List<Connector>();
            try
            {
                foreach (Connector reference in connector.AllRefs)
                {
                    if (reference != null && reference.Owner != null &&
                        targets.Any(target => target.OwnerId.Value == reference.Owner.Id.Value))
                    {
                        references.Add(reference);
                    }
                }
            }
            catch
            {
                return;
            }
            foreach (Connector reference in references)
            {
                try { connector.DisconnectFrom(reference); }
                catch { }
            }
        }

        private static void RestoreExternalTargets(
            Document document,
            Connector curveEnd,
            IList<ExternalConnectorTarget> targets,
            double tolerance)
        {
            foreach (ExternalConnectorTarget target in targets)
            {
                Element owner = document.GetElement(target.OwnerId);
                Connector external = FindConnector(owner, target, tolerance);
                if (external == null)
                {
                    throw new InvalidOperationException(
                        "Could not restore an existing Conduit/Cable Tray endpoint connection.");
                }
                try
                {
                    if (!curveEnd.IsConnectedTo(external))
                    {
                        curveEnd.ConnectTo(external);
                    }
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "Could not restore an electrical endpoint connection: " + exception.Message);
                }
            }
        }

        private static Connector FindConnector(
            Element element,
            ExternalConnectorTarget target,
            double tolerance)
        {
            ConnectorManager manager = GetConnectorManager(element);
            if (manager == null)
            {
                return null;
            }
            Connector best = null;
            double bestDistance = double.MaxValue;
            foreach (Connector connector in manager.Connectors)
            {
                if (connector == null || connector.ConnectorType == ConnectorType.Logical ||
                    connector.Domain != target.Domain || connector.Shape != target.Shape)
                {
                    continue;
                }
                double distance = connector.Origin.DistanceTo(target.Origin);
                if (distance <= tolerance && distance < bestDistance)
                {
                    best = connector;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static ConnectorManager GetConnectorManager(Element element)
        {
            MEPCurve curve = element as MEPCurve;
            if (curve != null)
            {
                return curve.ConnectorManager;
            }
            FamilyInstance family = element as FamilyInstance;
            return family == null || family.MEPModel == null
                ? null
                : family.MEPModel.ConnectorManager;
        }

        private static List<FittingEnvelope> CollectFittingEnvelopes(Document document)
        {
            BuiltInCategory[] categories =
            {
                BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_ConduitFitting,
                BuiltInCategory.OST_CableTrayFitting
            };
            var result = new List<FittingEnvelope>();
            var seen = new HashSet<long>();
            foreach (BuiltInCategory category in categories)
            {
                foreach (Element element in new FilteredElementCollector(document)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToElements())
                {
                    if (!seen.Add(element.Id.Value))
                    {
                        continue;
                    }
                    BoundingBoxXYZ box = element.get_BoundingBox(null);
                    FittingEnvelope envelope;
                    if (TryCreateEnvelope(element, box, out envelope))
                    {
                        result.Add(envelope);
                    }
                }
            }
            return result;
        }

        private static bool TryCreateEnvelope(
            Element element,
            BoundingBoxXYZ box,
            out FittingEnvelope envelope)
        {
            envelope = null;
            if (element == null || box == null)
            {
                return false;
            }
            Transform transform = box.Transform ?? Transform.Identity;
            double minimumX = double.MaxValue;
            double minimumY = double.MaxValue;
            double minimumZ = double.MaxValue;
            double maximumX = double.MinValue;
            double maximumY = double.MinValue;
            double maximumZ = double.MinValue;
            for (int x = 0; x <= 1; x++)
            {
                for (int y = 0; y <= 1; y++)
                {
                    for (int z = 0; z <= 1; z++)
                    {
                        XYZ corner = transform.OfPoint(new XYZ(
                            x == 0 ? box.Min.X : box.Max.X,
                            y == 0 ? box.Min.Y : box.Max.Y,
                            z == 0 ? box.Min.Z : box.Max.Z));
                        minimumX = Math.Min(minimumX, corner.X);
                        minimumY = Math.Min(minimumY, corner.Y);
                        minimumZ = Math.Min(minimumZ, corner.Z);
                        maximumX = Math.Max(maximumX, corner.X);
                        maximumY = Math.Max(maximumY, corner.Y);
                        maximumZ = Math.Max(maximumZ, corner.Z);
                    }
                }
            }
            envelope = new FittingEnvelope(
                element.Id,
                minimumX,
                minimumY,
                minimumZ,
                maximumX,
                maximumY,
                maximumZ);
            return true;
        }

        private static bool TryFindConnectorPair(
            Document document,
            IList<ElementId> liveSegmentIds,
            XYZ splitPoint,
            double tolerance,
            out Connector first,
            out Connector second,
            out bool alreadyConnected)
        {
            first = null;
            second = null;
            alreadyConnected = false;
            var connectors = new List<Connector>();
            foreach (ElementId id in liveSegmentIds)
            {
                MEPCurve curve = document.GetElement(id) as MEPCurve;
                if (curve == null || curve.ConnectorManager == null)
                {
                    continue;
                }
                foreach (Connector connector in curve.ConnectorManager.Connectors)
                {
                    if (connector != null && connector.ConnectorType == ConnectorType.End &&
                        connector.Origin.DistanceTo(splitPoint) <= tolerance)
                    {
                        connectors.Add(connector);
                    }
                }
            }

            double bestScore = double.MaxValue;
            for (int firstIndex = 0; firstIndex < connectors.Count; firstIndex++)
            {
                Connector left = connectors[firstIndex];
                for (int secondIndex = firstIndex + 1; secondIndex < connectors.Count; secondIndex++)
                {
                    Connector right = connectors[secondIndex];
                    if (left.Owner == null || right.Owner == null ||
                        left.Owner.Id.Value == right.Owner.Id.Value ||
                        left.Domain != right.Domain || left.Shape != right.Shape ||
                        left.Origin.DistanceTo(right.Origin) > tolerance)
                    {
                        continue;
                    }
                    try
                    {
                        if (left.IsConnectedTo(right))
                        {
                            alreadyConnected = true;
                            return false;
                        }
                    }
                    catch { }
                    if (left.IsConnected || right.IsConnected)
                    {
                        continue;
                    }
                    double alignmentPenalty = GetAlignmentPenalty(left, right);
                    if (alignmentPenalty >= 1.5)
                    {
                        continue;
                    }
                    double score = left.Origin.DistanceTo(splitPoint) +
                        right.Origin.DistanceTo(splitPoint) +
                        left.Origin.DistanceTo(right.Origin) + alignmentPenalty * tolerance;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        first = left;
                        second = right;
                    }
                }
            }
            return first != null && second != null;
        }

        private static double GetAlignmentPenalty(Connector first, Connector second)
        {
            try
            {
                XYZ firstDirection = first.CoordinateSystem.BasisZ.Normalize();
                XYZ secondDirection = second.CoordinateSystem.BasisZ.Normalize();
                return Math.Abs(firstDirection.DotProduct(secondDirection) + 1.0);
            }
            catch
            {
                return 0.0;
            }
        }

        private static void SetFirstFailure(
            ref string firstFailure,
            Element source,
            string detail)
        {
            if (!string.IsNullOrEmpty(firstFailure))
            {
                return;
            }
            firstFailure = (source == null ? "MEP curve" : "Element " + source.Id.Value) +
                ": " + detail;
        }

        private sealed class SplitByLevelSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return IsSupportedSource(element);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class PlannedLevelBreak
        {
            public PlannedLevelBreak(
                XYZ point,
                double station,
                double projectElevation,
                string levelNames,
                IEnumerable<long> levelIds)
            {
                Point = point;
                Station = station;
                ProjectElevation = projectElevation;
                LevelNames = levelNames;
                LevelIds = new List<long>(levelIds ?? new long[0]);
            }

            public XYZ Point { get; private set; }
            public double Station { get; private set; }
            public double ProjectElevation { get; private set; }
            public string LevelNames { get; private set; }
            public List<long> LevelIds { get; private set; }

            public void Merge(PlannedLevelBreak other)
            {
                if (other == null)
                {
                    return;
                }
                LevelNames = string.Join(" / ", new[] { LevelNames, other.LevelNames }
                    .Where(value => !string.IsNullOrEmpty(value))
                    .Distinct());
                foreach (long id in other.LevelIds)
                {
                    if (!LevelIds.Contains(id))
                    {
                        LevelIds.Add(id);
                    }
                }
            }
        }

        private sealed class ExternalConnectorTarget
        {
            public ExternalConnectorTarget(
                ElementId ownerId,
                XYZ origin,
                Domain domain,
                ConnectorProfileType shape)
            {
                OwnerId = ownerId;
                Origin = origin;
                Domain = domain;
                Shape = shape;
            }

            public ElementId OwnerId { get; private set; }
            public XYZ Origin { get; private set; }
            public Domain Domain { get; private set; }
            public ConnectorProfileType Shape { get; private set; }
        }

        private sealed class FittingEnvelope
        {
            public FittingEnvelope(
                ElementId elementId,
                double minimumX,
                double minimumY,
                double minimumZ,
                double maximumX,
                double maximumY,
                double maximumZ)
            {
                ElementId = elementId;
                MinimumX = minimumX;
                MinimumY = minimumY;
                MinimumZ = minimumZ;
                MaximumX = maximumX;
                MaximumY = maximumY;
                MaximumZ = maximumZ;
            }

            public ElementId ElementId { get; private set; }
            private double MinimumX { get; set; }
            private double MinimumY { get; set; }
            private double MinimumZ { get; set; }
            private double MaximumX { get; set; }
            private double MaximumY { get; set; }
            private double MaximumZ { get; set; }

            public bool Blocks(XYZ point, double projectElevation, double clearance)
            {
                return point.X >= MinimumX - clearance && point.X <= MaximumX + clearance &&
                    point.Y >= MinimumY - clearance && point.Y <= MaximumY + clearance &&
                    projectElevation >= MinimumZ - clearance &&
                    projectElevation <= MaximumZ + clearance;
            }
        }
    }

    internal sealed class SplitLevelOption
    {
        public SplitLevelOption(Level level)
        {
            ProjectElevation = level.ProjectElevation;
            LevelNames = level.Name;
            LevelIds = new List<long> { level.Id.Value };
            double millimetres = UnitUtils.ConvertFromInternalUnits(
                ProjectElevation,
                UnitTypeId.Millimeters);
            DisplayName = level.Name + "  (" + millimetres.ToString("+0;-0;0") + " mm)";
        }

        public double ProjectElevation { get; private set; }
        public string LevelNames { get; private set; }
        public List<long> LevelIds { get; private set; }
        public string DisplayName { get; private set; }
        public override string ToString() { return DisplayName; }
    }

    internal sealed class MepSplitByLevelForm : Form
    {
        private readonly CheckedListBox _levels;
        private readonly NumericUpDown _clearance;
        private readonly CheckBox _insertUnions;
        private readonly Label _validation;

        public MepSplitByLevelForm(IList<SplitLevelOption> levels)
        {
            Text = "Tung MEP - MEP SLIPT BY LEVEL";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(570, 510);

            Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Location = new Point(18, 18),
                Text = "Select Level boundaries to split the selected MEP curves"
            });
            Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = System.Drawing.Color.FromArgb(70, 70, 70),
                Location = new Point(18, 43),
                Text = "Only Levels crossing the interior of at least one selected curve are listed."
            });

            _levels = new CheckedListBox
            {
                CheckOnClick = true,
                IntegralHeight = false,
                Location = new Point(18, 70),
                Size = new Size(534, 255)
            };
            foreach (SplitLevelOption level in levels)
            {
                _levels.Items.Add(level, true);
            }
            _levels.ItemCheck += delegate { BeginInvoke(new Action(UpdateValidation)); };
            Controls.Add(_levels);

            var checkAll = new Button
            {
                Text = "Check all",
                Location = new Point(18, 334),
                Size = new Size(90, 27)
            };
            checkAll.Click += delegate
            {
                for (int index = 0; index < _levels.Items.Count; index++)
                {
                    _levels.SetItemChecked(index, true);
                }
            };
            var uncheckAll = new Button
            {
                Text = "Uncheck all",
                Location = new Point(116, 334),
                Size = new Size(90, 27)
            };
            uncheckAll.Click += delegate
            {
                for (int index = 0; index < _levels.Items.Count; index++)
                {
                    _levels.SetItemChecked(index, false);
                }
            };
            Controls.Add(checkAll);
            Controls.Add(uncheckAll);

            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(18, 382),
                Text = "Fitting/accessory safety clearance (mm)"
            });
            _clearance = new NumericUpDown
            {
                Location = new Point(330, 378),
                Size = new Size(120, 24),
                Minimum = 0,
                Maximum = 2000,
                Value = 50,
                DecimalPlaces = 0,
                ThousandsSeparator = true
            };
            Controls.Add(_clearance);

            _insertUnions = new CheckBox
            {
                AutoSize = true,
                Location = new Point(18, 416),
                Text = "Insert union fittings at successful split points",
                Checked = true
            };
            Controls.Add(_insertUnions);

            _validation = new Label
            {
                AutoSize = false,
                ForeColor = System.Drawing.Color.Firebrick,
                Location = new Point(18, 445),
                Size = new Size(320, 34)
            };
            Controls.Add(_validation);

            var split = new Button
            {
                Text = "Split by Level",
                Location = new Point(354, 462),
                Size = new Size(105, 30)
            };
            split.Click += delegate
            {
                if (SelectedLevels.Count == 0)
                {
                    UpdateValidation();
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(467, 462),
                Size = new Size(85, 30)
            };
            Controls.Add(split);
            Controls.Add(cancel);
            AcceptButton = split;
            CancelButton = cancel;
            UpdateValidation();
        }

        public List<SplitLevelOption> SelectedLevels
        {
            get { return _levels.CheckedItems.Cast<SplitLevelOption>().ToList(); }
        }

        public double FittingClearanceMm
        {
            get { return Decimal.ToDouble(_clearance.Value); }
        }

        public bool InsertUnions
        {
            get { return _insertUnions.Checked; }
        }

        private void UpdateValidation()
        {
            _validation.Text = SelectedLevels.Count == 0
                ? "Select at least one Level."
                : string.Empty;
        }
    }
}
