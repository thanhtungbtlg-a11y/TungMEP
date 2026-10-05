using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.AutoSprinkler
{
    internal enum BranchGuideKind
    {
        Branch,
        Detour,
        MainConnection
    }

    internal class BranchGuideSegment
    {
        public XYZ Start { get; set; }
        public XYZ End { get; set; }
        public BranchGuideKind Kind { get; set; }
        public bool IsDetour { get { return Kind == BranchGuideKind.Detour; } }
    }

    internal class AlignedBranchPlan
    {
        public List<XYZ> Centers { get; set; }
        public List<BranchGuideSegment> GuideSegments { get; set; }
        public int SampleCount { get; set; }
        public int CoveredSampleCount { get; set; }
        public double CoveragePercent { get; set; }
        public double GridSpacing { get; set; }
        public double ColumnSpacing { get; set; }
        public double AlongBranchSpacing { get; set; }
        public int ExpectedGridIntersectionCount { get; set; }
        public int MissingGridIntersectionCount { get; set; }
        public int BranchCount { get; set; }
        public int DetourBranchCount { get; set; }
        public int RouteFailureCount { get; set; }
    }

    internal static class AlignedBranchPlanner
    {
        private const double MillimetersPerFoot = 304.8;

        public static AlignedBranchPlan Create(
            IList<XYZ> polygon,
            XYZ mainStart,
            XYZ mainEnd,
            double coverageRadius,
            double minimumSpacing,
            double maximumSpacing,
            double boundaryClearance,
            IList<IList<XYZ>> obstacles,
            double obstacleClearance)
        {
            return Create(
                polygon,
                mainStart,
                mainEnd,
                coverageRadius,
                minimumSpacing,
                maximumSpacing,
                boundaryClearance,
                obstacles,
                new List<IList<XYZ>>(),
                obstacleClearance);
        }

        public static AlignedBranchPlan Create(
            IList<XYZ> polygon,
            XYZ mainStart,
            XYZ mainEnd,
            double coverageRadius,
            double minimumSpacing,
            double maximumSpacing,
            double boundaryClearance,
            IList<IList<XYZ>> physicalObstacles,
            IList<IList<XYZ>> excludedVoids,
            double obstacleClearance)
        {
            var transform = new LocalTransform(mainStart, mainEnd, PolygonCenter(polygon));
            List<Point2> localPolygon = polygon.Select(transform.ToLocal).ToList();
            List<List<Point2>> localPhysicalObstacles = (physicalObstacles ?? new List<IList<XYZ>>())
                .Select(obstacle => obstacle.Select(transform.ToLocal).ToList())
                .Where(obstacle => obstacle.Count >= 3)
                .ToList();
            List<List<Point2>> localExcludedVoids = (excludedVoids ?? new List<IList<XYZ>>())
                .Select(obstacle => obstacle.Select(transform.ToLocal).ToList())
                .Where(obstacle => obstacle.Count >= 3)
                .ToList();
            List<List<Point2>> localObstacles = localPhysicalObstacles
                .Concat(localExcludedVoids)
                .ToList();
            double sampleStep = Math.Min(250.0 / MillimetersPerFoot, coverageRadius / 5.0);
            List<Point2> samples = GenerateSamples(localPolygon, localObstacles, sampleStep);
            LayoutCandidate best = null;
            const int phaseDivisions = 8;
            double minU = localPolygon.Min(point => point.U);
            double maxU = localPolygon.Max(point => point.U);
            double minV = localPolygon.Min(point => point.V);
            double maxV = localPolygon.Max(point => point.V);

            bool completeLayoutFound = false;
            List<double> spacingValues = GenerateSpacings(minimumSpacing, maximumSpacing);
            List<double> uSpacingValues = OrderSpacingsForExtent(
                spacingValues, minU, maxU, boundaryClearance, maximumSpacing);
            List<double> vSpacingValues = OrderSpacingsForExtent(
                spacingValues, minV, maxV, boundaryClearance, maximumSpacing);
            foreach (double uSpacing in uSpacingValues)
            {
                foreach (double vSpacing in vSpacingValues)
                {
                    LayoutCandidate bestForSpacingPair = null;
                    List<double> uPhases = GeneratePhaseOffsets(
                        minU, maxU, uSpacing, boundaryClearance, coverageRadius, phaseDivisions);
                    List<double> vPhases = GeneratePhaseOffsets(
                        minV, maxV, vSpacing, boundaryClearance, coverageRadius, phaseDivisions);
                    foreach (double uPhase in uPhases)
                    {
                        foreach (double vPhase in vPhases)
                        {
                            CenterGeneration generated = GenerateCenters(
                                localPolygon,
                                uSpacing,
                                vSpacing,
                                uPhase,
                                vPhase,
                                boundaryClearance,
                                transform.MainLength,
                                maximumSpacing,
                                localObstacles,
                                localExcludedVoids,
                                obstacleClearance,
                                false);
                            List<Point2> centers = generated.Centers;
                            if (centers.Count == 0)
                            {
                                continue;
                            }
                            if (!CoversBoundaryExtents(centers, localPolygon, coverageRadius))
                            {
                                // Even when a perimeter beam overlaps boundary
                                // samples, the outermost sprinkler rows/columns
                                // may never be farther than the coverage half-span.
                                continue;
                            }
                            var candidate = new LayoutCandidate
                            {
                                Centers = centers,
                                CoveredSamples = CountCovered(samples, centers, coverageRadius),
                                USpacing = uSpacing,
                                VSpacing = vSpacing,
                                UPhase = uPhase,
                                VPhase = vPhase,
                                ExpectedCenters = generated.ExpectedCenters,
                                MissingCenters = generated.MissingCenters,
                                HasValidAxisSpacing = generated.HasValidAxisSpacing
                            };
                            if (IsBetter(candidate, bestForSpacingPair, samples.Count))
                            {
                                bestForSpacingPair = candidate;
                            }
                        }
                    }
                    if (bestForSpacingPair == null)
                    {
                        continue;
                    }
                    CenterGeneration repairedGeneration = GenerateCenters(
                        localPolygon,
                        uSpacing,
                        vSpacing,
                        bestForSpacingPair.UPhase,
                        bestForSpacingPair.VPhase,
                        boundaryClearance,
                        transform.MainLength,
                        maximumSpacing,
                        localObstacles,
                        localExcludedVoids,
                        obstacleClearance,
                        true);
                    List<Point2> repairedCenters = repairedGeneration.Centers;
                    if (repairedCenters.Count > 0 &&
                        CoversBoundaryExtents(repairedCenters, localPolygon, coverageRadius))
                    {
                        var repaired = new LayoutCandidate
                        {
                            Centers = repairedCenters,
                            CoveredSamples = CountCovered(samples, repairedCenters, coverageRadius),
                            USpacing = uSpacing,
                            VSpacing = vSpacing,
                            UPhase = bestForSpacingPair.UPhase,
                            VPhase = bestForSpacingPair.VPhase,
                            ExpectedCenters = repairedGeneration.ExpectedCenters,
                            MissingCenters = repairedGeneration.MissingCenters,
                            HasValidAxisSpacing = repairedGeneration.HasValidAxisSpacing
                        };
                        if (IsBetter(repaired, bestForSpacingPair, samples.Count))
                        {
                            bestForSpacingPair = repaired;
                        }
                    }
                    if (IsBetter(bestForSpacingPair, best, samples.Count))
                    {
                        best = bestForSpacingPair;
                    }
                    if (bestForSpacingPair.ExpectedCenters > 0 &&
                        bestForSpacingPair.MissingCenters == 0 &&
                        bestForSpacingPair.HasValidAxisSpacing)
                    {
                        // Spacing pairs are already ordered around the edge-anchored
                        // manual layout. Stop at the first pair that can retain every
                        // intended row/column intersection. Sample coverage is still
                        // reported, but it must not win by deleting isolated heads.
                        best = bestForSpacingPair;
                        completeLayoutFound = true;
                        break;
                    }
                }
                if (completeLayoutFound)
                {
                    break;
                }
            }

            if (best == null)
            {
                return new AlignedBranchPlan
                {
                    Centers = new List<XYZ>(),
                    GuideSegments = new List<BranchGuideSegment>(),
                    SampleCount = samples.Count
                };
            }

            int branchCount;
            int detourCount;
            int routeFailures;
            List<BranchGuideSegment> guideSegments = BuildRoutes(
                best.Centers,
                localPolygon,
                transform,
                transform.MainLength,
                boundaryClearance,
                localObstacles,
                obstacleClearance,
                out branchCount,
                out detourCount,
                out routeFailures);

            return new AlignedBranchPlan
            {
                Centers = best.Centers.Select(transform.ToWorld).ToList(),
                GuideSegments = guideSegments,
                SampleCount = samples.Count,
                CoveredSampleCount = best.CoveredSamples,
                CoveragePercent = samples.Count == 0 ? 0 : 100.0 * best.CoveredSamples / samples.Count,
                GridSpacing = Math.Min(best.USpacing, best.VSpacing),
                ColumnSpacing = best.USpacing,
                AlongBranchSpacing = best.VSpacing,
                ExpectedGridIntersectionCount = best.ExpectedCenters,
                MissingGridIntersectionCount = best.MissingCenters,
                BranchCount = branchCount,
                DetourBranchCount = detourCount,
                RouteFailureCount = routeFailures
            };
        }

        public static List<BranchGuideSegment> CreateGlobalRoutes(
            IList<XYZ> centers,
            XYZ mainStart,
            XYZ mainEnd,
            IList<IList<XYZ>> obstacles,
            double obstacleClearance,
            out int branchCount,
            out int detourCount,
            out int routeFailures)
        {
            var segments = new List<BranchGuideSegment>();
            branchCount = 0;
            detourCount = 0;
            routeFailures = 0;
            if (centers == null || centers.Count == 0)
            {
                return segments;
            }

            var transform = new LocalTransform(mainStart, mainEnd, PolygonCenter(centers));
            List<Point2> localCenters = centers.Select(transform.ToLocal).ToList();
            List<List<Point2>> localObstacles = (obstacles ?? new List<IList<XYZ>>())
                .Select(obstacle => obstacle.Select(transform.ToLocal).ToList())
                .Where(obstacle => obstacle.Count >= 3)
                .ToList();

            // A column is one branch direction perpendicular to the main. Separate
            // opposite sides of the main so they never get tied through the main.
            List<IGrouping<double, Point2>> columns = localCenters
                .GroupBy(point => Math.Round(point.U * MillimetersPerFoot, 1))
                .OrderBy(group => group.Key)
                .ToList();
            for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                IGrouping<double, Point2> column = columns[columnIndex];
                double columnU = column.Average(point => point.U);
                double minimumTakeoffU = columnIndex == 0
                    ? 0
                    : (columns[columnIndex - 1].Average(point => point.U) + columnU) / 2.0;
                double maximumTakeoffU = columnIndex == columns.Count - 1
                    ? transform.MainLength
                    : (columnU + columns[columnIndex + 1].Average(point => point.U)) / 2.0;
                foreach (IGrouping<int, Point2> side in column
                    .GroupBy(point => point.V >= 0 ? 1 : -1))
                {
                    List<Point2> heads = side.OrderBy(point => Math.Abs(point.V)).ToList();
                    if (heads.Count == 0)
                    {
                        continue;
                    }

                    branchCount++;
                    bool hasDetour = false;
                    Point2 previous = new Point2(heads[0].U, 0);
                    for (int index = 0; index < heads.Count; index++)
                    {
                        Point2 next = heads[index];
                        // Heads already aligned in the branch-flow direction are
                        // joined directly. Obstacle routing applies only to the
                        // takeoff toward the main; this prevents a blocked takeoff
                        // from turning the entire head column into a large loop.
                        List<Point2> path = index == 0
                            ? FindMainConnectionPath(
                                next,
                                localObstacles,
                                obstacleClearance,
                                minimumTakeoffU,
                                maximumTakeoffU)
                            : new List<Point2> { previous, next };
                        if (path == null)
                        {
                            routeFailures++;
                            previous = next;
                            continue;
                        }

                        if (path.Count > 2)
                        {
                            hasDetour = true;
                        }
                        BranchGuideKind kind = index == 0
                            ? BranchGuideKind.MainConnection
                            : BranchGuideKind.Branch;
                        for (int pathIndex = 1; pathIndex < path.Count; pathIndex++)
                        {
                            AddSegment(
                                segments,
                                transform,
                                path[pathIndex - 1],
                                path[pathIndex],
                                kind);
                        }
                        previous = next;
                    }
                    if (hasDetour)
                    {
                        detourCount++;
                    }
                }
            }
            return segments;
        }

        private static List<Point2> FindMainConnectionPath(
            Point2 head,
            IList<List<Point2>> obstacles,
            double clearance,
            double minimumTakeoffU,
            double maximumTakeoffU)
        {
            var projectedTakeoff = new Point2(head.U, 0);
            List<List<Point2>> blockingObstacles = obstacles
                .Where(obstacle => !SegmentClearOfObstacles(
                    projectedTakeoff,
                    head,
                    new[] { obstacle },
                    clearance))
                .ToList();
            if (blockingObstacles.Count == 0)
            {
                return new List<Point2> { projectedTakeoff, head };
            }

            double routingOffset = clearance + 0.5 / MillimetersPerFoot;
            List<Point2> simpleDogleg = FindSimpleMainDogleg(
                head,
                blockingObstacles,
                clearance,
                routingOffset,
                minimumTakeoffU,
                maximumTakeoffU);
            if (simpleDogleg != null)
            {
                return simpleDogleg;
            }

            var takeoffUValues = new List<double> { head.U };
            // Only obstacles intersecting this branch's direct takeoff may drive
            // the dogleg. Distant columns/beams must not pull the route toward
            // their far edges and create an unnecessarily long pipe run.
            foreach (List<Point2> obstacle in blockingObstacles)
            {
                foreach (Point2 vertex in obstacle)
                {
                    takeoffUValues.Add(vertex.U - routingOffset);
                    takeoffUValues.Add(vertex.U + routingOffset);
                }
            }

            List<Point2> best = null;
            double bestCost = double.MaxValue;
            foreach (double takeoffU in DistinctCoordinates(takeoffUValues)
                .Where(value => value >= minimumTakeoffU - 1e-8 &&
                    value <= maximumTakeoffU + 1e-8))
            {
                List<Point2> candidate = FindObstacleAvoidingOrthogonalPath(
                    new Point2(takeoffU, 0),
                    head,
                    blockingObstacles,
                    clearance,
                    minimumTakeoffU,
                    maximumTakeoffU);
                if (candidate == null)
                {
                    continue;
                }
                double cost = 0;
                for (int index = 1; index < candidate.Count; index++)
                {
                    cost += Distance(candidate[index - 1], candidate[index]);
                }
                cost += Math.Max(0, candidate.Count - 2) * (1.0 / MillimetersPerFoot);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = candidate;
                }
            }
            return best ?? FindBoundingDogleg(
                head,
                blockingObstacles,
                clearance,
                routingOffset,
                minimumTakeoffU,
                maximumTakeoffU);
        }

        private static List<Point2> FindBoundingDogleg(
            Point2 head,
            IList<List<Point2>> obstacles,
            double clearance,
            double routingOffset,
            double minimumTakeoffU,
            double maximumTakeoffU)
        {
            if (obstacles == null || obstacles.Count == 0)
            {
                return new List<Point2> { new Point2(head.U, 0), head };
            }
            double minimumU = obstacles.SelectMany(obstacle => obstacle).Min(point => point.U);
            double maximumU = obstacles.SelectMany(obstacle => obstacle).Max(point => point.U);
            double teeApproach = 600.0 / MillimetersPerFoot;
            double bendV = head.V >= 0
                ? obstacles.SelectMany(obstacle => obstacle).Max(point => point.V) + routingOffset
                : obstacles.SelectMany(obstacle => obstacle).Min(point => point.V) - routingOffset;
            bendV = head.V >= 0
                ? Math.Min(bendV, head.V - teeApproach)
                : Math.Max(bendV, head.V + teeApproach);
            var sideCandidates = new[]
            {
                minimumU - routingOffset,
                maximumU + routingOffset
            }
                .Where(value => value >= minimumTakeoffU - 1e-8 &&
                    value <= maximumTakeoffU + 1e-8)
                .OrderBy(value => Math.Abs(value - head.U))
                .ToList();
            List<Point2> nearestFallback = null;
            foreach (double routeU in sideCandidates)
            {
                var path = RemoveCollinearPoints(new List<Point2>
                {
                    new Point2(routeU, 0),
                    new Point2(routeU, bendV),
                    new Point2(head.U, bendV),
                    head
                });
                if (nearestFallback == null)
                {
                    nearestFallback = path;
                }
                bool clear = path
                    .Zip(path.Skip(1), (start, end) => new { start, end })
                    .All(segment => SegmentClearOfObstacles(
                        segment.start,
                        segment.end,
                        obstacles,
                        clearance));
                if (clear)
                {
                    return path;
                }
            }
            // The union bounding-box dogleg already lies outside the blocking
            // cluster. Keep it as a deterministic final route even when a complex
            // linked-element outline makes the sampling check inconclusive.
            return nearestFallback ?? new List<Point2>
            {
                new Point2(head.U, 0),
                head
            };
        }

        private static List<Point2> FindSimpleMainDogleg(
            Point2 head,
            IList<List<Point2>> obstacles,
            double clearance,
            double routingOffset,
            double minimumTakeoffU,
            double maximumTakeoffU)
        {
            double teeApproach = 600.0 / MillimetersPerFoot;
            var routeUValues = new List<double>();
            var bendVValues = new List<double>();
            foreach (List<Point2> obstacle in obstacles)
            {
                foreach (Point2 vertex in obstacle)
                {
                    routeUValues.Add(vertex.U - routingOffset);
                    routeUValues.Add(vertex.U + routingOffset);
                    bendVValues.Add(vertex.V + (head.V >= 0 ? routingOffset : -routingOffset));
                }
            }

            List<Point2> best = null;
            double bestCost = double.MaxValue;
            foreach (double routeU in DistinctCoordinates(routeUValues)
                .Where(value => value >= minimumTakeoffU - 1e-8 &&
                    value <= maximumTakeoffU + 1e-8)
                .OrderBy(value => Math.Abs(value - head.U)))
            {
                foreach (double bendV in DistinctCoordinates(bendVValues)
                    .Where(value => head.V >= 0
                        ? value > 1e-8 && value <= head.V - teeApproach + 1e-8
                        : value < -1e-8 && value >= head.V + teeApproach - 1e-8)
                    .OrderBy(value => Math.Abs(value)))
                {
                    var candidate = RemoveCollinearPoints(new List<Point2>
                    {
                        new Point2(routeU, 0),
                        new Point2(routeU, bendV),
                        new Point2(head.U, bendV),
                        head
                    });
                    if (candidate.Count < 2 || candidate
                        .Zip(candidate.Skip(1), (start, end) => new { start, end })
                        .Any(segment => !SegmentClearOfObstacles(
                            segment.start,
                            segment.end,
                            obstacles,
                            clearance)))
                    {
                        continue;
                    }
                    double cost = 0;
                    for (int index = 1; index < candidate.Count; index++)
                    {
                        cost += Distance(candidate[index - 1], candidate[index]);
                    }
                    cost += Math.Abs(routeU - head.U) * 0.25;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = candidate;
                    }
                }
            }
            return best;
        }

        private static List<Point2> FindObstacleAvoidingOrthogonalPath(
            Point2 start,
            Point2 end,
            IList<List<Point2>> obstacles,
            double clearance,
            double minimumTakeoffU,
            double maximumTakeoffU)
        {
            double teeApproach = 600.0 / MillimetersPerFoot;
            bool directlyAligned = Math.Abs(start.U - end.U) < 1e-8 ||
                Math.Abs(start.V - end.V) < 1e-8;
            if (directlyAligned && SegmentClearOfObstacles(start, end, obstacles, clearance))
            {
                return new List<Point2> { start, end };
            }

            double routingOffset = clearance + 0.5 / MillimetersPerFoot;
            var uValues = new List<double> { start.U, end.U };
            var vValues = new List<double> { start.V, end.V };
            foreach (List<Point2> obstacle in obstacles)
            {
                foreach (Point2 vertex in obstacle)
                {
                    uValues.Add(vertex.U - routingOffset);
                    uValues.Add(vertex.U + routingOffset);
                    vValues.Add(vertex.V - routingOffset);
                    vValues.Add(vertex.V + routingOffset);
                }
            }
            uValues = DistinctCoordinates(uValues)
                .Where(value => value >= minimumTakeoffU - 1e-8 &&
                    value <= maximumTakeoffU + 1e-8)
                .ToList();
            vValues = DistinctCoordinates(vValues);

            var nodes = new List<Point2>();
            foreach (double u in uValues)
            {
                foreach (double v in vValues)
                {
                    var point = new Point2(u, v);
                    if (IsClearOfObstacles(point, obstacles, clearance))
                    {
                        nodes.Add(point);
                    }
                }
            }

            int startIndex = FindPointIndex(nodes, start);
            int endIndex = FindPointIndex(nodes, end);
            if (startIndex < 0 || endIndex < 0)
            {
                return null;
            }

            int count = nodes.Count;
            var distances = Enumerable.Repeat(double.MaxValue, count).ToArray();
            var previous = Enumerable.Repeat(-1, count).ToArray();
            var visited = new bool[count];
            distances[startIndex] = 0;
            for (int iteration = 0; iteration < count; iteration++)
            {
                int current = -1;
                double bestDistance = double.MaxValue;
                for (int index = 0; index < count; index++)
                {
                    if (!visited[index] && distances[index] < bestDistance)
                    {
                        bestDistance = distances[index];
                        current = index;
                    }
                }
                if (current < 0 || current == endIndex)
                {
                    break;
                }
                visited[current] = true;

                for (int candidate = 0; candidate < count; candidate++)
                {
                    if (candidate == current || visited[candidate])
                    {
                        continue;
                    }
                    bool aligned = Math.Abs(nodes[current].U - nodes[candidate].U) < 1e-8 ||
                        Math.Abs(nodes[current].V - nodes[candidate].V) < 1e-8;
                    bool horizontal = Math.Abs(nodes[current].V - nodes[candidate].V) < 1e-8;
                    if ((current == startIndex || candidate == endIndex) && horizontal)
                    {
                        // A route must leave the main and arrive at the sprinkler
                        // column in the branch direction. This guarantees a valid
                        // collinear run for the Tee at the head.
                        continue;
                    }
                    if (candidate == endIndex &&
                        Math.Abs(nodes[current].V - nodes[candidate].V) + 1e-8 < teeApproach)
                    {
                        // Leave enough straight DN25 run between the final elbow/
                        // reducer and the head so Revit can physically insert the
                        // Tee without colliding with the adjacent fitting.
                        continue;
                    }
                    if (!aligned || !SegmentClearOfObstacles(
                        nodes[current], nodes[candidate], obstacles, clearance))
                    {
                        continue;
                    }
                    double proposed = distances[current] +
                        Distance(nodes[current], nodes[candidate]) +
                        0.1 / MillimetersPerFoot;
                    if (proposed + 1e-8 < distances[candidate])
                    {
                        distances[candidate] = proposed;
                        previous[candidate] = current;
                    }
                }
            }
            if (distances[endIndex] == double.MaxValue)
            {
                return null;
            }

            var reversed = new List<Point2>();
            for (int index = endIndex; index >= 0; index = previous[index])
            {
                reversed.Add(nodes[index]);
                if (index == startIndex)
                {
                    break;
                }
            }
            reversed.Reverse();
            return RemoveCollinearPoints(reversed);
        }

        private static List<double> DistinctCoordinates(IEnumerable<double> values)
        {
            return values
                .GroupBy(value => Math.Round(value, 8))
                .Select(group => group.First())
                .OrderBy(value => value)
                .ToList();
        }

        private static int FindPointIndex(IList<Point2> points, Point2 target)
        {
            for (int index = 0; index < points.Count; index++)
            {
                if (SquaredDistance(points[index], target) < 1e-12)
                {
                    return index;
                }
            }
            return -1;
        }

        private static List<Point2> RemoveCollinearPoints(IList<Point2> points)
        {
            var result = new List<Point2>();
            foreach (Point2 point in points)
            {
                while (result.Count >= 2)
                {
                    Point2 first = result[result.Count - 2];
                    Point2 second = result[result.Count - 1];
                    bool sameU = Math.Abs(first.U - second.U) < 1e-8 &&
                        Math.Abs(second.U - point.U) < 1e-8;
                    bool sameV = Math.Abs(first.V - second.V) < 1e-8 &&
                        Math.Abs(second.V - point.V) < 1e-8;
                    if (!sameU && !sameV)
                    {
                        break;
                    }
                    result.RemoveAt(result.Count - 1);
                }
                result.Add(point);
            }
            return result;
        }

        private static List<double> GenerateSpacings(double minimum, double maximum)
        {
            double step = 100.0 / MillimetersPerFoot;
            var values = new List<double>();
            for (double value = maximum; value >= minimum - 1e-8; value -= step)
            {
                values.Add(Math.Max(value, minimum));
            }
            if (values.All(value => Math.Abs(value - minimum) > 1e-8))
            {
                values.Add(minimum);
            }
            return values;
        }

        private static List<double> OrderSpacingsForExtent(
            IEnumerable<double> values,
            double minimumCoordinate,
            double maximumCoordinate,
            double boundaryClearance,
            double maximumSpacing)
        {
            double usableLength = Math.Max(
                0,
                maximumCoordinate - minimumCoordinate - 2.0 * boundaryClearance);
            int intervalCount = Math.Max(
                1,
                (int)Math.Ceiling(usableLength / Math.Max(maximumSpacing, 1e-8)));
            double edgeAnchoredSpacing = usableLength / intervalCount;
            var candidates = values.ToList();
            double minimumSpacing = candidates.Count == 0 ? 0 : candidates.Min();
            if (edgeAnchoredSpacing > 1e-8 &&
                edgeAnchoredSpacing >= minimumSpacing - 1e-8 &&
                edgeAnchoredSpacing <= maximumSpacing + 1e-8)
            {
                candidates.Add(edgeAnchoredSpacing);
            }
            return candidates
                .GroupBy(value => Math.Round(value, 8))
                .Select(group => group.First())
                .OrderBy(value => Math.Abs(value - edgeAnchoredSpacing))
                .ThenByDescending(value => value)
                .ToList();
        }

        private static List<double> GeneratePhaseOffsets(
            double minimum,
            double maximum,
            double spacing,
            double boundaryClearance,
            double coverageRadius,
            int regularDivisions)
        {
            var values = new List<double>();
            for (int division = 0; division < regularDivisions; division++)
            {
                values.Add(spacing * division / regularDivisions);
            }

            // Add exact boundary-aware phases. Regular phase sampling can miss a
            // narrow valid interval: for example, one shift leaves the right edge
            // over 1500 mm while the next shift violates the left clearance. These
            // phases explicitly center the grid and place an outer row/column on
            // each coverage and clearance limit.
            double length = Math.Max(0, maximum - minimum);
            int intervals = Math.Max(0, (int)Math.Floor(length / spacing));
            double centeredFirst = minimum + (length - intervals * spacing) / 2.0;
            values.Add(NormalizePhase(centeredFirst, spacing));
            values.Add(NormalizePhase(minimum + boundaryClearance, spacing));
            values.Add(NormalizePhase(maximum - boundaryClearance, spacing));
            values.Add(NormalizePhase(minimum + coverageRadius, spacing));
            values.Add(NormalizePhase(maximum - coverageRadius, spacing));

            return values
                .GroupBy(value => Math.Round(value, 8))
                .Select(group => group.First())
                .OrderBy(value => value)
                .ToList();
        }

        private static double NormalizePhase(double value, double spacing)
        {
            double phase = value % spacing;
            return phase < 0 ? phase + spacing : phase;
        }

        private static bool IsBetter(LayoutCandidate candidate, LayoutCandidate best, int sampleCount)
        {
            if (best == null)
            {
                return true;
            }

            bool candidateKeepsGrid = candidate.ExpectedCenters > 0 &&
                candidate.MissingCenters == 0 && candidate.HasValidAxisSpacing;
            bool bestKeepsGrid = best.ExpectedCenters > 0 &&
                best.MissingCenters == 0 && best.HasValidAxisSpacing;
            if (candidateKeepsGrid != bestKeepsGrid)
            {
                return candidateKeepsGrid;
            }
            if (candidate.HasValidAxisSpacing != best.HasValidAxisSpacing)
            {
                return candidate.HasValidAxisSpacing;
            }

            double candidateMissingRatio = candidate.ExpectedCenters <= 0
                ? 1.0
                : (double)candidate.MissingCenters / candidate.ExpectedCenters;
            double bestMissingRatio = best.ExpectedCenters <= 0
                ? 1.0
                : (double)best.MissingCenters / best.ExpectedCenters;
            if (candidateMissingRatio < bestMissingRatio - 1e-8)
            {
                return true;
            }
            if (candidateMissingRatio > bestMissingRatio + 1e-8)
            {
                return false;
            }

            double candidateCoverage = sampleCount == 0 ? 0 : 100.0 * candidate.CoveredSamples / sampleCount;
            double bestCoverage = sampleCount == 0 ? 0 : 100.0 * best.CoveredSamples / sampleCount;
            bool candidateFullyCovered = sampleCount == 0 || candidate.CoveredSamples == sampleCount;
            bool bestFullyCovered = sampleCount == 0 || best.CoveredSamples == sampleCount;

            // Never let a higher sample score win by punching isolated holes in
            // an otherwise regular row/column grid. Once structural completeness
            // is equal, use sampled coverage and then the requested wide spacing.
            if (candidateFullyCovered != bestFullyCovered)
            {
                return candidateFullyCovered;
            }
            const double spacingTolerance = 1e-8;
            if (candidateFullyCovered)
            {
                if (candidate.USpacing > best.USpacing + spacingTolerance)
                {
                    return true;
                }
                if (candidate.USpacing < best.USpacing - spacingTolerance)
                {
                    return false;
                }
                if (candidate.VSpacing > best.VSpacing + spacingTolerance)
                {
                    return true;
                }
                if (candidate.VSpacing < best.VSpacing - spacingTolerance)
                {
                    return false;
                }
                return candidate.Centers.Count < best.Centers.Count;
            }

            // If the requested Min/Max range cannot achieve complete coverage,
            // retain the safest fallback: highest coverage first, then the larger
            // spacing and finally fewer heads. The command reports this clearly.
            if (candidateCoverage > bestCoverage + 0.05)
            {
                return true;
            }
            if (Math.Abs(candidateCoverage - bestCoverage) <= 0.05)
            {
                if (candidate.USpacing > best.USpacing + spacingTolerance)
                {
                    return true;
                }
                if (candidate.USpacing < best.USpacing - spacingTolerance)
                {
                    return false;
                }
                if (candidate.VSpacing > best.VSpacing + spacingTolerance)
                {
                    return true;
                }
                if (candidate.VSpacing < best.VSpacing - spacingTolerance)
                {
                    return false;
                }
                return candidate.Centers.Count < best.Centers.Count;
            }
            return false;
        }

        private static CenterGeneration GenerateCenters(
            IList<Point2> polygon,
            double uSpacing,
            double vSpacing,
            double uPhase,
            double vPhase,
            double clearance,
            double mainLength,
            double maximumAllowedSpacing,
            IList<List<Point2>> obstacles,
            IList<List<Point2>> excludedVoids,
            double obstacleClearance,
            bool adjustBlockedColumns)
        {
            double minU = polygon.Min(point => point.U);
            double maxU = polygon.Max(point => point.U);
            double minV = polygon.Min(point => point.V);
            double maxV = polygon.Max(point => point.V);
            double firstU = Math.Floor((minU - uPhase) / uSpacing) * uSpacing + uPhase;
            double firstV = Math.Floor((minV - vPhase) / vSpacing) * vSpacing + vPhase;
            var uValues = new List<double>();
            var vValues = new List<double>();
            for (double u = firstU; u <= maxU + 1e-8; u += uSpacing)
            {
                uValues.Add(u);
            }
            for (double v = firstV; v <= maxV + 1e-8; v += vSpacing)
            {
                vValues.Add(v);
            }

            var intended = new bool[uValues.Count, vValues.Count];
            int expectedCenters = 0;
            for (int uIndex = 0; uIndex < uValues.Count; uIndex++)
            {
                for (int vIndex = 0; vIndex < vValues.Count; vIndex++)
                {
                    var nominalPoint = new Point2(uValues[uIndex], vValues[vIndex]);
                    if (IsEligibleCenter(nominalPoint, polygon, clearance, mainLength) &&
                        !IsInsideAnyObstacle(nominalPoint, excludedVoids))
                    {
                        intended[uIndex, vIndex] = true;
                        expectedCenters++;
                    }
                }
            }

            var adjustedUValues = new List<double>(uValues);
            var adjustedVValues = new List<double>(vValues);
            if (adjustBlockedColumns && expectedCenters > 0)
            {
                // Coordinate descent keeps the grid topology fixed. A blocked
                // point is repaired by moving its complete row or column, never
                // by deleting only that sprinkler. Repeating both directions lets
                // a row shift unlock a better column shift (and vice versa).
                for (int pass = 0; pass < 3; pass++)
                {
                    AdjustRows(
                        uValues,
                        vValues,
                        adjustedUValues,
                        adjustedVValues,
                        intended,
                        polygon,
                        clearance,
                        mainLength,
                        obstacles,
                        obstacleClearance,
                        minV,
                        maxV,
                        vSpacing,
                        maximumAllowedSpacing);
                    AdjustColumns(
                        uValues,
                        vValues,
                        adjustedUValues,
                        adjustedVValues,
                        intended,
                        polygon,
                        clearance,
                        mainLength,
                        obstacles,
                        obstacleClearance,
                        minU,
                        maxU,
                        uSpacing,
                        maximumAllowedSpacing);
                }
            }

            var centers = new List<Point2>();
            for (int uIndex = 0; uIndex < adjustedUValues.Count; uIndex++)
            {
                for (int vIndex = 0; vIndex < adjustedVValues.Count; vIndex++)
                {
                    if (!intended[uIndex, vIndex])
                    {
                        continue;
                    }
                    var point = new Point2(adjustedUValues[uIndex], adjustedVValues[vIndex]);
                    if (IsValidCenter(
                        point,
                        polygon,
                        clearance,
                        mainLength,
                        obstacles,
                        obstacleClearance))
                    {
                        centers.Add(point);
                    }
                }
            }
            return new CenterGeneration
            {
                Centers = centers,
                ExpectedCenters = expectedCenters,
                MissingCenters = Math.Max(0, expectedCenters - centers.Count),
                HasValidAxisSpacing = HasValidAxisSpacing(
                    adjustedUValues,
                    adjustedVValues,
                    intended,
                    maximumAllowedSpacing)
            };
        }

        private static void AdjustRows(
            IList<double> nominalUValues,
            IList<double> nominalVValues,
            IList<double> adjustedUValues,
            IList<double> adjustedVValues,
            bool[,] intended,
            IList<Point2> polygon,
            double boundaryClearance,
            double mainLength,
            IList<List<Point2>> obstacles,
            double obstacleClearance,
            double minimumV,
            double maximumV,
            double spacing,
            double maximumAllowedSpacing)
        {
            for (int vIndex = 0; vIndex < nominalVValues.Count; vIndex++)
            {
                if (!HasIntendedRow(intended, nominalUValues.Count, vIndex))
                {
                    continue;
                }
                double nominal = nominalVValues[vIndex];
                double lowerLimit = vIndex == 0
                    ? minimumV
                    : (nominalVValues[vIndex - 1] + nominal) / 2.0;
                double upperLimit = vIndex == nominalVValues.Count - 1
                    ? maximumV
                    : (nominal + nominalVValues[vIndex + 1]) / 2.0;
                int previousRow = FindPreviousIntendedRow(
                    intended, nominalUValues.Count, vIndex);
                int nextRow = FindNextIntendedRow(
                    intended, nominalUValues.Count, nominalVValues.Count, vIndex);
                if (previousRow >= 0)
                {
                    upperLimit = Math.Min(
                        upperLimit,
                        adjustedVValues[previousRow] + maximumAllowedSpacing);
                }
                if (nextRow >= 0)
                {
                    lowerLimit = Math.Max(
                        lowerLimit,
                        adjustedVValues[nextRow] - maximumAllowedSpacing);
                }
                List<double> candidates = GenerateShiftCandidates(
                    nominal,
                    adjustedVValues[vIndex],
                    lowerLimit,
                    upperLimit,
                    spacing,
                    obstacleClearance);
                double bestValue = adjustedVValues[vIndex];
                int bestValid = CountValidRow(
                    adjustedUValues,
                    bestValue,
                    vIndex,
                    intended,
                    polygon,
                    boundaryClearance,
                    mainLength,
                    obstacles,
                    obstacleClearance);
                foreach (double candidate in candidates)
                {
                    int valid = CountValidRow(
                        adjustedUValues,
                        candidate,
                        vIndex,
                        intended,
                        polygon,
                        boundaryClearance,
                        mainLength,
                        obstacles,
                        obstacleClearance);
                    if (valid > bestValid ||
                        (valid == bestValid &&
                         Math.Abs(candidate - nominal) < Math.Abs(bestValue - nominal) - 1e-8))
                    {
                        bestValue = candidate;
                        bestValid = valid;
                    }
                }
                adjustedVValues[vIndex] = bestValue;
            }
        }

        private static void AdjustColumns(
            IList<double> nominalUValues,
            IList<double> nominalVValues,
            IList<double> adjustedUValues,
            IList<double> adjustedVValues,
            bool[,] intended,
            IList<Point2> polygon,
            double boundaryClearance,
            double mainLength,
            IList<List<Point2>> obstacles,
            double obstacleClearance,
            double minimumU,
            double maximumU,
            double spacing,
            double maximumAllowedSpacing)
        {
            for (int uIndex = 0; uIndex < nominalUValues.Count; uIndex++)
            {
                if (!HasIntendedColumn(intended, uIndex, nominalVValues.Count))
                {
                    continue;
                }
                double nominal = nominalUValues[uIndex];
                double lowerLimit = uIndex == 0
                    ? minimumU
                    : (nominalUValues[uIndex - 1] + nominal) / 2.0;
                double upperLimit = uIndex == nominalUValues.Count - 1
                    ? maximumU
                    : (nominal + nominalUValues[uIndex + 1]) / 2.0;
                int previousColumn = FindPreviousIntendedColumn(
                    intended, uIndex, nominalVValues.Count);
                int nextColumn = FindNextIntendedColumn(
                    intended, nominalUValues.Count, uIndex, nominalVValues.Count);
                if (previousColumn >= 0)
                {
                    upperLimit = Math.Min(
                        upperLimit,
                        adjustedUValues[previousColumn] + maximumAllowedSpacing);
                }
                if (nextColumn >= 0)
                {
                    lowerLimit = Math.Max(
                        lowerLimit,
                        adjustedUValues[nextColumn] - maximumAllowedSpacing);
                }
                List<double> candidates = GenerateShiftCandidates(
                    nominal,
                    adjustedUValues[uIndex],
                    lowerLimit,
                    upperLimit,
                    spacing,
                    obstacleClearance);
                double bestValue = adjustedUValues[uIndex];
                int bestValid = CountValidColumn(
                    bestValue,
                    adjustedVValues,
                    uIndex,
                    intended,
                    polygon,
                    boundaryClearance,
                    mainLength,
                    obstacles,
                    obstacleClearance);
                foreach (double candidate in candidates)
                {
                    int valid = CountValidColumn(
                        candidate,
                        adjustedVValues,
                        uIndex,
                        intended,
                        polygon,
                        boundaryClearance,
                        mainLength,
                        obstacles,
                        obstacleClearance);
                    if (valid > bestValid ||
                        (valid == bestValid &&
                         Math.Abs(candidate - nominal) < Math.Abs(bestValue - nominal) - 1e-8))
                    {
                        bestValue = candidate;
                        bestValid = valid;
                    }
                }
                adjustedUValues[uIndex] = bestValue;
            }
        }

        private static List<double> GenerateShiftCandidates(
            double nominal,
            double current,
            double lowerLimit,
            double upperLimit,
            double spacing,
            double obstacleClearance)
        {
            double shiftStep = 100.0 / MillimetersPerFoot;
            double maximumShift = Math.Min(
                spacing * 0.45,
                Math.Max(600.0 / MillimetersPerFoot, obstacleClearance + 250.0 / MillimetersPerFoot));
            var values = new List<double>();
            AddShiftCandidate(values, nominal, lowerLimit, upperLimit);
            AddShiftCandidate(values, current, lowerLimit, upperLimit);
            for (double shift = shiftStep; shift <= maximumShift + 1e-8; shift += shiftStep)
            {
                AddShiftCandidate(values, nominal - shift, lowerLimit, upperLimit);
                AddShiftCandidate(values, nominal + shift, lowerLimit, upperLimit);
            }
            return values;
        }

        private static void AddShiftCandidate(
            IList<double> values,
            double value,
            double lowerLimit,
            double upperLimit)
        {
            if (value < lowerLimit - 1e-8 || value > upperLimit + 1e-8 ||
                values.Any(existing => Math.Abs(existing - value) < 1e-8))
            {
                return;
            }
            values.Add(value);
        }

        private static bool HasIntendedRow(bool[,] intended, int uCount, int vIndex)
        {
            for (int uIndex = 0; uIndex < uCount; uIndex++)
            {
                if (intended[uIndex, vIndex])
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasIntendedColumn(bool[,] intended, int uIndex, int vCount)
        {
            for (int vIndex = 0; vIndex < vCount; vIndex++)
            {
                if (intended[uIndex, vIndex])
                {
                    return true;
                }
            }
            return false;
        }

        private static int FindPreviousIntendedRow(
            bool[,] intended,
            int uCount,
            int currentVIndex)
        {
            for (int vIndex = currentVIndex - 1; vIndex >= 0; vIndex--)
            {
                if (HasIntendedRow(intended, uCount, vIndex))
                {
                    return vIndex;
                }
            }
            return -1;
        }

        private static int FindNextIntendedRow(
            bool[,] intended,
            int uCount,
            int vCount,
            int currentVIndex)
        {
            for (int vIndex = currentVIndex + 1; vIndex < vCount; vIndex++)
            {
                if (HasIntendedRow(intended, uCount, vIndex))
                {
                    return vIndex;
                }
            }
            return -1;
        }

        private static int FindPreviousIntendedColumn(
            bool[,] intended,
            int currentUIndex,
            int vCount)
        {
            for (int uIndex = currentUIndex - 1; uIndex >= 0; uIndex--)
            {
                if (HasIntendedColumn(intended, uIndex, vCount))
                {
                    return uIndex;
                }
            }
            return -1;
        }

        private static int FindNextIntendedColumn(
            bool[,] intended,
            int uCount,
            int currentUIndex,
            int vCount)
        {
            for (int uIndex = currentUIndex + 1; uIndex < uCount; uIndex++)
            {
                if (HasIntendedColumn(intended, uIndex, vCount))
                {
                    return uIndex;
                }
            }
            return -1;
        }

        private static bool HasValidAxisSpacing(
            IList<double> uValues,
            IList<double> vValues,
            bool[,] intended,
            double maximumAllowedSpacing)
        {
            double previous = 0;
            bool hasPrevious = false;
            for (int uIndex = 0; uIndex < uValues.Count; uIndex++)
            {
                if (!HasIntendedColumn(intended, uIndex, vValues.Count))
                {
                    continue;
                }
                if (hasPrevious && uValues[uIndex] - previous > maximumAllowedSpacing + 1e-8)
                {
                    return false;
                }
                previous = uValues[uIndex];
                hasPrevious = true;
            }

            hasPrevious = false;
            for (int vIndex = 0; vIndex < vValues.Count; vIndex++)
            {
                if (!HasIntendedRow(intended, uValues.Count, vIndex))
                {
                    continue;
                }
                if (hasPrevious && vValues[vIndex] - previous > maximumAllowedSpacing + 1e-8)
                {
                    return false;
                }
                previous = vValues[vIndex];
                hasPrevious = true;
            }
            return true;
        }

        private static int CountValidRow(
            IList<double> uValues,
            double v,
            int vIndex,
            bool[,] intended,
            IList<Point2> polygon,
            double boundaryClearance,
            double mainLength,
            IList<List<Point2>> obstacles,
            double obstacleClearance)
        {
            int count = 0;
            for (int uIndex = 0; uIndex < uValues.Count; uIndex++)
            {
                if (intended[uIndex, vIndex] && IsValidCenter(
                    new Point2(uValues[uIndex], v),
                    polygon,
                    boundaryClearance,
                    mainLength,
                    obstacles,
                    obstacleClearance))
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountValidColumn(
            double u,
            IList<double> vValues,
            int uIndex,
            bool[,] intended,
            IList<Point2> polygon,
            double boundaryClearance,
            double mainLength,
            IList<List<Point2>> obstacles,
            double obstacleClearance)
        {
            int count = 0;
            for (int vIndex = 0; vIndex < vValues.Count; vIndex++)
            {
                if (intended[uIndex, vIndex] && IsValidCenter(
                    new Point2(u, vValues[vIndex]),
                    polygon,
                    boundaryClearance,
                    mainLength,
                    obstacles,
                    obstacleClearance))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsEligibleCenter(
            Point2 point,
            IList<Point2> polygon,
            double boundaryClearance,
            double mainLength)
        {
            return point.U >= -1e-8 &&
                point.U <= mainLength + 1e-8 &&
                IsInside(point, polygon) &&
                DistanceToPolygon(point, polygon) + 1e-8 >= boundaryClearance;
        }

        private static bool IsValidCenter(
            Point2 point,
            IList<Point2> polygon,
            double boundaryClearance,
            double mainLength,
            IList<List<Point2>> obstacles,
            double obstacleClearance)
        {
            return IsEligibleCenter(point, polygon, boundaryClearance, mainLength) &&
                IsClearOfObstacles(point, obstacles, obstacleClearance);
        }

        private static List<Point2> GenerateSamples(
            IList<Point2> polygon,
            IList<List<Point2>> obstacles,
            double step)
        {
            double minU = polygon.Min(point => point.U);
            double maxU = polygon.Max(point => point.U);
            double minV = polygon.Min(point => point.V);
            double maxV = polygon.Max(point => point.V);
            var samples = new List<Point2>();
            var keys = new HashSet<string>();
            for (double u = minU; u <= maxU + 1e-8; u += step)
            {
                for (double v = minV; v <= maxV + 1e-8; v += step)
                {
                    var point = new Point2(u, v);
                    if (IsInside(point, polygon) && !IsInsideAnyObstacle(point, obstacles))
                    {
                        AddUnique(samples, keys, point, step / 5.0);
                    }
                }
            }
            // Sample usable boundary portions at high resolution. Portions lying
            // physically inside a column/beam do not require spray coverage; the
            // separate extent check still enforces the outer 1500 mm limit.
            double boundaryStep = Math.Min(step, 100.0 / MillimetersPerFoot);
            for (int index = 0; index < polygon.Count; index++)
            {
                Point2 start = polygon[index];
                Point2 end = polygon[(index + 1) % polygon.Count];
                int divisions = Math.Max(1, (int)Math.Ceiling(Distance(start, end) / boundaryStep));
                for (int division = 0; division <= divisions; division++)
                {
                    double ratio = (double)division / divisions;
                    var point = new Point2(
                        start.U + (end.U - start.U) * ratio,
                        start.V + (end.V - start.V) * ratio);
                    if (!IsInsideAnyObstacle(point, obstacles))
                    {
                        AddUnique(samples, keys, point, boundaryStep / 5.0);
                    }
                }
            }
            return samples;
        }

        private static bool CoversBoundaryExtents(
            IList<Point2> centers,
            IList<Point2> polygon,
            double coverageRadius)
        {
            if (centers == null || centers.Count == 0 || polygon == null || polygon.Count == 0)
            {
                return false;
            }
            double minimumCenterU = centers.Min(point => point.U);
            double maximumCenterU = centers.Max(point => point.U);
            double minimumCenterV = centers.Min(point => point.V);
            double maximumCenterV = centers.Max(point => point.V);
            double tolerance = 1.0 / MillimetersPerFoot;
            return minimumCenterU - polygon.Min(point => point.U) <= coverageRadius + tolerance &&
                polygon.Max(point => point.U) - maximumCenterU <= coverageRadius + tolerance &&
                minimumCenterV - polygon.Min(point => point.V) <= coverageRadius + tolerance &&
                polygon.Max(point => point.V) - maximumCenterV <= coverageRadius + tolerance;
        }

        private static int CountCovered(IList<Point2> samples, IList<Point2> centers, double radius)
        {
            // Sprinkler coverage is evaluated as the aligned design cell: a
            // 1500 mm half-span permits 3000 mm head spacing and requires each
            // boundary to remain within 1500 mm. A circular distance test would
            // incorrectly reject the corners of every valid 3000 x 3000 cell.
            return samples.Count(sample => centers.Any(center =>
                Math.Abs(sample.U - center.U) <= radius + 1e-8 &&
                Math.Abs(sample.V - center.V) <= radius + 1e-8));
        }

        private static List<BranchGuideSegment> BuildRoutes(
            IList<Point2> centers,
            IList<Point2> polygon,
            LocalTransform transform,
            double mainLength,
            double boundaryClearance,
            IList<List<Point2>> obstacles,
            double obstacleClearance,
            out int branchCount,
            out int detourCount,
            out int routeFailures)
        {
            var segments = new List<BranchGuideSegment>();
            var columns = centers.GroupBy(point => Math.Round(point.U, 7)).OrderBy(group => group.Key).ToList();
            branchCount = columns.Count;
            detourCount = 0;
            routeFailures = 0;
            double routeClearance = Math.Max(
                20.0 / MillimetersPerFoot,
                Math.Min(boundaryClearance, 100.0 / MillimetersPerFoot));

            foreach (IGrouping<double, Point2> column in columns)
            {
                List<Point2> heads = column.OrderBy(point => point.V).ToList();
                bool hasDetour = false;
                Point2 previous = new Point2(heads[0].U, 0);
                if (!SegmentClearOfObstacles(previous, heads[0], obstacles, obstacleClearance))
                {
                    List<Point2> initialDetour = FindInitialObstacleDetour(
                        previous,
                        heads[0],
                        polygon,
                        routeClearance,
                        obstacles,
                        obstacleClearance,
                        mainLength);
                    if (initialDetour == null)
                    {
                        routeFailures++;
                    }
                    else
                    {
                        hasDetour = true;
                        for (int pathIndex = 1; pathIndex < initialDetour.Count; pathIndex++)
                        {
                            AddSegment(segments, transform, initialDetour[pathIndex - 1], initialDetour[pathIndex], BranchGuideKind.MainConnection);
                        }
                    }
                }
                else
                {
                    Point2 firstEntry = FindFirstInteriorPoint(
                        previous, heads[0], polygon, routeClearance, obstacles, obstacleClearance);
                    if (firstEntry != null && !SegmentInside(
                        firstEntry, heads[0], polygon, routeClearance, obstacles, obstacleClearance))
                    {
                            AddSegment(segments, transform, previous, firstEntry, BranchGuideKind.MainConnection);
                        List<Point2> entryDetour = FindThreeSegmentDetour(
                            firstEntry,
                            heads[0],
                            polygon,
                            routeClearance,
                            obstacles,
                            obstacleClearance);
                        if (entryDetour == null)
                        {
                            routeFailures++;
                        }
                        else
                        {
                            hasDetour = true;
                            for (int pathIndex = 1; pathIndex < entryDetour.Count; pathIndex++)
                            {
                                AddSegment(segments, transform, entryDetour[pathIndex - 1], entryDetour[pathIndex], BranchGuideKind.MainConnection);
                            }
                        }
                    }
                    else
                    {
                            AddSegment(segments, transform, previous, heads[0], BranchGuideKind.MainConnection);
                    }
                }
                previous = heads[0];

                for (int index = 1; index < heads.Count; index++)
                {
                    Point2 next = heads[index];
                    if (SegmentInside(
                        previous, next, polygon, routeClearance, obstacles, obstacleClearance))
                    {
                        AddSegment(segments, transform, previous, next, BranchGuideKind.Branch);
                    }
                    else
                    {
                        List<Point2> path = FindThreeSegmentDetour(
                            previous, next, polygon, routeClearance, obstacles, obstacleClearance);
                        if (path == null)
                        {
                            routeFailures++;
                        }
                        else
                        {
                            hasDetour = true;
                            for (int pathIndex = 1; pathIndex < path.Count; pathIndex++)
                            {
                                AddSegment(segments, transform, path[pathIndex - 1], path[pathIndex], BranchGuideKind.Detour);
                            }
                        }
                    }
                    previous = next;
                }
                if (hasDetour)
                {
                    detourCount++;
                }
            }
            return segments;
        }

        private static List<Point2> FindInitialObstacleDetour(
            Point2 originalTakeoff,
            Point2 firstHead,
            IList<Point2> polygon,
            double boundaryClearance,
            IList<List<Point2>> obstacles,
            double obstacleClearance,
            double mainLength)
        {
            var candidateTakeoffUValues = new List<double> { originalTakeoff.U };
            foreach (List<Point2> obstacle in obstacles)
            {
                foreach (Point2 vertex in obstacle)
                {
                    candidateTakeoffUValues.Add(vertex.U - obstacleClearance);
                    candidateTakeoffUValues.Add(vertex.U + obstacleClearance);
                }
            }

            List<Point2> best = null;
            double bestLength = double.MaxValue;
            foreach (double takeoffU in candidateTakeoffUValues
                .Distinct()
                .Where(value => value >= -1e-8 && value <= mainLength + 1e-8)
                .OrderBy(value => Math.Abs(value - originalTakeoff.U)))
            {
                var takeoff = new Point2(takeoffU, 0);
                var corner = new Point2(takeoffU, firstHead.V);
                if (!SegmentClearOfObstacles(takeoff, corner, obstacles, obstacleClearance) ||
                    !SegmentInside(corner, firstHead, polygon, boundaryClearance, obstacles, obstacleClearance))
                {
                    continue;
                }
                double length = Distance(takeoff, corner) + Distance(corner, firstHead);
                if (length < bestLength)
                {
                    bestLength = length;
                    best = new List<Point2> { takeoff, corner, firstHead };
                }
            }
            return best;
        }

        private static bool SegmentClearOfObstacles(
            Point2 start,
            Point2 end,
            IEnumerable<List<Point2>> obstacles,
            double obstacleClearance)
        {
            int divisions = Math.Max(2, (int)Math.Ceiling(Distance(start, end) / (25.0 / MillimetersPerFoot)));
            for (int index = 0; index <= divisions; index++)
            {
                double ratio = (double)index / divisions;
                var point = new Point2(
                    start.U + (end.U - start.U) * ratio,
                    start.V + (end.V - start.V) * ratio);
                if (!IsClearOfObstacles(point, obstacles, obstacleClearance))
                {
                    return false;
                }
            }
            return true;
        }

        private static Point2 FindFirstInteriorPoint(
            Point2 start,
            Point2 end,
            IList<Point2> polygon,
            double clearance,
            IList<List<Point2>> obstacles,
            double obstacleClearance)
        {
            int divisions = Math.Max(2, (int)Math.Ceiling(Distance(start, end) / (25.0 / MillimetersPerFoot)));
            for (int index = 0; index <= divisions; index++)
            {
                double ratio = (double)index / divisions;
                var point = new Point2(
                    start.U + (end.U - start.U) * ratio,
                    start.V + (end.V - start.V) * ratio);
                if (IsInside(point, polygon) &&
                    DistanceToPolygon(point, polygon) + 1e-8 >= clearance &&
                    IsClearOfObstacles(point, obstacles, obstacleClearance))
                {
                    return point;
                }
            }
            return null;
        }

        private static List<Point2> FindThreeSegmentDetour(
            Point2 start,
            Point2 end,
            IList<Point2> polygon,
            double clearance,
            IList<List<Point2>> obstacles,
            double obstacleClearance)
        {
            var candidateUValues = new List<double>();
            foreach (Point2 vertex in polygon)
            {
                candidateUValues.Add(vertex.U - clearance);
                candidateUValues.Add(vertex.U + clearance);
            }
            foreach (List<Point2> obstacle in obstacles)
            {
                foreach (Point2 vertex in obstacle)
                {
                    candidateUValues.Add(vertex.U - obstacleClearance);
                    candidateUValues.Add(vertex.U + obstacleClearance);
                }
            }
            List<Point2> best = null;
            double bestLength = double.MaxValue;
            foreach (double routeU in candidateUValues.Distinct().OrderBy(value => Math.Abs(value - start.U)))
            {
                var firstCorner = new Point2(routeU, start.V);
                var secondCorner = new Point2(routeU, end.V);
                if (!SegmentInside(start, firstCorner, polygon, clearance, obstacles, obstacleClearance) ||
                    !SegmentInside(firstCorner, secondCorner, polygon, clearance, obstacles, obstacleClearance) ||
                    !SegmentInside(secondCorner, end, polygon, clearance, obstacles, obstacleClearance))
                {
                    continue;
                }
                double length = Distance(start, firstCorner) +
                    Distance(firstCorner, secondCorner) + Distance(secondCorner, end);
                if (length < bestLength)
                {
                    bestLength = length;
                    best = new List<Point2> { start, firstCorner, secondCorner, end };
                }
            }
            return best;
        }

        private static bool SegmentInside(
            Point2 start,
            Point2 end,
            IList<Point2> polygon,
            double clearance,
            IList<List<Point2>> obstacles,
            double obstacleClearance)
        {
            int divisions = Math.Max(2, (int)Math.Ceiling(Distance(start, end) / (50.0 / MillimetersPerFoot)));
            for (int index = 0; index <= divisions; index++)
            {
                double ratio = (double)index / divisions;
                var point = new Point2(
                    start.U + (end.U - start.U) * ratio,
                    start.V + (end.V - start.V) * ratio);
                if (!IsInsideOrBoundary(point, polygon) ||
                    DistanceToPolygon(point, polygon) + 1e-8 < clearance ||
                    !IsClearOfObstacles(point, obstacles, obstacleClearance))
                {
                    return false;
                }
            }
            return true;
        }

        private static void AddSegment(
            ICollection<BranchGuideSegment> segments,
            LocalTransform transform,
            Point2 start,
            Point2 end,
            BranchGuideKind kind)
        {
            if (SquaredDistance(start, end) < 1e-10)
            {
                return;
            }
            segments.Add(new BranchGuideSegment
            {
                Start = transform.ToWorld(start),
                End = transform.ToWorld(end),
                Kind = kind
            });
        }

        private static bool IsInsideOrBoundary(Point2 point, IList<Point2> polygon)
        {
            return DistanceToPolygon(point, polygon) < 1e-8 || IsInside(point, polygon);
        }

        private static bool IsInsideAnyObstacle(
            Point2 point,
            IEnumerable<List<Point2>> obstacles)
        {
            return obstacles.Any(obstacle =>
                IsInside(point, obstacle) || DistanceToPolygon(point, obstacle) < 1e-8);
        }

        private static bool IsClearOfObstacles(
            Point2 point,
            IEnumerable<List<Point2>> obstacles,
            double clearance)
        {
            foreach (List<Point2> obstacle in obstacles)
            {
                if (IsInside(point, obstacle) ||
                    DistanceToPolygon(point, obstacle) + 1e-8 < clearance)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsInside(Point2 point, IList<Point2> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point2 current = polygon[i];
                Point2 previous = polygon[j];
                bool crosses = ((current.V > point.V) != (previous.V > point.V)) &&
                    point.U < (previous.U - current.U) * (point.V - current.V) /
                    (previous.V - current.V + 1e-12) + current.U;
                if (crosses)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static double DistanceToPolygon(Point2 point, IList<Point2> polygon)
        {
            double minimum = double.MaxValue;
            for (int index = 0; index < polygon.Count; index++)
            {
                Point2 start = polygon[index];
                Point2 end = polygon[(index + 1) % polygon.Count];
                double du = end.U - start.U;
                double dv = end.V - start.V;
                double lengthSquared = du * du + dv * dv;
                double ratio = lengthSquared < 1e-12
                    ? 0
                    : ((point.U - start.U) * du + (point.V - start.V) * dv) / lengthSquared;
                ratio = Math.Max(0, Math.Min(1, ratio));
                minimum = Math.Min(minimum, Distance(point, new Point2(
                    start.U + ratio * du,
                    start.V + ratio * dv)));
            }
            return minimum;
        }

        private static void AddUnique(ICollection<Point2> points, ISet<string> keys, Point2 point, double quantum)
        {
            string key = Math.Round(point.U / quantum) + ":" + Math.Round(point.V / quantum);
            if (keys.Add(key))
            {
                points.Add(point);
            }
        }

        private static double Distance(Point2 first, Point2 second)
        {
            return Math.Sqrt(SquaredDistance(first, second));
        }

        private static double SquaredDistance(Point2 first, Point2 second)
        {
            double du = first.U - second.U;
            double dv = first.V - second.V;
            return du * du + dv * dv;
        }

        private static XYZ PolygonCenter(IList<XYZ> polygon)
        {
            return new XYZ(
                polygon.Average(point => point.X),
                polygon.Average(point => point.Y),
                polygon.Average(point => point.Z));
        }

        private class LocalTransform
        {
            private readonly XYZ _origin;
            private readonly double _ux;
            private readonly double _uy;
            private readonly double _vx;
            private readonly double _vy;
            private readonly double _z;

            public LocalTransform(XYZ mainStart, XYZ mainEnd, XYZ polygonCenter)
            {
                _origin = mainStart;
                _z = mainStart.Z;
                double dx = mainEnd.X - mainStart.X;
                double dy = mainEnd.Y - mainStart.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-8)
                {
                    throw new InvalidOperationException("The main pipe guide is too short.");
                }
                _ux = dx / length;
                _uy = dy / length;
                MainLength = length;
                double vx = -_uy;
                double vy = _ux;
                double side = (polygonCenter.X - mainStart.X) * vx + (polygonCenter.Y - mainStart.Y) * vy;
                double sign = side >= 0 ? 1 : -1;
                _vx = vx * sign;
                _vy = vy * sign;
            }

            public double MainLength { get; private set; }

            public Point2 ToLocal(XYZ point)
            {
                double dx = point.X - _origin.X;
                double dy = point.Y - _origin.Y;
                return new Point2(dx * _ux + dy * _uy, dx * _vx + dy * _vy);
            }

            public XYZ ToWorld(Point2 point)
            {
                return new XYZ(
                    _origin.X + point.U * _ux + point.V * _vx,
                    _origin.Y + point.U * _uy + point.V * _vy,
                    _z);
            }
        }

        private class Point2
        {
            public Point2(double u, double v)
            {
                U = u;
                V = v;
            }
            public double U { get; private set; }
            public double V { get; private set; }
        }

        private class CenterGeneration
        {
            public List<Point2> Centers { get; set; }
            public int ExpectedCenters { get; set; }
            public int MissingCenters { get; set; }
            public bool HasValidAxisSpacing { get; set; }
        }

        private class LayoutCandidate
        {
            public List<Point2> Centers { get; set; }
            public int CoveredSamples { get; set; }
            public double USpacing { get; set; }
            public double VSpacing { get; set; }
            public double UPhase { get; set; }
            public double VPhase { get; set; }
            public int ExpectedCenters { get; set; }
            public int MissingCenters { get; set; }
            public bool HasValidAxisSpacing { get; set; }
        }
    }
}
