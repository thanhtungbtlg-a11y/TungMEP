using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TungMEP.Modules.Dimensions;

namespace TungMEP.Modules.Tags
{
    internal sealed class MepDocumentationDimensionResult
    {
        public int GridDimensionsCreated { get; set; }
        public int ElevationDimensionsCreated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string FirstIssue { get; set; }
    }

    /// <summary>
    /// Adds native, associative Revit dimensions for the representative elements
    /// created by Smart MEP Annotation Pack.  The geometry reference provider is
    /// shared with Auto Dimension so no temporary detail lines or text are used.
    /// </summary>
    internal static class MepDocumentationDimensioner
    {
        private const double DirectionTolerance = 0.999;

        public static MepDocumentationDimensionResult Create(
            Document document,
            View view,
            IEnumerable<Element> sourceElements,
            SmartMepAnnotationSettings settings)
        {
            var result = new MepDocumentationDimensionResult();
            if (document == null || view == null || settings == null)
            {
                result.FirstIssue = "The document, view, or documentation settings are unavailable.";
                return result;
            }
            List<Element> sources = (sourceElements ?? Enumerable.Empty<Element>())
                .Where(element => element != null && element.IsValidObject)
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
            if (sources.Count == 0 ||
                (!settings.CreateGridReferenceDimensions &&
                 !settings.CreateElevationDimensions))
            {
                return result;
            }

            double offset = PaperMillimetersToInternal(
                settings.DimensionOffsetPaperMm,
                view);
            var provider = new AutoDimensionReferenceProvider(document, view);
            List<Grid> grids = settings.CreateGridReferenceDimensions
                ? new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Where(grid => grid != null && grid.IsValidObject)
                    .ToList()
                : new List<Grid>();
            List<Level> levels = settings.CreateElevationDimensions
                ? new FilteredElementCollector(document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .Where(level => level != null && level.IsValidObject)
                    .ToList()
                : new List<Level>();
            HashSet<string> existingKeys = CollectExistingDimensionKeys(document, view);

            using (var transaction = new Transaction(
                document,
                "Tung MEP - Orthographic Reference Dimensions"))
            {
                transaction.Start();
                int sourceIndex = 0;
                foreach (Element source in sources)
                {
                    XYZ center;
                    if (!TryGetCenter(source, view, out center))
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (settings.CreateGridReferenceDimensions)
                    {
                        TryCreateNearestGridDimension(
                            document,
                            view,
                            provider,
                            source,
                            center,
                            view.RightDirection,
                            view.UpDirection,
                            grids,
                            offset,
                            sourceIndex,
                            existingKeys,
                            result);
                        TryCreateNearestGridDimension(
                            document,
                            view,
                            provider,
                            source,
                            center,
                            view.UpDirection,
                            view.RightDirection,
                            grids,
                            offset,
                            sourceIndex,
                            existingKeys,
                            result);
                    }

                    if (settings.CreateElevationDimensions &&
                        (view.ViewType == ViewType.Section ||
                         view.ViewType == ViewType.Elevation))
                    {
                        TryCreateNearestLevelDimension(
                            document,
                            view,
                            provider,
                            source,
                            center,
                            levels,
                            offset,
                            sourceIndex,
                            existingKeys,
                            result);
                    }
                    sourceIndex++;
                }

                if (result.GridDimensionsCreated + result.ElevationDimensionsCreated == 0)
                {
                    transaction.RollBack();
                }
                else
                {
                    transaction.Commit();
                }
            }
            return result;
        }

        private static void TryCreateNearestGridDimension(
            Document document,
            View view,
            AutoDimensionReferenceProvider provider,
            Element source,
            XYZ center,
            XYZ measurementDirection,
            XYZ laneDirection,
            IList<Grid> grids,
            double offset,
            int sourceIndex,
            ISet<string> existingKeys,
            MepDocumentationDimensionResult result)
        {
            DimensionCandidateReference sourceReference = CollectBestReference(
                provider,
                source,
                measurementDirection,
                center);
            if (sourceReference == null)
            {
                result.Skipped++;
                return;
            }

            DimensionCandidateReference nearest = null;
            Grid nearestGrid = null;
            double nearestDistance = double.PositiveInfinity;
            foreach (Grid grid in grids)
            {
                DimensionCandidateReference candidate = CollectBestReference(
                    provider,
                    grid,
                    measurementDirection,
                    center);
                if (candidate == null)
                {
                    continue;
                }
                double distance = Math.Abs(
                    (candidate.Anchor - sourceReference.Anchor)
                        .DotProduct(measurementDirection));
                if (distance > UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters) &&
                    distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestGrid = grid;
                    nearestDistance = distance;
                }
            }
            if (nearest == null || nearestGrid == null)
            {
                result.Skipped++;
                return;
            }

            string axis = Math.Abs(measurementDirection.DotProduct(view.RightDirection)) >= 0.9
                ? "X"
                : "Y";
            string key = PairKey(source.Id, nearestGrid.Id, axis);
            if (existingKeys.Contains(key))
            {
                result.Skipped++;
                return;
            }
            double lane = offset + PaperMillimetersToInternal(
                (sourceIndex % 4) * 2.0,
                view);
            XYZ origin = center + Normalize(laneDirection) * lane;
            string issue;
            if (TryCreateDimension(
                document,
                view,
                measurementDirection,
                origin,
                sourceReference.Reference,
                nearest.Reference,
                out issue))
            {
                existingKeys.Add(key);
                result.GridDimensionsCreated++;
            }
            else
            {
                result.Failed++;
                SetFirstIssue(result, "Grid reference: " + issue);
            }
        }

        private static void TryCreateNearestLevelDimension(
            Document document,
            View view,
            AutoDimensionReferenceProvider provider,
            Element source,
            XYZ center,
            IList<Level> levels,
            double offset,
            int sourceIndex,
            ISet<string> existingKeys,
            MepDocumentationDimensionResult result)
        {
            XYZ up = Normalize(view.UpDirection);
            if (Math.Abs(up.DotProduct(XYZ.BasisZ)) < 0.9)
            {
                result.Skipped++;
                return;
            }
            DimensionCandidateReference sourceReference = CollectBestReference(
                provider,
                source,
                up,
                center);
            if (sourceReference == null)
            {
                result.Skipped++;
                return;
            }
            Level nearest = levels
                .OrderBy(level => Math.Abs(level.Elevation - center.Z))
                .FirstOrDefault();
            if (nearest == null)
            {
                result.Skipped++;
                return;
            }
            string key = PairKey(source.Id, nearest.Id, "Z");
            if (existingKeys.Contains(key))
            {
                result.Skipped++;
                return;
            }
            double lane = offset + PaperMillimetersToInternal(
                (sourceIndex % 4) * 2.0,
                view);
            XYZ origin = center + Normalize(view.RightDirection) * lane;
            string issue;
            if (TryCreateDimension(
                document,
                view,
                up,
                origin,
                sourceReference.Reference,
                new Reference(nearest),
                out issue))
            {
                existingKeys.Add(key);
                result.ElevationDimensionsCreated++;
            }
            else
            {
                result.Failed++;
                SetFirstIssue(result, "Elevation reference: " + issue);
            }
        }

        private static DimensionCandidateReference CollectBestReference(
            AutoDimensionReferenceProvider provider,
            Element element,
            XYZ measurementDirection,
            XYZ center)
        {
            IList<DimensionCandidateReference> candidates;
            string issue;
            if (!provider.TryCollect(
                element,
                measurementDirection,
                null,
                out candidates,
                out issue))
            {
                return null;
            }
            return candidates
                .OrderBy(candidate => candidate.Kind == DimensionReferenceKind.Center ? 0 : 1)
                .ThenBy(candidate => (candidate.Anchor - center).GetLength())
                .FirstOrDefault();
        }

        private static bool TryCreateDimension(
            Document document,
            View view,
            XYZ direction,
            XYZ origin,
            Reference first,
            Reference second,
            out string issue)
        {
            issue = null;
            using (var subTransaction = new SubTransaction(document))
            {
                subTransaction.Start();
                try
                {
                    var references = new ReferenceArray();
                    references.Append(Reparse(document, first));
                    references.Append(Reparse(document, second));
                    Line line = Line.CreateUnbound(origin, Normalize(direction));
                    Dimension dimension = document.Create.NewDimension(
                        view,
                        line,
                        references);
                    document.Regenerate();
                    if (dimension == null || !dimension.IsValidObject ||
                        !dimension.AreReferencesAvailable)
                    {
                        throw new InvalidOperationException(
                            "Revit did not retain both dimension references.");
                    }
                    subTransaction.Commit();
                    return true;
                }
                catch (Exception exception)
                {
                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                    {
                        subTransaction.RollBack();
                    }
                    issue = exception.Message;
                    return false;
                }
            }
        }

        private static Reference Reparse(Document document, Reference reference)
        {
            if (reference == null)
            {
                return reference;
            }
            try
            {
                string stable = reference.ConvertToStableRepresentation(document);
                Reference reparsed = Reference.ParseFromStableRepresentation(document, stable);
                return reparsed ?? reference;
            }
            catch
            {
                return reference;
            }
        }

        private static HashSet<string> CollectExistingDimensionKeys(
            Document document,
            View view)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dimension dimension in new FilteredElementCollector(document, view.Id)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>())
            {
                var ids = new List<ElementId>();
                try
                {
                    foreach (Reference reference in dimension.References)
                    {
                        if (reference != null && reference.ElementId != ElementId.InvalidElementId)
                        {
                            ids.Add(reference.ElementId);
                        }
                    }
                }
                catch
                {
                    continue;
                }
                if (ids.Count != 2)
                {
                    continue;
                }
                result.Add(PairKey(ids[0], ids[1], "X"));
                result.Add(PairKey(ids[0], ids[1], "Y"));
                result.Add(PairKey(ids[0], ids[1], "Z"));
            }
            return result;
        }

        private static string PairKey(ElementId first, ElementId second, string axis)
        {
            long a = first == null ? -1 : first.Value;
            long b = second == null ? -1 : second.Value;
            return Math.Min(a, b) + ":" + Math.Max(a, b) + ":" + axis;
        }

        private static bool TryGetCenter(Element element, View view, out XYZ center)
        {
            center = null;
            LocationCurve locationCurve = element.Location as LocationCurve;
            if (locationCurve != null && locationCurve.Curve != null)
            {
                center = locationCurve.Curve.Evaluate(0.5, true);
                return true;
            }
            LocationPoint locationPoint = element.Location as LocationPoint;
            if (locationPoint != null)
            {
                center = locationPoint.Point;
                return true;
            }
            BoundingBoxXYZ box = element.get_BoundingBox(view);
            if (box == null)
            {
                return false;
            }
            center = (box.Transform ?? Transform.Identity).OfPoint(
                (box.Min + box.Max) * 0.5);
            return true;
        }

        private static XYZ Normalize(XYZ value)
        {
            return value == null || value.GetLength() <= 1e-9
                ? XYZ.BasisX
                : value.Normalize();
        }

        private static double PaperMillimetersToInternal(double millimeters, View view)
        {
            return UnitUtils.ConvertToInternalUnits(
                millimeters * Math.Max(1, view.Scale),
                UnitTypeId.Millimeters);
        }

        private static void SetFirstIssue(
            MepDocumentationDimensionResult result,
            string issue)
        {
            if (string.IsNullOrEmpty(result.FirstIssue))
            {
                result.FirstIssue = issue;
            }
        }
    }
}
