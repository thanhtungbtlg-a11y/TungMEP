using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TungMEP.Modules.AutoSprinkler;

namespace TungMEP.Tools
{
    internal static class AlignedBranchPlannerConsoleTest
    {
        private static int Main()
        {
            const double mm = 1.0 / 304.8;
            var polygon = new List<XYZ>
            {
                P(0, 0, mm), P(12000, 0, mm), P(12000, 4000, mm),
                P(7000, 4000, mm), P(7000, 6000, mm), P(12000, 6000, mm),
                P(12000, 10000, mm), P(0, 10000, mm)
            };
            var obstacles = new List<IList<XYZ>>
            {
                new List<XYZ>
                {
                    P(2000, 4500, mm), P(5000, 4500, mm),
                    P(5000, 5500, mm), P(2000, 5500, mm)
                },
                new List<XYZ>
                {
                    P(6000, 0, mm), P(9000, 0, mm),
                    P(9000, 1800, mm), P(6000, 1800, mm)
                }
            };
            AlignedBranchPlan plan = AlignedBranchPlanner.Create(
                polygon,
                P(-1000, -2000, mm),
                P(13000, -2000, mm),
                1500 * mm,
                2000 * mm,
                3000 * mm,
                100 * mm,
                obstacles,
                150 * mm);

            Console.WriteLine("Heads: " + plan.Centers.Count);
            Console.WriteLine("Branches: " + plan.BranchCount);
            Console.WriteLine("Detour branches: " + plan.DetourBranchCount);
            Console.WriteLine("Route failures: " + plan.RouteFailureCount);
            Console.WriteLine("Grid spacing mm: " + (plan.GridSpacing * 304.8).ToString("0"));
            Console.WriteLine("Coverage: " + plan.CoveragePercent.ToString("0.00") + "%");

            bool columnsAligned = plan.Centers
                .GroupBy(point => Math.Round(point.X * 304.8, 3))
                .All(group => group.Count() >= 1);
            bool spacingInRange = plan.GridSpacing >= 2000 * mm - 1e-8 &&
                plan.GridSpacing <= 3000 * mm + 1e-8;
            bool headsClear = plan.Centers.All(point => !InsideExpandedObstacle(point, 150));
            bool routesClear = plan.GuideSegments.All(segment => SegmentClear(segment, 150));
            int shiftedTakeoffs = plan.GuideSegments.Count(segment =>
                segment.Kind == BranchGuideKind.MainConnection &&
                Math.Abs(segment.Start.Y * 304.8 + 2000) < 1.0);
            bool takeoffsStayOnMain = plan.GuideSegments
                .Where(segment => Math.Abs(segment.Start.Y * 304.8 + 2000) < 1.0)
                .All(segment => segment.Start.X * 304.8 >= -1000 - 1.0 && segment.Start.X * 304.8 <= 13000 + 1.0);
            bool branchesRemainPerpendicularToMain = plan.GuideSegments
                .Where(segment => segment.Kind == BranchGuideKind.Branch)
                .All(segment => Math.Abs(segment.Start.X - segment.End.X) < 1e-8);
            AlignedBranchPlan voidPlan = AlignedBranchPlanner.Create(
                new List<XYZ> { P(0, 0, mm), P(8000, 0, mm), P(8000, 8000, mm), P(0, 8000, mm) },
                P(0, -1000, mm), P(8000, -1000, mm),
                1500 * mm, 2000 * mm, 3000 * mm, 100 * mm,
                new List<IList<XYZ>>
                {
                    new List<XYZ> { P(3000, 3000, mm), P(5000, 3000, mm), P(5000, 5000, mm), P(3000, 5000, mm) }
                },
                100 * mm);
            bool voidRemainsClear = voidPlan.Centers.All(point =>
                point.X * 304.8 < 2900 || point.X * 304.8 > 5100 || point.Y * 304.8 < 2900 || point.Y * 304.8 > 5100);
            int globalBranches;
            int globalDetours;
            int globalFailures;
            List<BranchGuideSegment> globalRoutes = AlignedBranchPlanner.CreateGlobalRoutes(
                new List<XYZ>
                {
                    P(2000, 1000, mm), P(2000, 4000, mm), P(2000, 6000, mm),
                    P(6000, 4000, mm), P(6000, 6000, mm)
                },
                P(0, 0, mm),
                P(10000, 0, mm),
                new List<IList<XYZ>>
                {
                    new List<XYZ>
                    {
                        P(5500, 500, mm), P(6500, 500, mm),
                        P(6500, 2500, mm), P(5500, 2500, mm)
                    }
                },
                150 * mm,
                out globalBranches,
                out globalDetours,
                out globalFailures);
            bool separatedAlignedRunsJoined = globalRoutes.Any(segment =>
                segment.Kind == BranchGuideKind.Branch &&
                Math.Abs(segment.Start.X * 304.8 - 2000) < 1.0 &&
                Math.Abs(segment.End.X * 304.8 - 2000) < 1.0 &&
                Math.Min(segment.Start.Y, segment.End.Y) * 304.8 < 1100 &&
                Math.Max(segment.Start.Y, segment.End.Y) * 304.8 > 3900);
            bool mainRouteDetoursObstacle = globalRoutes.Any(segment =>
                segment.Kind == BranchGuideKind.MainConnection &&
                Math.Abs(segment.Start.Y - segment.End.Y) < 1e-8);
            bool onlyBlueAndGreenKinds = globalRoutes.All(segment =>
                segment.Kind == BranchGuideKind.Branch ||
                segment.Kind == BranchGuideKind.MainConnection);
            bool alignedHeadConnectionsStayStraight = globalRoutes
                .Where(segment => segment.Kind == BranchGuideKind.Branch)
                .All(segment => Math.Abs(segment.Start.X - segment.End.X) < 1e-8);
            double mainDetourHorizontalLengthMm = globalRoutes
                .Where(segment => segment.Kind == BranchGuideKind.MainConnection &&
                    Math.Abs(segment.Start.Y - segment.End.Y) < 1e-8)
                .Sum(segment => Math.Abs(segment.End.X - segment.Start.X) * 304.8);
            bool mainDetourStaysLocal = mainDetourHorizontalLengthMm < 1000;
            int crossingBranches;
            int crossingDetours;
            int crossingFailures;
            List<BranchGuideSegment> crossingMainRoutes = AlignedBranchPlanner.CreateGlobalRoutes(
                new List<XYZ> { P(5000, 3000, mm) },
                P(0, 0, mm),
                P(10000, 0, mm),
                new List<IList<XYZ>>
                {
                    new List<XYZ>
                    {
                        P(4500, -300, mm), P(5500, -300, mm),
                        P(5500, 1200, mm), P(4500, 1200, mm)
                    }
                },
                150 * mm,
                out crossingBranches,
                out crossingDetours,
                out crossingFailures);
            bool mainCrossingObstacleStillRoutes = crossingBranches == 1 &&
                crossingDetours == 1 && crossingFailures == 0 && crossingMainRoutes.Count >= 3;
            bool structuredGridRegression = RunStructuredGridRegression(mm);
            Console.WriteLine("Heads clear obstacle: " + headsClear);
            Console.WriteLine("Routes clear obstacle: " + routesClear);
            Console.WriteLine("Shifted obstacle takeoffs: " + shiftedTakeoffs);
            Console.WriteLine("Takeoffs remain on main: " + takeoffsStayOnMain);
            Console.WriteLine("Branches remain perpendicular to main: " + branchesRemainPerpendicularToMain);
            Console.WriteLine("Heads avoid nested void: " + voidRemainsClear);
            Console.WriteLine("Separated aligned runs joined: " + separatedAlignedRunsJoined);
            Console.WriteLine("Main route detours obstacle: " + mainRouteDetoursObstacle);
            Console.WriteLine("Only branch/main route kinds: " + onlyBlueAndGreenKinds);
            Console.WriteLine("Aligned head connections stay straight: " + alignedHeadConnectionsStayStraight);
            Console.WriteLine("Main detour stays local: " + mainDetourStaysLocal);
            Console.WriteLine("Main-crossing obstacle still routes: " + mainCrossingObstacleStillRoutes);
            Console.WriteLine("Structured grid regression: " + structuredGridRegression);
            return plan.Centers.Count > 0 && columnsAligned && spacingInRange &&
                plan.DetourBranchCount >= 3 && plan.RouteFailureCount == 0 &&
                headsClear && routesClear && shiftedTakeoffs > 0 && takeoffsStayOnMain &&
                branchesRemainPerpendicularToMain && voidRemainsClear &&
                globalBranches == 2 && globalDetours >= 1 && globalFailures == 0 &&
                separatedAlignedRunsJoined && mainRouteDetoursObstacle && onlyBlueAndGreenKinds &&
                alignedHeadConnectionsStayStraight && mainDetourStaysLocal &&
                mainCrossingObstacleStillRoutes && structuredGridRegression ? 0 : 2;
        }

        private static bool RunStructuredGridRegression(double mm)
        {
            var rectangle = new List<XYZ>
            {
                P(0, 0, mm), P(21200, 0, mm), P(21200, 6200, mm), P(0, 6200, mm)
            };
            var noObstacles = new List<IList<XYZ>>();
            AlignedBranchPlan baseline = AlignedBranchPlanner.Create(
                rectangle,
                P(0, -1000, mm),
                P(21200, -1000, mm),
                1500 * mm,
                2000 * mm,
                3000 * mm,
                100 * mm,
                noObstacles,
                150 * mm);
            List<double> baseColumns = DistinctCoordinates(baseline.Centers, true);
            List<double> baseRows = DistinctCoordinates(baseline.Centers, false);
            bool baselineComplete = baseline.Centers.Count == 24 &&
                baseline.ExpectedGridIntersectionCount == 24 &&
                baseline.MissingGridIntersectionCount == 0 &&
                baseColumns.Count == 8 && baseRows.Count == 3 &&
                IsCartesian(baseline.Centers, baseColumns, baseRows) &&
                GapsWithinMaximum(baseColumns, 3000) &&
                GapsWithinMaximum(baseRows, 3000) &&
                EdgeGapsWithinRadius(baseColumns, 0, 21200, 1500) &&
                EdgeGapsWithinRadius(baseRows, 0, 6200, 1500) &&
                baseline.BranchCount == 8;
            if (!baselineComplete)
            {
                Console.WriteLine("  Baseline structured grid failed: heads=" + baseline.Centers.Count +
                    ", columns=" + baseColumns.Count + ", rows=" + baseRows.Count +
                    ", expected=" + baseline.ExpectedGridIntersectionCount +
                    ", missing=" + baseline.MissingGridIntersectionCount);
                return false;
            }

            double blockedColumn = baseColumns[3];
            var verticalObstacle = new List<IList<XYZ>>
            {
                new List<XYZ>
                {
                    P(blockedColumn - 50, 0, mm), P(blockedColumn + 50, 0, mm),
                    P(blockedColumn + 50, 6200, mm), P(blockedColumn - 50, 6200, mm)
                }
            };
            AlignedBranchPlan columnPlan = AlignedBranchPlanner.Create(
                rectangle,
                P(0, -1000, mm), P(21200, -1000, mm),
                1500 * mm, 2000 * mm, 3000 * mm, 100 * mm,
                verticalObstacle, 150 * mm);
            bool columnComplete = IsCompleteStructuredGrid(columnPlan, 8, 3, 3000) &&
                columnPlan.Centers.All(point =>
                    Math.Abs(point.X * 304.8 - blockedColumn) >= 200 - 0.5);

            double blockedRow = baseRows[1];
            var horizontalObstacle = new List<IList<XYZ>>
            {
                new List<XYZ>
                {
                    P(0, blockedRow - 50, mm), P(21200, blockedRow - 50, mm),
                    P(21200, blockedRow + 50, mm), P(0, blockedRow + 50, mm)
                }
            };
            AlignedBranchPlan rowPlan = AlignedBranchPlanner.Create(
                rectangle,
                P(0, -1000, mm), P(21200, -1000, mm),
                1500 * mm, 2000 * mm, 3000 * mm, 100 * mm,
                horizontalObstacle, 150 * mm);
            bool rowComplete = IsCompleteStructuredGrid(rowPlan, 8, 3, 3000) &&
                rowPlan.Centers.All(point =>
                    Math.Abs(point.Y * 304.8 - blockedRow) >= 200 - 0.5);

            var excludedVoid = new List<IList<XYZ>>
            {
                new List<XYZ>
                {
                    P(8500, 2200, mm), P(12700, 2200, mm),
                    P(12700, 4000, mm), P(8500, 4000, mm)
                }
            };
            AlignedBranchPlan voidGridPlan = AlignedBranchPlanner.Create(
                rectangle,
                P(0, -1000, mm), P(21200, -1000, mm),
                1500 * mm, 2000 * mm, 3000 * mm, 100 * mm,
                noObstacles, excludedVoid, 150 * mm);
            bool voidHandled = voidGridPlan.MissingGridIntersectionCount == 0 &&
                voidGridPlan.Centers.Count == voidGridPlan.ExpectedGridIntersectionCount &&
                voidGridPlan.Centers.All(point =>
                    point.X * 304.8 <= 8350 || point.X * 304.8 >= 12850 ||
                    point.Y * 304.8 <= 2050 || point.Y * 304.8 >= 4150);

            if (!columnComplete || !rowComplete || !voidHandled)
            {
                Console.WriteLine("  Column repair=" + columnComplete +
                    ", row repair=" + rowComplete + ", void mask=" + voidHandled);
                Console.WriteLine("  Column heads/expected/missing: " + columnPlan.Centers.Count + "/" +
                    columnPlan.ExpectedGridIntersectionCount + "/" + columnPlan.MissingGridIntersectionCount);
                Console.WriteLine("  Row heads/expected/missing: " + rowPlan.Centers.Count + "/" +
                    rowPlan.ExpectedGridIntersectionCount + "/" + rowPlan.MissingGridIntersectionCount);
            }
            return columnComplete && rowComplete && voidHandled;
        }

        private static bool IsCompleteStructuredGrid(
            AlignedBranchPlan plan,
            int expectedColumns,
            int expectedRows,
            double maximumGapMm)
        {
            List<double> columns = DistinctCoordinates(plan.Centers, true);
            List<double> rows = DistinctCoordinates(plan.Centers, false);
            return plan.MissingGridIntersectionCount == 0 &&
                plan.Centers.Count == plan.ExpectedGridIntersectionCount &&
                columns.Count == expectedColumns && rows.Count == expectedRows &&
                plan.Centers.Count == expectedColumns * expectedRows &&
                IsCartesian(plan.Centers, columns, rows) &&
                GapsWithinMaximum(columns, maximumGapMm) &&
                GapsWithinMaximum(rows, maximumGapMm);
        }

        private static List<double> DistinctCoordinates(IList<XYZ> points, bool useX)
        {
            return points
                .Select(point => Math.Round((useX ? point.X : point.Y) * 304.8, 1))
                .Distinct()
                .OrderBy(value => value)
                .ToList();
        }

        private static bool IsCartesian(
            IList<XYZ> points,
            IEnumerable<double> columns,
            IEnumerable<double> rows)
        {
            var keys = new HashSet<string>(points.Select(point =>
                Math.Round(point.X * 304.8, 1).ToString("0.0") + ":" +
                Math.Round(point.Y * 304.8, 1).ToString("0.0")));
            return columns.All(column => rows.All(row =>
                keys.Contains(column.ToString("0.0") + ":" + row.ToString("0.0"))));
        }

        private static bool GapsWithinMaximum(IList<double> values, double maximumMm)
        {
            for (int index = 1; index < values.Count; index++)
            {
                if (values[index] - values[index - 1] > maximumMm + 0.5)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool EdgeGapsWithinRadius(
            IList<double> values,
            double minimum,
            double maximum,
            double radiusMm)
        {
            return values.Count > 0 &&
                values[0] - minimum <= radiusMm + 0.5 &&
                maximum - values[values.Count - 1] <= radiusMm + 0.5;
        }

        private static bool SegmentClear(BranchGuideSegment segment, double clearanceMm)
        {
            double dx = segment.End.X - segment.Start.X;
            double dy = segment.End.Y - segment.Start.Y;
            double lengthMm = Math.Sqrt(dx * dx + dy * dy) * 304.8;
            int divisions = Math.Max(1, (int)Math.Ceiling(lengthMm / 25.0));
            for (int index = 0; index <= divisions; index++)
            {
                double ratio = (double)index / divisions;
                var point = new XYZ(
                    segment.Start.X + dx * ratio,
                    segment.Start.Y + dy * ratio,
                    0);
                if (InsideExpandedObstacle(point, clearanceMm - 0.1))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool InsideExpandedObstacle(XYZ point, double clearanceMm)
        {
            double x = point.X * 304.8;
            double y = point.Y * 304.8;
            bool middleObstacle = x > 2000 - clearanceMm && x < 5000 + clearanceMm &&
                y > 4500 - clearanceMm && y < 5500 + clearanceMm;
            bool initialObstacle = x > 6000 - clearanceMm && x < 9000 + clearanceMm &&
                y > 0 - clearanceMm && y < 1800 + clearanceMm;
            return middleObstacle || initialObstacle;
        }

        private static XYZ P(double x, double y, double mm)
        {
            return new XYZ(x * mm, y * mm, 0);
        }
    }
}
