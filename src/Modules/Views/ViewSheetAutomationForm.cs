using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Control = System.Windows.Forms.Control;
using Color = System.Drawing.Color;
using Form = System.Windows.Forms.Form;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.Views
{
    internal sealed class ViewSheetAutomationForm : Form
    {
        private readonly CheckedListBox _levels;
        private readonly CheckBox _plans;
        private readonly CheckBox _rcps;
        private readonly CheckBox _sectionX;
        private readonly CheckBox _sectionY;
        private readonly ComboBox _planType;
        private readonly ComboBox _rcpType;
        private readonly ComboBox _sectionType;
        private readonly ComboBox _planTemplate;
        private readonly ComboBox _rcpTemplate;
        private readonly ComboBox _sectionTemplate;
        private readonly ComboBox _zone;
        private readonly TextBox _viewPattern;
        private readonly NumericUpDown _scale;
        private readonly NumericUpDown _sectionPadding;
        private readonly CheckBox _createSheets;
        private readonly ComboBox _titleBlock;
        private readonly TextBox _sheetPrefix;
        private readonly NumericUpDown _sheetStart;
        private readonly NumericUpDown _sheetPadding;
        private readonly TextBox _sheetPattern;
        private readonly NumericUpDown _viewsPerSheet;
        private readonly DataGridView _previewGrid;
        private readonly ISet<string> _existingViewNames;
        private readonly ISet<string> _existingSheetNumbers;
        private readonly Label _validation;
        private readonly Button _ok;

        public ViewSheetAutomationForm(
            IEnumerable<Level> levels,
            IEnumerable<ViewFamilyType> planTypes,
            IEnumerable<ViewFamilyType> rcpTypes,
            IEnumerable<ViewFamilyType> sectionTypes,
            IEnumerable<View> planTemplates,
            IEnumerable<View> rcpTemplates,
            IEnumerable<View> sectionTemplates,
            IEnumerable<Element> zones,
            IEnumerable<FamilySymbol> titleBlocks,
            IEnumerable<string> existingViewNames,
            IEnumerable<string> existingSheetNumbers,
            ViewSheetAutomationSettings initial)
        {
            _existingViewNames = new HashSet<string>(
                existingViewNames ?? new string[0],
                StringComparer.CurrentCultureIgnoreCase);
            _existingSheetNumbers = new HashSet<string>(
                existingSheetNumbers ?? new string[0],
                StringComparer.CurrentCultureIgnoreCase);
            ViewSheetAutomationSettings settings = initial == null ? ViewSheetAutomationSettings.CreateDefault() : initial.Clone();
            settings.Normalize();
            Text = "Tung MEP - View & Sheet Automation";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; MinimizeBox = false;
            MinimumSize = new Size(860, 720);
            ClientSize = new Size(980, 900);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 8 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            var levelGroup = new GroupBox { Text = "Levels", Dock = DockStyle.Fill };
            _levels = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (Level level in (levels ?? new List<Level>()).OrderBy(item => item.Elevation)) _levels.Items.Add(new ElementOption(level.Id, level.Name), true);
            var levelButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 120, FlowDirection = FlowDirection.TopDown };
            var all = new Button { Text = "Select all", Width = 100 };
            var none = new Button { Text = "Clear", Width = 100 };
            all.Click += delegate { for (int i = 0; i < _levels.Items.Count; i++) _levels.SetItemChecked(i, true); ValidateForm(); };
            none.Click += delegate { for (int i = 0; i < _levels.Items.Count; i++) _levels.SetItemChecked(i, false); ValidateForm(); };
            levelButtons.Controls.Add(all); levelButtons.Controls.Add(none);
            levelGroup.Controls.Add(_levels); levelGroup.Controls.Add(levelButtons);
            root.Controls.Add(levelGroup, 0, 0);

            var outputGroup = new GroupBox { Text = "Create view types", Dock = DockStyle.Fill };
            var outputs = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
            _plans = Box("Floor / Engineering Plan"); _rcps = Box("Reflected Ceiling Plan");
            _sectionX = Box("Section X through zone"); _sectionY = Box("Section Y through zone");
            outputs.Controls.Add(_plans); outputs.Controls.Add(_rcps); outputs.Controls.Add(_sectionX); outputs.Controls.Add(_sectionY);
            outputGroup.Controls.Add(outputs); root.Controls.Add(outputGroup, 0, 1);

            var typeGroup = new GroupBox { Text = "View Family Type and View Template", Dock = DockStyle.Fill };
            var typeGrid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 5, RowCount = 3 };
            typeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            typeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            typeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            typeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            typeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            _planType = Combo(); _rcpType = Combo(); _sectionType = Combo();
            _planTemplate = Combo(); _rcpTemplate = Combo(); _sectionTemplate = Combo();
            Fill(_planType, planTypes == null ? null : planTypes.Select(item => new ElementOption(item.Id, item.Name)), false);
            Fill(_rcpType, rcpTypes == null ? null : rcpTypes.Select(item => new ElementOption(item.Id, item.Name)), false);
            Fill(_sectionType, sectionTypes == null ? null : sectionTypes.Select(item => new ElementOption(item.Id, item.Name)), false);
            Fill(_planTemplate, planTemplates == null ? null : planTemplates.Select(item => new ElementOption(item.Id, item.Name)), true);
            Fill(_rcpTemplate, rcpTemplates == null ? null : rcpTemplates.Select(item => new ElementOption(item.Id, item.Name)), true);
            Fill(_sectionTemplate, sectionTemplates == null ? null : sectionTemplates.Select(item => new ElementOption(item.Id, item.Name)), true);
            AddTypeRow(typeGrid, "Plan", _planType, _planTemplate, 0);
            AddTypeRow(typeGrid, "RCP", _rcpType, _rcpTemplate, 1);
            AddTypeRow(typeGrid, "Section", _sectionType, _sectionTemplate, 2);
            typeGroup.Controls.Add(typeGrid); root.Controls.Add(typeGroup, 0, 2);

            var namingGroup = new GroupBox { Text = "Zone, naming and scale", Dock = DockStyle.Fill };
            var naming = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 6, RowCount = 2 };
            naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75)); naming.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); naming.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); naming.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
            _zone = Combo(); Fill(_zone, zones == null ? null : zones.Select(item => new ElementOption(item.Id, item.Name)), true, "<No Scope Box>");
            _viewPattern = new TextBox { Dock = DockStyle.Fill }; _scale = Number(1, 10000, 100, 1); _sectionPadding = Number(0, 10000, 500, 50);
            Add(naming, "Scope Box", _zone, 0, 0); Add(naming, "View name", _viewPattern, 2, 0); Add(naming, "Scale 1:", _scale, 4, 0);
            naming.Controls.Add(new Label { Text = "Section padding (mm)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            naming.Controls.Add(_sectionPadding, 1, 1);
            naming.Controls.Add(new Label { Text = "Placeholders: {Level}, {Type}, {Zone}, {Index}", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left }, 2, 1);
            naming.SetColumnSpan(naming.GetControlFromPosition(2, 1), 4);
            namingGroup.Controls.Add(naming); root.Controls.Add(namingGroup, 0, 3);

            var sheetGroup = new GroupBox { Text = "Sheets and viewport layout", Dock = DockStyle.Fill };
            var sheets = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 6, RowCount = 3 };
            sheets.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); sheets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
            sheets.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105)); sheets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            sheets.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); sheets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            _createSheets = Box("Create Sheets"); _titleBlock = Combo();
            Fill(_titleBlock, titleBlocks == null ? null : titleBlocks.Select(item => new ElementOption(item.Id, (item.Family == null ? "" : item.Family.Name + " : ") + item.Name)), false);
            _sheetPrefix = new TextBox { Dock = DockStyle.Fill }; _sheetStart = Number(0, 999999, 1, 1); _sheetPadding = Number(1, 10, 3, 1);
            _sheetPattern = new TextBox { Dock = DockStyle.Fill }; _viewsPerSheet = Number(1, 4, 1, 1);
            sheets.Controls.Add(_createSheets, 0, 0); sheets.Controls.Add(_titleBlock, 1, 0); sheets.SetColumnSpan(_titleBlock, 5);
            Add(sheets, "Number prefix", _sheetPrefix, 0, 1); Add(sheets, "Start number", _sheetStart, 2, 1); Add(sheets, "Padding digits", _sheetPadding, 4, 1);
            Add(sheets, "Sheet name", _sheetPattern, 0, 2); Add(sheets, "Views per sheet", _viewsPerSheet, 2, 2);
            sheets.Controls.Add(new Label { Text = "Viewports are centered and distributed on a 1x1, 1x2, or 2x2 grid.", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left }, 4, 2);
            sheets.SetColumnSpan(sheets.GetControlFromPosition(4, 2), 2);
            sheetGroup.Controls.Add(sheets); root.Controls.Add(sheetGroup, 0, 4);

            var previewGroup = new GroupBox { Text = "Live preview - views and sheets that will be created", Dock = DockStyle.Fill };
            _previewGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = SystemColors.Window
            };
            _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "Output", Width = 70 });
            _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Number", HeaderText = "Sheet number", Width = 125 });
            _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Final name preview", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 280 });
            _previewGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Contents", HeaderText = "Level / contents", Width = 260 });
            previewGroup.Controls.Add(_previewGrid);
            root.Controls.Add(previewGroup, 0, 5);

            _validation = new Label { Dock = DockStyle.Fill, ForeColor = Color.Firebrick, Padding = new Padding(6) }; root.Controls.Add(_validation, 0, 6);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            _ok = new Button { Text = "Create", Width = 90, Height = 28 };
            var cancel = new Button { Text = "Cancel", Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            _ok.Click += delegate { if (ValidateForm()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(_ok); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 7);
            AcceptButton = _ok; CancelButton = cancel;

            _plans.Checked = settings.CreatePlans; _rcps.Checked = settings.CreateRcps; _sectionX.Checked = settings.CreateSectionX; _sectionY.Checked = settings.CreateSectionY;
            Select(_planType, settings.PlanTypeId); Select(_rcpType, settings.RcpTypeId); Select(_sectionType, settings.SectionTypeId);
            Select(_planTemplate, settings.PlanTemplateId); Select(_rcpTemplate, settings.RcpTemplateId); Select(_sectionTemplate, settings.SectionTemplateId);
            Select(_zone, settings.ScopeBoxId); _viewPattern.Text = settings.ViewNamePattern; _scale.Value = Clamp(settings.ViewScale, _scale); _sectionPadding.Value = Clamp(settings.SectionPaddingMm, _sectionPadding);
            _createSheets.Checked = settings.CreateSheets && _titleBlock.Items.Count > 0; Select(_titleBlock, settings.TitleBlockTypeId);
            _sheetPrefix.Text = settings.SheetNumberPrefix; _sheetStart.Value = Clamp(settings.SheetNumberStart, _sheetStart); _sheetPadding.Value = Clamp(settings.SheetNumberPadding, _sheetPadding);
            _sheetPattern.Text = settings.SheetNamePattern; _viewsPerSheet.Value = Clamp(settings.ViewsPerSheet, _viewsPerSheet);
            foreach (Control control in new Control[] { _plans, _rcps, _sectionX, _sectionY, _createSheets, _zone })
            {
                CheckBox check = control as CheckBox;
                if (check != null) check.CheckedChanged += delegate { UpdateState(); };
                ComboBox combo = control as ComboBox;
                if (combo != null) combo.SelectedIndexChanged += delegate { UpdateState(); };
            }
            foreach (TextBox input in new[] { _viewPattern, _sheetPrefix, _sheetPattern })
                input.TextChanged += delegate { UpdatePreview(); };
            foreach (NumericUpDown input in new[] { _sheetStart, _sheetPadding, _viewsPerSheet })
                input.ValueChanged += delegate { UpdatePreview(); };
            _levels.ItemCheck += delegate { BeginInvoke(new Action(UpdateState)); };
            UpdateState();
        }

        public IList<ElementId> SelectedLevelIds
        {
            get { return _levels.CheckedItems.Cast<ElementOption>().Select(item => item.Id).ToList(); }
        }

        public ViewSheetAutomationSettings Settings
        {
            get
            {
                var value = new ViewSheetAutomationSettings
                {
                    CreatePlans = _plans.Checked, CreateRcps = _rcps.Checked, CreateSectionX = _sectionX.Checked, CreateSectionY = _sectionY.Checked,
                    PlanTypeId = Id(_planType), RcpTypeId = Id(_rcpType), SectionTypeId = Id(_sectionType),
                    PlanTemplateId = Id(_planTemplate), RcpTemplateId = Id(_rcpTemplate), SectionTemplateId = Id(_sectionTemplate), ScopeBoxId = Id(_zone),
                    ViewNamePattern = _viewPattern.Text, ViewScale = (int)_scale.Value, SectionPaddingMm = (double)_sectionPadding.Value,
                    CreateSheets = _createSheets.Checked, TitleBlockTypeId = Id(_titleBlock), SheetNumberPrefix = _sheetPrefix.Text,
                    SheetNumberStart = (int)_sheetStart.Value, SheetNumberPadding = (int)_sheetPadding.Value, SheetNamePattern = _sheetPattern.Text,
                    ViewsPerSheet = (int)_viewsPerSheet.Value
                };
                value.Normalize(); return value;
            }
        }

        private void UpdateState()
        {
            _planType.Enabled = _plans.Checked; _planTemplate.Enabled = _plans.Checked;
            _rcpType.Enabled = _rcps.Checked; _rcpTemplate.Enabled = _rcps.Checked;
            bool sections = _sectionX.Checked || _sectionY.Checked;
            _sectionType.Enabled = sections; _sectionTemplate.Enabled = sections; _sectionPadding.Enabled = sections;
            foreach (Control control in new Control[] { _titleBlock, _sheetPrefix, _sheetStart, _sheetPadding, _sheetPattern, _viewsPerSheet }) control.Enabled = _createSheets.Checked;
            ValidateForm();
            UpdatePreview();
        }

        private bool ValidateForm()
        {
            string error = null;
            if (_levels.CheckedItems.Count == 0) error = "Select at least one Level.";
            else if (!_plans.Checked && !_rcps.Checked && !_sectionX.Checked && !_sectionY.Checked) error = "Select at least one view output.";
            else if (_plans.Checked && _planType.SelectedItem == null) error = "Choose a Floor/Engineering Plan View Family Type.";
            else if (_rcps.Checked && _rcpType.SelectedItem == null) error = "Choose an RCP View Family Type.";
            else if ((_sectionX.Checked || _sectionY.Checked) && _sectionType.SelectedItem == null) error = "Choose a Section View Family Type.";
            else if ((_sectionX.Checked || _sectionY.Checked) && Id(_zone) < 0) error = "Sections require a Scope Box zone.";
            else if (_createSheets.Checked && _titleBlock.SelectedItem == null) error = "Choose a Title Block Type.";
            _validation.Text = error ?? string.Empty; _ok.Enabled = error == null; return error == null;
        }

        private void UpdatePreview()
        {
            if (_previewGrid == null)
            {
                return;
            }
            _previewGrid.Rows.Clear();
            string zoneName = (_zone.SelectedItem as ElementOption) == null ||
                (_zone.SelectedItem as ElementOption).Id == ElementId.InvalidElementId
                ? "Project"
                : (_zone.SelectedItem as ElementOption).Name;
            var usedViewNames = new HashSet<string>(
                _existingViewNames,
                StringComparer.CurrentCultureIgnoreCase);
            var outputs = new List<PreviewOutput>();
            int index = 1;
            foreach (ElementOption level in _levels.CheckedItems.Cast<ElementOption>())
            {
                if (_plans.Checked)
                {
                    outputs.Add(CreatePreviewOutput(
                        level.Name,
                        "PLAN",
                        zoneName,
                        index++,
                        usedViewNames));
                }
                if (_rcps.Checked)
                {
                    outputs.Add(CreatePreviewOutput(
                        level.Name,
                        "RCP",
                        zoneName,
                        index++,
                        usedViewNames));
                }
                if (_sectionX.Checked)
                {
                    outputs.Add(CreatePreviewOutput(
                        level.Name,
                        "SECTION X",
                        zoneName,
                        index++,
                        usedViewNames));
                }
                if (_sectionY.Checked)
                {
                    outputs.Add(CreatePreviewOutput(
                        level.Name,
                        "SECTION Y",
                        zoneName,
                        index++,
                        usedViewNames));
                }
            }

            foreach (PreviewOutput output in outputs)
            {
                _previewGrid.Rows.Add(
                    "View",
                    string.Empty,
                    output.Name,
                    output.Level + " / " + output.Type +
                        (output.WasRenamed ? " / name conflict resolved" : string.Empty));
            }

            if (_createSheets.Checked && outputs.Count > 0)
            {
                var usedSheetNumbers = new HashSet<string>(
                    _existingSheetNumbers,
                    StringComparer.CurrentCultureIgnoreCase);
                int sequence = (int)_sheetStart.Value;
                int perSheet = Math.Max(1, (int)_viewsPerSheet.Value);
                for (int start = 0; start < outputs.Count; start += perSheet)
                {
                    List<PreviewOutput> batch = outputs.Skip(start).Take(perSheet).ToList();
                    string number = NextPreviewSheetNumber(
                        _sheetPrefix.Text,
                        ref sequence,
                        (int)_sheetPadding.Value,
                        usedSheetNumbers);
                    string sheetName = MakePreviewSheetName(
                        _sheetPattern.Text,
                        batch,
                        zoneName);
                    _previewGrid.Rows.Add(
                        "Sheet",
                        number,
                        sheetName,
                        string.Join(" | ", batch.Select(item => item.Name)));
                }
            }
        }

        private PreviewOutput CreatePreviewOutput(
            string level,
            string type,
            string zone,
            int index,
            ISet<string> usedNames)
        {
            string desired = ExpandPreviewName(
                _viewPattern.Text,
                level,
                type,
                zone,
                index);
            string finalName = UniquePreviewName(desired, usedNames);
            return new PreviewOutput
            {
                Name = finalName,
                Level = level,
                Type = type,
                WasRenamed = !string.Equals(
                    desired,
                    finalName,
                    StringComparison.CurrentCulture)
            };
        }

        private static string ExpandPreviewName(
            string pattern,
            string level,
            string type,
            string zone,
            int index)
        {
            string name = (pattern ?? "{Level} - {Type} - {Zone}")
                .Replace("{Level}", level)
                .Replace("{Type}", type)
                .Replace("{Zone}", zone)
                .Replace("{Index}", index.ToString("000"))
                .Trim();
            return name.Trim(' ', '-');
        }

        private static string UniquePreviewName(string desired, ISet<string> usedNames)
        {
            string baseName = string.IsNullOrWhiteSpace(desired)
                ? "TungMEP View"
                : desired;
            string candidate = baseName;
            int suffix = 2;
            while (usedNames.Contains(candidate))
            {
                candidate = baseName + " (" + suffix++ + ")";
            }
            usedNames.Add(candidate);
            return candidate;
        }

        private static string NextPreviewSheetNumber(
            string prefix,
            ref int sequence,
            int padding,
            ISet<string> usedNumbers)
        {
            string candidate;
            do
            {
                candidate = (prefix ?? string.Empty) +
                    sequence.ToString(new string('0', Math.Max(1, padding)));
                sequence++;
            }
            while (usedNumbers.Contains(candidate));
            usedNumbers.Add(candidate);
            return candidate;
        }

        private static string MakePreviewSheetName(
            string pattern,
            IList<PreviewOutput> views,
            string zone)
        {
            string first = views.Count == 0 ? "MEP Views" : views[0].Name;
            string level = views.Count == 0 ? string.Empty : views[0].Level;
            return (pattern ?? "{FirstView}")
                .Replace("{FirstView}", first)
                .Replace("{Level}", level)
                .Replace("{Zone}", zone)
                .Trim();
        }

        private static void AddTypeRow(TableLayoutPanel grid, string name, ComboBox type, ComboBox template, int row)
        {
            grid.Controls.Add(new Label { Text = name + " Type", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); grid.Controls.Add(type, 1, row);
            grid.Controls.Add(new Label { Text = "Template", AutoSize = true, Anchor = AnchorStyles.Left }, 2, row); grid.Controls.Add(template, 3, row); grid.SetColumnSpan(template, 2);
        }

        private static void Add(TableLayoutPanel panel, string label, Control control, int column, int row)
        {
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, column, row); panel.Controls.Add(control, column + 1, row);
        }

        private static CheckBox Box(string text) { return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(12, 8, 18, 6) }; }
        private static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill }; }
        private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal increment) { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Increment = increment, DecimalPlaces = increment < 1 ? 1 : 0, ThousandsSeparator = true, Dock = DockStyle.Fill }; }
        private static decimal Clamp(double value, NumericUpDown input) { return Math.Max(input.Minimum, Math.Min(input.Maximum, (decimal)value)); }

        private static void Fill(ComboBox combo, IEnumerable<ElementOption> options, bool includeNone, string noneText = "<None>")
        {
            if (includeNone) combo.Items.Add(new ElementOption(ElementId.InvalidElementId, noneText));
            if (options != null) foreach (ElementOption option in options.OrderBy(item => item.Name)) combo.Items.Add(option);
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static void Select(ComboBox combo, long id)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                ElementOption option = combo.Items[i] as ElementOption;
                if (option != null && option.Id.Value == id) { combo.SelectedIndex = i; return; }
            }
        }

        private static long Id(ComboBox combo) { ElementOption option = combo.SelectedItem as ElementOption; return option == null ? -1 : option.Id.Value; }

        internal sealed class ElementOption
        {
            public ElementOption(ElementId id, string name) { Id = id; Name = name ?? string.Empty; }
            public ElementId Id { get; private set; }
            public string Name { get; private set; }
            public override string ToString() { return Name; }
        }

        private sealed class PreviewOutput
        {
            public string Name { get; set; }
            public string Level { get; set; }
            public string Type { get; set; }
            public bool WasRenamed { get; set; }
        }
    }
}
