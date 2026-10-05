using System;
using System.Collections.Generic;

namespace TungMEP.Modules.Tags
{
    internal struct AnnotationRoutePoint2
    {
        public AnnotationRoutePoint2(double x, double y)
            : this()
        {
            X = x;
            Y = y;
        }

        public double X { get; private set; }
        public double Y { get; private set; }
    }

    internal struct AnnotationRoute2
    {
        public AnnotationRoute2(
            AnnotationRoutePoint2 start,
            AnnotationRoutePoint2 elbow,
            AnnotationRoutePoint2 end)
            : this()
        {
            Start = start;
            Elbow = elbow;
            End = end;
        }

        public AnnotationRoutePoint2 Start { get; private set; }
        public AnnotationRoutePoint2 Elbow { get; private set; }
        public AnnotationRoutePoint2 End { get; private set; }

        public bool ConflictsWith(AnnotationRoute2 other)
        {
            return AnnotationRouteGeometry.SegmentsConflict(
                       Start,
                       Elbow,
                       other.Start,
                       other.Elbow) ||
                   AnnotationRouteGeometry.SegmentsConflict(
                       Start,
                       Elbow,
                       other.Elbow,
                       other.End) ||
                   AnnotationRouteGeometry.SegmentsConflict(
                       Elbow,
                       End,
                       other.Start,
                       other.Elbow) ||
                   AnnotationRouteGeometry.SegmentsConflict(
                       Elbow,
                       End,
                       other.Elbow,
                       other.End);
        }
    }

    internal static class AnnotationRouteGeometry
    {
        private const double Tolerance = 1e-8;

        public static bool SegmentsConflict(
            AnnotationRoutePoint2 firstStart,
            AnnotationRoutePoint2 firstEnd,
            AnnotationRoutePoint2 secondStart,
            AnnotationRoutePoint2 secondEnd)
        {
            if (Distance(firstStart, firstEnd) <= Tolerance ||
                Distance(secondStart, secondEnd) <= Tolerance ||
                !BoundingBoxesOverlap(
                    firstStart,
                    firstEnd,
                    secondStart,
                    secondEnd))
            {
                return false;
            }

            double firstSecondStart = Orientation(
                firstStart,
                firstEnd,
                secondStart);
            double firstSecondEnd = Orientation(
                firstStart,
                firstEnd,
                secondEnd);
            double secondFirstStart = Orientation(
                secondStart,
                secondEnd,
                firstStart);
            double secondFirstEnd = Orientation(
                secondStart,
                secondEnd,
                firstEnd);

            bool collinear = Math.Abs(firstSecondStart) <= Tolerance &&
                Math.Abs(firstSecondEnd) <= Tolerance &&
                Math.Abs(secondFirstStart) <= Tolerance &&
                Math.Abs(secondFirstEnd) <= Tolerance;
            if (collinear)
            {
                return CollinearSegmentsConflict(
                    firstStart,
                    firstEnd,
                    secondStart,
                    secondEnd);
            }

            if (OppositeSides(firstSecondStart, firstSecondEnd) &&
                OppositeSides(secondFirstStart, secondFirstEnd))
            {
                return true;
            }

            return EndpointTouchesSegmentInterior(
                       secondStart,
                       firstStart,
                       firstEnd) ||
                   EndpointTouchesSegmentInterior(
                       secondEnd,
                       firstStart,
                       firstEnd) ||
                   EndpointTouchesSegmentInterior(
                       firstStart,
                       secondStart,
                       secondEnd) ||
                   EndpointTouchesSegmentInterior(
                       firstEnd,
                       secondStart,
                       secondEnd);
        }

        private static bool CollinearSegmentsConflict(
            AnnotationRoutePoint2 firstStart,
            AnnotationRoutePoint2 firstEnd,
            AnnotationRoutePoint2 secondStart,
            AnnotationRoutePoint2 secondEnd)
        {
            bool useX = Math.Abs(firstEnd.X - firstStart.X) >=
                Math.Abs(firstEnd.Y - firstStart.Y);
            double firstMinimum = Math.Min(
                Coordinate(firstStart, useX),
                Coordinate(firstEnd, useX));
            double firstMaximum = Math.Max(
                Coordinate(firstStart, useX),
                Coordinate(firstEnd, useX));
            double secondMinimum = Math.Min(
                Coordinate(secondStart, useX),
                Coordinate(secondEnd, useX));
            double secondMaximum = Math.Max(
                Coordinate(secondStart, useX),
                Coordinate(secondEnd, useX));
            double overlapMinimum = Math.Max(firstMinimum, secondMinimum);
            double overlapMaximum = Math.Min(firstMaximum, secondMaximum);
            if (overlapMaximum < overlapMinimum - Tolerance)
            {
                return false;
            }
            if (overlapMaximum - overlapMinimum > Tolerance)
            {
                return true;
            }

            AnnotationRoutePoint2 touch = ClosestEndpointAtCoordinate(
                firstStart,
                firstEnd,
                overlapMinimum,
                useX);
            return !IsEndpoint(touch, firstStart, firstEnd) ||
                !IsEndpoint(touch, secondStart, secondEnd);
        }

        private static bool EndpointTouchesSegmentInterior(
            AnnotationRoutePoint2 point,
            AnnotationRoutePoint2 segmentStart,
            AnnotationRoutePoint2 segmentEnd)
        {
            if (!PointOnSegment(point, segmentStart, segmentEnd))
            {
                return false;
            }

            return !IsEndpoint(point, segmentStart, segmentEnd);
        }

        private static bool PointOnSegment(
            AnnotationRoutePoint2 point,
            AnnotationRoutePoint2 start,
            AnnotationRoutePoint2 end)
        {
            return Math.Abs(Orientation(start, end, point)) <= Tolerance &&
                point.X >= Math.Min(start.X, end.X) - Tolerance &&
                point.X <= Math.Max(start.X, end.X) + Tolerance &&
                point.Y >= Math.Min(start.Y, end.Y) - Tolerance &&
                point.Y <= Math.Max(start.Y, end.Y) + Tolerance;
        }

        private static bool BoundingBoxesOverlap(
            AnnotationRoutePoint2 firstStart,
            AnnotationRoutePoint2 firstEnd,
            AnnotationRoutePoint2 secondStart,
            AnnotationRoutePoint2 secondEnd)
        {
            return Math.Max(
                       Math.Min(firstStart.X, firstEnd.X),
                       Math.Min(secondStart.X, secondEnd.X)) <=
                   Math.Min(
                       Math.Max(firstStart.X, firstEnd.X),
                       Math.Max(secondStart.X, secondEnd.X)) + Tolerance &&
                   Math.Max(
                       Math.Min(firstStart.Y, firstEnd.Y),
                       Math.Min(secondStart.Y, secondEnd.Y)) <=
                   Math.Min(
                       Math.Max(firstStart.Y, firstEnd.Y),
                       Math.Max(secondStart.Y, secondEnd.Y)) + Tolerance;
        }

        private static AnnotationRoutePoint2 ClosestEndpointAtCoordinate(
            AnnotationRoutePoint2 start,
            AnnotationRoutePoint2 end,
            double coordinate,
            bool useX)
        {
            return Math.Abs(Coordinate(start, useX) - coordinate) <=
                Math.Abs(Coordinate(end, useX) - coordinate)
                ? start
                : end;
        }

        private static double Coordinate(
            AnnotationRoutePoint2 point,
            bool useX)
        {
            return useX ? point.X : point.Y;
        }

        private static bool OppositeSides(double first, double second)
        {
            return (first > Tolerance && second < -Tolerance) ||
                (first < -Tolerance && second > Tolerance);
        }

        private static bool IsEndpoint(
            AnnotationRoutePoint2 point,
            AnnotationRoutePoint2 start,
            AnnotationRoutePoint2 end)
        {
            return PointsEqual(point, start) || PointsEqual(point, end);
        }

        private static bool PointsEqual(
            AnnotationRoutePoint2 first,
            AnnotationRoutePoint2 second)
        {
            return Distance(first, second) <= Tolerance;
        }

        private static double Orientation(
            AnnotationRoutePoint2 start,
            AnnotationRoutePoint2 end,
            AnnotationRoutePoint2 point)
        {
            return (end.X - start.X) * (point.Y - start.Y) -
                (end.Y - start.Y) * (point.X - start.X);
        }

        private static double Distance(
            AnnotationRoutePoint2 first,
            AnnotationRoutePoint2 second)
        {
            double x = first.X - second.X;
            double y = first.Y - second.Y;
            return Math.Sqrt(x * x + y * y);
        }
    }

    internal static class AnnotationRouteAssignmentOptimizer
    {
        public static int[] Optimize(
            IList<AnnotationRoutePoint2> sources,
            IList<AnnotationRoutePoint2> heads,
            bool verticalStack)
        {
            return Optimize(sources, heads, verticalStack, null);
        }

        public static int[] Optimize(
            IList<AnnotationRoutePoint2> sources,
            IList<AnnotationRoutePoint2> heads,
            bool verticalStack,
            IList<AnnotationRoute2> plannedRoutes)
        {
            Validate(sources, heads);
            int[] assignment = new int[sources.Count];
            for (int index = 0; index < assignment.Length; index++)
            {
                assignment[index] = index;
            }

            int currentConflicts = CountConflicts(
                sources,
                heads,
                assignment,
                verticalStack,
                plannedRoutes);
            bool improved = true;
            while (currentConflicts > 0 && improved)
            {
                improved = false;
                int bestFirst = -1;
                int bestSecond = -1;
                int bestConflicts = currentConflicts;
                for (int first = 0; first < assignment.Length; first++)
                {
                    for (int second = first + 1;
                        second < assignment.Length;
                        second++)
                    {
                        Swap(assignment, first, second);
                        int candidateConflicts = CountConflicts(
                            sources,
                            heads,
                            assignment,
                            verticalStack,
                            plannedRoutes);
                        Swap(assignment, first, second);
                        if (candidateConflicts < bestConflicts)
                        {
                            bestConflicts = candidateConflicts;
                            bestFirst = first;
                            bestSecond = second;
                        }
                    }
                }

                if (bestFirst >= 0)
                {
                    Swap(assignment, bestFirst, bestSecond);
                    currentConflicts = bestConflicts;
                    improved = true;
                }
            }
            return assignment;
        }

        public static int CountConflicts(
            IList<AnnotationRoutePoint2> sources,
            IList<AnnotationRoutePoint2> heads,
            IList<int> assignment,
            bool verticalStack)
        {
            return CountConflicts(
                sources,
                heads,
                assignment,
                verticalStack,
                null);
        }

        public static int CountConflicts(
            IList<AnnotationRoutePoint2> sources,
            IList<AnnotationRoutePoint2> heads,
            IList<int> assignment,
            bool verticalStack,
            IList<AnnotationRoute2> plannedRoutes)
        {
            Validate(sources, heads);
            if (assignment == null || assignment.Count != sources.Count)
            {
                throw new ArgumentException(
                    "The route assignment must contain one source index per head.",
                    "assignment");
            }

            var routes = new List<AnnotationRoute2>();
            var usedSources = new HashSet<int>();
            for (int headIndex = 0; headIndex < heads.Count; headIndex++)
            {
                int sourceIndex = assignment[headIndex];
                if (sourceIndex < 0 || sourceIndex >= sources.Count ||
                    !usedSources.Add(sourceIndex))
                {
                    throw new ArgumentException(
                        "The route assignment must be a permutation of source indices.",
                        "assignment");
                }
                AnnotationRoutePoint2 source = sources[sourceIndex];
                AnnotationRoutePoint2 head = heads[headIndex];
                AnnotationRoutePoint2 elbow = verticalStack
                    ? new AnnotationRoutePoint2(source.X, head.Y)
                    : new AnnotationRoutePoint2(head.X, source.Y);
                routes.Add(new AnnotationRoute2(source, elbow, head));
            }

            int conflicts = 0;
            for (int first = 0; first < routes.Count; first++)
            {
                for (int second = first + 1;
                    second < routes.Count;
                    second++)
                {
                    if (routes[first].ConflictsWith(routes[second]))
                    {
                        conflicts++;
                    }
                }
            }
            if (plannedRoutes != null)
            {
                foreach (AnnotationRoute2 route in routes)
                {
                    foreach (AnnotationRoute2 plannedRoute in plannedRoutes)
                    {
                        if (route.ConflictsWith(plannedRoute))
                        {
                            conflicts++;
                        }
                    }
                }
            }
            return conflicts;
        }

        private static void Validate(
            IList<AnnotationRoutePoint2> sources,
            IList<AnnotationRoutePoint2> heads)
        {
            if (sources == null)
            {
                throw new ArgumentNullException("sources");
            }
            if (heads == null)
            {
                throw new ArgumentNullException("heads");
            }
            if (sources.Count != heads.Count)
            {
                throw new ArgumentException(
                    "Sources and heads must have the same item count.");
            }
        }

        private static void Swap(int[] values, int first, int second)
        {
            int temporary = values[first];
            values[first] = values[second];
            values[second] = temporary;
        }
    }
}
