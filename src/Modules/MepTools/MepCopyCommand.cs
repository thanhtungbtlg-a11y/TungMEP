using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;

namespace TungMEP.Modules.MepTools
{
    [Transaction(TransactionMode.Manual)]
    public class MepCopyCommand : IExternalCommand
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
                TaskDialog.Show("Tung MEP - MEP Copy", "Open a Revit project before running MEP Copy.");
                return Result.Cancelled;
            }

            List<ElementId> sourceIds = uiDocument.Selection.GetElementIds()
                .Where(id => IsMepCopyCandidate(document.GetElement(id)))
                .Distinct(new ElementIdComparer())
                .ToList();
            if (sourceIds.Count == 0)
            {
                try
                {
                    sourceIds = uiDocument.Selection
                        .PickObjects(ObjectType.Element, new MepCopySelectionFilter(), "Select MEP elements to copy")
                        .Select(reference => reference.ElementId)
                        .Distinct(new ElementIdComparer())
                        .ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }
            if (sourceIds.Count == 0)
            {
                TaskDialog.Show("Tung MEP - MEP Copy", "No MEP elements were selected.");
                return Result.Cancelled;
            }

            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToList();
            Level sourceLevel = FindSourceLevel(document, sourceIds, levels);
            using (var dialog = new MepCopyForm(levels, sourceLevel))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                List<double> offsets = dialog.CopyByLevels
                    ? BuildLevelOffsets(dialog.SelectedLevels, sourceLevel)
                    : BuildVerticalOffsets(dialog.QuantityUp, dialog.QuantityDown, dialog.DistanceMm);
                if (offsets.Count == 0)
                {
                    TaskDialog.Show("Tung MEP - MEP Copy", "Choose at least one destination level or enter a copy quantity.");
                    return Result.Cancelled;
                }

                int groupsCreated = 0;
                int elementsCreated = 0;
                int failed = 0;
                using (var transaction = new Transaction(document, "Tung MEP - MEP Copy"))
                {
                    transaction.Start();
                    foreach (double zOffset in offsets)
                    {
                        try
                        {
                            ICollection<ElementId> copied = ElementTransformUtils.CopyElements(
                                document,
                                sourceIds,
                                document,
                                Transform.CreateTranslation(new XYZ(0, 0, zOffset)),
                                new CopyPasteOptions());
                            groupsCreated++;
                            elementsCreated += copied.Count;
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                    transaction.Commit();
                }

                string result = "Created " + groupsCreated + " MEP copy group(s), " +
                    elementsCreated + " element(s).";
                if (failed > 0)
                {
                    result += "\nCopy positions skipped: " + failed + ".";
                }
                TaskDialog.Show("Tung MEP - MEP Copy", result);
                return groupsCreated > 0 ? Result.Succeeded : Result.Failed;
            }
        }

        private static List<double> BuildVerticalOffsets(int up, int down, double distanceMm)
        {
            var result = new List<double>();
            double distance = UnitUtils.ConvertToInternalUnits(distanceMm, UnitTypeId.Millimeters);
            for (int index = 1; index <= up; index++)
            {
                result.Add(distance * index);
            }
            for (int index = 1; index <= down; index++)
            {
                result.Add(-distance * index);
            }
            return result;
        }

        private static List<double> BuildLevelOffsets(IList<Level> targets, Level source)
        {
            if (source == null)
            {
                return new List<double>();
            }
            return targets
                .Select(level => level.Elevation - source.Elevation)
                .Where(offset => Math.Abs(offset) > 1e-7)
                .Distinct()
                .OrderBy(offset => offset)
                .ToList();
        }

        private static Level FindSourceLevel(
            Document document,
            IList<ElementId> ids,
            IList<Level> levels)
        {
            Level byElement = ids
                .Select(id => document.GetElement(id))
                .Where(element => element != null && element.LevelId != ElementId.InvalidElementId)
                .GroupBy(element => element.LevelId, new ElementIdComparer())
                .OrderByDescending(group => group.Count())
                .Select(group => document.GetElement(group.Key) as Level)
                .FirstOrDefault(level => level != null);
            if (byElement != null)
            {
                return byElement;
            }

            var elevations = new List<double>();
            foreach (ElementId id in ids)
            {
                Element element = document.GetElement(id);
                BoundingBoxXYZ box = element == null ? null : element.get_BoundingBox(null);
                if (box != null)
                {
                    elevations.Add((box.Min.Z + box.Max.Z) * 0.5);
                }
            }
            if (levels.Count == 0)
            {
                return null;
            }
            double elevation = elevations.Count == 0 ? levels[0].Elevation : elevations.Average();
            return levels.OrderBy(level => Math.Abs(level.Elevation - elevation)).First();
        }

        internal static bool IsMepCopyCandidate(Element element)
        {
            if (element == null || element is ElementType || element.ViewSpecific)
            {
                return false;
            }
            if (element is MEPCurve)
            {
                return true;
            }
            FamilyInstance family = element as FamilyInstance;
            if (family != null && family.MEPModel != null)
            {
                return true;
            }
            if (element.Category == null)
            {
                return false;
            }
            long categoryId = element.Category.Id.Value;
            return categoryId == (int)BuiltInCategory.OST_PipeInsulations ||
                   categoryId == (int)BuiltInCategory.OST_DuctInsulations ||
                   categoryId == (int)BuiltInCategory.OST_PipeFitting ||
                   categoryId == (int)BuiltInCategory.OST_DuctFitting ||
                   categoryId == (int)BuiltInCategory.OST_CableTrayFitting ||
                   categoryId == (int)BuiltInCategory.OST_ConduitFitting;
        }

        private sealed class MepCopySelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return IsMepCopyCandidate(element);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class ElementIdComparer : IEqualityComparer<ElementId>
        {
            public bool Equals(ElementId left, ElementId right)
            {
                return left != null && right != null && left.Value == right.Value;
            }

            public int GetHashCode(ElementId id)
            {
                return id == null ? 0 : id.Value.GetHashCode();
            }
        }
    }

    internal sealed class MepCopyForm : Form
    {
        private readonly TabControl _tabs;
        private readonly NumericUpDown _quantityUp;
        private readonly NumericUpDown _quantityDown;
        private readonly NumericUpDown _distance;
        private readonly CheckedListBox _levels;
        private readonly IList<Level> _levelItems;

        public MepCopyForm(IList<Level> levels, Level sourceLevel)
        {
            _levelItems = levels;
            Text = "Tung MEP - MEP Copy";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(430, 390);

            var sourceLabel = new Label
            {
                AutoSize = true,
                Location = new Point(12, 12),
                Text = "Source level: " + (sourceLevel == null ? "Not detected" : sourceLevel.Name)
            };
            Controls.Add(sourceLabel);

            _tabs = new TabControl { Location = new Point(12, 38), Size = new Size(406, 298) };
            var verticalTab = new TabPage("Vertical copies");
            var levelsTab = new TabPage("Copy to levels");
            _tabs.TabPages.Add(verticalTab);
            _tabs.TabPages.Add(levelsTab);
            Controls.Add(_tabs);

            verticalTab.Controls.Add(new Label { AutoSize = true, Location = new Point(18, 27), Text = "Quantity up" });
            _quantityUp = CreateWholeNumberBox(190, 23, 1);
            verticalTab.Controls.Add(_quantityUp);
            verticalTab.Controls.Add(new Label { AutoSize = true, Location = new Point(18, 67), Text = "Quantity down" });
            _quantityDown = CreateWholeNumberBox(190, 63, 0);
            verticalTab.Controls.Add(_quantityDown);
            verticalTab.Controls.Add(new Label { AutoSize = true, Location = new Point(18, 107), Text = "Distance (mm)" });
            _distance = new NumericUpDown
            {
                Location = new Point(190, 103),
                Size = new Size(150, 24),
                DecimalPlaces = 0,
                Minimum = 1,
                Maximum = 100000,
                Value = 3000,
                ThousandsSeparator = true
            };
            verticalTab.Controls.Add(_distance);
            verticalTab.Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(18, 157),
                Text = "Connected selected elements are copied as one group."
            });

            _levels = new CheckedListBox
            {
                CheckOnClick = true,
                Location = new Point(12, 12),
                Size = new Size(370, 220)
            };
            foreach (Level level in levels)
            {
                string elevation = UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Millimeters)
                    .ToString("0");
                int itemIndex = _levels.Items.Add(level.Name + "   (" + elevation + " mm)");
                if (sourceLevel != null && level.Id.Value == sourceLevel.Id.Value)
                {
                    _levels.SetItemCheckState(itemIndex, CheckState.Indeterminate);
                }
            }
            levelsTab.Controls.Add(_levels);

            var ok = new Button
            {
                Text = "Copy",
                DialogResult = DialogResult.OK,
                Location = new Point(250, 350),
                Size = new Size(80, 28)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(338, 350),
                Size = new Size(80, 28)
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public bool CopyByLevels { get { return _tabs.SelectedIndex == 1; } }
        public int QuantityUp { get { return Decimal.ToInt32(_quantityUp.Value); } }
        public int QuantityDown { get { return Decimal.ToInt32(_quantityDown.Value); } }
        public double DistanceMm { get { return Decimal.ToDouble(_distance.Value); } }
        public IList<Level> SelectedLevels
        {
            get
            {
                var result = new List<Level>();
                foreach (int index in _levels.CheckedIndices)
                {
                    if (_levels.GetItemCheckState(index) == CheckState.Checked)
                    {
                        result.Add(_levelItems[index]);
                    }
                }
                return result;
            }
        }

        private static NumericUpDown CreateWholeNumberBox(int x, int y, int value)
        {
            return new NumericUpDown
            {
                Location = new Point(x, y),
                Size = new Size(150, 24),
                Minimum = 0,
                Maximum = 100,
                Value = value
            };
        }
    }
}
