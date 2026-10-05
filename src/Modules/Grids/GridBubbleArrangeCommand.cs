using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.Grids
{
    [Transaction(TransactionMode.Manual)]
    public class GridBubbleArrangeCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            View view = document == null ? null : document.ActiveView;
            if (document == null || view == null || document.IsFamilyDocument ||
                view.IsTemplate || view.ViewType == ViewType.ThreeD ||
                view.ViewType == ViewType.Schedule ||
                view.ViewType == ViewType.DrawingSheet)
            {
                TaskDialog.Show(
                    "Tung MEP - Grid Bubble Arrange",
                    "Open a plan, section, or elevation view before arranging Grid bubbles.");
                return Result.Cancelled;
            }

            List<Grid> selected = uiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .OfType<Grid>()
                .Where(grid => grid.CanBeVisibleInView(view))
                .GroupBy(grid => grid.Id.Value)
                .Select(group => group.First())
                .ToList();
            bool selectionScope = selected.Count > 0;
            List<Grid> grids = selectionScope
                ? selected
                : new FilteredElementCollector(document, view.Id)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Where(grid => grid.CanBeVisibleInView(view))
                    .ToList();
            if (grids.Count < 2)
            {
                TaskDialog.Show(
                    "Tung MEP - Grid Bubble Arrange",
                    "Select at least two Grids, or open a view containing at least two visible Grids.");
                return Result.Cancelled;
            }

            double paperSpacingMm;
            using (var form = new GridBubbleArrangeForm(
                grids.Count,
                selectionScope))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                paperSpacingMm = form.MinimumSpacingMm;
            }
            double modelSpacing = UnitUtils.ConvertToInternalUnits(
                paperSpacingMm * Math.Max(1, view.Scale),
                UnitTypeId.Millimeters);
            XYZ viewRight = Normalize(view.RightDirection, XYZ.BasisX);
            XYZ viewUp = Normalize(view.UpDirection, XYZ.BasisY);
            List<GridBubbleState> states = CollectBubbleStates(
                grids,
                view,
                viewRight,
                viewUp);
            List<List<GridBubbleState>> collisionGroups = BuildCollisionGroups(
                states,
                modelSpacing,
                viewRight,
                viewUp);
            if (collisionGroups.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Grid Bubble Arrange",
                    "No overlapping visible Grid bubbles were found at the selected " +
                    Math.Round(paperSpacingMm, 1) + " mm paper-space spacing.");
                return Result.Succeeded;
            }

            int moved = 0;
            int leadersCreated = 0;
            int failed = 0;
            string firstIssue = null;
            using (var transaction = new Transaction(
                document,
                "Tung MEP - Arrange Grid Bubbles"))
            {
                transaction.Start();
                foreach (List<GridBubbleState> group in collisionGroups)
                {
                    XYZ arrangeAxis = ResolveArrangeAxis(
                        group,
                        viewRight,
                        viewUp);
                    List<GridBubbleState> ordered = group
                        .OrderBy(state => state.BubblePoint.DotProduct(arrangeAxis))
                        .ToList();
                    double center = ordered.Average(state =>
                        state.BubblePoint.DotProduct(arrangeAxis));
                    for (int index = 0; index < ordered.Count; index++)
                    {
                        GridBubbleState state = ordered[index];
                        double desired = center +
                            (index - (ordered.Count - 1) * 0.5) * modelSpacing;
                        double current = state.BubblePoint.DotProduct(arrangeAxis);
                        double delta = desired - current;
                        if (Math.Abs(delta) <= UnitUtils.ConvertToInternalUnits(
                            0.1 * Math.Max(1, view.Scale),
                            UnitTypeId.Millimeters))
                        {
                            continue;
                        }
                        using (var subTransaction = new SubTransaction(document))
                        {
                            subTransaction.Start();
                            try
                            {
                                bool created = MoveBubbleWithLeader(
                                    state,
                                    view,
                                    arrangeAxis * delta);
                                subTransaction.Commit();
                                moved++;
                                if (created)
                                {
                                    leadersCreated++;
                                }
                            }
                            catch (Exception exception)
                            {
                                if (subTransaction.GetStatus() == TransactionStatus.Started)
                                {
                                    subTransaction.RollBack();
                                }
                                failed++;
                                if (string.IsNullOrEmpty(firstIssue))
                                {
                                    firstIssue = exception.Message;
                                }
                            }
                        }
                    }
                }
                if (moved == 0)
                {
                    transaction.RollBack();
                }
                else
                {
                    transaction.Commit();
                }
            }

            string report = "Collision groups found: " + collisionGroups.Count + ".\n" +
                "Grid bubbles rearranged: " + moved + ".\n" +
                "New 2D Grid leaders created: " + leadersCreated + ".\n" +
                "Skipped/failed bubble ends: " + failed + ".\n\n" +
                "The Grid model lines and existing Grid references were not moved.";
            if (!string.IsNullOrEmpty(firstIssue))
            {
                report += "\n\nFirst issue: " + firstIssue;
            }
            TaskDialog.Show("Tung MEP - Grid Bubble Arrange", report);
            return moved > 0 ? Result.Succeeded : Result.Cancelled;
        }

        private static List<GridBubbleState> CollectBubbleStates(
            IEnumerable<Grid> grids,
            View view,
            XYZ viewRight,
            XYZ viewUp)
        {
            var result = new List<GridBubbleState>();
            foreach (Grid grid in grids)
            {
                if (grid == null)
                {
                    continue;
                }
                Curve curve = GetVisibleCurve(grid, view);
                if (curve == null || !curve.IsBound)
                {
                    continue;
                }
                foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                {
                    try
                    {
                        if (!grid.HasBubbleInView(end, view) ||
                            !grid.IsBubbleVisibleInView(end, view))
                        {
                            continue;
                        }
                        Leader leader = grid.GetLeader(end, view);
                        int endpointIndex = end == DatumEnds.End0 ? 0 : 1;
                        XYZ tangent = curve.ComputeDerivatives(
                            endpointIndex,
                            true).BasisX;
                        tangent = ProjectToView(tangent, view.ViewDirection);
                        if (tangent.GetLength() <= 1e-9)
                        {
                            continue;
                        }
                        tangent = tangent.Normalize();
                        XYZ point = leader == null
                            ? curve.GetEndPoint(endpointIndex)
                            : leader.Anchor;
                        result.Add(new GridBubbleState
                        {
                            Grid = grid,
                            End = end,
                            BubblePoint = point,
                            VisibleCurve = curve.CreateTransformed(Transform.Identity),
                            Tangent = tangent,
                            Orientation = Math.Abs(tangent.DotProduct(viewUp)) >=
                                Math.Abs(tangent.DotProduct(viewRight))
                                ? GridOrientation.Vertical
                                : GridOrientation.Horizontal
                        });
                    }
                    catch
                    {
                    }
                }
            }
            return result;
        }

        private static List<List<GridBubbleState>> BuildCollisionGroups(
            IList<GridBubbleState> states,
            double spacing,
            XYZ viewRight,
            XYZ viewUp)
        {
            var result = new List<List<GridBubbleState>>();
            foreach (GridOrientation orientation in new[]
            {
                GridOrientation.Vertical,
                GridOrientation.Horizontal
            })
            {
                List<GridBubbleState> candidates = states
                    .Where(state => state.Orientation == orientation)
                    .ToList();
                var remaining = new HashSet<GridBubbleState>(candidates);
                while (remaining.Count > 0)
                {
                    GridBubbleState seed = remaining.First();
                    remaining.Remove(seed);
                    var group = new List<GridBubbleState> { seed };
                    var queue = new Queue<GridBubbleState>();
                    queue.Enqueue(seed);
                    while (queue.Count > 0)
                    {
                        GridBubbleState current = queue.Dequeue();
                        List<GridBubbleState> neighbors = remaining
                            .Where(candidate => ViewDistance(
                                current.BubblePoint,
                                candidate.BubblePoint,
                                viewRight,
                                viewUp) < spacing - 1e-8)
                            .ToList();
                        foreach (GridBubbleState neighbor in neighbors)
                        {
                            remaining.Remove(neighbor);
                            group.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                    if (group.Count > 1)
                    {
                        result.Add(group);
                    }
                }
            }
            return result;
        }

        private static bool MoveBubbleWithLeader(
            GridBubbleState state,
            View view,
            XYZ movement)
        {
            Grid grid = state.Grid;
            Document document = grid.Document;
            grid.SetDatumExtentType(
                state.End,
                view,
                DatumExtentType.ViewSpecific);
            if (state.VisibleCurve != null &&
                grid.IsCurveValidInView(
                    DatumExtentType.ViewSpecific,
                    view,
                    state.VisibleCurve))
            {
                grid.SetCurveInView(
                    DatumExtentType.ViewSpecific,
                    view,
                    state.VisibleCurve);
            }
            Leader leader = grid.GetLeader(state.End, view);
            bool created = leader == null;
            if (leader == null)
            {
                leader = grid.AddLeader(state.End, view);
                document.Regenerate();
                leader = grid.GetLeader(state.End, view);
            }
            if (leader == null)
            {
                throw new InvalidOperationException(
                    "Revit could not create a 2D Grid leader for Grid " +
                    grid.Name + ".");
            }

            // A Datum leader End must remain on the Grid curve. The previous
            // implementation moved End with the bubble, which made the leader
            // invalid. The bubble is the read-only Anchor; moving Elbow makes
            // Revit recompute Anchor while preserving the Grid curve and End.
            XYZ desiredAnchor = state.BubblePoint + movement;
            double targetTolerance = UnitUtils.ConvertToInternalUnits(
                0.05 * Math.Max(1, view.Scale),
                UnitTypeId.Millimeters);
            double initialError = ViewPlaneDistance(
                leader.Anchor,
                desiredAnchor,
                view.ViewDirection);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                leader = grid.GetLeader(state.End, view);
                if (leader == null)
                {
                    throw new InvalidOperationException(
                        "The 2D Grid leader disappeared while arranging Grid " +
                        grid.Name + ".");
                }

                XYZ residual = ProjectToView(
                    desiredAnchor - leader.Anchor,
                    view.ViewDirection);
                if (residual.GetLength() <= targetTolerance)
                {
                    break;
                }

                leader.Elbow = leader.Elbow + residual;
                if (!grid.IsLeaderValid(state.End, view, leader))
                {
                    throw new InvalidOperationException(
                        "Revit rejected the calculated Grid elbow position for Grid " +
                        grid.Name + ".");
                }

                grid.SetLeader(state.End, view, leader);
                document.Regenerate();
            }

            leader = grid.GetLeader(state.End, view);
            double finalError = leader == null
                ? double.MaxValue
                : ViewPlaneDistance(
                    leader.Anchor,
                    desiredAnchor,
                    view.ViewDirection);
            if (leader == null ||
                finalError >= initialError - 1e-9 ||
                finalError > Math.Max(targetTolerance * 4.0, movement.GetLength() * 0.35))
            {
                throw new InvalidOperationException(
                    "Revit did not move the Grid bubble to the calculated position for Grid " +
                    grid.Name + ".");
            }

            return created;
        }

        private static double ViewPlaneDistance(
            XYZ first,
            XYZ second,
            XYZ viewDirection)
        {
            return ProjectToView(first - second, viewDirection).GetLength();
        }

        private static Curve GetVisibleCurve(Grid grid, View view)
        {
            foreach (DatumExtentType extentType in new[]
            {
                DatumExtentType.ViewSpecific,
                DatumExtentType.Model
            })
            {
                try
                {
                    Curve curve = grid.GetCurvesInView(extentType, view)
                        .Where(item => item != null && item.IsBound)
                        .OrderByDescending(item => item.Length)
                        .FirstOrDefault();
                    if (curve != null)
                    {
                        return curve;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static XYZ ResolveArrangeAxis(
            IList<GridBubbleState> group,
            XYZ viewRight,
            XYZ viewUp)
        {
            return group[0].Orientation == GridOrientation.Vertical
                ? viewRight
                : viewUp;
        }

        private static double ViewDistance(
            XYZ first,
            XYZ second,
            XYZ viewRight,
            XYZ viewUp)
        {
            XYZ delta = first - second;
            double x = delta.DotProduct(viewRight);
            double y = delta.DotProduct(viewUp);
            return Math.Sqrt(x * x + y * y);
        }

        private static XYZ ProjectToView(XYZ value, XYZ viewDirection)
        {
            XYZ normal = Normalize(viewDirection, XYZ.BasisZ);
            return value - normal * value.DotProduct(normal);
        }

        private static XYZ Normalize(XYZ value, XYZ fallback)
        {
            return value == null || value.GetLength() <= 1e-9
                ? fallback
                : value.Normalize();
        }

        private enum GridOrientation
        {
            Vertical,
            Horizontal
        }

        private sealed class GridBubbleState
        {
            public Grid Grid { get; set; }
            public DatumEnds End { get; set; }
            public XYZ BubblePoint { get; set; }
            public Curve VisibleCurve { get; set; }
            public XYZ Tangent { get; set; }
            public GridOrientation Orientation { get; set; }
        }
    }

    internal sealed class GridBubbleArrangeForm : Form
    {
        private readonly NumericUpDown _spacing;

        public GridBubbleArrangeForm(int gridCount, bool selectionScope)
        {
            Text = "Tung MEP - Grid Bubble Arrange";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 230);
            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(18, 24),
                Text = "Scope"
            });
            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(185, 24),
                Text = (selectionScope ? "Preselected Grids: " : "Visible Grids: ") + gridCount
            });
            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(18, 67),
                Text = "Minimum bubble spacing (paper mm)"
            });
            _spacing = new NumericUpDown
            {
                Location = new Point(300, 63),
                Size = new Size(180, 24),
                Minimum = 2,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 0.5M,
                Value = 12.0M
            };
            Controls.Add(_spacing);
            Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(18, 108),
                Size = new Size(462, 60),
                Text = "Only visible bubble ends whose centers are closer than this paper-space distance are rearranged. TungMEP creates or edits view-specific Grid leaders; the model Grid line is not translated."
            });
            var apply = new Button
            {
                Text = "Arrange",
                DialogResult = DialogResult.OK,
                Location = new Point(312, 184),
                Size = new Size(80, 28)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(400, 184),
                Size = new Size(80, 28)
            };
            Controls.Add(apply);
            Controls.Add(cancel);
            AcceptButton = apply;
            CancelButton = cancel;
        }

        public double MinimumSpacingMm
        {
            get { return Decimal.ToDouble(_spacing.Value); }
        }
    }
}
