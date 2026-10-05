using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace TungMEP.Modules.AutoSprinkler
{
    internal class SprinklerSettings
    {
        public FamilySymbol Symbol { get; set; }
        public double CoverageRadiusMm { get; set; }
        public double MinimumSpacingMm { get; set; }
        public double MaximumSpacingMm { get; set; }
        public double BoundaryClearanceMm { get; set; }
        public double ObstacleClearanceMm { get; set; }
        public double HeightMm { get; set; }
        public double BranchOffsetMm { get; set; }
        public bool DrawBranchGuides { get; set; }
        public PipeType PipeType { get; set; }
        public PipingSystemType PipingSystemType { get; set; }
    }

    internal class SprinklerSettingsForm : System.Windows.Forms.Form
    {
        private readonly NumericUpDown _coverageRadiusInput;
        private readonly NumericUpDown _minimumSpacingInput;
        private readonly NumericUpDown _maximumSpacingInput;
        private readonly NumericUpDown _boundaryClearanceInput;
        private readonly NumericUpDown _obstacleClearanceInput;
        private readonly NumericUpDown _heightInput;
        private readonly NumericUpDown _branchOffsetInput;
        private readonly CheckBox _drawBranchGuidesCheckBox;

        public SprinklerSettingsForm(SprinklerSettings initialSettings)
        {
            Text = "Smart Auto Sprinkler - Tung MEP";
            Width = 470;
            Height = 390;
            MinimumSize = new Size(470, 390);
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 9
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _coverageRadiusInput = CreateNumberInput(500, 10000, 1500, 100);
            _minimumSpacingInput = CreateNumberInput(500, 10000, 2000, 100);
            _maximumSpacingInput = CreateNumberInput(500, 10000, 3000, 100);
            _boundaryClearanceInput = CreateNumberInput(50, 5000, 100, 50);
            _obstacleClearanceInput = CreateNumberInput(0, 5000, 150, 50);
            _heightInput = CreateNumberInput(0, 10000, 2340, 100);
            _branchOffsetInput = CreateNumberInput(50, 5000, 150, 50);
            _drawBranchGuidesCheckBox = new CheckBox
            {
                Text = "Show route guide lines (blue/red)",
                Dock = DockStyle.Fill,
                Checked = false
            };

            if (initialSettings != null)
            {
                SetNumberValue(_coverageRadiusInput, initialSettings.CoverageRadiusMm);
                SetNumberValue(_minimumSpacingInput, initialSettings.MinimumSpacingMm);
                SetNumberValue(_maximumSpacingInput, initialSettings.MaximumSpacingMm);
                SetNumberValue(_boundaryClearanceInput, initialSettings.BoundaryClearanceMm);
                SetNumberValue(_obstacleClearanceInput, initialSettings.ObstacleClearanceMm);
                SetNumberValue(_heightInput, initialSettings.HeightMm);
                SetNumberValue(_branchOffsetInput, initialSettings.BranchOffsetMm);
                _drawBranchGuidesCheckBox.Checked = initialSettings.DrawBranchGuides;
            }

            AddRow(layout, 0, "Coverage radius (mm)", _coverageRadiusInput);
            AddRow(layout, 1, "Preferred minimum spacing (mm)", _minimumSpacingInput);
            AddRow(layout, 2, "Maximum spacing (mm)", _maximumSpacingInput);
            AddRow(layout, 3, "Minimum edge clearance (mm)", _boundaryClearanceInput);
            AddRow(layout, 4, "Obstacle-line clearance (mm)", _obstacleClearanceInput);
            AddRow(layout, 5, "Height from level (mm)", _heightInput);
            AddRow(layout, 6, "Branch offset from head (mm)", _branchOffsetInput);
            AddRow(layout, 7, string.Empty, _drawBranchGuidesCheckBox);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill
            };
            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 86 };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 86 };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(buttons, 0, 8);
            layout.SetColumnSpan(buttons, 2);

            AcceptButton = okButton;
            CancelButton = cancelButton;
            Controls.Add(layout);
        }

        public SprinklerSettings Settings
        {
            get
            {
                return new SprinklerSettings
                {
                    CoverageRadiusMm = (double)_coverageRadiusInput.Value,
                    MinimumSpacingMm = (double)_minimumSpacingInput.Value,
                    MaximumSpacingMm = (double)_maximumSpacingInput.Value,
                    BoundaryClearanceMm = (double)_boundaryClearanceInput.Value,
                    ObstacleClearanceMm = (double)_obstacleClearanceInput.Value,
                    HeightMm = (double)_heightInput.Value,
                    BranchOffsetMm = (double)_branchOffsetInput.Value,
                    DrawBranchGuides = _drawBranchGuidesCheckBox.Checked
                };
            }
        }

        private static NumericUpDown CreateNumberInput(int minimum, int maximum, int value, int increment)
        {
            return new NumericUpDown
            {
                Dock = DockStyle.Left,
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Increment = increment,
                Width = 130,
                ThousandsSeparator = true
            };
        }

        private static void SetNumberValue(NumericUpDown input, double value)
        {
            decimal requested = (decimal)value;
            input.Value = System.Math.Max(input.Minimum, System.Math.Min(input.Maximum, requested));
        }

        private static void AddRow(TableLayoutPanel layout, int row, string text, System.Windows.Forms.Control control)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.Controls.Add(new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

    }

    internal class SprinklerTypePickerForm : System.Windows.Forms.Form
    {
        private readonly ListView _typeList;
        private readonly Button _okButton;

        public SprinklerTypePickerForm(
            IEnumerable<FamilySymbol> symbols,
            string lastFamilyName,
            string lastTypeName)
        {
            Text = "Select Sprinkler Type - Tung MEP";
            Width = 640;
            Height = 420;
            MinimumSize = new Size(540, 340);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            _typeList = new ListView
            {
                Dock = DockStyle.Fill,
                View = System.Windows.Forms.View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _typeList.Columns.Add("Family", 330);
            _typeList.Columns.Add("Type", 260);
            foreach (FamilySymbol symbol in symbols
                .OrderBy(symbol => symbol.FamilyName)
                .ThenBy(symbol => symbol.Name))
            {
                var item = new ListViewItem(symbol.FamilyName) { Tag = symbol };
                item.SubItems.Add(symbol.Name);
                _typeList.Items.Add(item);
            }

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(8),
                FlowDirection = FlowDirection.RightToLeft
            };
            _okButton = new Button
            {
                Text = "Use Selected Type",
                DialogResult = DialogResult.OK,
                Width = 130,
                Enabled = false
            };
            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 86
            };
            var backButton = new Button
            {
                Text = "Back",
                DialogResult = DialogResult.Retry,
                Width = 86
            };
            buttons.Controls.Add(_okButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(backButton);
            _typeList.SelectedIndexChanged += (sender, args) =>
                _okButton.Enabled = _typeList.SelectedItems.Count == 1;
            _typeList.ItemActivate += (sender, args) =>
            {
                if (_typeList.SelectedItems.Count == 1)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            AcceptButton = _okButton;
            CancelButton = cancelButton;
            Controls.Add(_typeList);
            Controls.Add(buttons);

            ListViewItem remembered = _typeList.Items.Cast<ListViewItem>().FirstOrDefault(item =>
            {
                FamilySymbol symbol = item.Tag as FamilySymbol;
                return symbol != null &&
                    symbol.FamilyName.Equals(lastFamilyName ?? string.Empty, System.StringComparison.OrdinalIgnoreCase) &&
                    symbol.Name.Equals(lastTypeName ?? string.Empty, System.StringComparison.OrdinalIgnoreCase);
            });
            if (remembered != null)
            {
                remembered.Selected = true;
                remembered.Focused = true;
                remembered.EnsureVisible();
                _okButton.Enabled = true;
            }
        }

        public FamilySymbol SelectedSymbol
        {
            get
            {
                return _typeList.SelectedItems.Count == 1
                    ? _typeList.SelectedItems[0].Tag as FamilySymbol
                    : null;
            }
        }
    }

    internal class PipeTypePickerForm : System.Windows.Forms.Form
    {
        private readonly ListView _typeList;
        private readonly Button _okButton;

        public PipeTypePickerForm(IEnumerable<PipeType> types, string lastPipeTypeName)
        {
            Text = "Select DN25 Pipe Type - Tung MEP";
            Width = 560;
            Height = 360;
            MinimumSize = new Size(500, 300);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            _typeList = new ListView
            {
                Dock = DockStyle.Fill,
                View = System.Windows.Forms.View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _typeList.Columns.Add("Pipe Type", 500);
            foreach (PipeType type in types.OrderBy(type => type.Name))
            {
                _typeList.Items.Add(new ListViewItem(type.Name) { Tag = type });
            }

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(8),
                FlowDirection = FlowDirection.RightToLeft
            };
            _okButton = new Button
            {
                Text = "Use DN25 Pipe Type",
                DialogResult = DialogResult.OK,
                Width = 140,
                Enabled = false
            };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 86 };
            var backButton = new Button { Text = "Back", DialogResult = DialogResult.Retry, Width = 86 };
            buttons.Controls.Add(_okButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(backButton);
            _typeList.SelectedIndexChanged += (sender, args) =>
                _okButton.Enabled = _typeList.SelectedItems.Count == 1;
            _typeList.ItemActivate += (sender, args) =>
            {
                if (_typeList.SelectedItems.Count == 1)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            AcceptButton = _okButton;
            CancelButton = cancelButton;
            Controls.Add(_typeList);
            Controls.Add(buttons);

            ListViewItem remembered = _typeList.Items.Cast<ListViewItem>().FirstOrDefault(item =>
            {
                PipeType type = item.Tag as PipeType;
                return type != null && type.Name.Equals(
                    lastPipeTypeName ?? string.Empty,
                    System.StringComparison.OrdinalIgnoreCase);
            });
            if (remembered != null)
            {
                remembered.Selected = true;
                remembered.Focused = true;
                remembered.EnsureVisible();
                _okButton.Enabled = true;
            }
        }

        public PipeType SelectedPipeType
        {
            get
            {
                return _typeList.SelectedItems.Count == 1
                    ? _typeList.SelectedItems[0].Tag as PipeType
                    : null;
            }
        }
    }

    internal class PipingSystemTypePickerForm : System.Windows.Forms.Form
    {
        private readonly ListView _typeList;
        private readonly Button _okButton;

        public PipingSystemTypePickerForm(IEnumerable<PipingSystemType> types, string lastSystemTypeName)
        {
            Text = "Select Fire Protection System Type - Tung MEP";
            Width = 640;
            Height = 360;
            MinimumSize = new Size(540, 300);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;

            _typeList = new ListView
            {
                Dock = DockStyle.Fill,
                View = System.Windows.Forms.View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _typeList.Columns.Add("System Type", 390);
            _typeList.Columns.Add("Classification", 210);
            foreach (PipingSystemType type in types.OrderBy(type => type.Name))
            {
                var item = new ListViewItem(type.Name) { Tag = type };
                item.SubItems.Add(type.SystemClassification.ToString());
                _typeList.Items.Add(item);
            }

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                Padding = new Padding(8),
                FlowDirection = FlowDirection.RightToLeft
            };
            _okButton = new Button
            {
                Text = "Use Selected System",
                DialogResult = DialogResult.OK,
                Width = 145,
                Enabled = false
            };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 86 };
            var backButton = new Button { Text = "Back", DialogResult = DialogResult.Retry, Width = 86 };
            buttons.Controls.Add(_okButton);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(backButton);
            _typeList.SelectedIndexChanged += (sender, args) =>
                _okButton.Enabled = _typeList.SelectedItems.Count == 1;
            _typeList.ItemActivate += (sender, args) =>
            {
                if (_typeList.SelectedItems.Count == 1)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            AcceptButton = _okButton;
            CancelButton = cancelButton;
            Controls.Add(_typeList);
            Controls.Add(buttons);

            ListViewItem remembered = _typeList.Items.Cast<ListViewItem>().FirstOrDefault(item =>
            {
                PipingSystemType type = item.Tag as PipingSystemType;
                return type != null && type.Name.Equals(
                    lastSystemTypeName ?? string.Empty,
                    System.StringComparison.OrdinalIgnoreCase);
            });
            if (remembered != null)
            {
                remembered.Selected = true;
                remembered.Focused = true;
                remembered.EnsureVisible();
                _okButton.Enabled = true;
            }
        }

        public PipingSystemType SelectedSystemType
        {
            get
            {
                return _typeList.SelectedItems.Count == 1
                    ? _typeList.SelectedItems[0].Tag as PipingSystemType
                    : null;
            }
        }
    }
}
