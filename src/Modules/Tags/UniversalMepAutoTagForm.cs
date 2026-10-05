using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Button = System.Windows.Forms.Button;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;

namespace TungMEP.Modules.Tags
{
    internal sealed class UniversalMepAutoTagForm : Form
    {
        private readonly ComboBox _scopeInput;
        private readonly ComboBox _arrowheadTypeInput;
        private readonly ComboBox _sideInput;
        private readonly CheckBox _leaderCheck;
        private readonly CheckBox _skipTaggedCheck;
        private readonly CheckBox _previewCheck;
        private readonly CheckBox _optimizeDocumentationCheck;
        private readonly NumericUpDown _offsetInput;
        private readonly NumericUpDown _gapInput;
        private readonly NumericUpDown _clearanceInput;
        private readonly NumericUpDown _laneInput;
        private readonly List<CategoryRow> _categoryRows;
        private readonly Label _validationLabel;
        private readonly Button _okButton;

        public UniversalMepAutoTagForm(
            IEnumerable<UniversalAutoTagTypeOption> tagTypes,
            IEnumerable<AutoTagArrowheadOption> arrowheadTypes,
            UniversalMepAutoTagSettings initialSettings)
        {
            UniversalMepAutoTagSettings settings = initialSettings == null
                ? UniversalMepAutoTagSettings.CreateDefault()
                : initialSettings.Clone();
            settings.Normalize();
            List<UniversalAutoTagTypeOption> availableTypes = (tagTypes ??
                new List<UniversalAutoTagTypeOption>())
                .Where(item => item != null)
                .ToList();

            Text = "Tung MEP - Universal Auto Tag MEP";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(790, 738);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 6
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            var sourceGroup = new GroupBox { Text = "Scope and leader", Dock = DockStyle.Fill };
            var source = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 4,
                RowCount = 2
            };
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
            source.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            source.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            source.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _scopeInput = CreateDropDown();
            _scopeInput.Items.Add(new UniversalNamedValue<AutoTagScope>(
                "Current selection / pick MEP elements",
                AutoTagScope.SelectionOrPick));
            _scopeInput.Items.Add(new UniversalNamedValue<AutoTagScope>(
                "Drag a rectangle around MEP elements",
                AutoTagScope.Rectangle));
            _scopeInput.Items.Add(new UniversalNamedValue<AutoTagScope>(
                "All enabled categories visible in active view",
                AutoTagScope.ActiveView));
            _sideInput = CreateDropDown();
            _sideInput.Items.Add(new UniversalNamedValue<AutoTagSidePreference>(
                "Auto - least congested side", AutoTagSidePreference.Auto));
            _sideInput.Items.Add(new UniversalNamedValue<AutoTagSidePreference>(
                "Left", AutoTagSidePreference.Left));
            _sideInput.Items.Add(new UniversalNamedValue<AutoTagSidePreference>(
                "Right", AutoTagSidePreference.Right));
            _sideInput.Items.Add(new UniversalNamedValue<AutoTagSidePreference>(
                "Top", AutoTagSidePreference.Top));
            _sideInput.Items.Add(new UniversalNamedValue<AutoTagSidePreference>(
                "Bottom", AutoTagSidePreference.Bottom));
            _arrowheadTypeInput = CreateDropDown();
            foreach (AutoTagArrowheadOption option in (arrowheadTypes ??
                new List<AutoTagArrowheadOption>()).Where(item => item != null))
            {
                _arrowheadTypeInput.Items.Add(option);
            }
            _leaderCheck = CreateCheckBox("Add short elbow leaders");
            AddSetting(source, "Scope", _scopeInput, 0, 0);
            AddSetting(source, "Preferred tag side", _sideInput, 2, 0);
            AddSetting(source, "Leader arrowhead", _arrowheadTypeInput, 0, 1);
            source.Controls.Add(_leaderCheck, 2, 1);
            source.SetColumnSpan(_leaderCheck, 2);
            sourceGroup.Controls.Add(source);
            root.Controls.Add(sourceGroup, 0, 0);

            var categoriesGroup = new GroupBox
            {
                Text = "MEP categories and native Revit Tag Family Types",
                Dock = DockStyle.Fill
            };
            var categoriesRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 5, 8, 5),
                ColumnCount = 1,
                RowCount = 2
            };
            categoriesRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            categoriesRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            var categoryTable = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                RowCount = UniversalTagCategoryDefinition.All.Count + 1,
                Location = new Point(0, 0),
                Padding = new Padding(4),
                Width = 735
            };
            categoryTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
            categoryTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            categoryTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));
            categoryTable.Controls.Add(CreateHeader("Use"), 0, 0);
            categoryTable.Controls.Add(CreateHeader("Category"), 1, 0);
            categoryTable.Controls.Add(CreateHeader("Tag Family : Type"), 2, 0);
            _categoryRows = new List<CategoryRow>();
            int categoryRow = 1;
            foreach (UniversalTagCategoryDefinition definition in UniversalTagCategoryDefinition.All)
            {
                List<UniversalAutoTagTypeOption> options = availableTypes
                    .Where(item => string.Equals(
                        item.CategoryKey,
                        definition.Key,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                UniversalTagCategorySelection saved = settings.FindCategory(definition.Key);
                var row = new CategoryRow(definition, options, saved, ValidateInputs);
                _categoryRows.Add(row);
                categoryTable.Controls.Add(row.EnabledCheck, 0, categoryRow);
                categoryTable.Controls.Add(row.CategoryLabel, 1, categoryRow);
                categoryTable.Controls.Add(row.TagTypeInput, 2, categoryRow);
                categoryRow++;
            }
            scroll.Controls.Add(categoryTable);
            categoriesRoot.Controls.Add(scroll, 0, 0);
            var categoryButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 2, 0, 0)
            };
            var selectAll = new Button { Text = "Select all available", Width = 140, Height = 26 };
            var clear = new Button { Text = "Clear", Width = 75, Height = 26 };
            selectAll.Click += delegate
            {
                foreach (CategoryRow row in _categoryRows.Where(item => item.HasTagTypes))
                {
                    row.EnabledCheck.Checked = true;
                }
                ValidateInputs();
            };
            clear.Click += delegate
            {
                foreach (CategoryRow row in _categoryRows)
                {
                    row.EnabledCheck.Checked = false;
                }
                ValidateInputs();
            };
            categoryButtons.Controls.Add(selectAll);
            categoryButtons.Controls.Add(clear);
            categoriesRoot.Controls.Add(categoryButtons, 0, 1);
            categoriesGroup.Controls.Add(categoriesRoot);
            root.Controls.Add(categoriesGroup, 0, 1);

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
            root.Controls.Add(placementGroup, 0, 2);

            var outputGroup = new GroupBox { Text = "Behavior", Dock = DockStyle.Fill };
            var output = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 2,
                RowCount = 2
            };
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _skipTaggedCheck = CreateCheckBox("Skip elements already tagged in this view");
            _previewCheck = CreateCheckBox("Preview result before Apply");
            _optimizeDocumentationCheck = CreateCheckBox("Optimize white space and leader crossings after tagging");
            output.Controls.Add(_skipTaggedCheck, 0, 0);
            output.Controls.Add(_previewCheck, 1, 0);
            output.Controls.Add(_optimizeDocumentationCheck, 0, 1);
            output.SetColumnSpan(_optimizeDocumentationCheck, 2);
            outputGroup.Controls.Add(output);
            root.Controls.Add(outputGroup, 0, 3);

            var infoPanel = new Panel { Dock = DockStyle.Fill };
            var information = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Text = "Each native Tag Family controls its displayed labels. Existing manual tags are never deleted or moved. Pipe/Duct runs remain in Smart MEP Annotation Pack."
            };
            _validationLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Firebrick,
                TextAlign = ContentAlignment.MiddleLeft
            };
            infoPanel.Controls.Add(_validationLabel);
            infoPanel.Controls.Add(information);
            root.Controls.Add(infoPanel, 0, 4);

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
            root.Controls.Add(buttons, 0, 5);
            AcceptButton = _okButton;
            CancelButton = cancelButton;

            SelectNamedValue(_scopeInput, settings.Scope);
            SelectNamedValue(_sideInput, settings.SidePreference);
            SelectArrowheadType(_arrowheadTypeInput, settings);
            _leaderCheck.Checked = settings.AddLeader;
            _skipTaggedCheck.Checked = settings.SkipAlreadyTagged;
            _previewCheck.Checked = settings.PreviewBeforeApply;
            _optimizeDocumentationCheck.Checked = settings.OptimizeDocumentationLayout;
            _offsetInput.Value = ToDecimal(settings.OffsetPaperMm, _offsetInput);
            _gapInput.Value = ToDecimal(settings.TagGapPaperMm, _gapInput);
            _clearanceInput.Value = ToDecimal(settings.ClearancePaperMm, _clearanceInput);
            _laneInput.Value = ToDecimal(settings.MaximumLaneSteps, _laneInput);
            _leaderCheck.CheckedChanged += delegate
            {
                _arrowheadTypeInput.Enabled = _leaderCheck.Checked;
                ValidateInputs();
            };
            _arrowheadTypeInput.SelectedIndexChanged += delegate { ValidateInputs(); };
            _arrowheadTypeInput.Enabled = _leaderCheck.Checked;
            ValidateInputs();
        }

        public UniversalMepAutoTagSettings Settings
        {
            get
            {
                AutoTagArrowheadOption arrowhead =
                    _arrowheadTypeInput.SelectedItem as AutoTagArrowheadOption;
                var settings = new UniversalMepAutoTagSettings
                {
                    Scope = GetNamedValue(_scopeInput, AutoTagScope.SelectionOrPick),
                    SidePreference = GetNamedValue(_sideInput, AutoTagSidePreference.Auto),
                    SelectedArrowheadTypeId = arrowhead == null ? -1 : arrowhead.IdValue,
                    SelectedArrowheadTypeName = arrowhead == null ? string.Empty : arrowhead.Name,
                    AddLeader = _leaderCheck.Checked,
                    SkipAlreadyTagged = _skipTaggedCheck.Checked,
                    PreviewBeforeApply = _previewCheck.Checked,
                    OptimizeDocumentationLayout = _optimizeDocumentationCheck.Checked,
                    OffsetPaperMm = (double)_offsetInput.Value,
                    TagGapPaperMm = (double)_gapInput.Value,
                    ClearancePaperMm = (double)_clearanceInput.Value,
                    MaximumLaneSteps = (int)_laneInput.Value,
                    Categories = _categoryRows.Select(row => row.CreateSelection()).ToList()
                };
                settings.Normalize();
                return settings;
            }
        }

        private bool ValidateInputs()
        {
            if (_categoryRows == null || _validationLabel == null || _okButton == null)
            {
                return false;
            }
            List<CategoryRow> enabled = _categoryRows
                .Where(row => row.EnabledCheck.Checked)
                .ToList();
            string error = enabled.Count == 0
                ? "Enable at least one category that has a loaded native Tag Family Type."
                : enabled.Any(row => !(row.TagTypeInput.SelectedItem is UniversalAutoTagTypeOption))
                    ? "Choose a Tag Family Type for every enabled category."
                    : _leaderCheck.Checked &&
                        !(_arrowheadTypeInput.SelectedItem is AutoTagArrowheadOption)
                        ? "Select a Leader Arrowhead Type or turn leaders off."
                        : string.Empty;
            _validationLabel.Text = error;
            _okButton.Enabled = string.IsNullOrEmpty(error);
            return string.IsNullOrEmpty(error);
        }

        private static Label CreateHeader(string text)
        {
            return new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 24,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = text
            };
        }

        private static ComboBox CreateDropDown()
        {
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                MaxDropDownItems = 16
            };
        }

        private static CheckBox CreateCheckBox(string text)
        {
            return new CheckBox
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = text
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
                ThousandsSeparator = true,
                Dock = DockStyle.Fill
            };
        }

        private static void AddSetting(
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
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            panel.Controls.Add(input, column + 1, row);
        }

        private static void SelectNamedValue<T>(ComboBox input, T value)
        {
            for (int index = 0; index < input.Items.Count; index++)
            {
                UniversalNamedValue<T> item = input.Items[index] as UniversalNamedValue<T>;
                if (item != null && EqualityComparer<T>.Default.Equals(item.Value, value))
                {
                    input.SelectedIndex = index;
                    return;
                }
            }
            if (input.Items.Count > 0) input.SelectedIndex = 0;
        }

        private static T GetNamedValue<T>(ComboBox input, T fallback)
        {
            UniversalNamedValue<T> item = input.SelectedItem as UniversalNamedValue<T>;
            return item == null ? fallback : item.Value;
        }

        private static void SelectArrowheadType(
            ComboBox input,
            UniversalMepAutoTagSettings settings)
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
            if (input.Items.Count > 0) input.SelectedIndex = 0;
        }

        private static decimal ToDecimal(double value, NumericUpDown input)
        {
            decimal converted;
            try { converted = Convert.ToDecimal(value); }
            catch { converted = input.Minimum; }
            return Math.Max(input.Minimum, Math.Min(input.Maximum, converted));
        }

        private sealed class CategoryRow
        {
            public CategoryRow(
                UniversalTagCategoryDefinition definition,
                IList<UniversalAutoTagTypeOption> options,
                UniversalTagCategorySelection saved,
                Func<bool> validate)
            {
                Definition = definition;
                EnabledCheck = new CheckBox
                {
                    Anchor = AnchorStyles.Left,
                    AutoSize = true
                };
                CategoryLabel = new Label
                {
                    Anchor = AnchorStyles.Left,
                    AutoSize = true,
                    Text = definition.DisplayName
                };
                TagTypeInput = CreateDropDown();
                TagTypeInput.Width = 450;
                TagTypeInput.Dock = DockStyle.Fill;
                foreach (UniversalAutoTagTypeOption option in options ??
                    new List<UniversalAutoTagTypeOption>())
                {
                    TagTypeInput.Items.Add(option);
                }
                HasTagTypes = TagTypeInput.Items.Count > 0;
                EnabledCheck.Enabled = HasTagTypes;
                TagTypeInput.Enabled = HasTagTypes;
                if (HasTagTypes)
                {
                    SelectTagType(saved);
                }
                else
                {
                    TagTypeInput.Items.Add("No compatible Tag Family Type loaded");
                    TagTypeInput.SelectedIndex = 0;
                }
                EnabledCheck.Checked = HasTagTypes && saved != null && saved.Enabled;
                EnabledCheck.CheckedChanged += delegate
                {
                    TagTypeInput.Enabled = HasTagTypes && EnabledCheck.Checked;
                    validate();
                };
                TagTypeInput.SelectedIndexChanged += delegate { validate(); };
                TagTypeInput.Enabled = HasTagTypes && EnabledCheck.Checked;
            }

            public UniversalTagCategoryDefinition Definition { get; private set; }
            public CheckBox EnabledCheck { get; private set; }
            public Label CategoryLabel { get; private set; }
            public ComboBox TagTypeInput { get; private set; }
            public bool HasTagTypes { get; private set; }

            public UniversalTagCategorySelection CreateSelection()
            {
                UniversalAutoTagTypeOption type =
                    TagTypeInput.SelectedItem as UniversalAutoTagTypeOption;
                return new UniversalTagCategorySelection
                {
                    CategoryKey = Definition.Key,
                    Enabled = EnabledCheck.Checked && type != null,
                    TagTypeId = type == null
                        ? Autodesk.Revit.DB.ElementId.InvalidElementId.Value
                        : type.IdValue,
                    TagTypeName = type == null ? string.Empty : type.DisplayName
                };
            }

            private void SelectTagType(UniversalTagCategorySelection saved)
            {
                if (saved != null)
                {
                    for (int index = 0; index < TagTypeInput.Items.Count; index++)
                    {
                        UniversalAutoTagTypeOption option =
                            TagTypeInput.Items[index] as UniversalAutoTagTypeOption;
                        if (option != null && option.IdValue == saved.TagTypeId)
                        {
                            TagTypeInput.SelectedIndex = index;
                            return;
                        }
                    }
                    if (!string.IsNullOrEmpty(saved.TagTypeName))
                    {
                        for (int index = 0; index < TagTypeInput.Items.Count; index++)
                        {
                            UniversalAutoTagTypeOption option =
                                TagTypeInput.Items[index] as UniversalAutoTagTypeOption;
                            if (option != null && string.Equals(
                                option.DisplayName,
                                saved.TagTypeName,
                                StringComparison.CurrentCultureIgnoreCase))
                            {
                                TagTypeInput.SelectedIndex = index;
                                return;
                            }
                        }
                    }
                }
                if (TagTypeInput.Items.Count > 0) TagTypeInput.SelectedIndex = 0;
            }
        }

        private sealed class UniversalNamedValue<T>
        {
            public UniversalNamedValue(string name, T value)
            {
                Name = name;
                Value = value;
            }
            public string Name { get; private set; }
            public T Value { get; private set; }
            public override string ToString() { return Name; }
        }
    }
}
