using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TungMEP.Modules.Tags
{
    internal sealed class SmartMepAnnotationForm : Form
    {
        private readonly ComboBox _scope;
        private readonly CheckBox _pipes;
        private readonly CheckBox _ducts;
        private readonly CheckBox _equipment;
        private readonly ComboBox _pipeType;
        private readonly ComboBox _ductType;
        private readonly ComboBox _equipmentType;
        private readonly CheckBox _skip;
        private readonly CheckBox _group;
        private readonly CheckBox _leaders;
        private readonly CheckBox _twoSides;
        private readonly CheckBox _avoid;
        private readonly CheckBox _preview;
        private readonly CheckBox _documentationLayout;
        private readonly CheckBox _gridDimensions;
        private readonly CheckBox _elevationDimensions;
        private readonly NumericUpDown _groupTolerance;
        private readonly NumericUpDown _spacing;
        private readonly NumericUpDown _offset;
        private readonly NumericUpDown _clearance;
        private readonly NumericUpDown _dimensionOffset;
        private readonly Label _validation;
        private readonly Button _ok;

        public SmartMepAnnotationForm(
            IEnumerable<SmartMepTagTypeOption> tagTypes,
            SmartMepAnnotationSettings initial)
        {
            SmartMepAnnotationSettings settings = initial == null
                ? SmartMepAnnotationSettings.CreateDefault()
                : initial.Clone();
            settings.Normalize();

            Text = "Tung MEP - Smart MEP Annotation Pack";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(760, 725);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 7
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 174));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            var scopeGroup = new GroupBox { Text = "Scope", Dock = DockStyle.Fill };
            _scope = DropDown();
            _scope.Items.Add(new ScopeOption("Current selection / pick MEP elements", SmartMepAnnotationScope.SelectionOrPick));
            _scope.Items.Add(new ScopeOption("Rectangle selection", SmartMepAnnotationScope.Rectangle));
            _scope.Items.Add(new ScopeOption("All supported elements in active view", SmartMepAnnotationScope.ActiveView));
            _scope.Location = new Point(12, 24);
            _scope.Size = new Size(700, 26);
            scopeGroup.Controls.Add(_scope);
            root.Controls.Add(scopeGroup, 0, 0);

            var typesGroup = new GroupBox { Text = "Native Revit Tag Family Types", Dock = DockStyle.Fill };
            var types = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                ColumnCount = 2,
                RowCount = 4
            };
            types.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
            types.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _pipes = Box("Pipes");
            _ducts = Box("Ducts");
            _equipment = Box("Mechanical equipment");
            _pipeType = DropDown();
            _ductType = DropDown();
            _equipmentType = DropDown();
            List<SmartMepTagTypeOption> options = (tagTypes ?? new List<SmartMepTagTypeOption>()).ToList();
            AddOptions(_pipeType, options, SmartMepTagKind.Pipe);
            AddOptions(_ductType, options, SmartMepTagKind.Duct);
            AddOptions(_equipmentType, options, SmartMepTagKind.Equipment);
            types.Controls.Add(_pipes, 0, 0); types.Controls.Add(_pipeType, 1, 0);
            types.Controls.Add(_ducts, 0, 1); types.Controls.Add(_ductType, 1, 1);
            types.Controls.Add(_equipment, 0, 2); types.Controls.Add(_equipmentType, 1, 2);
            types.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(55, 55, 55),
                Text = "Tag text is controlled by the selected Revit family. Choose a family containing System / DN / BOP-TOP / Slope labels."
            }, 0, 3);
            types.SetColumnSpan(types.GetControlFromPosition(0, 3), 2);
            typesGroup.Controls.Add(types);
            root.Controls.Add(typesGroup, 0, 1);

            var behaviorGroup = new GroupBox { Text = "Tag behavior", Dock = DockStyle.Fill };
            var behavior = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 2, RowCount = 3 };
            behavior.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            behavior.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _skip = Box("Skip elements already tagged in this view");
            _group = Box("Group matching parallel runs into one multi-leader tag");
            _leaders = Box("Create leaders");
            _twoSides = Box("Arrange new tags on two smart rails");
            _avoid = Box("Avoid visible model, annotations, and dimensions");
            _preview = Box("Preview before keeping result");
            behavior.Controls.Add(_skip, 0, 0); behavior.Controls.Add(_group, 1, 0);
            behavior.Controls.Add(_leaders, 0, 1); behavior.Controls.Add(_twoSides, 1, 1);
            behavior.Controls.Add(_avoid, 0, 2); behavior.Controls.Add(_preview, 1, 2);
            behaviorGroup.Controls.Add(behavior);
            root.Controls.Add(behaviorGroup, 0, 2);

            var distanceGroup = new GroupBox { Text = "Distances", Dock = DockStyle.Fill };
            var distance = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 4, RowCount = 2 };
            distance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            distance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            distance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            distance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            _groupTolerance = Number(0, 10000, 1200, 50);
            _spacing = Number(0, 100, 5, 0.5M);
            _offset = Number(0, 300, 12, 1);
            _clearance = Number(0, 100, 3, 0.5M);
            AddDistance(distance, "Parallel grouping tolerance (mm)", _groupTolerance, 0, 0);
            AddDistance(distance, "Item spacing - paper mm", _spacing, 2, 0);
            AddDistance(distance, "Rail offset - paper mm", _offset, 0, 1);
            AddDistance(distance, "Clearance - paper mm", _clearance, 2, 1);
            distanceGroup.Controls.Add(distance);
            root.Controls.Add(distanceGroup, 0, 3);

            var documentationGroup = new GroupBox { Text = "Orthographic documentation", Dock = DockStyle.Fill };
            var documentation = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                ColumnCount = 2,
                RowCount = 2
            };
            documentation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            documentation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _documentationLayout = Box("Optimize white space and leader crossings");
            _gridDimensions = Box("Dimension MEP elements to nearest grid");
            _elevationDimensions = Box("Dimension elevation to nearest level (section/elevation)");
            _dimensionOffset = Number(1, 100, 8, 0.5M);
            documentation.Controls.Add(_documentationLayout, 0, 0);
            documentation.Controls.Add(_gridDimensions, 1, 0);
            documentation.Controls.Add(_elevationDimensions, 0, 1);
            var offsetPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            offsetPanel.Controls.Add(new Label { Text = "Dimension offset (paper mm)", AutoSize = true, Anchor = AnchorStyles.Left });
            _dimensionOffset.Width = 90;
            offsetPanel.Controls.Add(_dimensionOffset);
            documentation.Controls.Add(offsetPanel, 1, 1);
            documentationGroup.Controls.Add(documentation);
            root.Controls.Add(documentationGroup, 0, 4);

            _validation = new Label { Dock = DockStyle.Fill, ForeColor = Color.Firebrick, Padding = new Padding(6) };
            root.Controls.Add(_validation, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            _ok = new Button { Text = "OK", Width = 90, Height = 28 };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            _ok.Click += delegate { if (ValidateForm()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(_ok); buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 6);
            AcceptButton = _ok; CancelButton = cancel;

            SelectScope(settings.Scope);
            _pipes.Checked = settings.IncludePipes && _pipeType.Items.Count > 0;
            _ducts.Checked = settings.IncludeDucts && _ductType.Items.Count > 0;
            _equipment.Checked = settings.IncludeEquipment && _equipmentType.Items.Count > 0;
            SelectType(_pipeType, settings.PipeTagTypeId);
            SelectType(_ductType, settings.DuctTagTypeId);
            SelectType(_equipmentType, settings.EquipmentTagTypeId);
            _skip.Checked = settings.SkipAlreadyTagged;
            _group.Checked = settings.GroupParallelRuns;
            _leaders.Checked = settings.AddLeaders;
            _twoSides.Checked = settings.ArrangeTwoSides;
            _avoid.Checked = settings.AvoidModel;
            _preview.Checked = settings.Preview;
            _documentationLayout.Checked = settings.OptimizeDocumentationLayout;
            _gridDimensions.Checked = settings.CreateGridReferenceDimensions;
            _elevationDimensions.Checked = settings.CreateElevationDimensions;
            _groupTolerance.Value = DecimalValue(settings.GroupToleranceMm, _groupTolerance);
            _spacing.Value = DecimalValue(settings.ItemSpacingPaperMm, _spacing);
            _offset.Value = DecimalValue(settings.RailOffsetPaperMm, _offset);
            _clearance.Value = DecimalValue(settings.ClearancePaperMm, _clearance);
            _dimensionOffset.Value = DecimalValue(settings.DimensionOffsetPaperMm, _dimensionOffset);
            _pipes.CheckedChanged += delegate { UpdateState(); };
            _ducts.CheckedChanged += delegate { UpdateState(); };
            _equipment.CheckedChanged += delegate { UpdateState(); };
            UpdateState();
        }

        public SmartMepAnnotationSettings Settings
        {
            get
            {
                var value = new SmartMepAnnotationSettings
                {
                    Scope = ((_scope.SelectedItem as ScopeOption) ?? new ScopeOption("", SmartMepAnnotationScope.SelectionOrPick)).Value,
                    IncludePipes = _pipes.Checked,
                    IncludeDucts = _ducts.Checked,
                    IncludeEquipment = _equipment.Checked,
                    PipeTagTypeId = SelectedTypeId(_pipeType),
                    DuctTagTypeId = SelectedTypeId(_ductType),
                    EquipmentTagTypeId = SelectedTypeId(_equipmentType),
                    SkipAlreadyTagged = _skip.Checked,
                    GroupParallelRuns = _group.Checked,
                    AddLeaders = _leaders.Checked,
                    ArrangeTwoSides = _twoSides.Checked,
                    AvoidModel = _avoid.Checked,
                    Preview = _preview.Checked,
                    OptimizeDocumentationLayout = _documentationLayout.Checked,
                    CreateGridReferenceDimensions = _gridDimensions.Checked,
                    CreateElevationDimensions = _elevationDimensions.Checked,
                    GroupToleranceMm = (double)_groupTolerance.Value,
                    ItemSpacingPaperMm = (double)_spacing.Value,
                    RailOffsetPaperMm = (double)_offset.Value,
                    ClearancePaperMm = (double)_clearance.Value,
                    DimensionOffsetPaperMm = (double)_dimensionOffset.Value
                };
                value.Normalize();
                return value;
            }
        }

        private void UpdateState()
        {
            _pipeType.Enabled = _pipes.Checked;
            _ductType.Enabled = _ducts.Checked;
            _equipmentType.Enabled = _equipment.Checked;
            _groupTolerance.Enabled = _group.Checked;
            ValidateForm();
        }

        private bool ValidateForm()
        {
            string error = null;
            if (!_pipes.Checked && !_ducts.Checked && !_equipment.Checked) error = "Select at least one MEP category with an available Tag Type.";
            else if (_pipes.Checked && _pipeType.SelectedItem == null) error = "Choose a Pipe Tag Type.";
            else if (_ducts.Checked && _ductType.SelectedItem == null) error = "Choose a Duct Tag Type.";
            else if (_equipment.Checked && _equipmentType.SelectedItem == null) error = "Choose a Mechanical Equipment Tag Type.";
            _validation.Text = error ?? string.Empty;
            _ok.Enabled = error == null;
            return error == null;
        }

        private void SelectScope(SmartMepAnnotationScope value)
        {
            for (int i = 0; i < _scope.Items.Count; i++)
            {
                ScopeOption option = _scope.Items[i] as ScopeOption;
                if (option != null && option.Value == value) { _scope.SelectedIndex = i; return; }
            }
            _scope.SelectedIndex = 0;
        }

        private static void AddOptions(ComboBox combo, IEnumerable<SmartMepTagTypeOption> options, SmartMepTagKind kind)
        {
            foreach (SmartMepTagTypeOption option in options.Where(item => item.Kind == kind).OrderBy(item => item.DisplayName)) combo.Items.Add(option);
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static void SelectType(ComboBox combo, long id)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                SmartMepTagTypeOption option = combo.Items[i] as SmartMepTagTypeOption;
                if (option != null && option.Symbol.Id.Value == id) { combo.SelectedIndex = i; return; }
            }
        }

        private static long SelectedTypeId(ComboBox combo)
        {
            SmartMepTagTypeOption option = combo.SelectedItem as SmartMepTagTypeOption;
            return option == null ? -1 : option.Symbol.Id.Value;
        }

        private static ComboBox DropDown() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill }; }
        private static CheckBox Box(string text) { return new CheckBox { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }; }
        private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal increment)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Increment = increment, DecimalPlaces = increment < 1 ? 1 : 0, ThousandsSeparator = true, Dock = DockStyle.Fill };
        }

        private static void AddDistance(TableLayoutPanel panel, string text, Control control, int column, int row)
        {
            panel.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, column, row);
            panel.Controls.Add(control, column + 1, row);
        }

        private static decimal DecimalValue(double value, NumericUpDown input)
        {
            return Math.Max(input.Minimum, Math.Min(input.Maximum, (decimal)value));
        }

        private sealed class ScopeOption
        {
            public ScopeOption(string text, SmartMepAnnotationScope value) { Text = text; Value = value; }
            public string Text { get; private set; }
            public SmartMepAnnotationScope Value { get; private set; }
            public override string ToString() { return Text; }
        }
    }
}
