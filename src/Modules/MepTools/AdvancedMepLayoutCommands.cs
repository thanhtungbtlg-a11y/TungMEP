using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.MepTools
{
    [Transaction(TransactionMode.Manual)]
    public class AlignObjectsByBopCommand : IExternalCommand
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
                TaskDialog.Show("Tung MEP - Align Objects by BOP", "Open a Revit project first.");
                return Result.Cancelled;
            }

            List<Element> selected;
            try
            {
                selected = AdvancedMepLayoutSupport.CollectSelection(
                    uiDocument,
                    document,
                    2,
                    "Select pipes, ducts, cable trays, conduits, sprinklers, plumbing fixtures, or other movable MEP families");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (selected.Count < 2)
            {
                TaskDialog.Show("Tung MEP - Align Objects by BOP", "Select at least two supported objects.");
                return Result.Cancelled;
            }

            Element referenceElement;
            try
            {
                var ids = new HashSet<long>(selected.Select(element => element.Id.Value));
                Reference reference = uiDocument.Selection.PickObject(
                    ObjectType.Element,
                    new AllowedElementIdFilter(ids),
                    "Pick the reference object whose BOP will prefill the target elevation");
                referenceElement = document.GetElement(reference.ElementId);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            double referenceBop;
            if (!AdvancedMepLayoutSupport.TryGetBottomElevation(
                referenceElement,
                out referenceBop))
            {
                TaskDialog.Show(
                    "Tung MEP - Align Objects by BOP",
                    "The reference object does not expose usable model bounds.");
                return Result.Cancelled;
            }

            double targetBop;
            using (var form = new BopAlignmentForm(
                UnitUtils.ConvertFromInternalUnits(referenceBop, UnitTypeId.Millimeters),
                selected.Count))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                targetBop = UnitUtils.ConvertToInternalUnits(
                    form.TargetBopMm,
                    UnitTypeId.Millimeters);
            }

            int moved = 0;
            int unchanged = 0;
            int skipped = 0;
            string firstIssue = null;
            using (var transaction = new Transaction(
                document,
                "Tung MEP - Align Objects by BOP"))
            {
                transaction.Start();
                foreach (Element target in selected)
                {
                    double currentBop;
                    if (target.Pinned ||
                        !AdvancedMepLayoutSupport.TryGetBottomElevation(target, out currentBop))
                    {
                        skipped++;
                        continue;
                    }
                    double delta = targetBop - currentBop;
                    if (Math.Abs(delta) <= UnitUtils.ConvertToInternalUnits(
                        0.5,
                        UnitTypeId.Millimeters))
                    {
                        unchanged++;
                        continue;
                    }
                    using (var subTransaction = new SubTransaction(document))
                    {
                        subTransaction.Start();
                        try
                        {
                            ElementTransformUtils.MoveElement(
                                document,
                                target.Id,
                                XYZ.BasisZ * delta);
                            subTransaction.Commit();
                            moved++;
                        }
                        catch (Exception exception)
                        {
                            if (subTransaction.GetStatus() == TransactionStatus.Started)
                            {
                                subTransaction.RollBack();
                            }
                            skipped++;
                            if (string.IsNullOrEmpty(firstIssue))
                            {
                                firstIssue = exception.Message;
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

            string report = "Objects moved to common BOP: " + moved + ".\n" +
                "Already at target BOP: " + unchanged + ".\n" +
                "Skipped (pinned, hosted, connected, constrained, or invalid): " + skipped + ".\n" +
                "Target project BOP: " + Math.Round(
                    UnitUtils.ConvertFromInternalUnits(targetBop, UnitTypeId.Millimeters),
                    1) + " mm.";
            if (!string.IsNullOrEmpty(firstIssue))
            {
                report += "\n\nFirst issue: " + firstIssue;
            }
            TaskDialog.Show("Tung MEP - Align Objects by BOP", report);
            return moved > 0 || unchanged > 0 ? Result.Succeeded : Result.Cancelled;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class EqualSpacingCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            View view = document == null ? null : document.ActiveView;
            if (document == null || view == null || document.IsFamilyDocument)
            {
                TaskDialog.Show("Tung MEP - Equal Spacing", "Open a Revit project view first.");
                return Result.Cancelled;
            }
            List<Element> selected;
            try
            {
                selected = AdvancedMepLayoutSupport.CollectSelection(
                    uiDocument,
                    document,
                    3,
                    "Select three or more movable model objects to distribute");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (selected.Count < 3)
            {
                TaskDialog.Show("Tung MEP - Equal Spacing", "Select at least three supported objects.");
                return Result.Cancelled;
            }

            var centers = new Dictionary<Element, XYZ>();
            foreach (Element element in selected)
            {
                XYZ center;
                if (AdvancedMepLayoutSupport.TryGetCenter(element, out center))
                {
                    centers[element] = center;
                }
            }
            if (centers.Count < 3)
            {
                TaskDialog.Show("Tung MEP - Equal Spacing", "Fewer than three selected objects have usable model bounds.");
                return Result.Cancelled;
            }

            XYZ automaticAxis = ResolveAutomaticAxis(
                centers.Values,
                view.RightDirection,
                view.UpDirection);
            double automaticSpan = ProjectionSpan(centers.Values, automaticAxis);
            double defaultSpacingMm = UnitUtils.ConvertFromInternalUnits(
                automaticSpan / Math.Max(1, centers.Count - 1),
                UnitTypeId.Millimeters);
            EqualSpacingSettings settings;
            using (var form = new EqualSpacingForm(defaultSpacingMm))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                settings = form.Settings;
            }

            XYZ axis = settings.Direction == EqualSpacingDirection.ViewX
                ? AdvancedMepLayoutSupport.Normalize(view.RightDirection)
                : settings.Direction == EqualSpacingDirection.ViewY
                    ? AdvancedMepLayoutSupport.Normalize(view.UpDirection)
                    : settings.Direction == EqualSpacingDirection.WorldZ
                        ? XYZ.BasisZ
                        : automaticAxis;
            List<KeyValuePair<Element, XYZ>> ordered = centers
                .OrderBy(pair => pair.Value.DotProduct(axis))
                .ToList();
            double minimum = ordered.First().Value.DotProduct(axis);
            double maximum = ordered.Last().Value.DotProduct(axis);
            double spacing = settings.Mode == EqualSpacingMode.KeepFirstAndLast
                ? (maximum - minimum) / Math.Max(1, ordered.Count - 1)
                : UnitUtils.ConvertToInternalUnits(
                    settings.FixedSpacingMm,
                    UnitTypeId.Millimeters);

            int moved = 0;
            int unchanged = 0;
            int skipped = 0;
            string firstIssue = null;
            using (var transaction = new Transaction(document, "Tung MEP - Equal Spacing"))
            {
                transaction.Start();
                for (int index = 0; index < ordered.Count; index++)
                {
                    Element element = ordered[index].Key;
                    if (index == 0 ||
                        (settings.Mode == EqualSpacingMode.KeepFirstAndLast &&
                         index == ordered.Count - 1))
                    {
                        unchanged++;
                        continue;
                    }
                    if (element.Pinned)
                    {
                        skipped++;
                        continue;
                    }
                    double current = ordered[index].Value.DotProduct(axis);
                    double desired = minimum + spacing * index;
                    double delta = desired - current;
                    if (Math.Abs(delta) <= UnitUtils.ConvertToInternalUnits(
                        0.5,
                        UnitTypeId.Millimeters))
                    {
                        unchanged++;
                        continue;
                    }
                    using (var subTransaction = new SubTransaction(document))
                    {
                        subTransaction.Start();
                        try
                        {
                            ElementTransformUtils.MoveElement(
                                document,
                                element.Id,
                                axis * delta);
                            subTransaction.Commit();
                            moved++;
                        }
                        catch (Exception exception)
                        {
                            if (subTransaction.GetStatus() == TransactionStatus.Started)
                            {
                                subTransaction.RollBack();
                            }
                            skipped++;
                            if (string.IsNullOrEmpty(firstIssue))
                            {
                                firstIssue = exception.Message;
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

            string report = "Objects moved: " + moved + ".\n" +
                "Objects retained: " + unchanged + ".\n" +
                "Skipped (pinned, hosted, connected, or constrained): " + skipped + ".\n" +
                "Resulting center spacing: " + Math.Round(
                    UnitUtils.ConvertFromInternalUnits(spacing, UnitTypeId.Millimeters),
                    1) + " mm.";
            if (!string.IsNullOrEmpty(firstIssue))
            {
                report += "\n\nFirst issue: " + firstIssue;
            }
            TaskDialog.Show("Tung MEP - Equal Spacing", report);
            return moved > 0 || unchanged >= 3 ? Result.Succeeded : Result.Cancelled;
        }

        private static XYZ ResolveAutomaticAxis(
            IEnumerable<XYZ> centers,
            XYZ viewRight,
            XYZ viewUp)
        {
            XYZ right = AdvancedMepLayoutSupport.Normalize(viewRight);
            XYZ up = AdvancedMepLayoutSupport.Normalize(viewUp);
            return ProjectionSpan(centers, right) >= ProjectionSpan(centers, up)
                ? right
                : up;
        }

        private static double ProjectionSpan(IEnumerable<XYZ> points, XYZ axis)
        {
            List<double> values = points.Select(point => point.DotProduct(axis)).ToList();
            return values.Count == 0 ? 0.0 : values.Max() - values.Min();
        }
    }

    internal static class AdvancedMepLayoutSupport
    {
        private static readonly ISet<long> SupportedFamilyCategories =
            new HashSet<long>
            {
                (long)BuiltInCategory.OST_Sprinklers,
                (long)BuiltInCategory.OST_PlumbingFixtures,
                (long)BuiltInCategory.OST_MechanicalEquipment,
                (long)BuiltInCategory.OST_DuctTerminal,
                (long)BuiltInCategory.OST_PipeAccessory,
                (long)BuiltInCategory.OST_DuctAccessory,
                (long)BuiltInCategory.OST_ElectricalEquipment,
                (long)BuiltInCategory.OST_ElectricalFixtures,
                (long)BuiltInCategory.OST_LightingFixtures,
                (long)BuiltInCategory.OST_GenericModel,
                (long)BuiltInCategory.OST_SpecialityEquipment
            };

        public static List<Element> CollectSelection(
            UIDocument uiDocument,
            Document document,
            int minimum,
            string prompt)
        {
            List<Element> selected = uiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .Where(IsSupported)
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
            if (selected.Count >= minimum)
            {
                return selected;
            }
            return uiDocument.Selection.PickObjects(
                    ObjectType.Element,
                    new AdvancedMepObjectSelectionFilter(),
                    prompt)
                .Select(reference => document.GetElement(reference.ElementId))
                .Where(IsSupported)
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
        }

        public static bool IsSupported(Element element)
        {
            if (element == null || element.Location == null)
            {
                return false;
            }
            if (element is MEPCurve)
            {
                return true;
            }
            return element is FamilyInstance && element.Category != null &&
                SupportedFamilyCategories.Contains(element.Category.Id.Value);
        }

        public static bool TryGetBottomElevation(Element element, out double elevation)
        {
            elevation = 0.0;
            BoundingBoxXYZ box;
            try
            {
                box = element == null ? null : element.get_BoundingBox(null);
            }
            catch
            {
                return false;
            }
            if (box == null)
            {
                return false;
            }
            Transform transform = box.Transform ?? Transform.Identity;
            elevation = double.PositiveInfinity;
            for (int x = 0; x <= 1; x++)
            {
                for (int y = 0; y <= 1; y++)
                {
                    for (int z = 0; z <= 1; z++)
                    {
                        XYZ point = transform.OfPoint(new XYZ(
                            x == 0 ? box.Min.X : box.Max.X,
                            y == 0 ? box.Min.Y : box.Max.Y,
                            z == 0 ? box.Min.Z : box.Max.Z));
                        elevation = Math.Min(elevation, point.Z);
                    }
                }
            }
            return !double.IsInfinity(elevation) && !double.IsNaN(elevation);
        }

        public static bool TryGetCenter(Element element, out XYZ center)
        {
            center = null;
            BoundingBoxXYZ box;
            try
            {
                box = element == null ? null : element.get_BoundingBox(null);
            }
            catch
            {
                return false;
            }
            if (box == null)
            {
                return false;
            }
            center = (box.Transform ?? Transform.Identity).OfPoint(
                (box.Min + box.Max) * 0.5);
            return true;
        }

        public static XYZ Normalize(XYZ value)
        {
            return value == null || value.GetLength() <= 1e-9
                ? XYZ.BasisX
                : value.Normalize();
        }

        private sealed class AdvancedMepObjectSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element) { return IsSupported(element); }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }
    }

    internal sealed class AllowedElementIdFilter : ISelectionFilter
    {
        private readonly ISet<long> _ids;
        public AllowedElementIdFilter(ISet<long> ids) { _ids = ids; }
        public bool AllowElement(Element element)
        {
            return element != null && _ids.Contains(element.Id.Value);
        }
        public bool AllowReference(Reference reference, XYZ position) { return false; }
    }

    internal sealed class BopAlignmentForm : Form
    {
        private readonly NumericUpDown _target;
        public BopAlignmentForm(double referenceBopMm, int targetCount)
        {
            Text = "Tung MEP - Align Objects by BOP";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(460, 190);
            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(16, 22),
                Text = "Target project BOP (mm)"
            });
            _target = new NumericUpDown
            {
                Location = new Point(220, 18),
                Size = new Size(210, 24),
                Minimum = -1000000,
                Maximum = 1000000,
                DecimalPlaces = 1,
                Increment = 10,
                ThousandsSeparator = true,
                Value = ClampDecimal(referenceBopMm, -1000000, 1000000)
            };
            Controls.Add(_target);
            Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(16, 62),
                Size = new Size(414, 55),
                Text = "The picked reference BOP is prefilled and may be edited. All " + targetCount +
                    " selected object(s) are evaluated and move only in world Z. Connected, hosted, pinned, or constrained objects that Revit cannot move are skipped."
            });
            var apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, Location = new Point(262, 140), Size = new Size(80, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(350, 140), Size = new Size(80, 28) };
            Controls.Add(apply);
            Controls.Add(cancel);
            AcceptButton = apply;
            CancelButton = cancel;
        }
        public double TargetBopMm { get { return Decimal.ToDouble(_target.Value); } }
        private static decimal ClampDecimal(double value, decimal minimum, decimal maximum)
        {
            decimal converted;
            try { converted = (decimal)value; }
            catch { converted = 0; }
            return Math.Max(minimum, Math.Min(maximum, converted));
        }
    }

    internal enum EqualSpacingDirection
    {
        Auto,
        ViewX,
        ViewY,
        WorldZ
    }

    internal enum EqualSpacingMode
    {
        KeepFirstAndLast,
        FixedCenterSpacing
    }

    internal sealed class EqualSpacingSettings
    {
        public EqualSpacingDirection Direction { get; set; }
        public EqualSpacingMode Mode { get; set; }
        public double FixedSpacingMm { get; set; }
    }

    internal sealed class EqualSpacingForm : Form
    {
        private readonly ComboBox _direction;
        private readonly ComboBox _mode;
        private readonly NumericUpDown _spacing;

        public EqualSpacingForm(double defaultSpacingMm)
        {
            Text = "Tung MEP - Equal Spacing";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(500, 245);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 24), Text = "Distribution direction" });
            _direction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(205, 20), Size = new Size(265, 24) };
            _direction.Items.Add("Auto - longest view axis");
            _direction.Items.Add("View X / horizontal");
            _direction.Items.Add("View Y / vertical");
            _direction.Items.Add("World Z / elevation");
            _direction.SelectedIndex = 0;
            Controls.Add(_direction);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 66), Text = "Spacing mode" });
            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(205, 62), Size = new Size(265, 24) };
            _mode.Items.Add("Keep first and last fixed; distribute evenly");
            _mode.Items.Add("Fixed center-to-center spacing");
            _mode.SelectedIndex = 0;
            _mode.SelectedIndexChanged += delegate { UpdateState(); };
            Controls.Add(_mode);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 108), Text = "Center spacing (mm)" });
            _spacing = new NumericUpDown
            {
                Location = new Point(205, 104),
                Size = new Size(170, 24),
                Minimum = 1,
                Maximum = 1000000,
                DecimalPlaces = 1,
                Increment = 10,
                ThousandsSeparator = true,
                Value = Math.Max(1, Math.Min(1000000, (decimal)Math.Max(1.0, defaultSpacingMm)))
            };
            Controls.Add(_spacing);
            Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(16, 143),
                Size = new Size(454, 40),
                Text = "Objects are ordered by their model bounding-box centers. The first object remains fixed; in endpoint mode the last object also remains fixed."
            });
            var apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, Location = new Point(302, 198), Size = new Size(80, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(390, 198), Size = new Size(80, 28) };
            Controls.Add(apply);
            Controls.Add(cancel);
            AcceptButton = apply;
            CancelButton = cancel;
            UpdateState();
        }

        public EqualSpacingSettings Settings
        {
            get
            {
                return new EqualSpacingSettings
                {
                    Direction = (EqualSpacingDirection)_direction.SelectedIndex,
                    Mode = (EqualSpacingMode)_mode.SelectedIndex,
                    FixedSpacingMm = Decimal.ToDouble(_spacing.Value)
                };
            }
        }

        private void UpdateState()
        {
            _spacing.Enabled = _mode.SelectedIndex == (int)EqualSpacingMode.FixedCenterSpacing;
        }
    }
}
