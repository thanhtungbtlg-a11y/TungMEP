using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Button = System.Windows.Forms.Button;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;

namespace TungMEP.Modules.Tags
{
    internal sealed class AutoTagForm : Form
    {
        private readonly ComboBox _scopeInput;
        private readonly ComboBox _tagTypeInput;
        private readonly ComboBox _arrowheadTypeInput;
        private readonly ComboBox _sideInput;
        private readonly CheckBox _leaderCheck;
        private readonly CheckBox _skipTaggedCheck;
        private readonly CheckBox _optimizeDocumentationCheck;
        private readonly CheckBox _updateBopCheck;
        private readonly TextBox _bopParameterInput;
        private readonly NumericUpDown _offsetInput;
        private readonly NumericUpDown _gapInput;
        private readonly NumericUpDown _clearanceInput;
        private readonly NumericUpDown _laneInput;
        private readonly Label _validationLabel;
        private readonly Button _okButton;

        public AutoTagForm(
            IEnumerable<AutoTagTypeOption> tagTypes,
            IEnumerable<AutoTagArrowheadOption> arrowheadTypes,
            AutoTagSettings initialSettings)
        {
            AutoTagSettings settings = initialSettings == null
                ? AutoTagSettings.CreateDefault()
                : initialSettings.Clone();
            settings.Normalize();

            Text = "Tung MEP - Smart Auto Tag MEP";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(680, 572);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 5
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            var sourceGroup = new GroupBox { Text = "Source and tag type", Dock = DockStyle.Fill };
            var source = CreateSettingsPanel(4);
            _scopeInput = CreateDropDown();
            _scopeInput.Items.Add(new NamedValue<AutoTagScope>(
                "Current selection / pick pipes",
                AutoTagScope.SelectionOrPick));
            _scopeInput.Items.Add(new NamedValue<AutoTagScope>(
                "Drag a rectangle around pipes",
                AutoTagScope.Rectangle));
            _scopeInput.Items.Add(new NamedValue<AutoTagScope>(
                "All visible pipes in active view",
                AutoTagScope.ActiveView));
            _tagTypeInput = CreateDropDown();
            foreach (AutoTagTypeOption option in (tagTypes ?? new List<AutoTagTypeOption>())
                .Where(option => option != null)
                .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                _tagTypeInput.Items.Add(option);
            }
            _arrowheadTypeInput = CreateDropDown();
            foreach (AutoTagArrowheadOption option in (arrowheadTypes ??
                new List<AutoTagArrowheadOption>())
                .Where(option => option != null))
            {
                _arrowheadTypeInput.Items.Add(option);
            }
            _sideInput = CreateDropDown();
            _sideInput.Items.Add(new NamedValue<AutoTagSidePreference>(
                "Auto - least congested side",
                AutoTagSidePreference.Auto));
            _sideInput.Items.Add(new NamedValue<AutoTagSidePreference>("Left", AutoTagSidePreference.Left));
            _sideInput.Items.Add(new NamedValue<AutoTagSidePreference>("Right", AutoTagSidePreference.Right));
            _sideInput.Items.Add(new NamedValue<AutoTagSidePreference>("Top", AutoTagSidePreference.Top));
            _sideInput.Items.Add(new NamedValue<AutoTagSidePreference>("Bottom", AutoTagSidePreference.Bottom));
            AddSetting(source, "Scope", _scopeInput, 0);
            AddSetting(source, "Pipe Tag Type", _tagTypeInput, 1);
            AddSetting(source, "Leader arrowhead", _arrowheadTypeInput, 2);
            AddSetting(source, "Preferred tag side", _sideInput, 3);
            sourceGroup.Controls.Add(source);
            root.Controls.Add(sourceGroup, 0, 0);

            var placementGroup = new GroupBox { Text = "Smart placement", Dock = DockStyle.Fill };
            var placement = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 4,
                RowCount = 2
            };
            placement.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
            placement.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
            placement.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
            placement.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
            placement.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            placement.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _offsetInput = CreateNumberInput(1, 200, 12, 1);
            _gapInput = CreateNumberInput(0.5M, 100, 4, 0.5M);
            _clearanceInput = CreateNumberInput(0, 100, 2, 0.5M);
            _laneInput = CreateNumberInput(1, 30, 8, 1);
            AddCompactSetting(placement, "Offset on sheet (mm)", _offsetInput, 0, 0);
            AddCompactSetting(placement, "Tag gap on sheet (mm)", _gapInput, 2, 0);
            AddCompactSetting(placement, "Clearance on sheet (mm)", _clearanceInput, 0, 1);
            AddCompactSetting(placement, "Candidate lane steps", _laneInput, 2, 1);
            placementGroup.Controls.Add(placement);
            root.Controls.Add(placementGroup, 0, 1);

            var outputGroup = new GroupBox { Text = "Output", Dock = DockStyle.Fill };
            var output = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 2,
                RowCount = 3
            };
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            output.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            output.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            output.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            _leaderCheck = CreateCheckBox("Add short elbow leaders");
            _skipTaggedCheck = CreateCheckBox("Skip pipes already tagged in this view");
            _updateBopCheck = CreateCheckBox("Update optional BOP-FFL pipe parameter");
            _optimizeDocumentationCheck = CreateCheckBox("Optimize white space and leader crossings");
            _bopParameterInput = new TextBox { Dock = DockStyle.Fill };
            output.Controls.Add(_leaderCheck, 0, 0);
            output.Controls.Add(_skipTaggedCheck, 1, 0);
            output.Controls.Add(_updateBopCheck, 0, 1);
            output.Controls.Add(_optimizeDocumentationCheck, 1, 1);
            output.Controls.Add(new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = "BOP parameter name"
            }, 0, 2);
            output.Controls.Add(_bopParameterInput, 1, 2);
            outputGroup.Controls.Add(output);
            root.Controls.Add(outputGroup, 0, 2);

            var infoPanel = new Panel { Dock = DockStyle.Fill };
            var information = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 72,
                ForeColor = Color.FromArgb(45, 45, 45),
                Text = "The selected Revit Pipe Tag Type controls the displayed fields. " +
                    "Use a tag family containing System Abbreviation, Diameter, Slope and your BOP-FFL shared parameter for the case-study format. " +
                    "Existing manual tags are never deleted or moved."
            };
            _validationLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.Firebrick,
                TextAlign = ContentAlignment.MiddleLeft
            };
            infoPanel.Controls.Add(_validationLabel);
            infoPanel.Controls.Add(information);
            root.Controls.Add(infoPanel, 0, 3);

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
            _okButton.Click += delegate
            {
                if (ValidateInputs())
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            buttons.Controls.Add(_okButton);
            buttons.Controls.Add(cancelButton);
            root.Controls.Add(buttons, 0, 4);
            AcceptButton = _okButton;
            CancelButton = cancelButton;

            SelectNamedValue(_scopeInput, settings.Scope);
            SelectTagType(_tagTypeInput, settings);
            SelectArrowheadType(_arrowheadTypeInput, settings);
            SelectNamedValue(_sideInput, settings.SidePreference);
            _leaderCheck.Checked = settings.AddLeader;
            _skipTaggedCheck.Checked = settings.SkipAlreadyTagged;
            _optimizeDocumentationCheck.Checked = settings.OptimizeDocumentationLayout;
            _updateBopCheck.Checked = settings.UpdateBopParameter;
            _bopParameterInput.Text = settings.BopParameterName;
            _offsetInput.Value = ToDecimal(settings.OffsetPaperMm, _offsetInput);
            _gapInput.Value = ToDecimal(settings.TagGapPaperMm, _gapInput);
            _clearanceInput.Value = ToDecimal(settings.ClearancePaperMm, _clearanceInput);
            _laneInput.Value = ToDecimal(settings.MaximumLaneSteps, _laneInput);
            _updateBopCheck.CheckedChanged += delegate { UpdateEnabledState(); };
            _tagTypeInput.SelectedIndexChanged += delegate { ValidateInputs(); };
            _arrowheadTypeInput.SelectedIndexChanged += delegate { ValidateInputs(); };
            _bopParameterInput.TextChanged += delegate { ValidateInputs(); };
            UpdateEnabledState();
            ValidateInputs();
        }

        public AutoTagSettings Settings
        {
            get
            {
                var settings = new AutoTagSettings
                {
                    Scope = GetNamedValue(_scopeInput, AutoTagScope.SelectionOrPick),
                    SidePreference = GetNamedValue(_sideInput, AutoTagSidePreference.Auto),
                    AddLeader = _leaderCheck.Checked,
                    SkipAlreadyTagged = _skipTaggedCheck.Checked,
                    OptimizeDocumentationLayout = _optimizeDocumentationCheck.Checked,
                    UpdateBopParameter = _updateBopCheck.Checked,
                    BopParameterName = _bopParameterInput.Text,
                    OffsetPaperMm = (double)_offsetInput.Value,
                    TagGapPaperMm = (double)_gapInput.Value,
                    ClearancePaperMm = (double)_clearanceInput.Value,
                    MaximumLaneSteps = (int)_laneInput.Value
                };
                AutoTagTypeOption tagType = _tagTypeInput.SelectedItem as AutoTagTypeOption;
                AutoTagArrowheadOption arrowheadType =
                    _arrowheadTypeInput.SelectedItem as AutoTagArrowheadOption;
                settings.SelectedTagTypeId = tagType == null
                    ? -1
                    : tagType.IdValue;
                settings.SelectedTagTypeName = tagType == null
                    ? string.Empty
                    : tagType.DisplayName;
                settings.SelectedArrowheadTypeId = arrowheadType == null
                    ? -1
                    : arrowheadType.IdValue;
                settings.SelectedArrowheadTypeName = arrowheadType == null
                    ? string.Empty
                    : arrowheadType.Name;
                settings.Normalize();
                return settings;
            }
        }

        private bool ValidateInputs()
        {
            string error = null;
            if (!(_tagTypeInput.SelectedItem is AutoTagTypeOption))
            {
                error = "Select a loaded Pipe Tag Type.";
            }
            else if (!(_arrowheadTypeInput.SelectedItem is AutoTagArrowheadOption))
            {
                error = "Select a Leader Arrowhead Type.";
            }
            else if (_updateBopCheck.Checked && string.IsNullOrWhiteSpace(_bopParameterInput.Text))
            {
                error = "Enter the BOP-FFL pipe parameter name, or turn off BOP update.";
            }
            _validationLabel.Text = error ?? string.Empty;
            _okButton.Enabled = error == null;
            return error == null;
        }

        private void UpdateEnabledState()
        {
            _bopParameterInput.Enabled = _updateBopCheck.Checked;
            ValidateInputs();
        }

        private static TableLayoutPanel CreateSettingsPanel(int rows)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 2,
                RowCount = rows
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < rows; row++)
            {
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F / rows));
            }
            return panel;
        }

        private static void AddSetting(
            TableLayoutPanel panel,
            string label,
            Control input,
            int row)
        {
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = label
            }, 0, row);
            input.Dock = DockStyle.Fill;
            panel.Controls.Add(input, 1, row);
        }

        private static void AddCompactSetting(
            TableLayoutPanel panel,
            string label,
            Control input,
            int column,
            int row)
        {
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = label
            }, column, row);
            input.Dock = DockStyle.Fill;
            panel.Controls.Add(input, column + 1, row);
        }

        private static ComboBox CreateDropDown()
        {
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false
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
                DecimalPlaces = increment < 1 ? 1 : 0,
                ThousandsSeparator = true
            };
        }

        private static decimal ToDecimal(double value, NumericUpDown input)
        {
            decimal converted;
            try
            {
                converted = (decimal)value;
            }
            catch
            {
                converted = input.Value;
            }
            return Math.Max(input.Minimum, Math.Min(input.Maximum, converted));
        }

        private static void SelectTagType(ComboBox input, AutoTagSettings settings)
        {
            for (int index = 0; index < input.Items.Count; index++)
            {
                AutoTagTypeOption option = input.Items[index] as AutoTagTypeOption;
                if (option != null && option.IdValue == settings.SelectedTagTypeId)
                {
                    input.SelectedIndex = index;
                    return;
                }
            }
            if (!string.IsNullOrEmpty(settings.SelectedTagTypeName))
            {
                for (int index = 0; index < input.Items.Count; index++)
                {
                    AutoTagTypeOption option = input.Items[index] as AutoTagTypeOption;
                    if (option != null && string.Equals(
                        option.DisplayName,
                        settings.SelectedTagTypeName,
                        StringComparison.CurrentCultureIgnoreCase))
                    {
                        input.SelectedIndex = index;
                        return;
                    }
                }
            }
            if (input.Items.Count > 0)
            {
                input.SelectedIndex = 0;
            }
        }

        private static void SelectArrowheadType(ComboBox input, AutoTagSettings settings)
        {
            for (int index = 0; index < input.Items.Count; index++)
            {
                AutoTagArrowheadOption option = input.Items[index] as AutoTagArrowheadOption;
                if (option != null && option.IdValue == settings.SelectedArrowheadTypeId)
                {
                    input.SelectedIndex = index;
                    return;
                }
            }
            if (!string.IsNullOrEmpty(settings.SelectedArrowheadTypeName))
            {
                for (int index = 0; index < input.Items.Count; index++)
                {
                    AutoTagArrowheadOption option = input.Items[index] as AutoTagArrowheadOption;
                    if (option != null && string.Equals(
                        option.Name,
                        settings.SelectedArrowheadTypeName,
                        StringComparison.CurrentCultureIgnoreCase))
                    {
                        input.SelectedIndex = index;
                        return;
                    }
                }
            }
            if (input.Items.Count > 0)
            {
                input.SelectedIndex = 0;
            }
        }

        private static void SelectNamedValue<T>(ComboBox input, T value)
        {
            for (int index = 0; index < input.Items.Count; index++)
            {
                NamedValue<T> item = input.Items[index] as NamedValue<T>;
                if (item != null && EqualityComparer<T>.Default.Equals(item.Value, value))
                {
                    input.SelectedIndex = index;
                    return;
                }
            }
            if (input.Items.Count > 0)
            {
                input.SelectedIndex = 0;
            }
        }

        private static T GetNamedValue<T>(ComboBox input, T fallback)
        {
            NamedValue<T> item = input.SelectedItem as NamedValue<T>;
            return item == null ? fallback : item.Value;
        }
    }

    internal sealed class NamedValue<T>
    {
        public NamedValue(string name, T value)
        {
            Name = name;
            Value = value;
        }
        public string Name { get; private set; }
        public T Value { get; private set; }
        public override string ToString() { return Name; }
    }
}
