using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Button = System.Windows.Forms.Button;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;

namespace TungMEP.Modules.Dimensions
{
    internal sealed class AutoDimensionForm : Form
    {
        private readonly ComboBox _modeInput;
        private readonly ComboBox _byLineInputModeInput;
        private readonly ComboBox _dimensionTypeInput;
        private readonly ComboBox _referenceModeInput;
        private readonly CheckBox _gridCheck;
        private readonly CheckBox _wallCheck;
        private readonly CheckBox _columnCheck;
        private readonly CheckBox _pipeCheck;
        private readonly CheckBox _ductCheck;
        private readonly CheckBox _sprinklerCheck;
        private readonly CheckBox _mechanicalEquipmentCheck;
        private readonly NumericUpDown _offsetInput;
        private readonly NumericUpDown _laneStepInput;
        private readonly NumericUpDown _toleranceInput;
        private readonly CheckBox _chainCheck;
        private readonly CheckBox _overallCheck;
        private readonly CheckBox _avoidOverlapCheck;
        private readonly CheckBox _repeatRunCheck;
        private readonly CheckBox _sprinklerAlongRowsCheck;
        private readonly CheckBox _sprinklerBetweenRowsCheck;
        private readonly CheckBox _sprinklerOverallCheck;
        private readonly GroupBox _categoryGroup;
        private readonly GroupBox _sprinklerGroup;
        private readonly Label _validationLabel;
        private readonly Button _okButton;

        public AutoDimensionForm(
            IEnumerable<DimensionType> dimensionTypes,
            AutoDimensionSettings initialSettings)
            : this(
                dimensionTypes == null
                    ? new List<AutoDimensionTypeOption>()
                    : dimensionTypes
                        .Where(type => type != null)
                        .Select(type => new AutoDimensionTypeOption(type)),
                initialSettings)
        {
        }

        public AutoDimensionForm(
            IEnumerable<AutoDimensionTypeOption> dimensionTypes,
            AutoDimensionSettings initialSettings)
        {
            AutoDimensionSettings settings = initialSettings == null
                ? AutoDimensionSettings.CreateDefault()
                : initialSettings.Clone();
            settings.Normalize();

            Text = "Tung MEP - Auto Dimension";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(690, 680);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 7
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            var generalGroup = new GroupBox
            {
                Text = "General",
                Dock = DockStyle.Fill
            };
            var general = CreateTwoColumnSettingsPanel(4);
            _modeInput = CreateDropDown();
            _modeInput.Items.Add(new NamedValue<AutoDimensionMode>("By Line", AutoDimensionMode.ByLine));
            _modeInput.Items.Add(new NamedValue<AutoDimensionMode>("Sprinkler", AutoDimensionMode.Sprinkler));
            _modeInput.Items.Add(new NamedValue<AutoDimensionMode>(
                "Single sprinkler to nearest column/wall (X + Y)",
                AutoDimensionMode.SingleSprinklerToWalls));
            _byLineInputModeInput = CreateDropDown();
            _byLineInputModeInput.Items.Add(new NamedValue<AutoDimensionByLineInputMode>(
                "Select objects by rectangle (recommended)",
                AutoDimensionByLineInputMode.SelectObjects));
            _byLineInputModeInput.Items.Add(new NamedValue<AutoDimensionByLineInputMode>(
                "Draw scan line",
                AutoDimensionByLineInputMode.DrawScanLine));
            _dimensionTypeInput = CreateDropDown();
            foreach (AutoDimensionTypeOption option in (dimensionTypes ??
                new List<AutoDimensionTypeOption>())
                .Where(option => option != null)
                .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _dimensionTypeInput.Items.Add(option);
            }
            _referenceModeInput = CreateDropDown();
            _referenceModeInput.Items.Add(new NamedValue<AutoDimensionReferenceMode>(
                "Center",
                AutoDimensionReferenceMode.Center));
            _referenceModeInput.Items.Add(new NamedValue<AutoDimensionReferenceMode>(
                "Outer planar faces",
                AutoDimensionReferenceMode.OuterFaces));
            _referenceModeInput.Items.Add(new NamedValue<AutoDimensionReferenceMode>(
                "Center + outer planar faces",
                AutoDimensionReferenceMode.CenterAndOuterFaces));
            AddSetting(general, "Mode", _modeInput, 0);
            AddSetting(general, "Selection", _byLineInputModeInput, 1);
            AddSetting(general, "Dimension type", _dimensionTypeInput, 2);
            AddSetting(general, "References", _referenceModeInput, 3);
            generalGroup.Controls.Add(general);
            root.Controls.Add(generalGroup, 0, 0);

            _categoryGroup = new GroupBox
            {
                Text = "By Line categories",
                Dock = DockStyle.Fill
            };
            var categories = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 5, 8, 5),
                ColumnCount = 4,
                RowCount = 2
            };
            for (int column = 0; column < 4; column++)
            {
                categories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            }
            categories.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            categories.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _gridCheck = CreateCheckBox("Grid");
            _wallCheck = CreateCheckBox("Wall");
            _columnCheck = CreateCheckBox("Column");
            _pipeCheck = CreateCheckBox("Pipe");
            _ductCheck = CreateCheckBox("Duct");
            _sprinklerCheck = CreateCheckBox("Sprinkler");
            _mechanicalEquipmentCheck = CreateCheckBox("Mechanical Equipment");
            categories.Controls.Add(_gridCheck, 0, 0);
            categories.Controls.Add(_wallCheck, 1, 0);
            categories.Controls.Add(_columnCheck, 2, 0);
            categories.Controls.Add(_pipeCheck, 3, 0);
            categories.Controls.Add(_ductCheck, 0, 1);
            categories.Controls.Add(_sprinklerCheck, 1, 1);
            categories.Controls.Add(_mechanicalEquipmentCheck, 2, 1);
            categories.SetColumnSpan(_mechanicalEquipmentCheck, 2);
            _categoryGroup.Controls.Add(categories);
            root.Controls.Add(_categoryGroup, 0, 1);

            var placementGroup = new GroupBox
            {
                Text = "Placement",
                Dock = DockStyle.Fill
            };
            var placement = CreateThreeColumnSettingsPanel();
            _offsetInput = CreateNumberInput(0, 500, 10, 1);
            _laneStepInput = CreateNumberInput(0.1M, 500, 6, 1);
            _toleranceInput = CreateNumberInput(0.1M, 5000, 5, 0.5M);
            AddCompactSetting(placement, "Offset on sheet (mm)", _offsetInput, 0);
            AddCompactSetting(placement, "Lane step on sheet (mm)", _laneStepInput, 1);
            AddCompactSetting(placement, "Model tolerance (mm)", _toleranceInput, 2);
            placementGroup.Controls.Add(placement);
            root.Controls.Add(placementGroup, 0, 2);

            var outputGroup = new GroupBox
            {
                Text = "Output",
                Dock = DockStyle.Fill
            };
            var output = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 5, 8, 5),
                ColumnCount = 2,
                RowCount = 2
            };
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            output.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            output.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _chainCheck = CreateCheckBox("Chain dimensions");
            _overallCheck = CreateCheckBox("Overall dimension");
            _avoidOverlapCheck = CreateCheckBox("Avoid existing dimension lanes");
            _repeatRunCheck = CreateCheckBox("Repeat command after creation");
            output.Controls.Add(_chainCheck, 0, 0);
            output.Controls.Add(_overallCheck, 1, 0);
            output.Controls.Add(_avoidOverlapCheck, 0, 1);
            output.Controls.Add(_repeatRunCheck, 1, 1);
            outputGroup.Controls.Add(output);
            root.Controls.Add(outputGroup, 0, 3);

            _sprinklerGroup = new GroupBox
            {
                Text = "Sprinkler preset",
                Dock = DockStyle.Fill
            };
            var sprinklerPreset = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 10, 8, 8),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            _sprinklerAlongRowsCheck = CreateCheckBox("Dimensions along rows");
            _sprinklerBetweenRowsCheck = CreateCheckBox("Dimensions between rows");
            _sprinklerOverallCheck = CreateCheckBox("Overall dimensions");
            _sprinklerAlongRowsCheck.AutoSize = true;
            _sprinklerBetweenRowsCheck.AutoSize = true;
            _sprinklerOverallCheck.AutoSize = true;
            _sprinklerAlongRowsCheck.Margin = new Padding(5, 4, 22, 4);
            _sprinklerBetweenRowsCheck.Margin = new Padding(5, 4, 22, 4);
            _sprinklerOverallCheck.Margin = new Padding(5, 4, 5, 4);
            sprinklerPreset.Controls.Add(_sprinklerAlongRowsCheck);
            sprinklerPreset.Controls.Add(_sprinklerBetweenRowsCheck);
            sprinklerPreset.Controls.Add(_sprinklerOverallCheck);
            _sprinklerGroup.Controls.Add(sprinklerPreset);
            root.Controls.Add(_sprinklerGroup, 0, 4);

            _validationLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = Color.Firebrick,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 3, 4, 3)
            };
            root.Controls.Add(_validationLabel, 0, 5);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            _okButton = new Button { Text = "OK", Width = 90, Height = 28 };
            var cancelButton = new Button
            {
                Text = "Cancel",
                Width = 90,
                Height = 28,
                DialogResult = DialogResult.Cancel
            };
            _okButton.Click += HandleAccept;
            buttons.Controls.Add(_okButton);
            buttons.Controls.Add(cancelButton);
            root.Controls.Add(buttons, 0, 6);
            AcceptButton = _okButton;
            CancelButton = cancelButton;

            ApplyInitialSettings(settings);
            AttachValidationEvents();
            UpdateModeState();
        }

        public AutoDimensionSettings Settings
        {
            get { return GetSettings(); }
        }

        public AutoDimensionSettings GetSettings()
        {
            AutoDimensionTypeOption dimensionType =
                _dimensionTypeInput.SelectedItem as AutoDimensionTypeOption;
            return new AutoDimensionSettings
            {
                Mode = SelectedValue(_modeInput, AutoDimensionMode.ByLine),
                ByLineInputMode = SelectedValue(
                    _byLineInputModeInput,
                    AutoDimensionByLineInputMode.SelectObjects),
                IncludeGrids = _gridCheck.Checked,
                IncludeWalls = _wallCheck.Checked,
                IncludeColumns = _columnCheck.Checked,
                IncludePipes = _pipeCheck.Checked,
                IncludeDucts = _ductCheck.Checked,
                IncludeSprinklers = _sprinklerCheck.Checked,
                IncludeMechanicalEquipment = _mechanicalEquipmentCheck.Checked,
                ReferenceMode = SelectedValue(
                    _referenceModeInput,
                    AutoDimensionReferenceMode.Center),
                SelectedDimensionTypeId = dimensionType == null
                    ? ElementId.InvalidElementId.Value
                    : dimensionType.IdValue,
                SelectedDimensionTypeName = dimensionType == null
                    ? string.Empty
                    : dimensionType.Name,
                OffsetPaperMm = Decimal.ToDouble(_offsetInput.Value),
                LaneStepPaperMm = Decimal.ToDouble(_laneStepInput.Value),
                ToleranceMm = Decimal.ToDouble(_toleranceInput.Value),
                CreateChain = _chainCheck.Checked,
                CreateOverall = _overallCheck.Checked,
                AvoidOverlap = _avoidOverlapCheck.Checked,
                RepeatRun = _repeatRunCheck.Checked,
                SprinklerDimensionAlongRows = _sprinklerAlongRowsCheck.Checked,
                SprinklerDimensionBetweenRows = _sprinklerBetweenRowsCheck.Checked,
                SprinklerDimensionOverall = _sprinklerOverallCheck.Checked
            };
        }

        private void ApplyInitialSettings(AutoDimensionSettings settings)
        {
            SelectNamedValue(_modeInput, settings.Mode);
            SelectNamedValue(_byLineInputModeInput, settings.ByLineInputMode);
            SelectDimensionType(settings.SelectedDimensionTypeId, settings.SelectedDimensionTypeName);
            SelectNamedValue(_referenceModeInput, settings.ReferenceMode);
            _gridCheck.Checked = settings.IncludeGrids;
            _wallCheck.Checked = settings.IncludeWalls;
            _columnCheck.Checked = settings.IncludeColumns;
            _pipeCheck.Checked = settings.IncludePipes;
            _ductCheck.Checked = settings.IncludeDucts;
            _sprinklerCheck.Checked = settings.IncludeSprinklers;
            _mechanicalEquipmentCheck.Checked = settings.IncludeMechanicalEquipment;
            SetNumberValue(_offsetInput, settings.OffsetPaperMm);
            SetNumberValue(_laneStepInput, settings.LaneStepPaperMm);
            SetNumberValue(_toleranceInput, settings.ToleranceMm);
            _chainCheck.Checked = settings.CreateChain;
            _overallCheck.Checked = settings.CreateOverall;
            _avoidOverlapCheck.Checked = settings.AvoidOverlap;
            _repeatRunCheck.Checked = settings.RepeatRun;
            _sprinklerAlongRowsCheck.Checked = settings.SprinklerDimensionAlongRows;
            _sprinklerBetweenRowsCheck.Checked = settings.SprinklerDimensionBetweenRows;
            _sprinklerOverallCheck.Checked = settings.SprinklerDimensionOverall;
        }

        private void SelectDimensionType(long rememberedId, string rememberedName)
        {
            AutoDimensionTypeOption selected = _dimensionTypeInput.Items
                .Cast<AutoDimensionTypeOption>()
                .FirstOrDefault(option => option.IdValue == rememberedId);
            if (selected == null && !string.IsNullOrEmpty(rememberedName))
            {
                selected = _dimensionTypeInput.Items
                    .Cast<AutoDimensionTypeOption>()
                    .FirstOrDefault(option => option.Name.Equals(
                        rememberedName,
                        StringComparison.CurrentCultureIgnoreCase));
            }
            if (selected != null)
            {
                _dimensionTypeInput.SelectedItem = selected;
            }
            else if (_dimensionTypeInput.Items.Count > 0)
            {
                _dimensionTypeInput.SelectedIndex = 0;
            }
        }

        private void AttachValidationEvents()
        {
            _modeInput.SelectedIndexChanged += delegate
            {
                UpdateModeState();
            };
            _dimensionTypeInput.SelectedIndexChanged += delegate { ValidateInputs(); };
            _byLineInputModeInput.SelectedIndexChanged += delegate { ValidateInputs(); };
            _referenceModeInput.SelectedIndexChanged += delegate { ValidateInputs(); };
            foreach (CheckBox checkBox in new[]
            {
                _gridCheck, _wallCheck, _columnCheck, _pipeCheck, _ductCheck,
                _sprinklerCheck, _mechanicalEquipmentCheck, _chainCheck, _overallCheck,
                _avoidOverlapCheck, _repeatRunCheck, _sprinklerAlongRowsCheck,
                _sprinklerBetweenRowsCheck, _sprinklerOverallCheck
            })
            {
                checkBox.CheckedChanged += delegate { ValidateInputs(); };
            }
            _offsetInput.ValueChanged += delegate { ValidateInputs(); };
            _laneStepInput.ValueChanged += delegate { ValidateInputs(); };
            _toleranceInput.ValueChanged += delegate { ValidateInputs(); };
        }

        private void UpdateModeState()
        {
            AutoDimensionMode mode = SelectedValue(
                _modeInput,
                AutoDimensionMode.ByLine);
            bool byLine = mode == AutoDimensionMode.ByLine;
            bool sprinklerPreset = mode == AutoDimensionMode.Sprinkler;
            _categoryGroup.Enabled = byLine;
            _byLineInputModeInput.Enabled = byLine;
            _referenceModeInput.Enabled = byLine;
            _chainCheck.Enabled = byLine;
            _overallCheck.Enabled = byLine;
            _repeatRunCheck.Enabled = byLine ||
                mode == AutoDimensionMode.SingleSprinklerToWalls;
            _sprinklerGroup.Enabled = sprinklerPreset;
            if (!byLine)
            {
                SelectNamedValue(
                    _referenceModeInput,
                    AutoDimensionReferenceMode.Center);
            }
            ValidateInputs();
        }

        private bool ValidateInputs()
        {
            string error = GetValidationError();
            bool valid = string.IsNullOrEmpty(error);
            _okButton.Enabled = valid;
            _validationLabel.ForeColor = valid ? Color.DarkGreen : Color.Firebrick;
            if (!valid)
            {
                _validationLabel.Text = error;
            }
            else if (SelectedValue(_modeInput, AutoDimensionMode.ByLine) ==
                AutoDimensionMode.ByLine)
            {
                bool selectObjects = SelectedValue(
                    _byLineInputModeInput,
                    AutoDimensionByLineInputMode.SelectObjects) ==
                    AutoDimensionByLineInputMode.SelectObjects;
                _validationLabel.Text = selectObjects
                    ? "After OK: drag a rectangle, review the ghost dimension with values, then click to place it. Preselect 2+ objects to skip the rectangle."
                    : "After OK: click 2 scan-line points, review the ghost dimension with values, then click to place it.";
            }
            else
            {
                AutoDimensionMode mode = SelectedValue(
                    _modeInput,
                    AutoDimensionMode.ByLine);
                _validationLabel.Text = mode == AutoDimensionMode.SingleSprinklerToWalls
                    ? "After OK: select one sprinkler, then click once to choose the placement quadrant for both X/Y dimensions."
                    : "Settings are ready.";
            }
            return valid;
        }

        private string GetValidationError()
        {
            if (_dimensionTypeInput.SelectedItem == null)
            {
                return "No Dimension Type is available. Load or create a linear Dimension Type first.";
            }
            AutoDimensionSettings settings = GetSettings();
            if (settings.OffsetPaperMm < 0)
            {
                return "Offset on sheet cannot be negative.";
            }
            if (settings.LaneStepPaperMm <= 0)
            {
                return "Lane step must be greater than zero.";
            }
            if (settings.ToleranceMm <= 0)
            {
                return "Tolerance must be greater than zero.";
            }
            if (settings.Mode == AutoDimensionMode.ByLine)
            {
                if (!settings.HasAnyByLineCategory)
                {
                    return "Select at least one category for By Line mode.";
                }
                if (!settings.HasAnyByLineOutput)
                {
                    return "Enable Chain dimensions, Overall dimension, or both.";
                }
            }
            else if (settings.Mode == AutoDimensionMode.Sprinkler &&
                !settings.HasAnySprinklerOutput)
            {
                return "Enable at least one Sprinkler dimension preset.";
            }
            return string.Empty;
        }

        private void HandleAccept(object sender, EventArgs args)
        {
            if (!ValidateInputs())
            {
                MessageBox.Show(
                    this,
                    GetValidationError(),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private static TableLayoutPanel CreateTwoColumnSettingsPanel(int rowCount)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(9, 6, 9, 6),
                ColumnCount = 2,
                RowCount = rowCount
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < rowCount; row++)
            {
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F / rowCount));
            }
            return panel;
        }

        private static TableLayoutPanel CreateThreeColumnSettingsPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(9, 7, 9, 7),
                ColumnCount = 3,
                RowCount = 2
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return panel;
        }

        private static void AddSetting(
            TableLayoutPanel panel,
            string label,
            System.Windows.Forms.Control input,
            int row)
        {
            panel.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            }, 0, row);
            panel.Controls.Add(input, 1, row);
        }

        private static void AddCompactSetting(
            TableLayoutPanel panel,
            string label,
            System.Windows.Forms.Control input,
            int column)
        {
            panel.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            }, column, 0);
            input.Dock = DockStyle.Top;
            input.Margin = new Padding(3, 2, 16, 3);
            panel.Controls.Add(input, column, 1);
        }

        private static ComboBox CreateDropDown()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
        }

        private static CheckBox CreateCheckBox(string text)
        {
            return new CheckBox
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };
        }

        private static NumericUpDown CreateNumberInput(
            decimal minimum,
            decimal maximum,
            decimal value,
            decimal increment)
        {
            return new NumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                Increment = increment,
                DecimalPlaces = 1,
                ThousandsSeparator = true,
                Width = 180
            };
        }

        private static void SetNumberValue(NumericUpDown input, double value)
        {
            decimal requested;
            try
            {
                requested = (decimal)value;
            }
            catch
            {
                requested = input.Minimum;
            }
            input.Value = Math.Max(input.Minimum, Math.Min(input.Maximum, requested));
        }

        private static void SelectNamedValue<T>(ComboBox input, T value)
        {
            NamedValue<T> item = input.Items
                .Cast<NamedValue<T>>()
                .FirstOrDefault(candidate => EqualityComparer<T>.Default.Equals(candidate.Value, value));
            input.SelectedItem = item ?? (input.Items.Count > 0 ? input.Items[0] : null);
        }

        private static T SelectedValue<T>(ComboBox input, T fallback)
        {
            NamedValue<T> selected = input.SelectedItem as NamedValue<T>;
            return selected == null ? fallback : selected.Value;
        }

        private sealed class NamedValue<T>
        {
            private readonly string _name;

            public NamedValue(string name, T value)
            {
                _name = name;
                Value = value;
            }

            public T Value { get; private set; }

            public override string ToString()
            {
                return _name;
            }
        }
    }
}
