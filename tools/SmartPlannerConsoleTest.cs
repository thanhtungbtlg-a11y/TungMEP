using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TungMEP.Modules.AutoSprinkler;

namespace TungMEP.Tools
{
    internal static class SmartPlannerConsoleTest
    {
        private static int Main()
        {
            const double mm = 1.0 / 304.8;
            var polygon = new List<XYZ>
            {
                new XYZ(0 * mm, 1200 * mm, 0),
                new XYZ(1800 * mm, 10000 * mm, 0),
                new XYZ(11800 * mm, 10000 * mm, 0),
                new XYZ(15200 * mm, 6100 * mm, 0),
                new XYZ(14100 * mm, 3300 * mm, 0),
                new XYZ(8000 * mm, 0 * mm, 0),
                new XYZ(0 * mm, 2100 * mm, 0)
            };

            double bestCoverage = 0;
            foreach (double minimumSpacingMm in new[] { 2000.0, 1900.0, 1800.0, 1700.0, 1500.0 })
            {
                SmartCoveragePlan plan = SmartCoveragePlanner.Create(
                    polygon,
                    1500 * mm,
                    minimumSpacingMm * mm,
                    3000 * mm,
                    100 * mm,
                    new List<XYZ>());
                double minimumEdgeDistance = plan.NewCenters.Min(point => DistanceToPolygon(point, polygon));

                Console.WriteLine("--- Requested minimum spacing mm: " + minimumSpacingMm.ToString("0"));
                Console.WriteLine("Optimized heads: " + plan.NewCenters.Count);
                Console.WriteLine("Boundary repair heads: " + plan.RepairCenterCount);
                Console.WriteLine("Coverage percent: " + plan.CoveragePercent.ToString("0.00"));
                Console.WriteLine("Covered samples: " + plan.CoveredSampleCount + " / " + plan.SampleCount);
                Console.WriteLine("Minimum spacing mm: " + (plan.MinimumNearestSpacing * 304.8).ToString("0"));
                Console.WriteLine("Maximum spacing mm: " + (plan.MaximumNearestSpacing * 304.8).ToString("0"));
                Console.WriteLine("Minimum edge distance mm: " + (minimumEdgeDistance * 304.8).ToString("0"));
                bestCoverage = Math.Max(bestCoverage, plan.CoveragePercent);
            }
            return bestCoverage >= 99.0 ? 0 : 2;
        }

        private static double DistanceToPolygon(XYZ point, IList<XYZ> polygon)
        {
            double minimum = double.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                XYZ start = polygon[i];
                XYZ end = polygon[(i + 1) % polygon.Count];
                double dx = end.X - start.X;
                double dy = end.Y - start.Y;
                double lengthSquared = dx * dx + dy * dy;
                double ratio = lengthSquared < 1e-9
                    ? 0
                    : ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
                ratio = Math.Max(0, Math.Min(1, ratio));
                double px = point.X - (start.X + ratio * dx);
                double py = point.Y - (start.Y + ratio * dy);
                minimum = Math.Min(minimum, Math.Sqrt(px * px + py * py));
            }
            return minimum;
        }
    }
}
