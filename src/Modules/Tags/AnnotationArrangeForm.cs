using System;
using System.Drawing;
using System.Windows.Forms;
using Button = System.Windows.Forms.Button;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;

namespace TungMEP.Modules.Tags
{
    internal sealed class AnnotationArrangeForm : Form
    {
        private readonly ComboBox _modeInput;
        private readonly ComboBox _scopeInput;
        private readonly ComboBox _sideInput;
        private readonly ComboBox _alignmentInput;
        private readonly ComboBox _leaderLayoutInput;
        private readonly ComboBox _sortInput;
        private readonly CheckBox _tagCheck;
        private readonly CheckBox _textCheck;
        private readonly CheckBox _textWithoutLeaderCheck;
        private readonly CheckBox _preserveLeaderEndCheck;
        private readonly CheckBox _createMissingLeaderCheck;
        private readonly CheckBox _avoidObstacleCheck;
        private readonly CheckBox _autoColumnCheck;
        private readonly CheckBox _keepPinnedCheck;
        private readonly CheckBox _reverseCheck;
        private readonly CheckBox _previewCheck;
        private readonly CheckBox _optimizeLeadersCheck;
        private readonly NumericUpDown _spacingInput;
        private readonly NumericUpDown _offsetInput;
        private readonly NumericUpDown _clearanceInput;
        private readonly NumericUpDown _columnGapInput;
        private readonly NumericUpDown _maximumItemsInput;
        private readonly NumericUpDown _searchStepsInput;
        private readonly Label _description;
        private readonly Label _validation;
        private readonly Button _okButton;

        public AnnotationArrangeForm(AnnotationArrangeSettings initialSettings)
        {
            AnnotationArrangeSettings settings = initialSettings == null
                ? AnnotationArrangeSettings.CreateDefault()
                : initialSettings.Clone();
            settings.Normalize();

            Text = "Tung MEP - Smart Annotation Arrange";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(780, 748);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 6
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 212));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 172));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            var generalGroup = new GroupBox { Text = "Layout", Dock = DockStyle.Fill };
            var general = CreateSettingsPanel(6, 205);
            _modeInput = CreateDropDown();
            _modeInput.Items.Add(new NamedValue<AnnotationArrangeMode>(
                "Align only",
                AnnotationArrangeMode.AlignOnly));
            _modeInput.Items.Add(new NamedValue<AnnotationArrangeMode>(
                "Distribute evenly",
                AnnotationArrangeMode.DistributeEvenly));
            _modeInput.Items.Add(new NamedValue<AnnotationArrangeMode>(
                "Compact stack",
                AnnotationArrangeMode.CompactStack));
            _modeInput.Items.Add(new NamedValue<AnnotationArrangeMode>(
                "Smart rail",
                AnnotationArrangeMode.SmartRail));
            _modeInput.Items.Add(new NamedValue<AnnotationArrangeMode>(
                "Documentation rail - minimum crossings",
                AnnotationArrangeMode.DocumentationRail));
            _modeInput.Items.Add(new NamedValue<AnnotationArrangeMode>(
                "Reflow after edit",
                AnnotationArrangeMode.ReflowAfterEdit));
            _scopeInput = CreateDropDown();
            _scopeInput.Items.Add(new NamedValue<AnnotationArrangeScope>(
                "Current selection / pick annotations",
                AnnotationArrangeScope.SelectionOrPick));
            _scopeInput.Items.Add(new NamedValue<AnnotationArrangeScope>(
                "Drag a rectangle",
                AnnotationArrangeScope.Rectangle));
            _scopeInput.Items.Add(new NamedValue<AnnotationArrangeScope>(
                "All supported annotations in active view",
                AnnotationArrangeScope.ActiveView));
            _sideInput = CreateDropDown();
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>(
                "Auto - least congested side",
                AnnotationArrangeSide.Auto));
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>("Left", AnnotationArrangeSide.Left));
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>("Right", AnnotationArrangeSide.Right));
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>("Top", AnnotationArrangeSide.Top));
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>("Bottom", AnnotationArrangeSide.Bottom));
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>(
                "Both sides - split by source clusters",
                AnnotationArrangeSide.BothSidesBySource));
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>(
                "Both sides - keep current left/right groups",
                AnnotationArrangeSide.BothSidesKeepCurrent));
            _sideInput.Items.Add(new NamedValue<AnnotationArrangeSide>(
                "Pick a rail with two points",
                AnnotationArrangeSide.PickRail));
            _alignmentInput = CreateDropDown();
            _alignmentInput.Items.Add(new NamedValue<AnnotationRailAlignment>(
                "Near model edge",
                AnnotationRailAlignment.NearModelEdge));
            _alignmentInput.Items.Add(new NamedValue<AnnotationRailAlignment>(
                "Center",
                AnnotationRailAlignment.Center));
            _alignmentInput.Items.Add(new NamedValue<AnnotationRailAlignment>(
                "Far model edge",
                AnnotationRailAlignment.FarModelEdge));
            _leaderLayoutInput = CreateDropDown();
            _leaderLayoutInput.Items.Add(new NamedValue<AnnotationLeaderLayout>(
                "Preserve current geometry",
                AnnotationLeaderLayout.Preserve));
            _leaderLayoutInput.Items.Add(new NamedValue<AnnotationLeaderLayout>(
                "Straight",
                AnnotationLeaderLayout.Straight));
            _leaderLayoutInput.Items.Add(new NamedValue<AnnotationLeaderLayout>(
                "Orthogonal elbow",
                AnnotationLeaderLayout.OrthogonalElbow));
            _sortInput = CreateDropDown();
            _sortInput.Items.Add(new NamedValue<AnnotationSortMode>(
                "Tagged source position (minimum crossings)",
                AnnotationSortMode.SourcePosition));
            _sortInput.Items.Add(new NamedValue<AnnotationSortMode>(
                "Nested leader fan (section/elevation equipment)",
                AnnotationSortMode.NestedLeaderFan));
            _sortInput.Items.Add(new NamedValue<AnnotationSortMode>(
                "Current annotation position",
                AnnotationSortMode.CurrentAnnotationPosition));
            _sortInput.Items.Add(new NamedValue<AnnotationSortMode>(
                "Element ID",
                AnnotationSortMode.ElementId));
            AddSetting(general, "Mode", _modeInput, 0);
            AddSetting(general, "Scope", _scopeInput, 1);
            AddSetting(general, "Side / rail", _sideInput, 2);
            AddSetting(general, "Alignment", _alignmentInput, 3);
            AddSetting(general, "Leader layout", _leaderLayoutInput, 4);
            AddSetting(general, "Sort order", _sortInput, 5);
            generalGroup.Controls.Add(general);
            root.Controls.Add(generalGroup, 0, 0);

            var categoryGroup = new GroupBox { Text = "Annotation types", Dock = DockStyle.Fill };
            var categories = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 8, 8, 6),
                ColumnCount = 3,
                RowCount = 1
            };
            categories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            categories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            categories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            _tagCheck = CreateCheckBox("Independent Tags");
            _textCheck = CreateCheckBox("Text Notes with leaders");
            _textWithoutLeaderCheck = CreateCheckBox("Text Notes without leaders");
            categories.Controls.Add(_tagCheck, 0, 0);
            categories.Controls.Add(_textCheck, 1, 0);
            categories.Controls.Add(_textWithoutLeaderCheck, 2, 0);
            categoryGroup.Controls.Add(categories);
            root.Controls.Add(categoryGroup, 0, 1);

            var spacingGroup = new GroupBox { Text = "Paper-space distances", Dock = DockStyle.Fill };
            var spacing = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 7, 8, 7),
                ColumnCount = 4,
                RowCount = 3
            };
            spacing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            spacing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            spacing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            spacing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            spacing.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            spacing.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
            spacing.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            _spacingInput = CreateNumberInput(0, 100, 4, 0.5M);
            _offsetInput = CreateNumberInput(0, 300, 12, 1);
            _clearanceInput = CreateNumberInput(0, 100, 2, 0.5M);
            _columnGapInput = CreateNumberInput(0, 200, 10, 1);
            _maximumItemsInput = CreateNumberInput(1, 100, 12, 1);
            _searchStepsInput = CreateNumberInput(1, 30, 12, 1);
            AddCompactSetting(spacing, "Item spacing (mm)", _spacingInput, 0, 0);
            AddCompactSetting(spacing, "Rail offset (mm)", _offsetInput, 2, 0);
            AddCompactSetting(spacing, "Obstacle clearance (mm)", _clearanceInput, 0, 1);
            AddCompactSetting(spacing, "Column gap (mm)", _columnGapInput, 2, 1);
            AddCompactSetting(spacing, "Max items per column", _maximumItemsInput, 0, 2);
            AddCompactSetting(spacing, "Placement search steps", _searchStepsInput, 2, 2);
            spacingGroup.Controls.Add(spacing);
            root.Controls.Add(spacingGroup, 0, 2);

            var behaviorGroup = new GroupBox { Text = "Behavior", Dock = DockStyle.Fill };
            var behavior = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 7, 8, 7),
                ColumnCount = 2,
                RowCount = 4
            };
            behavior.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            behavior.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            behavior.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            behavior.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            behavior.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            behavior.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _preserveLeaderEndCheck = CreateCheckBox("Preserve leader/arrow endpoints");
            _createMissingLeaderCheck = CreateCheckBox("Create missing leaders for Tags");
            _avoidObstacleCheck = CreateCheckBox("Avoid model, annotations, and dimensions");
            _autoColumnCheck = CreateCheckBox("Automatically create additional columns");
            _keepPinnedCheck = CreateCheckBox("Keep pinned annotations fixed");
            _reverseCheck = CreateCheckBox("Reverse item order");
            _previewCheck = CreateCheckBox("Preview result before Apply");
            _optimizeLeadersCheck = CreateCheckBox("Minimize leader crossings and model overlap");
            behavior.Controls.Add(_preserveLeaderEndCheck, 0, 0);
            behavior.Controls.Add(_createMissingLeaderCheck, 1, 0);
            behavior.Controls.Add(_avoidObstacleCheck, 0, 1);
            behavior.Controls.Add(_autoColumnCheck, 1, 1);
            behavior.Controls.Add(_keepPinnedCheck, 0, 2);
            behavior.Controls.Add(_reverseCheck, 1, 2);
            behavior.Controls.Add(_optimizeLeadersCheck, 0, 3);
            behavior.Controls.Add(_previewCheck, 1, 3);
            behaviorGroup.Controls.Add(behavior);
            root.Controls.Add(behaviorGroup, 0, 3);

            var infoPanel = new Panel { Dock = DockStyle.Fill };
            _description = new Label
            {
                Dock = DockStyle.Top,
                Height = 58,
                ForeColor = Color.FromArgb(45, 45, 45)
            };
            _validation = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Firebrick,
                TextAlign = ContentAlignment.MiddleLeft
            };
            infoPanel.Controls.Add(_validation);
            infoPanel.Controls.Add(_description);
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

            SelectValue(_modeInput, settings.Mode);
            SelectValue(_scopeInput, settings.Scope);
            SelectValue(_sideInput, settings.Side);
            SelectValue(_alignmentInput, settings.Alignment);
            SelectValue(_leaderLayoutInput, settings.LeaderLayout);
            SelectValue(_sortInput, settings.SortMode);
            _tagCheck.Checked = settings.IncludeIndependentTags;
            _textCheck.Checked = settings.IncludeTextNotes;
            _textWithoutLeaderCheck.Checked = settings.IncludeTextNotesWithoutLeaders;
            _preserveLeaderEndCheck.Checked = settings.PreserveLeaderEnds;
            _createMissingLeaderCheck.Checked = settings.CreateMissingTagLeaders;
            _avoidObstacleCheck.Checked = settings.AvoidObstacles;
            _autoColumnCheck.Checked = settings.AutoCreateColumns;
            _keepPinnedCheck.Checked = settings.KeepPinnedFixed;
            _reverseCheck.Checked = settings.ReverseOrder;
            _previewCheck.Checked = settings.PreviewBeforeApply;
            _optimizeLeadersCheck.Checked = settings.OptimizeLeaderRoutes;
            _spacingInput.Value = ToDecimal(settings.SpacingPaperMm, _spacingInput);
            _offsetInput.Value = ToDecimal(settings.RailOffsetPaperMm, _offsetInput);
            _clearanceInput.Value = ToDecimal(settings.ClearancePaperMm, _clearanceInput);
            _columnGapInput.Value = ToDecimal(settings.ColumnGapPaperMm, _columnGapInput);
            _maximumItemsInput.Value = ToDecimal(settings.MaximumItemsPerColumn, _maximumItemsInput);
            _searchStepsInput.Value = ToDecimal(settings.MaximumSearchSteps, _searchStepsInput);

            _modeInput.SelectedIndexChanged += delegate { UpdateState(); };
            _sideInput.SelectedIndexChanged += delegate { UpdateState(); };
            _tagCheck.CheckedChanged += delegate { UpdateState(); };
            _textCheck.CheckedChanged += delegate { UpdateState(); };
            _textWithoutLeaderCheck.CheckedChanged += delegate { UpdateState(); };
            _autoColumnCheck.CheckedChanged += delegate { UpdateState(); };
            UpdateState();
        }

        public AnnotationArrangeSettings Settings
        {
            get
            {
                var settings = new AnnotationArrangeSettings
                {
                    Mode = GetValue(_modeInput, AnnotationArrangeMode.DocumentationRail),
                    Scope = GetValue(_scopeInput, AnnotationArrangeScope.SelectionOrPick),
                    Side = GetValue(_sideInput, AnnotationArrangeSide.Auto),
                    Alignment = GetValue(_alignmentInput, AnnotationRailAlignment.NearModelEdge),
                    LeaderLayout = GetValue(_leaderLayoutInput, AnnotationLeaderLayout.OrthogonalElbow),
                    SortMode = GetValue(_sortInput, AnnotationSortMode.SourcePosition),
                    IncludeIndependentTags = _tagCheck.Checked,
                    IncludeTextNotes = _textCheck.Checked,
                    IncludeTextNotesWithoutLeaders = _textWithoutLeaderCheck.Checked,
                    PreserveLeaderEnds = _preserveLeaderEndCheck.Checked,
                    CreateMissingTagLeaders = _createMissingLeaderCheck.Checked,
                    AvoidObstacles = _avoidObstacleCheck.Checked,
                    AutoCreateColumns = _autoColumnCheck.Checked,
                    KeepPinnedFixed = _keepPinnedCheck.Checked,
                    ReverseOrder = _reverseCheck.Checked,
                    PreviewBeforeApply = _previewCheck.Checked,
                    OptimizeLeaderRoutes = _optimizeLeadersCheck.Checked,
                    SpacingPaperMm = (double)_spacingInput.Value,
                    RailOffsetPaperMm = (double)_offsetInput.Value,
                    ClearancePaperMm = (double)_clearanceInput.Value,
                    ColumnGapPaperMm = (double)_columnGapInput.Value,
                    MaximumItemsPerColumn = (int)_maximumItemsInput.Value,
                    MaximumSearchSteps = (int)_searchStepsInput.Value
                };
                settings.Normalize();
                return settings;
            }
        }

        private void UpdateState()
        {
            AnnotationArrangeMode mode = GetValue(
                _modeInput,
                AnnotationArrangeMode.DocumentationRail);
            _maximumItemsInput.Enabled = _autoColumnCheck.Checked &&
                (mode == AnnotationArrangeMode.SmartRail ||
                 mode == AnnotationArrangeMode.DocumentationRail ||
                 mode == AnnotationArrangeMode.CompactStack ||
                 mode == AnnotationArrangeMode.ReflowAfterEdit);
            _columnGapInput.Enabled = _autoColumnCheck.Checked;
            _description.Text = GetModeDescription(mode) +
                (GetValue(_sideInput, AnnotationArrangeSide.Auto) == AnnotationArrangeSide.PickRail
                    ? " After OK, pick two points for an orthogonal rail."
                    : IsBothSides(GetValue(_sideInput, AnnotationArrangeSide.Auto))
                        ? " Two independent vertical rails will be created and each side will be packed separately."
                        : string.Empty);
            ValidateInputs();
        }

        private bool ValidateInputs()
        {
            string error = null;
            if (!_tagCheck.Checked && !_textCheck.Checked)
            {
                error = "Select Independent Tags and/or Text Notes.";
            }
            else if (_textWithoutLeaderCheck.Checked && !_textCheck.Checked)
            {
                error = "Enable Text Notes before including notes without leaders.";
            }
            _validation.Text = error ?? string.Empty;
            _okButton.Enabled = error == null;
            return error == null;
        }

        private static string GetModeDescription(AnnotationArrangeMode mode)
        {
            if (mode == AnnotationArrangeMode.AlignOnly)
            {
                return "Align only: move all annotation heads to one rail while preserving their current order and spacing.";
            }
            if (mode == AnnotationArrangeMode.DistributeEvenly)
            {
                return "Distribute evenly: keep the first/last extents and divide all intermediate annotation positions equally.";
            }
            if (mode == AnnotationArrangeMode.CompactStack)
            {
                return "Compact stack: pack variable-height labels with the selected minimum paper-space gap.";
            }
            if (mode == AnnotationArrangeMode.ReflowAfterEdit)
            {
                return "Reflow after edit: retain the current rail location, then compact and reorder labels after content changes.";
            }
            if (mode == AnnotationArrangeMode.DocumentationRail)
            {
                return "Documentation rail: searches multiple rails, stack shifts, and source orders to reduce tag/model overlap and leader crossings.";
            }
            return "Smart rail: choose a clear outer rail, sort by tagged source to reduce crossings, pack labels, and create extra columns when required.";
        }

        private static bool IsBothSides(AnnotationArrangeSide side)
        {
            return side == AnnotationArrangeSide.BothSidesBySource ||
                side == AnnotationArrangeSide.BothSidesKeepCurrent;
        }

        private static TableLayoutPanel CreateSettingsPanel(int rows, int labelWidth)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 6, 8, 6),
                ColumnCount = 2,
                RowCount = rows
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < rows; row++)
            {
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0F / rows));
            }
            return panel;
        }

        private static void AddSetting(TableLayoutPanel panel, string label, Control input, int row)
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
            try { converted = (decimal)value; }
            catch { converted = input.Value; }
            return Math.Max(input.Minimum, Math.Min(input.Maximum, converted));
        }

        private static void SelectValue<T>(ComboBox input, T value)
        {
            for (int index = 0; index < input.Items.Count; index++)
            {
                NamedValue<T> item = input.Items[index] as NamedValue<T>;
                if (item != null && object.Equals(item.Value, value))
                {
                    input.SelectedIndex = index;
                    return;
                }
            }
            if (input.Items.Count > 0) { input.SelectedIndex = 0; }
        }

        private static T GetValue<T>(ComboBox input, T fallback)
        {
            NamedValue<T> item = input.SelectedItem as NamedValue<T>;
            return item == null ? fallback : item.Value;
        }
    }
}
