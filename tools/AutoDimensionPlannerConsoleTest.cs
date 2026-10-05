using System;
using System.Collections.Generic;
using System.Linq;
using TungMEP.Modules.Dimensions;

internal static class AutoDimensionPlannerConsoleTest
{
    private static int _assertions;

    public static int Main()
    {
        try
        {
            TestRotatedProjectionRoundTrip();
            TestScanIntersectionAndNearTolerance();
            TestPointAndCollinearIntersections();
            TestSortAndDedupe();
            TestRotatedRowAndColumnGrouping();
            TestChainPlanningAndGapSplit();
            TestOuterOffsetCandidates();
            TestRotatedScanIntersection();
            Console.WriteLine(
                "Auto Dimension planner regression passed: " + _assertions + " assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Auto Dimension planner regression failed:");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void TestRotatedProjectionRoundTrip()
    {
        double diagonal = 1.0 / Math.Sqrt(2.0);
        var frame = new ProjectionFrame2(
            new Point2(10.0, -4.0),
            new Point2(diagonal, diagonal));
        Point2 world = frame.Unproject(12.5, -3.25);
        Projection2 projected = frame.Project(world);
        AssertClose(12.5, projected.Along, 1e-9, "Rotated along projection");
        AssertClose(-3.25, projected.Perpendicular, 1e-9, "Rotated perpendicular projection");
    }

    private static void TestScanIntersectionAndNearTolerance()
    {
        var items = new List<ProjectedItem>
        {
            new ProjectedItem(
                "Exact",
                new Point2(2.0, -2.0),
                new Point2(2.0, 2.0),
                "Grid"),
            new ProjectedItem(
                "Near",
                new Point2(5.0, 0.4),
                new Point2(5.0, 2.0),
                "Grid"),
            new ProjectedItem(
                "OutsideTolerance",
                new Point2(8.0, 0.6),
                new Point2(8.0, 2.0),
                "Grid"),
            ProjectedItem.FromPoint("PointOnScan", new Point2(9.0, 0.0), "Grid")
        };
        IList<ScanHit> hits = DimensionChainPlanner.FindScanHits(
            items,
            new Point2(0.0, 0.0),
            new Point2(10.0, 0.0),
            0.5);
        AssertEqual(3, hits.Count, "Scan hit count");
        AssertEqual("Exact", hits[0].Item.ReferenceKey, "First scan hit");
        AssertTrue(hits[0].ExactIntersection, "Crossing segment is exact");
        ScanHit near = hits.First(hit => hit.Item.ReferenceKey == "Near");
        AssertClose(0.4, near.DistanceToScan, 1e-9, "Near distance");
        AssertTrue(!near.ExactIntersection, "Near segment is not exact");
        AssertTrue(
            hits.All(hit => hit.Item.ReferenceKey != "OutsideTolerance"),
            "Outside-tolerance item is excluded");
    }

    private static void TestSortAndDedupe()
    {
        var exactItem = ProjectedItem.FromPoint("Exact", new Point2(2.0, 0.0), "A");
        var nearDuplicate = ProjectedItem.FromPoint("Duplicate", new Point2(2.08, 0.0), "A");
        var other = ProjectedItem.FromPoint("Other", new Point2(5.0, 0.0), "A");
        var hits = new List<ScanHit>
        {
            new ScanHit(other, other.Anchor, other.Anchor, 5.0, 0.0, true),
            new ScanHit(nearDuplicate, nearDuplicate.Anchor, nearDuplicate.Anchor, 2.08, 0.0, false),
            new ScanHit(exactItem, exactItem.Anchor, exactItem.Anchor, 2.0, 0.0, true)
        };
        IList<ScanHit> deduped = DimensionChainPlanner.SortAndDedupeHits(hits, 0.1);
        AssertEqual(2, deduped.Count, "Dedupe count");
        AssertEqual("Exact", deduped[0].Item.ReferenceKey, "Exact hit wins duplicate cluster");
        AssertClose(5.0, deduped[1].ScanCoordinate, 1e-9, "Dedupe remains sorted");
    }

    private static void TestPointAndCollinearIntersections()
    {
        var items = new List<ProjectedItem>
        {
            ProjectedItem.FromPoint("Point", new Point2(4.0, 0.0), "A"),
            new ProjectedItem(
                "Collinear",
                new Point2(6.0, 0.0),
                new Point2(8.0, 0.0),
                "A")
        };
        IList<ScanHit> hits = DimensionChainPlanner.FindScanHits(
            items,
            new Point2(0.0, 0.0),
            new Point2(10.0, 0.0),
            0.0);
        AssertEqual(2, hits.Count, "Point and collinear hit count");
        AssertTrue(hits.All(hit => hit.ExactIntersection), "Point and collinear hits are exact");
        AssertClose(4.0, hits[0].ScanCoordinate, 1e-9, "Point hit coordinate");
        AssertClose(7.0, hits[1].ScanCoordinate, 1e-9, "Collinear overlap midpoint");
    }

    private static void TestRotatedRowAndColumnGrouping()
    {
        double angle = Math.PI / 6.0;
        var frame = new ProjectionFrame2(
            new Point2(100.0, 50.0),
            new Point2(Math.Cos(angle), Math.Sin(angle)));
        var items = new List<ProjectedItem>
        {
            ProjectedItem.FromPoint("A", frame.Unproject(0.0, 0.0), string.Empty),
            ProjectedItem.FromPoint("B", frame.Unproject(10.0, 0.15), string.Empty),
            ProjectedItem.FromPoint("C", frame.Unproject(0.1, 5.0), string.Empty),
            ProjectedItem.FromPoint("D", frame.Unproject(10.1, 5.1), string.Empty)
        };
        IList<ProjectedItemGroup> rows = DimensionChainPlanner.GroupPointsIntoRows(
            items,
            frame,
            0.3);
        IList<ProjectedItemGroup> columns = DimensionChainPlanner.GroupPointsIntoColumns(
            items,
            frame,
            0.3);
        AssertEqual(2, rows.Count, "Rotated row count");
        AssertEqual(2, rows[0].Items.Count, "First rotated row size");
        AssertEqual("A", rows[0].Items[0].ReferenceKey, "Row items sort along axis");
        AssertEqual(2, columns.Count, "Rotated column count");
        AssertEqual(2, columns[1].Items.Count, "Second rotated column size");
        AssertEqual("B", columns[1].Items[0].ReferenceKey, "Column items sort perpendicular");
    }

    private static void TestChainPlanningAndGapSplit()
    {
        var hits = new List<ScanHit>();
        AddHit(hits, "A1", "A", 1.0, true);
        AddHit(hits, "A2", "A", 3.0, true);
        AddHit(hits, "A2Duplicate", "A", 3.05, false);
        AddHit(hits, "A3", "A", 5.0, true);
        AddHit(hits, "A4", "A", 12.0, true);
        AddHit(hits, "A5", "A", 14.0, true);
        AddHit(hits, "Single", "B", 8.0, true);
        var options = new DimensionChainPlannerOptions
        {
            DedupeTolerance = 0.1,
            HitTolerance = 0.0,
            MaximumChainGap = 4.0
        };
        IList<DimensionChainPlan> plans = DimensionChainPlanner.PlanChains(
            hits,
            new Point2(0.0, 0.0),
            new Point2(20.0, 0.0),
            options);
        AssertEqual(2, plans.Count, "Only usable chain groups remain");
        AssertEqual(3, plans[0].Hits.Count, "First gap-separated chain size");
        AssertEqual(2, plans[1].Hits.Count, "Second gap-separated chain size");
        AssertEqual("A2", plans[0].ReferenceKeys[1], "Duplicate reference coordinate removed");
        AssertTrue(plans.All(plan => plan.GroupKey == "A"), "Single-reference group omitted");
    }

    private static void TestOuterOffsetCandidates()
    {
        double angle = Math.PI / 5.0;
        var frame = new ProjectionFrame2(
            new Point2(-20.0, 30.0),
            new Point2(Math.Cos(angle), Math.Sin(angle)));
        var boundary = new List<Point2>
        {
            frame.Unproject(0.0, -2.0),
            frame.Unproject(10.0, 3.0)
        };
        IList<OuterOffsetCandidate> candidates =
            DimensionChainPlanner.CreateOuterOffsetCandidates(
                boundary,
                frame,
                2.0,
                1.0,
                2,
                1.0,
                OuterOffsetSide.Both);
        AssertEqual(4, candidates.Count, "Both-side offset candidate count");
        AssertClose(5.0, candidates[0].PerpendicularCoordinate, 1e-9, "Positive outer offset");
        AssertClose(-4.0, candidates[1].PerpendicularCoordinate, 1e-9, "Negative outer offset");
        AssertClose(6.0, candidates[2].PerpendicularCoordinate, 1e-9, "Second positive offset");
        Projection2 lineStart = frame.Project(candidates[0].LineStart);
        Projection2 lineEnd = frame.Project(candidates[0].LineEnd);
        AssertClose(-1.0, lineStart.Along, 1e-9, "Offset line start padding");
        AssertClose(11.0, lineEnd.Along, 1e-9, "Offset line end padding");
        AssertClose(5.0, lineStart.Perpendicular, 1e-9, "Offset line is parallel in rotated frame");
    }

    private static void TestRotatedScanIntersection()
    {
        double diagonal = 1.0 / Math.Sqrt(2.0);
        var frame = new ProjectionFrame2(
            new Point2(3.0, 7.0),
            new Point2(diagonal, diagonal));
        Point2 scanStart = frame.Unproject(0.0, 0.0);
        Point2 scanEnd = frame.Unproject(10.0, 0.0);
        var item = new ProjectedItem(
            "RotatedCrossing",
            frame.Unproject(5.0, -2.0),
            frame.Unproject(5.0, 2.0),
            "Grid");
        IList<ScanHit> hits = DimensionChainPlanner.FindScanHits(
            new[] { item },
            scanStart,
            scanEnd,
            1e-6);
        AssertEqual(1, hits.Count, "Rotated scan hit count");
        AssertTrue(hits[0].ExactIntersection, "Rotated axes intersect exactly");
        AssertClose(5.0, hits[0].ScanCoordinate, 1e-8, "Rotated scan scalar");
    }

    private static void AddHit(
        ICollection<ScanHit> hits,
        string referenceKey,
        string groupKey,
        double coordinate,
        bool exact)
    {
        ProjectedItem item = ProjectedItem.FromPoint(
            referenceKey,
            new Point2(coordinate, 0.0),
            groupKey);
        hits.Add(new ScanHit(
            item,
            item.Anchor,
            item.Anchor,
            coordinate,
            exact ? 0.0 : 0.01,
            exact));
    }

    private static void AssertEqual(int expected, int actual, string description)
    {
        _assertions++;
        if (expected != actual)
        {
            throw new InvalidOperationException(
                description + ": expected " + expected + ", actual " + actual + ".");
        }
    }

    private static void AssertEqual(string expected, string actual, string description)
    {
        _assertions++;
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                description + ": expected '" + expected + "', actual '" + actual + "'.");
        }
    }

    private static void AssertClose(
        double expected,
        double actual,
        double tolerance,
        string description)
    {
        _assertions++;
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                description + ": expected " + expected + ", actual " + actual + ".");
        }
    }

    private static void AssertTrue(bool value, string description)
    {
        _assertions++;
        if (!value)
        {
            throw new InvalidOperationException(description + ".");
        }
    }
}
