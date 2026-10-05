using System;
using TungMEP.Modules.Tags;

internal static class AnnotationRouteGeometryConsoleTest
{
    private static int _assertions;

    public static int Main()
    {
        try
        {
            ProperCrossingIsAConflict();
            TJunctionIsAConflict();
            CollinearOverlapIsAConflict();
            SharedEndpointOnlyIsNotAConflict();
            SeparatedRoutesAreNotAConflict();
            PairSwapRemovesAvoidableCrossing();
            PairSwapAvoidsPreviouslyPlannedRoutes();
            Console.WriteLine(
                "Annotation route geometry regression passed: " +
                _assertions + " assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Annotation route geometry regression failed:");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void ProperCrossingIsAConflict()
    {
        AnnotationRoute2 horizontal = Route(
            Point(0, 0), Point(5, 0), Point(10, 0));
        AnnotationRoute2 vertical = Route(
            Point(5, -5), Point(5, 2), Point(5, 5));
        AssertTrue(horizontal.ConflictsWith(vertical),
            "Proper crossing must be detected");
    }

    private static void TJunctionIsAConflict()
    {
        AnnotationRoute2 horizontal = Route(
            Point(0, 0), Point(5, 0), Point(10, 0));
        AnnotationRoute2 touching = Route(
            Point(5, -5), Point(5, 0), Point(8, 0));
        AssertTrue(horizontal.ConflictsWith(touching),
            "Endpoint touching the middle of another leader must be detected");
    }

    private static void CollinearOverlapIsAConflict()
    {
        AnnotationRoute2 first = Route(
            Point(0, 0), Point(4, 0), Point(10, 0));
        AnnotationRoute2 second = Route(
            Point(5, 0), Point(8, 0), Point(12, 0));
        AssertTrue(first.ConflictsWith(second),
            "Collinear leader overlap must be detected");
    }

    private static void SharedEndpointOnlyIsNotAConflict()
    {
        AnnotationRoute2 first = Route(
            Point(0, 0), Point(0, 4), Point(4, 4));
        AnnotationRoute2 second = Route(
            Point(0, 0), Point(-4, 0), Point(-4, 4));
        AssertTrue(!first.ConflictsWith(second),
            "A shared tagged source endpoint alone is allowed");
    }

    private static void SeparatedRoutesAreNotAConflict()
    {
        AnnotationRoute2 first = Route(
            Point(0, 0), Point(0, 2), Point(4, 2));
        AnnotationRoute2 second = Route(
            Point(0, 5), Point(0, 7), Point(4, 7));
        AssertTrue(!first.ConflictsWith(second),
            "Separated leaders must not be reported as conflicts");
    }

    private static void PairSwapRemovesAvoidableCrossing()
    {
        AnnotationRoutePoint2[] sources =
        {
            Point(10, 0),
            Point(5, 4)
        };
        AnnotationRoutePoint2[] heads =
        {
            Point(0, 3),
            Point(0, 1)
        };
        int initialConflicts = AnnotationRouteAssignmentOptimizer.CountConflicts(
            sources,
            heads,
            new[] { 0, 1 },
            verticalStack: true);
        int[] assignment = AnnotationRouteAssignmentOptimizer.Optimize(
            sources,
            heads,
            verticalStack: true);
        int optimizedConflicts = AnnotationRouteAssignmentOptimizer.CountConflicts(
            sources,
            heads,
            assignment,
            verticalStack: true);

        AssertTrue(initialConflicts == 1,
            "Regression setup must start with one crossing");
        AssertTrue(assignment[0] == 1 && assignment[1] == 0,
            "The two source-to-head assignments should be swapped");
        AssertTrue(optimizedConflicts == 0,
            "Pair swapping must remove an avoidable crossing");
    }

    private static void PairSwapAvoidsPreviouslyPlannedRoutes()
    {
        AnnotationRoutePoint2[] sources =
        {
            Point(10, 0),
            Point(5, 4)
        };
        AnnotationRoutePoint2[] heads =
        {
            Point(0, 1),
            Point(0, 3)
        };
        AnnotationRoute2[] plannedRoutes =
        {
            Route(Point(8, 0.5), Point(8, 1.5), Point(8, 1.5)),
            Route(Point(7, 0.5), Point(7, 1.5), Point(7, 1.5))
        };

        int initialConflicts = AnnotationRouteAssignmentOptimizer.CountConflicts(
            sources,
            heads,
            new[] { 0, 1 },
            verticalStack: true,
            plannedRoutes: plannedRoutes);
        int[] assignment = AnnotationRouteAssignmentOptimizer.Optimize(
            sources,
            heads,
            verticalStack: true,
            plannedRoutes: plannedRoutes);
        int optimizedConflicts = AnnotationRouteAssignmentOptimizer.CountConflicts(
            sources,
            heads,
            assignment,
            verticalStack: true,
            plannedRoutes: plannedRoutes);

        AssertTrue(initialConflicts == 2,
            "Regression setup must cross both previously planned routes");
        AssertTrue(assignment[0] == 1 && assignment[1] == 0,
            "Assignment should swap to avoid the previously planned routes");
        AssertTrue(optimizedConflicts == 1,
            "The swap should reduce total route conflicts from two to one");
    }

    private static AnnotationRoutePoint2 Point(double x, double y)
    {
        return new AnnotationRoutePoint2(x, y);
    }

    private static AnnotationRoute2 Route(
        AnnotationRoutePoint2 start,
        AnnotationRoutePoint2 elbow,
        AnnotationRoutePoint2 end)
    {
        return new AnnotationRoute2(start, elbow, end);
    }

    private static void AssertTrue(bool condition, string description)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(description + ".");
        }
    }
}
