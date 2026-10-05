using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.AutoSprinkler
{
    internal class SmartCoveragePlan
    {
        public List<XYZ> NewCenters { get; set; }
        public int SampleCount { get; set; }
        public int CoveredSampleCount { get; set; }
        public int FixedCenterCount { get; set; }
        public double CoveragePercent { get; set; }
        public double MinimumNearestSpacing { get; set; }
        public double MaximumNearestSpacing { get; set; }
        public int RepairCenterCount { get; set; }
    }

    internal static class SmartCoveragePlanner
    {
        private const double MillimetersPerFoot = 304.8;

        public static SmartCoveragePlan Create(
            IList<XYZ> polygon,
            double coverageRadius,
            double minimumSpacing,
            double maximumSpacing,
            double boundaryClearance,
            IEnumerable<XYZ> fixedCenters)
        {
            double minX = polygon.Min(point => point.X);
            double maxX = polygon.Max(point => point.X);
            double minY = polygon.Min(point => point.Y);
            double maxY = polygon.Max(point => point.Y);
            double width = Math.Max(maxX - minX, 1e-6);
            double height = Math.Max(maxY - minY, 1e-6);
            double areaEstimate = width * height;

            double sampleStep = Math.Min(
                250.0 / MillimetersPerFoot,
                coverageRadius / 5.0);
            sampleStep = Math.Max(sampleStep, Math.Sqrt(areaEstimate / 15000.0));
            double candidateStep = Math.Min(
                300.0 / MillimetersPerFoot,
                coverageRadius / 4.0);
            candidateStep = Math.Max(candidateStep, Math.Sqrt(areaEstimate / 12000.0));

            List<XYZ> samples = GenerateCoverageSamples(polygon, sampleStep);
            List<XYZ> candidates = GenerateCandidates(polygon, candidateStep, boundaryClearance);
            List<XYZ> fixedList = (fixedCenters ?? Enumerable.Empty<XYZ>())
                .Where(point => IsPointInPolygon(point, polygon))
                .ToList();

            if (samples.Count == 0 || candidates.Count == 0)
            {
                return new SmartCoveragePlan
                {
                    NewCenters = new List<XYZ>(),
                    SampleCount = samples.Count,
                    CoveredSampleCount = 0,
                    FixedCenterCount = fixedList.Count,
                    CoveragePercent = 0
                };
            }

            double safetyRadius = coverageRadius;
            double safetyRadiusSquared = safetyRadius * safetyRadius;
            double actualRadiusSquared = coverageRadius * coverageRadius;
            var coverageCount = new int[samples.Count];

            foreach (XYZ fixedCenter in fixedList)
            {
                for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
                {
                    if (DistanceSquared2D(fixedCenter, samples[sampleIndex]) <= safetyRadiusSquared)
                    {
                        coverageCount[sampleIndex]++;
                    }
                }
            }

            var candidateCoverage = new List<int>[candidates.Count];
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                var covered = new List<int>();
                for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
                {
                    if (DistanceSquared2D(candidates[candidateIndex], samples[sampleIndex]) <= safetyRadiusSquared)
                    {
                        covered.Add(sampleIndex);
                    }
                }
                candidateCoverage[candidateIndex] = covered;
            }

            var selectedIndices = new List<int>();
            var selectedCenters = new List<XYZ>(fixedList);
            var unavailable = new bool[candidates.Count];
            double idealSpacing = Math.Min(maximumSpacing, coverageRadius * Math.Sqrt(3.0) * 0.95);
            idealSpacing = Math.Max(minimumSpacing, idealSpacing);

            while (coverageCount.Any(count => count == 0))
            {
                int bestIndex = -1;
                int bestGain = 0;
                double bestSpacingPenalty = double.MaxValue;
                double bestEdgeDistance = -1;

                for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    if (unavailable[candidateIndex])
                    {
                        continue;
                    }

                    XYZ candidate = candidates[candidateIndex];
                    double nearest = NearestDistance(candidate, selectedCenters);
                    if (selectedCenters.Count > 0 && nearest + 1e-8 < minimumSpacing)
                    {
                        continue;
                    }

                    int gain = 0;
                    foreach (int sampleIndex in candidateCoverage[candidateIndex])
                    {
                        if (coverageCount[sampleIndex] == 0)
                        {
                            gain++;
                        }
                    }
                    if (gain == 0)
                    {
                        continue;
                    }

                    double spacingPenalty = selectedCenters.Count == 0
                        ? 0
                        : Math.Abs(nearest - idealSpacing);
                    double edgeDistance = DistanceToPolygon(candidate, polygon);
                    bool better = gain > bestGain ||
                        (gain == bestGain && spacingPenalty < bestSpacingPenalty - 1e-8) ||
                        (gain == bestGain && Math.Abs(spacingPenalty - bestSpacingPenalty) < 1e-8 && edgeDistance > bestEdgeDistance);
                    if (better)
                    {
                        bestIndex = candidateIndex;
                        bestGain = gain;
                        bestSpacingPenalty = spacingPenalty;
                        bestEdgeDistance = edgeDistance;
                    }
                }

                if (bestIndex < 0)
                {
                    break;
                }

                unavailable[bestIndex] = true;
                selectedIndices.Add(bestIndex);
                selectedCenters.Add(candidates[bestIndex]);
                foreach (int sampleIndex in candidateCoverage[bestIndex])
                {
                    coverageCount[sampleIndex]++;
                }
            }

            // Greedy selection can block better later candidates. Swap out conflicting
            // selected centers whenever doing so reduces the uncovered sample count.
            for (int improvementPass = 0; improvementPass < 40 && coverageCount.Any(count => count == 0); improvementPass++)
            {
                int bestCandidate = -1;
                List<int> bestConflicts = null;
                int bestImprovement = 0;
                int bestResultingDelta = int.MaxValue;
                var uncoveredSamples = new HashSet<int>(Enumerable.Range(0, coverageCount.Length)
                    .Where(index => coverageCount[index] == 0));
                var selectedSet = new HashSet<int>(selectedIndices);
                List<int> promisingCandidates = Enumerable.Range(0, candidates.Count)
                    .Where(index => !selectedSet.Contains(index))
                    .Where(index => candidateCoverage[index].Any(uncoveredSamples.Contains))
                    .OrderByDescending(index => candidateCoverage[index].Count(uncoveredSamples.Contains))
                    .Take(400)
                    .ToList();

                foreach (int candidateIndex in promisingCandidates)
                {
                    XYZ candidate = candidates[candidateIndex];
                    if (fixedList.Any(center => Math.Sqrt(DistanceSquared2D(center, candidate)) + 1e-8 < minimumSpacing))
                    {
                        continue;
                    }

                    List<int> conflicts = selectedIndices
                        .Where(index => Math.Sqrt(DistanceSquared2D(candidates[index], candidate)) + 1e-8 < minimumSpacing)
                        .ToList();
                    var deltas = new Dictionary<int, int>();
                    foreach (int conflict in conflicts)
                    {
                        foreach (int sampleIndex in candidateCoverage[conflict])
                        {
                            deltas[sampleIndex] = deltas.ContainsKey(sampleIndex)
                                ? deltas[sampleIndex] - 1
                                : -1;
                        }
                    }
                    foreach (int sampleIndex in candidateCoverage[candidateIndex])
                    {
                        deltas[sampleIndex] = deltas.ContainsKey(sampleIndex)
                            ? deltas[sampleIndex] + 1
                            : 1;
                    }

                    int improvement = 0;
                    foreach (KeyValuePair<int, int> delta in deltas)
                    {
                        bool coveredBefore = coverageCount[delta.Key] > 0;
                        bool coveredAfter = coverageCount[delta.Key] + delta.Value > 0;
                        if (!coveredBefore && coveredAfter)
                        {
                            improvement++;
                        }
                        else if (coveredBefore && !coveredAfter)
                        {
                            improvement--;
                        }
                    }

                    int resultingDelta = 1 - conflicts.Count;
                    if (improvement > bestImprovement ||
                        (improvement == bestImprovement && improvement > 0 && resultingDelta < bestResultingDelta))
                    {
                        bestCandidate = candidateIndex;
                        bestConflicts = conflicts;
                        bestImprovement = improvement;
                        bestResultingDelta = resultingDelta;
                    }
                }

                if (bestCandidate < 0 || bestImprovement <= 0)
                {
                    break;
                }

                foreach (int conflict in bestConflicts)
                {
                    foreach (int sampleIndex in candidateCoverage[conflict])
                    {
                        coverageCount[sampleIndex]--;
                    }
                    selectedIndices.Remove(conflict);
                    unavailable[conflict] = false;
                }
                selectedIndices.Add(bestCandidate);
                unavailable[bestCandidate] = true;
                foreach (int sampleIndex in candidateCoverage[bestCandidate])
                {
                    coverageCount[sampleIndex]++;
                }
            }

            var repairIndices = new HashSet<int>();
            double repairMinimumSpacing = Math.Min(
                minimumSpacing,
                Math.Max(coverageRadius, minimumSpacing * 0.75));
            while (coverageCount.Any(count => count == 0))
            {
                int bestIndex = -1;
                int bestGain = 0;
                double bestSpacingPenalty = double.MaxValue;
                List<XYZ> currentCenters = fixedList
                    .Concat(selectedIndices.Select(index => candidates[index]))
                    .ToList();

                for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    if (selectedIndices.Contains(candidateIndex))
                    {
                        continue;
                    }

                    XYZ candidate = candidates[candidateIndex];
                    double nearest = NearestDistance(candidate, currentCenters);
                    if (currentCenters.Count > 0 && nearest + 1e-8 < repairMinimumSpacing)
                    {
                        continue;
                    }

                    int gain = candidateCoverage[candidateIndex]
                        .Count(sampleIndex => coverageCount[sampleIndex] == 0);
                    if (gain == 0)
                    {
                        continue;
                    }

                    double spacingPenalty = currentCenters.Count == 0
                        ? 0
                        : Math.Abs(nearest - minimumSpacing);
                    if (gain > bestGain || (gain == bestGain && spacingPenalty < bestSpacingPenalty))
                    {
                        bestIndex = candidateIndex;
                        bestGain = gain;
                        bestSpacingPenalty = spacingPenalty;
                    }
                }

                if (bestIndex < 0)
                {
                    break;
                }

                selectedIndices.Add(bestIndex);
                repairIndices.Add(bestIndex);
                foreach (int sampleIndex in candidateCoverage[bestIndex])
                {
                    coverageCount[sampleIndex]++;
                }
            }

            for (int selectedPosition = selectedIndices.Count - 1; selectedPosition >= 0; selectedPosition--)
            {
                int candidateIndex = selectedIndices[selectedPosition];
                bool redundant = candidateCoverage[candidateIndex]
                    .All(sampleIndex => coverageCount[sampleIndex] > 1);
                if (!redundant)
                {
                    continue;
                }

                foreach (int sampleIndex in candidateCoverage[candidateIndex])
                {
                    coverageCount[sampleIndex]--;
                }
                selectedIndices.RemoveAt(selectedPosition);
                repairIndices.Remove(candidateIndex);
            }

            List<XYZ> newCenters = selectedIndices.Select(index => candidates[index]).ToList();
            List<XYZ> allCenters = fixedList.Concat(newCenters).ToList();
            int actualCovered = samples.Count(sample =>
                allCenters.Any(center => DistanceSquared2D(center, sample) <= actualRadiusSquared));
            double minimumNearest;
            double maximumNearest;
            GetNearestSpacingRange(allCenters, out minimumNearest, out maximumNearest);

            var result = new SmartCoveragePlan
            {
                NewCenters = newCenters,
                SampleCount = samples.Count,
                CoveredSampleCount = actualCovered,
                FixedCenterCount = fixedList.Count,
                CoveragePercent = samples.Count == 0 ? 0 : 100.0 * actualCovered / samples.Count,
                MinimumNearestSpacing = minimumNearest,
                MaximumNearestSpacing = maximumNearest,
                RepairCenterCount = repairIndices.Count
            };

            // A preferred-spacing layout can paint itself into a corner. Compare it
            // with progressively relaxed global layouts, then keep the plan with
            // better coverage and, at equal coverage, fewer new heads.
            if (repairMinimumSpacing + 1e-8 < minimumSpacing)
            {
                double nextMinimumSpacing = Math.Max(
                    repairMinimumSpacing,
                    minimumSpacing * 0.95);
                SmartCoveragePlan relaxed = Create(
                    polygon,
                    coverageRadius,
                    nextMinimumSpacing,
                    maximumSpacing,
                    boundaryClearance,
                    fixedList);
                bool betterCoverage = relaxed.CoveragePercent > result.CoveragePercent + 0.01;
                bool equalCoverageWithFewerHeads =
                    Math.Abs(relaxed.CoveragePercent - result.CoveragePercent) <= 0.01 &&
                    relaxed.NewCenters.Count < result.NewCenters.Count;
                if (betterCoverage || equalCoverageWithFewerHeads)
                {
                    return relaxed;
                }
            }

            return result;
        }

        private static List<XYZ> GenerateCoverageSamples(IList<XYZ> polygon, double step)
        {
            double minX = polygon.Min(point => point.X);
            double maxX = polygon.Max(point => point.X);
            double minY = polygon.Min(point => point.Y);
            double maxY = polygon.Max(point => point.Y);
            var samples = new List<XYZ>();
            var keys = new HashSet<string>();

            for (double x = minX; x <= maxX + 1e-8; x += step)
            {
                for (double y = minY; y <= maxY + 1e-8; y += step)
                {
                    XYZ point = new XYZ(x, y, 0);
                    if (IsPointInPolygon(point, polygon))
                    {
                        AddUnique(samples, keys, point, step / 5.0);
                    }
                }
            }

            for (int i = 0; i < polygon.Count; i++)
            {
                XYZ start = polygon[i];
                XYZ end = polygon[(i + 1) % polygon.Count];
                double length = Math.Sqrt(DistanceSquared2D(start, end));
                int divisions = Math.Max(1, (int)Math.Ceiling(length / step));
                for (int division = 0; division <= divisions; division++)
                {
                    double ratio = (double)division / divisions;
                    XYZ point = new XYZ(
                        start.X + (end.X - start.X) * ratio,
                        start.Y + (end.Y - start.Y) * ratio,
                        0);
                    AddUnique(samples, keys, point, step / 5.0);
                }
            }

            return samples;
        }

        private static List<XYZ> GenerateCandidates(IList<XYZ> polygon, double step, double boundaryClearance)
        {
            double minX = polygon.Min(point => point.X);
            double maxX = polygon.Max(point => point.X);
            double minY = polygon.Min(point => point.Y);
            double maxY = polygon.Max(point => point.Y);
            var candidates = new List<XYZ>();

            for (double x = minX + step / 2.0; x <= maxX + 1e-8; x += step)
            {
                for (double y = minY + step / 2.0; y <= maxY + 1e-8; y += step)
                {
                    XYZ point = new XYZ(x, y, 0);
                    if (IsPointInPolygon(point, polygon) && DistanceToPolygon(point, polygon) + 1e-8 >= boundaryClearance)
                    {
                        candidates.Add(point);
                    }
                }
            }

            XYZ centroid = PolygonCentroid(polygon);
            if (IsPointInPolygon(centroid, polygon) && DistanceToPolygon(centroid, polygon) + 1e-8 >= boundaryClearance)
            {
                candidates.Add(centroid);
            }

            return candidates;
        }

        private static void AddUnique(List<XYZ> points, HashSet<string> keys, XYZ point, double quantum)
        {
            long x = (long)Math.Round(point.X / quantum);
            long y = (long)Math.Round(point.Y / quantum);
            string key = x + ":" + y;
            if (keys.Add(key))
            {
                points.Add(point);
            }
        }

        private static XYZ PolygonCentroid(IList<XYZ> polygon)
        {
            double signedArea = 0;
            double cx = 0;
            double cy = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                XYZ current = polygon[i];
                XYZ next = polygon[(i + 1) % polygon.Count];
                double cross = current.X * next.Y - next.X * current.Y;
                signedArea += cross;
                cx += (current.X + next.X) * cross;
                cy += (current.Y + next.Y) * cross;
            }

            if (Math.Abs(signedArea) < 1e-9)
            {
                return new XYZ(polygon.Average(point => point.X), polygon.Average(point => point.Y), 0);
            }
            return new XYZ(cx / (3 * signedArea), cy / (3 * signedArea), 0);
        }

        private static double NearestDistance(XYZ point, IList<XYZ> centers)
        {
            if (centers.Count == 0)
            {
                return double.MaxValue;
            }
            return Math.Sqrt(centers.Min(center => DistanceSquared2D(point, center)));
        }

        private static void GetNearestSpacingRange(IList<XYZ> centers, out double minimum, out double maximum)
        {
            minimum = 0;
            maximum = 0;
            if (centers.Count < 2)
            {
                return;
            }

            minimum = double.MaxValue;
            for (int i = 0; i < centers.Count; i++)
            {
                double nearest = double.MaxValue;
                for (int j = 0; j < centers.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }
                    nearest = Math.Min(nearest, Math.Sqrt(DistanceSquared2D(centers[i], centers[j])));
                }
                minimum = Math.Min(minimum, nearest);
                maximum = Math.Max(maximum, nearest);
            }
        }

        private static bool IsPointInPolygon(XYZ point, IList<XYZ> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                XYZ current = polygon[i];
                XYZ previous = polygon[j];
                bool intersects = ((current.Y > point.Y) != (previous.Y > point.Y)) &&
                    point.X < (previous.X - current.X) * (point.Y - current.Y) /
                    (previous.Y - current.Y + 1e-9) + current.X;
                if (intersects)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static double DistanceToPolygon(XYZ point, IList<XYZ> polygon)
        {
            double minimum = double.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                minimum = Math.Min(minimum, DistancePointToSegment2D(point, polygon[i], polygon[(i + 1) % polygon.Count]));
            }
            return minimum;
        }

        private static double DistancePointToSegment2D(XYZ point, XYZ start, XYZ end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-9)
            {
                return Math.Sqrt(DistanceSquared2D(point, start));
            }
            double ratio = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            ratio = Math.Max(0, Math.Min(1, ratio));
            double px = point.X - (start.X + ratio * dx);
            double py = point.Y - (start.Y + ratio * dy);
            return Math.Sqrt(px * px + py * py);
        }

        private static double DistanceSquared2D(XYZ first, XYZ second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }
    }
}
