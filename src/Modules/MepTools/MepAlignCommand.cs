using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;

namespace TungMEP.Modules.MepTools
{
    [Transaction(TransactionMode.Manual)]
    public class MepAlignCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            if (document == null || document.IsFamilyDocument)
            {
                TaskDialog.Show("Tung MEP - MEP Align", "Open a Revit project before running MEP Align.");
                return Result.Cancelled;
            }

            List<MEPCurve> curves = uiDocument.Selection.GetElementIds()
                .Select(id => document.GetElement(id) as MEPCurve)
                .Where(curve => curve != null)
                .GroupBy(curve => curve.Id.Value)
                .Select(group => group.First())
                .ToList();
            if (curves.Count < 2)
            {
                try
                {
                    curves = uiDocument.Selection
                        .PickObjects(ObjectType.Element, new MepCurveSelectionFilter(), "Select two or more MEP curves")
                        .Select(reference => document.GetElement(reference.ElementId) as MEPCurve)
                        .Where(curve => curve != null)
                        .GroupBy(curve => curve.Id.Value)
                        .Select(group => group.First())
                        .ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }
            if (curves.Count < 2)
            {
                TaskDialog.Show("Tung MEP - MEP Align", "Select at least two pipes, ducts, conduits, or cable trays.");
                return Result.Cancelled;
            }

            MEPCurve baseCurve;
            try
            {
                var allowedIds = new HashSet<long>(curves.Select(curve => curve.Id.Value));
                Reference picked = uiDocument.Selection.PickObject(
                    ObjectType.Element,
                    new MepCurveIdSelectionFilter(allowedIds),
                    "Select the base MEP curve");
                baseCurve = document.GetElement(picked.ElementId) as MEPCurve;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (baseCurve == null)
            {
                return Result.Cancelled;
            }

            using (var dialog = new MepAlignForm())
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                List<MEPCurve> targets = curves
                    .Where(curve => curve.Id.Value != baseCurve.Id.Value)
                    .ToList();
                int changed = 0;
                int skipped = 0;
                using (var transaction = new Transaction(document, "Tung MEP - MEP Align"))
                {
                    transaction.Start();
                    if (dialog.Mode == MepAlignMode.EqualSpacing)
                    {
                        ApplyEqualSpacing(document, baseCurve, targets, dialog.SpacingMm, ref changed, ref skipped);
                    }
                    else
                    {
                        foreach (MEPCurve target in targets)
                        {
                            XYZ translation;
                            bool valid = dialog.Mode == MepAlignMode.MatchElevation
                                ? TryBuildElevationTranslation(baseCurve, target, out translation)
                                : TryBuildAxisTranslation(baseCurve, target, out translation);
                            if (!valid || target.Pinned)
                            {
                                skipped++;
                                continue;
                            }
                            try
                            {
                                ElementTransformUtils.MoveElement(document, target.Id, translation);
                                changed++;
                            }
                            catch
                            {
                                skipped++;
                            }
                        }
                    }
                    transaction.Commit();
                }

                string result = "Aligned MEP curves: " + changed + ".";
                if (skipped > 0)
                {
                    result += "\nSkipped (pinned, non-straight, non-parallel, or constrained): " + skipped + ".";
                }
                TaskDialog.Show("Tung MEP - MEP Align", result);
                return changed > 0 ? Result.Succeeded : Result.Cancelled;
            }
        }

        private static void ApplyEqualSpacing(
            Document document,
            MEPCurve baseCurve,
            IList<MEPCurve> targets,
            double spacingMm,
            ref int changed,
            ref int skipped)
        {
            XYZ direction;
            XYZ normal;
            XYZ baseMidpoint;
            if (!TryGetPlanBasis(baseCurve, out direction, out normal, out baseMidpoint))
            {
                skipped += targets.Count;
                return;
            }

            var candidates = new List<SpacingCandidate>();
            foreach (MEPCurve target in targets)
            {
                XYZ targetDirection;
                XYZ unusedNormal;
                XYZ midpoint;
                if (!TryGetPlanBasis(target, out targetDirection, out unusedNormal, out midpoint) ||
                    Math.Abs(direction.DotProduct(targetDirection)) < 0.999 || target.Pinned)
                {
                    skipped++;
                    continue;
                }
                candidates.Add(new SpacingCandidate
                {
                    Curve = target,
                    CurrentDistance = (midpoint - baseMidpoint).DotProduct(normal)
                });
            }

            double spacing = UnitUtils.ConvertToInternalUnits(spacingMm, UnitTypeId.Millimeters);
            List<SpacingCandidate> positive = candidates
                .Where(item => item.CurrentDistance >= 0)
                .OrderBy(item => Math.Abs(item.CurrentDistance))
                .ToList();
            List<SpacingCandidate> negative = candidates
                .Where(item => item.CurrentDistance < 0)
                .OrderBy(item => Math.Abs(item.CurrentDistance))
                .ToList();
            MoveSpacingSide(document, positive, normal, spacing, 1, ref changed, ref skipped);
            MoveSpacingSide(document, negative, normal, spacing, -1, ref changed, ref skipped);
        }

        private static void MoveSpacingSide(
            Document document,
            IList<SpacingCandidate> candidates,
            XYZ normal,
            double spacing,
            int sign,
            ref int changed,
            ref int skipped)
        {
            for (int index = 0; index < candidates.Count; index++)
            {
                SpacingCandidate candidate = candidates[index];
                double desired = sign * spacing * (index + 1);
                XYZ translation = normal * (desired - candidate.CurrentDistance);
                try
                {
                    ElementTransformUtils.MoveElement(document, candidate.Curve.Id, translation);
                    changed++;
                }
                catch
                {
                    skipped++;
                }
            }
        }

        private static bool TryBuildAxisTranslation(MEPCurve baseCurve, MEPCurve target, out XYZ translation)
        {
            translation = XYZ.Zero;
            XYZ direction;
            XYZ normal;
            XYZ baseMidpoint;
            XYZ targetDirection;
            XYZ unusedNormal;
            XYZ targetMidpoint;
            if (!TryGetPlanBasis(baseCurve, out direction, out normal, out baseMidpoint) ||
                !TryGetPlanBasis(target, out targetDirection, out unusedNormal, out targetMidpoint) ||
                Math.Abs(direction.DotProduct(targetDirection)) < 0.999)
            {
                return false;
            }
            translation = normal * ((baseMidpoint - targetMidpoint).DotProduct(normal));
            return translation.GetLength() > 1e-8;
        }

        private static bool TryBuildElevationTranslation(MEPCurve baseCurve, MEPCurve target, out XYZ translation)
        {
            translation = XYZ.Zero;
            XYZ baseMidpoint;
            XYZ targetMidpoint;
            if (!TryGetMidpoint(baseCurve, out baseMidpoint) || !TryGetMidpoint(target, out targetMidpoint))
            {
                return false;
            }
            double offset = baseMidpoint.Z - targetMidpoint.Z;
            translation = new XYZ(0, 0, offset);
            return Math.Abs(offset) > 1e-8;
        }

        private static bool TryGetPlanBasis(
            MEPCurve curve,
            out XYZ direction,
            out XYZ normal,
            out XYZ midpoint)
        {
            direction = XYZ.Zero;
            normal = XYZ.Zero;
            midpoint = XYZ.Zero;
            LocationCurve location = curve.Location as LocationCurve;
            Line line = location == null ? null : location.Curve as Line;
            if (line == null)
            {
                return false;
            }
            XYZ start = line.GetEndPoint(0);
            XYZ end = line.GetEndPoint(1);
            XYZ projected = new XYZ(end.X - start.X, end.Y - start.Y, 0);
            if (projected.GetLength() < 1e-8)
            {
                return false;
            }
            direction = projected.Normalize();
            normal = new XYZ(-direction.Y, direction.X, 0);
            midpoint = (start + end) * 0.5;
            return true;
        }

        private static bool TryGetMidpoint(MEPCurve curve, out XYZ midpoint)
        {
            midpoint = XYZ.Zero;
            LocationCurve location = curve.Location as LocationCurve;
            Curve geometry = location == null ? null : location.Curve;
            if (geometry == null)
            {
                return false;
            }
            midpoint = (geometry.GetEndPoint(0) + geometry.GetEndPoint(1)) * 0.5;
            return true;
        }

        private sealed class SpacingCandidate
        {
            public MEPCurve Curve { get; set; }
            public double CurrentDistance { get; set; }
        }

        private sealed class MepCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element is MEPCurve;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class MepCurveIdSelectionFilter : ISelectionFilter
        {
            private readonly HashSet<long> _ids;

            public MepCurveIdSelectionFilter(HashSet<long> ids)
            {
                _ids = ids;
            }

            public bool AllowElement(Element element)
            {
                return element is MEPCurve && _ids.Contains(element.Id.Value);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }
    }

    internal enum MepAlignMode
    {
        MatchAxis,
        EqualSpacing,
        MatchElevation
    }

    internal sealed class MepAlignForm : Form
    {
        private readonly ComboBox _mode;
        private readonly NumericUpDown _spacing;
        private readonly Label _spacingLabel;

        public MepAlignForm()
        {
            Text = "Tung MEP - MEP Align";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(390, 205);

            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 23), Text = "Mode" });
            _mode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(145, 19),
                Size = new Size(225, 24)
            };
            _mode.Items.Add("Align center axes in plan");
            _mode.Items.Add("Set equal center spacing");
            _mode.Items.Add("Match center elevation");
            _mode.SelectedIndex = 0;
            _mode.SelectedIndexChanged += delegate { UpdateSpacingState(); };
            Controls.Add(_mode);

            _spacingLabel = new Label { AutoSize = true, Location = new Point(16, 67), Text = "Spacing (mm)" };
            Controls.Add(_spacingLabel);
            _spacing = new NumericUpDown
            {
                Location = new Point(145, 63),
                Size = new Size(150, 24),
                Minimum = 1,
                Maximum = 100000,
                Value = 300,
                ThousandsSeparator = true
            };
            Controls.Add(_spacing);
            Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(16, 103),
                Size = new Size(350, 45),
                Text = "Straight parallel MEP curves are supported. The selected base curve stays fixed."
            });

            var ok = new Button
            {
                Text = "Apply",
                DialogResult = DialogResult.OK,
                Location = new Point(202, 163),
                Size = new Size(80, 28)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(290, 163),
                Size = new Size(80, 28)
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            UpdateSpacingState();
        }

        public MepAlignMode Mode { get { return (MepAlignMode)_mode.SelectedIndex; } }
        public double SpacingMm { get { return Decimal.ToDouble(_spacing.Value); } }

        private void UpdateSpacingState()
        {
            bool enabled = _mode.SelectedIndex == (int)MepAlignMode.EqualSpacing;
            _spacing.Enabled = enabled;
            _spacingLabel.Enabled = enabled;
        }
    }
}
