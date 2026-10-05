using System;
using System.Collections.Generic;
using System.Linq;

namespace TungMEP.Modules.Dimensions
{
    internal struct Point2
    {
        public Point2(double x, double y)
            : this()
        {
            X = x;
            Y = y;
        }

        public double X { get; private set; }
        public double Y { get; private set; }
        public double Length { get { return Math.Sqrt((X * X) + (Y * Y)); } }

        public bool IsFinite
        {
            get
            {
                return !double.IsNaN(X) && !double.IsInfinity(X) &&
                       !double.IsNaN(Y) && !double.IsInfinity(Y);
            }
        }

        public Point2 Normalize()
        {
            double length = Length;
            if (length <= DimensionChainPlanner.GeometryEpsilon)
            {
                throw new ArgumentException("A direction vector must have a non-zero length.");
            }
            return this / length;
        }

        public double DistanceTo(Point2 other)
        {
            return (this - other).Length;
        }

        public static double Dot(Point2 left, Point2 right)
        {
            return (left.X * right.X) + (left.Y * right.Y);
        }

        public static double Cross(Point2 left, Point2 right)
        {
            return (left.X * right.Y) - (left.Y * right.X);
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

        public static Point2 operator /(Point2 value, double divisor)
        {
            return new Point2(value.X / divisor, value.Y / divisor);
        }

        public override string ToString()
        {
            return "(" + X.ToString("0.###") + ", " + Y.ToString("0.###") + ")";
        }
    }

    internal struct Projection2
    {
        public Projection2(double along, double perpendicular)
            : this()
        {
            Along = along;
            Perpendicular = perpendicular;
        }

        public double Along { get; private set; }
        public double Perpendicular { get; private set; }
    }

    internal sealed class ProjectionFrame2
    {
        public ProjectionFrame2(Point2 origin, Point2 scanDirection)
        {
            if (!origin.IsFinite || !scanDirection.IsFinite)
            {
                throw new ArgumentException("Projection frame coordinates must be finite.");
            }
            Origin = origin;
            AlongDirection = scanDirection.Normalize();
            PerpendicularDirection = new Point2(-AlongDirection.Y, AlongDirection.X);
        }

        public Point2 Origin { get; private set; }
        public Point2 AlongDirection { get; private set; }
        public Point2 PerpendicularDirection { get; private set; }

        public Projection2 Project(Point2 point)
        {
            Point2 relative = point - Origin;
            return new Projection2(
                Point2.Dot(relative, AlongDirection),
                Point2.Dot(relative, PerpendicularDirection));
        }

        public Point2 Unproject(double along, double perpendicular)
        {
            return Origin +
                   (AlongDirection * along) +
                   (PerpendicularDirection * perpendicular);
        }
    }

    internal sealed class ProjectedItem
    {
        public ProjectedItem(
            string referenceKey,
            Point2 segmentStart,
            Point2 segmentEnd,
            string groupKey)
        {
            if (string.IsNullOrWhiteSpace(referenceKey))
            {
                throw new ArgumentException("A reference key is required.", "referenceKey");
            }
            if (!segmentStart.IsFinite || !segmentEnd.IsFinite)
            {
                throw new ArgumentException("Projected item coordinates must be finite.");
            }
            ReferenceKey = referenceKey;
            SegmentStart = segmentStart;
            SegmentEnd = segmentEnd;
            GroupKey = groupKey ?? string.Empty;
        }

        public string ReferenceKey { get; private set; }
        public string GroupKey { get; private set; }
        public Point2 SegmentStart { get; private set; }
        public Point2 SegmentEnd { get; private set; }
        public Point2 Anchor { get { return (SegmentStart + SegmentEnd) / 2.0; } }

        public static ProjectedItem FromPoint(
            string referenceKey,
            Point2 point,
            string groupKey)
        {
            return new ProjectedItem(referenceKey, point, point, groupKey);
        }
    }

    internal sealed class ScanHit
    {
        internal ScanHit(
            ProjectedItem item,
            Point2 scanPoint,
            Point2 itemPoint,
            double scanCoordinate,
            double distanceToScan,
            bool exactIntersection)
        {
            Item = item;
            ScanPoint = scanPoint;
            ItemPoint = itemPoint;
            ScanCoordinate = scanCoordinate;
            DistanceToScan = distanceToScan;
            ExactIntersection = exactIntersection;
        }

        public ProjectedItem Item { get; private set; }
        public Point2 ScanPoint { get; private set; }
        public Point2 ItemPoint { get; private set; }
        public double ScanCoordinate { get; private set; }
        public double DistanceToScan { get; private set; }
        public bool ExactIntersection { get; private set; }
    }

    internal enum ProjectedGroupingAxis
    {
        Row,
        Column
    }

    internal sealed class ProjectedItemGroup
    {
        internal ProjectedItemGroup(double coordinate, IList<ProjectedItem> items)
        {
            Coordinate = coordinate;
            Items = items;
        }

        public double Coordinate { get; private set; }
        public IList<ProjectedItem> Items { get; private set; }
    }

    internal sealed class DimensionChainPlannerOptions
    {
        public DimensionChainPlannerOptions()
        {
            HitTolerance = 0.0;
            DedupeTolerance = 0.0;
            MaximumChainGap = 0.0;
        }

        public double HitTolerance { get; set; }
        public double DedupeTolerance { get; set; }

        // Zero means that a group is never split by distance.
        public double MaximumChainGap { get; set; }

        internal void Validate()
        {
            if (!DimensionChainPlanner.IsNonNegativeFinite(HitTolerance) ||
                !DimensionChainPlanner.IsNonNegativeFinite(DedupeTolerance) ||
                !DimensionChainPlanner.IsNonNegativeFinite(MaximumChainGap))
            {
                throw new ArgumentException("Planner tolerances must be finite and non-negative.");
            }
        }
    }

    internal sealed class DimensionChainPlan
    {
        internal DimensionChainPlan(
            string groupKey,
            Point2 scanStart,
            Point2 scanEnd,
            IList<ScanHit> hits)
        {
            GroupKey = groupKey;
            ScanStart = scanStart;
            ScanEnd = scanEnd;
            Hits = hits;
        }

        public string GroupKey { get; private set; }
        public Point2 ScanStart { get; private set; }
        public Point2 ScanEnd { get; private set; }
        public IList<ScanHit> Hits { get; private set; }
        public double FirstCoordinate { get { return Hits[0].ScanCoordinate; } }
        public double LastCoordinate { get { return Hits[Hits.Count - 1].ScanCoordinate; } }

        public IList<string> ReferenceKeys
        {
            get { return Hits.Select(hit => hit.Item.ReferenceKey).ToList(); }
        }
    }

    internal enum OuterOffsetSide
    {
        Positive,
        Negative,
        Both
    }

    internal sealed class OuterOffsetCandidate
    {
        internal OuterOffsetCandidate(
            OuterOffsetSide side,
            int rank,
            double perpendicularCoordinate,
            Point2 lineStart,
            Point2 lineEnd)
        {
            Side = side;
            Rank = rank;
            PerpendicularCoordinate = perpendicularCoordinate;
            LineStart = lineStart;
            LineEnd = lineEnd;
        }

        public OuterOffsetSide Side { get; private set; }
        public int Rank { get; private set; }
        public double PerpendicularCoordinate { get; private set; }
        public Point2 LineStart { get; private set; }
        public Point2 LineEnd { get; private set; }
    }

    internal static class DimensionChainPlanner
    {
        internal const double GeometryEpsilon = 1e-9;

        public static ProjectionFrame2 CreateFrame(Point2 scanStart, Point2 scanEnd)
        {
            return new ProjectionFrame2(scanStart, scanEnd - scanStart);
        }

        public static IList<ScanHit> FindScanHits(
            IEnumerable<ProjectedItem> items,
            Point2 scanStart,
            Point2 scanEnd,
            double tolerance)
        {
            if (items == null)
            {
                throw new ArgumentNullException("items");
            }
            if (!IsNonNegativeFinite(tolerance))
            {
                throw new ArgumentException("Scan tolerance must be finite and non-negative.");
            }
            ProjectionFrame2 frame = CreateFrame(scanStart, scanEnd);
            double scanLength = scanStart.DistanceTo(scanEnd);
            var result = new List<ScanHit>();
            foreach (ProjectedItem item in items)
            {
                if (item == null)
                {
                    continue;
                }
                Point2 scanPoint;
                Point2 itemPoint;
                bool intersects = TrySegmentIntersection(
                    scanStart,
                    scanEnd,
                    item.SegmentStart,
                    item.SegmentEnd,
                    out scanPoint);
                if (intersects)
                {
                    itemPoint = scanPoint;
                }
                else
                {
                    FindClosestSegmentPoints(
                        scanStart,
                        scanEnd,
                        item.SegmentStart,
                        item.SegmentEnd,
                        out scanPoint,
                        out itemPoint);
                }

                double distance = scanPoint.DistanceTo(itemPoint);
                Projection2 projection = frame.Project(scanPoint);
                if (distance <= tolerance + GeometryEpsilon &&
                    projection.Along >= -tolerance &&
                    projection.Along <= scanLength + tolerance)
                {
                    result.Add(new ScanHit(
                        item,
                        scanPoint,
                        itemPoint,
                        projection.Along,
                        distance,
                        intersects));
                }
            }
            return result
                .OrderBy(hit => hit.ScanCoordinate)
                .ThenBy(hit => hit.Item.ReferenceKey, StringComparer.Ordinal)
                .ToList();
        }

        public static IList<ScanHit> SortAndDedupeHits(
            IEnumerable<ScanHit> hits,
            double coordinateTolerance)
        {
            if (hits == null)
            {
                throw new ArgumentNullException("hits");
            }
            if (!IsNonNegativeFinite(coordinateTolerance))
            {
                throw new ArgumentException("Dedupe tolerance must be finite and non-negative.");
            }
            List<ScanHit> ordered = hits
                .Where(hit => hit != null)
                .OrderBy(hit => hit.ScanCoordinate)
                .ThenBy(hit => hit.Item.ReferenceKey, StringComparer.Ordinal)
                .ToList();
            var result = new List<ScanHit>();
            int index = 0;
            while (index < ordered.Count)
            {
                double clusterStart = ordered[index].ScanCoordinate;
                var cluster = new List<ScanHit> { ordered[index] };
                index++;
                while (index < ordered.Count &&
                    ordered[index].ScanCoordinate - clusterStart <=
                    coordinateTolerance + GeometryEpsilon)
                {
                    cluster.Add(ordered[index]);
                    index++;
                }
                result.Add(cluster
                    .OrderByDescending(hit => hit.ExactIntersection)
                    .ThenBy(hit => hit.DistanceToScan)
                    .ThenBy(hit => hit.Item.ReferenceKey, StringComparer.Ordinal)
                    .First());
            }
            return result.OrderBy(hit => hit.ScanCoordinate).ToList();
        }

        public static IList<ProjectedItemGroup> GroupPointsIntoRows(
            IEnumerable<ProjectedItem> items,
            ProjectionFrame2 frame,
            double tolerance)
        {
            return GroupProjectedItems(items, frame, ProjectedGroupingAxis.Row, tolerance);
        }

        public static IList<ProjectedItemGroup> GroupPointsIntoColumns(
            IEnumerable<ProjectedItem> items,
            ProjectionFrame2 frame,
            double tolerance)
        {
            return GroupProjectedItems(items, frame, ProjectedGroupingAxis.Column, tolerance);
        }

        public static IList<ProjectedItemGroup> GroupProjectedItems(
            IEnumerable<ProjectedItem> items,
            ProjectionFrame2 frame,
            ProjectedGroupingAxis groupingAxis,
            double tolerance)
        {
            if (items == null)
            {
                throw new ArgumentNullException("items");
            }
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }
            if (!IsNonNegativeFinite(tolerance))
            {
                throw new ArgumentException("Grouping tolerance must be finite and non-negative.");
            }

            List<ProjectedCoordinateItem> ordered = items
                .Where(item => item != null)
                .Select(item =>
                {
                    Projection2 projection = frame.Project(item.Anchor);
                    return new ProjectedCoordinateItem
                    {
                        Item = item,
                        GroupCoordinate = groupingAxis == ProjectedGroupingAxis.Row
                            ? projection.Perpendicular
                            : projection.Along,
                        SortCoordinate = groupingAxis == ProjectedGroupingAxis.Row
                            ? projection.Along
                            : projection.Perpendicular
                    };
                })
                .OrderBy(item => item.GroupCoordinate)
                .ThenBy(item => item.SortCoordinate)
                .ToList();

            var result = new List<ProjectedItemGroup>();
            int index = 0;
            while (index < ordered.Count)
            {
                double minimum = ordered[index].GroupCoordinate;
                var cluster = new List<ProjectedCoordinateItem> { ordered[index] };
                index++;
                while (index < ordered.Count &&
                    ordered[index].GroupCoordinate - minimum <= tolerance + GeometryEpsilon)
                {
                    cluster.Add(ordered[index]);
                    index++;
                }
                result.Add(new ProjectedItemGroup(
                    cluster.Average(item => item.GroupCoordinate),
                    cluster
                        .OrderBy(item => item.SortCoordinate)
                        .ThenBy(item => item.Item.ReferenceKey, StringComparer.Ordinal)
                        .Select(item => item.Item)
                        .ToList()));
            }
            return result;
        }

        public static IList<DimensionChainPlan> PlanChains(
            IEnumerable<ProjectedItem> items,
            Point2 scanStart,
            Point2 scanEnd,
            DimensionChainPlannerOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            options.Validate();
            IList<ScanHit> hits = FindScanHits(
                items,
                scanStart,
                scanEnd,
                options.HitTolerance);
            return PlanChains(hits, scanStart, scanEnd, options);
        }

        public static IList<DimensionChainPlan> PlanChains(
            IEnumerable<ScanHit> hits,
            Point2 scanStart,
            Point2 scanEnd,
            DimensionChainPlannerOptions options)
        {
            if (hits == null)
            {
                throw new ArgumentNullException("hits");
            }
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            options.Validate();
            var plans = new List<DimensionChainPlan>();
            foreach (IGrouping<string, ScanHit> group in hits
                .Where(hit => hit != null)
                .GroupBy(hit => hit.Item.GroupKey ?? string.Empty, StringComparer.Ordinal))
            {
                IList<ScanHit> deduped = SortAndDedupeHits(group, options.DedupeTolerance);
                var current = new List<ScanHit>();
                foreach (ScanHit hit in deduped)
                {
                    if (current.Count > 0 &&
                        options.MaximumChainGap > 0.0 &&
                        hit.ScanCoordinate - current[current.Count - 1].ScanCoordinate >
                        options.MaximumChainGap + GeometryEpsilon)
                    {
                        AddChainIfUsable(plans, group.Key, scanStart, scanEnd, current);
                        current = new List<ScanHit>();
                    }
                    current.Add(hit);
                }
                AddChainIfUsable(plans, group.Key, scanStart, scanEnd, current);
            }
            return plans
                .OrderBy(plan => plan.FirstCoordinate)
                .ThenBy(plan => plan.GroupKey, StringComparer.Ordinal)
                .ToList();
        }

        public static IList<OuterOffsetCandidate> CreateOuterOffsetCandidates(
            IEnumerable<Point2> boundaryPoints,
            ProjectionFrame2 frame,
            double initialOffset,
            double offsetStep,
            int countPerSide,
            double endPadding,
            OuterOffsetSide side)
        {
            if (boundaryPoints == null)
            {
                throw new ArgumentNullException("boundaryPoints");
            }
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }
            if (!IsNonNegativeFinite(initialOffset) ||
                !IsNonNegativeFinite(offsetStep) ||
                !IsNonNegativeFinite(endPadding) ||
                countPerSide < 1)
            {
                throw new ArgumentException(
                    "Offset distances must be finite and non-negative, and candidate count must be positive.");
            }
            List<Projection2> projected = boundaryPoints
                .Where(point => point.IsFinite)
                .Select(frame.Project)
                .ToList();
            if (projected.Count == 0)
            {
                return new List<OuterOffsetCandidate>();
            }

            double minimumAlong = projected.Min(point => point.Along) - endPadding;
            double maximumAlong = projected.Max(point => point.Along) + endPadding;
            double minimumPerpendicular = projected.Min(point => point.Perpendicular);
            double maximumPerpendicular = projected.Max(point => point.Perpendicular);
            var result = new List<OuterOffsetCandidate>();
            for (int rank = 0; rank < countPerSide; rank++)
            {
                double distance = initialOffset + (offsetStep * rank);
                if (side == OuterOffsetSide.Positive || side == OuterOffsetSide.Both)
                {
                    double coordinate = maximumPerpendicular + distance;
                    result.Add(new OuterOffsetCandidate(
                        OuterOffsetSide.Positive,
                        rank,
                        coordinate,
                        frame.Unproject(minimumAlong, coordinate),
                        frame.Unproject(maximumAlong, coordinate)));
                }
                if (side == OuterOffsetSide.Negative || side == OuterOffsetSide.Both)
                {
                    double coordinate = minimumPerpendicular - distance;
                    result.Add(new OuterOffsetCandidate(
                        OuterOffsetSide.Negative,
                        rank,
                        coordinate,
                        frame.Unproject(minimumAlong, coordinate),
                        frame.Unproject(maximumAlong, coordinate)));
                }
            }
            return result;
        }

        internal static bool IsNonNegativeFinite(double value)
        {
            return value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void AddChainIfUsable(
            ICollection<DimensionChainPlan> plans,
            string groupKey,
            Point2 scanStart,
            Point2 scanEnd,
            IList<ScanHit> hits)
        {
            if (hits.Count >= 2)
            {
                plans.Add(new DimensionChainPlan(
                    groupKey,
                    scanStart,
                    scanEnd,
                    hits.ToList()));
            }
        }

        private static bool TrySegmentIntersection(
            Point2 firstStart,
            Point2 firstEnd,
            Point2 secondStart,
            Point2 secondEnd,
            out Point2 intersection)
        {
            intersection = new Point2();
            Point2 firstDirection = firstEnd - firstStart;
            Point2 secondDirection = secondEnd - secondStart;
            double firstSquaredLength = Point2.Dot(firstDirection, firstDirection);
            double secondSquaredLength = Point2.Dot(secondDirection, secondDirection);
            if (firstSquaredLength <= GeometryEpsilon * GeometryEpsilon)
            {
                return false;
            }
            if (secondSquaredLength <= GeometryEpsilon * GeometryEpsilon)
            {
                Point2 closest = ClosestPointOnSegment(secondStart, firstStart, firstEnd);
                if (closest.DistanceTo(secondStart) <= GeometryEpsilon)
                {
                    intersection = closest;
                    return true;
                }
                return false;
            }
            double denominator = Point2.Cross(firstDirection, secondDirection);
            if (Math.Abs(denominator) <= GeometryEpsilon)
            {
                if (Math.Abs(Point2.Cross(secondStart - firstStart, firstDirection)) >
                    GeometryEpsilon * Math.Sqrt(firstSquaredLength))
                {
                    return false;
                }
                double collinearStartParameter = Point2.Dot(
                    secondStart - firstStart,
                    firstDirection) / firstSquaredLength;
                double collinearEndParameter = Point2.Dot(
                    secondEnd - firstStart,
                    firstDirection) / firstSquaredLength;
                double overlapStart = Math.Max(
                    0.0,
                    Math.Min(collinearStartParameter, collinearEndParameter));
                double overlapEnd = Math.Min(
                    1.0,
                    Math.Max(collinearStartParameter, collinearEndParameter));
                if (overlapStart > overlapEnd + GeometryEpsilon)
                {
                    return false;
                }
                double middle = (Clamp(overlapStart, 0.0, 1.0) +
                                 Clamp(overlapEnd, 0.0, 1.0)) / 2.0;
                intersection = firstStart + (firstDirection * middle);
                return true;
            }
            Point2 betweenStarts = secondStart - firstStart;
            double firstParameter = Point2.Cross(betweenStarts, secondDirection) / denominator;
            double secondParameter = Point2.Cross(betweenStarts, firstDirection) / denominator;
            if (firstParameter < -GeometryEpsilon || firstParameter > 1.0 + GeometryEpsilon ||
                secondParameter < -GeometryEpsilon || secondParameter > 1.0 + GeometryEpsilon)
            {
                return false;
            }
            firstParameter = Clamp(firstParameter, 0.0, 1.0);
            intersection = firstStart + (firstDirection * firstParameter);
            return true;
        }

        private static void FindClosestSegmentPoints(
            Point2 firstStart,
            Point2 firstEnd,
            Point2 secondStart,
            Point2 secondEnd,
            out Point2 firstPoint,
            out Point2 secondPoint)
        {
            var candidates = new List<ClosestPointPair>();
            Point2 point = ClosestPointOnSegment(firstStart, secondStart, secondEnd);
            candidates.Add(new ClosestPointPair(firstStart, point));
            point = ClosestPointOnSegment(firstEnd, secondStart, secondEnd);
            candidates.Add(new ClosestPointPair(firstEnd, point));
            point = ClosestPointOnSegment(secondStart, firstStart, firstEnd);
            candidates.Add(new ClosestPointPair(point, secondStart));
            point = ClosestPointOnSegment(secondEnd, firstStart, firstEnd);
            candidates.Add(new ClosestPointPair(point, secondEnd));
            ClosestPointPair best = candidates.OrderBy(candidate => candidate.Distance).First();
            firstPoint = best.First;
            secondPoint = best.Second;
        }

        private static Point2 ClosestPointOnSegment(
            Point2 point,
            Point2 segmentStart,
            Point2 segmentEnd)
        {
            Point2 direction = segmentEnd - segmentStart;
            double squaredLength = Point2.Dot(direction, direction);
            if (squaredLength <= GeometryEpsilon * GeometryEpsilon)
            {
                return segmentStart;
            }
            double parameter = Point2.Dot(point - segmentStart, direction) / squaredLength;
            return segmentStart + (direction * Clamp(parameter, 0.0, 1.0));
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private sealed class ClosestPointPair
        {
            public ClosestPointPair(Point2 first, Point2 second)
            {
                First = first;
                Second = second;
            }

            public Point2 First { get; private set; }
            public Point2 Second { get; private set; }
            public double Distance { get { return First.DistanceTo(Second); } }
        }

        private sealed class ProjectedCoordinateItem
        {
            public ProjectedItem Item { get; set; }
            public double GroupCoordinate { get; set; }
            public double SortCoordinate { get; set; }
        }
    }
}
