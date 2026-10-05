using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.Views
{
    [Transaction(TransactionMode.Manual)]
    public class SheetRenumberCommand : IExternalCommand
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
                TaskDialog.Show(
                    "Tung MEP - Sheet Renumber",
                    "Open a Revit project before running Sheet Renumber.");
                return Result.Cancelled;
            }

            List<ViewSheet> sheets = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(sheet => !sheet.IsTemplate)
                .ToList();
            if (sheets.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Sheet Renumber",
                    "This project contains no sheets.");
                return Result.Cancelled;
            }

            ViewSheet activeSheet = document.ActiveView as ViewSheet;
            ElementId activeSheetId = activeSheet == null
                ? ElementId.InvalidElementId
                : activeSheet.Id;
            ISet<long> preselectedSheetIds = CollectPreselectedSheetIds(
                uiDocument,
                document);
            if (preselectedSheetIds.Count == 0 &&
                activeSheetId != ElementId.InvalidElementId)
            {
                preselectedSheetIds.Add(activeSheetId.Value);
            }
            using (var dialog = new SheetRenumberForm(
                sheets,
                activeSheetId,
                preselectedSheetIds,
                CollectBrowserFolderPaths(document, sheets)))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                List<SheetRenumberAssignment> assignments = dialog.Assignments.ToList();
                string validationError;
                if (!TryValidateAssignments(sheets, assignments, out validationError))
                {
                    TaskDialog.Show("Tung MEP - Sheet Renumber", validationError);
                    return Result.Cancelled;
                }

                Transaction transaction = new Transaction(document, "Tung MEP - Sheet Renumber");
                try
                {
                    transaction.Start();
                    HashSet<string> reservedNumbers = new HashSet<string>(
                        sheets.Select(sheet => sheet.SheetNumber),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (SheetRenumberAssignment assignment in assignments)
                    {
                        reservedNumbers.Add(assignment.NewNumber);
                    }
                    foreach (SheetRenumberAssignment assignment in assignments)
                    {
                        assignment.Sheet.SheetNumber = CreateTemporaryNumber(reservedNumbers);
                    }
                    document.Regenerate();

                    foreach (SheetRenumberAssignment assignment in assignments)
                    {
                        assignment.Sheet.SheetNumber = assignment.NewNumber;
                    }
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException(
                            "The Revit transaction ended with status " + status + ".");
                    }
                }
                catch (Exception ex)
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        transaction.RollBack();
                    }
                    message = string.Empty;
                    TaskDialog.Show(
                        "Tung MEP - Sheet Renumber",
                        "No sheet number was changed because Revit could not complete the renumbering.\n\n" +
                        ex.Message);
                    return Result.Cancelled;
                }
                finally
                {
                    transaction.Dispose();
                }

                TaskDialog.Show(
                    "Tung MEP - Sheet Renumber",
                    "Renumbered " + assignments.Count + " sheet(s).\n\n" +
                    "First: " + assignments[0].NewNumber +
                    "\nLast: " + assignments[assignments.Count - 1].NewNumber);
                return Result.Succeeded;
            }
        }

        private static ISet<long> CollectPreselectedSheetIds(
            UIDocument uiDocument,
            Document document)
        {
            var result = new HashSet<long>();
            foreach (ElementId id in uiDocument.Selection.GetElementIds())
            {
                Element element = document.GetElement(id);
                ViewSheet sheet = element as ViewSheet;
                if (sheet != null)
                {
                    result.Add(sheet.Id.Value);
                    continue;
                }
                Viewport viewport = element as Viewport;
                if (viewport != null &&
                    viewport.SheetId != ElementId.InvalidElementId)
                {
                    result.Add(viewport.SheetId.Value);
                    continue;
                }
                if (element != null &&
                    element.OwnerViewId != ElementId.InvalidElementId &&
                    document.GetElement(element.OwnerViewId) is ViewSheet)
                {
                    result.Add(element.OwnerViewId.Value);
                }
            }
            return result;
        }

        private static IDictionary<long, IList<string>> CollectBrowserFolderPaths(
            Document document,
            IEnumerable<ViewSheet> sheets)
        {
            var result = new Dictionary<long, IList<string>>();
            BrowserOrganization organization = null;
            try
            {
                organization = BrowserOrganization
                    .GetCurrentBrowserOrganizationForSheets(document);
            }
            catch
            {
            }
            if (organization == null)
            {
                return result;
            }
            foreach (ViewSheet sheet in sheets)
            {
                var path = new List<string>();
                IList<FolderItemInfo> items = null;
                try
                {
                    items = organization.GetFolderItems(sheet.Id);
                    foreach (FolderItemInfo item in items)
                    {
                        if (item != null && !string.IsNullOrWhiteSpace(item.Name))
                        {
                            path.Add(item.Name.Trim());
                        }
                    }
                }
                catch
                {
                    path.Clear();
                }
                finally
                {
                    if (items != null)
                    {
                        foreach (FolderItemInfo item in items)
                        {
                            if (item != null)
                            {
                                item.Dispose();
                            }
                        }
                    }
                }
                if (path.Count > 0)
                {
                    result[sheet.Id.Value] = path;
                }
            }
            return result;
        }

        internal static bool TryValidateAssignments(
            IList<ViewSheet> allSheets,
            IList<SheetRenumberAssignment> assignments,
            out string error)
        {
            error = string.Empty;
            if (assignments == null || assignments.Count == 0)
            {
                error = "Check at least one sheet to renumber.";
                return false;
            }

            var selectedIds = new HashSet<long>(assignments.Select(item => item.Sheet.Id.Value));
            var generatedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SheetRenumberAssignment assignment in assignments)
            {
                if (assignment.Sheet == null || !assignment.Sheet.IsValidObject)
                {
                    error = "One of the selected sheets is no longer available.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(assignment.NewNumber))
                {
                    error = "A sheet number cannot be empty.";
                    return false;
                }
                if (!generatedNumbers.Add(assignment.NewNumber))
                {
                    error = "The preview contains the duplicate sheet number '" +
                        assignment.NewNumber + "'.";
                    return false;
                }
            }

            var unselectedNumbers = new HashSet<string>(
                allSheets
                    .Where(sheet => !selectedIds.Contains(sheet.Id.Value))
                    .Select(sheet => sheet.SheetNumber),
                StringComparer.OrdinalIgnoreCase);
            string collision = generatedNumbers.FirstOrDefault(unselectedNumbers.Contains);
            if (!string.IsNullOrEmpty(collision))
            {
                error = "The sheet number '" + collision +
                    "' is already used by a sheet that is not selected.";
                return false;
            }
            return true;
        }

        private static string CreateTemporaryNumber(ISet<string> reservedNumbers)
        {
            string value;
            do
            {
                value = "TM-TMP-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            }
            while (reservedNumbers.Contains(value));
            reservedNumbers.Add(value);
            return value;
        }
    }

    internal sealed class SheetRenumberAssignment
    {
        public ViewSheet Sheet { get; set; }
        public string NewNumber { get; set; }
    }

    internal enum SheetRenumberSort
    {
        Number,
        Name
    }

    internal sealed class SheetRenumberForm : Form
    {
        private readonly IList<ViewSheet> _allSheets;
        private readonly ElementId _activeSheetId;
        private readonly HashSet<long> _checkedSheetIds;
        private readonly IDictionary<long, IList<string>> _browserFolderPaths;
        private readonly DataGridView _sheetGrid;
        private readonly TreeView _sheetTree;
        private readonly TextBox _search;
        private readonly ComboBox _sort;
        private readonly TextBox _prefix;
        private readonly TextBox _suffix;
        private readonly NumericUpDown _start;
        private readonly NumericUpDown _increment;
        private readonly NumericUpDown _padding;
        private readonly CheckBox _includePlaceholders;
        private readonly Label _status;
        private readonly Button _apply;
        private bool _rebuilding;
        private bool _updatingTree;
        private TreeNode _selectedTreeNode;

        public SheetRenumberForm(
            IList<ViewSheet> sheets,
            ElementId activeSheetId,
            ISet<long> initiallyCheckedIds,
            IDictionary<long, IList<string>> browserFolderPaths)
        {
            _allSheets = sheets;
            _activeSheetId = activeSheetId ?? ElementId.InvalidElementId;
            _checkedSheetIds = new HashSet<long>(initiallyCheckedIds ?? new HashSet<long>());
            _browserFolderPaths = browserFolderPaths ??
                new Dictionary<long, IList<string>>();

            Text = "Tung MEP - Sheet Renumber";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            MinimumSize = new Size(940, 580);
            ClientSize = new Size(1180, 680);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);

            var settings = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 9, 10, 4),
                ColumnCount = 8,
                RowCount = 2
            };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            _sort = CreateComboBox();
            _sort.Items.Add("Sheet number");
            _sort.Items.Add("Sheet name");
            _sort.SelectedIndex = 0;
            _prefix = CreateTextBox();
            _suffix = CreateTextBox();
            _start = CreateNumericBox(-999999, 999999, 1);
            _increment = CreateNumericBox(1, 999999, 1);
            _padding = CreateNumericBox(0, 12, 3);
            _includePlaceholders = new CheckBox
            {
                AutoSize = true,
                Text = "Include placeholder sheets",
                Anchor = AnchorStyles.Left,
                Checked = false
            };

            AddSetting(settings, "Sort by", _sort, 0, 0);
            AddSetting(settings, "Prefix", _prefix, 2, 0);
            AddSetting(settings, "Suffix", _suffix, 4, 0);
            AddSetting(settings, "Start", _start, 6, 0);
            AddSetting(settings, "Increment", _increment, 0, 1);
            AddSetting(settings, "Zero pad", _padding, 2, 1);
            settings.Controls.Add(_includePlaceholders, 4, 1);
            settings.SetColumnSpan(_includePlaceholders, 4);
            root.Controls.Add(settings, 0, 0);

            _sheetGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoGenerateColumns = false,
                MultiSelect = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.Fixed3D
            };
            _sheetGrid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Use",
                HeaderText = "Use",
                Width = 48,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _sheetGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "CurrentNumber",
                HeaderText = "Current number",
                ReadOnly = true,
                Width = 145,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _sheetGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "SheetName",
                HeaderText = "Sheet name",
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 220,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _sheetGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Placeholder",
                HeaderText = "Placeholder",
                ReadOnly = true,
                Width = 92,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            _sheetGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "NewNumber",
                HeaderText = "New number preview",
                ReadOnly = true,
                Width = 180,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
            var content = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 325,
                FixedPanel = FixedPanel.Panel1,
                Panel1MinSize = 260,
                Panel2MinSize = 520
            };
            var treePanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(7)
            };
            treePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            treePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            treePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            treePanel.Controls.Add(new Label
            {
                Text = "Sheet folders / search",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            _search = new TextBox
            {
                Dock = DockStyle.Fill
            };
            treePanel.Controls.Add(_search, 0, 1);
            _sheetTree = new TreeView
            {
                Dock = DockStyle.Fill,
                CheckBoxes = true,
                HideSelection = false,
                FullRowSelect = true
            };
            treePanel.Controls.Add(_sheetTree, 0, 2);
            content.Panel1.Controls.Add(treePanel);
            content.Panel2.Controls.Add(_sheetGrid);
            root.Controls.Add(content, 0, 2);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 8, 10, 8),
                ColumnCount = 3,
                RowCount = 1
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            _apply = new Button
            {
                Text = "Renumber",
                DialogResult = DialogResult.None,
                Dock = DockStyle.Fill
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Dock = DockStyle.Fill
            };
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(_apply, 1, 0);
            footer.Controls.Add(cancel, 2, 0);
            root.Controls.Add(footer, 0, 3);

            var selectionBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 3, 8, 3),
                FlowDirection = FlowDirection.LeftToRight
            };
            var selectAll = new Button { Text = "Check all", Width = 88, Height = 26 };
            var selectNone = new Button { Text = "Check none", Width = 88, Height = 26 };
            selectAll.Click += delegate { SetAllChecked(true); };
            selectNone.Click += delegate { SetAllChecked(false); };
            selectionBar.Controls.Add(selectAll);
            selectionBar.Controls.Add(selectNone);
            root.Controls.Add(selectionBar, 0, 1);

            _sort.SelectedIndexChanged += delegate { RebuildRows(); };
            _includePlaceholders.CheckedChanged += delegate
            {
                if (!_includePlaceholders.Checked)
                {
                    foreach (ViewSheet placeholder in _allSheets.Where(sheet => sheet.IsPlaceholder))
                    {
                        _checkedSheetIds.Remove(placeholder.Id.Value);
                    }
                }
                BuildSheetTree();
                RebuildRows();
            };
            _search.TextChanged += delegate
            {
                BuildSheetTree();
                RebuildRows();
            };
            _prefix.TextChanged += delegate { UpdatePreview(); };
            _suffix.TextChanged += delegate { UpdatePreview(); };
            _start.ValueChanged += delegate { UpdatePreview(); };
            _increment.ValueChanged += delegate { UpdatePreview(); };
            _padding.ValueChanged += delegate { UpdatePreview(); };
            _sheetGrid.CurrentCellDirtyStateChanged += delegate
            {
                if (_sheetGrid.IsCurrentCellDirty)
                {
                    _sheetGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            _sheetGrid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs args)
            {
                if (!_rebuilding && args.RowIndex >= 0 && args.ColumnIndex == 0)
                {
                    ViewSheet sheet = _sheetGrid.Rows[args.RowIndex].Tag as ViewSheet;
                    if (sheet != null)
                    {
                        if (IsRowChecked(_sheetGrid.Rows[args.RowIndex]))
                        {
                            _checkedSheetIds.Add(sheet.Id.Value);
                        }
                        else
                        {
                            _checkedSheetIds.Remove(sheet.Id.Value);
                        }
                        SyncTreeChecks();
                    }
                    UpdatePreview();
                }
            };
            _sheetTree.AfterSelect += delegate(object sender, TreeViewEventArgs args)
            {
                if (_updatingTree)
                {
                    return;
                }
                _selectedTreeNode = args.Node;
                RebuildRows();
            };
            _sheetTree.AfterCheck += delegate(object sender, TreeViewEventArgs args)
            {
                if (_updatingTree)
                {
                    return;
                }
                SetTreeNodeChecked(args.Node, args.Node.Checked);
                RebuildRows();
            };
            _apply.Click += delegate
            {
                string error;
                if (!SheetRenumberCommand.TryValidateAssignments(_allSheets, Assignments.ToList(), out error))
                {
                    MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };

            AcceptButton = _apply;
            CancelButton = cancel;

            BuildSheetTree();
            RebuildRows();
        }

        public IEnumerable<SheetRenumberAssignment> Assignments
        {
            get { return BuildAssignments(); }
        }

        private void RebuildRows()
        {
            _rebuilding = true;
            try
            {
                _sheetGrid.Rows.Clear();
                IEnumerable<ViewSheet> visible = OrderedSheets(
                    FilteredSheets().Where(IsInSelectedTreeNode));

                foreach (ViewSheet sheet in visible)
                {
                    bool isChecked = _checkedSheetIds.Contains(sheet.Id.Value);
                    int index = _sheetGrid.Rows.Add(
                        isChecked,
                        sheet.SheetNumber,
                        sheet.Name,
                        sheet.IsPlaceholder ? "Yes" : string.Empty,
                        string.Empty);
                    _sheetGrid.Rows[index].Tag = sheet;
                    if (_activeSheetId != ElementId.InvalidElementId &&
                        sheet.Id.Value == _activeSheetId.Value)
                    {
                        _sheetGrid.Rows[index].DefaultCellStyle.Font = new Font(
                            _sheetGrid.Font,
                            FontStyle.Bold);
                    }
                }
            }
            finally
            {
                _rebuilding = false;
            }
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_rebuilding)
            {
                return;
            }
            List<SheetRenumberAssignment> assignments = BuildAssignments().ToList();
            Dictionary<long, string> previewById = assignments.ToDictionary(
                assignment => assignment.Sheet.Id.Value,
                assignment => assignment.NewNumber);
            foreach (DataGridViewRow row in _sheetGrid.Rows)
            {
                ViewSheet sheet = row.Tag as ViewSheet;
                string preview;
                if (sheet == null || !previewById.TryGetValue(sheet.Id.Value, out preview))
                {
                    row.Cells["NewNumber"].Value = string.Empty;
                    row.Cells["NewNumber"].Style.ForeColor = SystemColors.GrayText;
                    continue;
                }
                row.Cells["NewNumber"].Value = preview;
                row.Cells["NewNumber"].Style.ForeColor = SystemColors.ControlText;
            }

            string error;
            bool valid = SheetRenumberCommand.TryValidateAssignments(
                _allSheets,
                assignments,
                out error);
            _apply.Enabled = valid;
            _status.Text = valid
                ? assignments.Count + " sheet(s) ready to renumber. Preselected sheets are checked automatically."
                : error;
            _status.ForeColor = valid ? Color.DarkGreen : Color.Firebrick;
        }

        private IEnumerable<SheetRenumberAssignment> BuildAssignments()
        {
            int sequenceIndex = 0;
            foreach (ViewSheet sheet in OrderedSheets(_allSheets.Where(item =>
                (_includePlaceholders.Checked || !item.IsPlaceholder) &&
                _checkedSheetIds.Contains(item.Id.Value))))
            {
                decimal value = _start.Value + _increment.Value * sequenceIndex;
                yield return new SheetRenumberAssignment
                {
                    Sheet = sheet,
                    NewNumber = _prefix.Text +
                        FormatSequenceValue(value, Decimal.ToInt32(_padding.Value)) +
                        _suffix.Text
                };
                sequenceIndex++;
            }
        }

        private void SetAllChecked(bool value)
        {
            foreach (DataGridViewRow row in _sheetGrid.Rows)
            {
                ViewSheet sheet = row.Tag as ViewSheet;
                if (sheet == null)
                {
                    continue;
                }
                if (value)
                {
                    _checkedSheetIds.Add(sheet.Id.Value);
                }
                else
                {
                    _checkedSheetIds.Remove(sheet.Id.Value);
                }
            }
            SyncTreeChecks();
            RebuildRows();
        }

        private IEnumerable<ViewSheet> OrderedSheets(IEnumerable<ViewSheet> source)
        {
            return SelectedSort == SheetRenumberSort.Name
                ? source.OrderBy(sheet => sheet.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(sheet => sheet.SheetNumber, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderBy(sheet => sheet.SheetNumber, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(sheet => sheet.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        private IEnumerable<ViewSheet> FilteredSheets()
        {
            string query = (_search.Text ?? string.Empty).Trim();
            return _allSheets.Where(sheet =>
                (_includePlaceholders.Checked || !sheet.IsPlaceholder) &&
                (query.Length == 0 ||
                 sheet.SheetNumber.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                 sheet.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0));
        }

        private bool IsInSelectedTreeNode(ViewSheet sheet)
        {
            if (_selectedTreeNode == null || _selectedTreeNode.Parent == null)
            {
                return true;
            }
            ViewSheet selectedSheet = _selectedTreeNode.Tag as ViewSheet;
            if (selectedSheet != null)
            {
                return sheet.Id.Value == selectedSheet.Id.Value;
            }
            string folderPath = _selectedTreeNode.Tag as string;
            string sheetPath = FolderPath(sheet);
            return string.IsNullOrEmpty(folderPath) ||
                string.Equals(sheetPath, folderPath, StringComparison.CurrentCultureIgnoreCase) ||
                sheetPath.StartsWith(folderPath + "/", StringComparison.CurrentCultureIgnoreCase);
        }

        private void BuildSheetTree()
        {
            _updatingTree = true;
            try
            {
                string selectedPath = _selectedTreeNode == null
                    ? string.Empty
                    : _selectedTreeNode.Tag as string ?? string.Empty;
                _sheetTree.BeginUpdate();
                _sheetTree.Nodes.Clear();
                List<ViewSheet> sheets = OrderedSheets(FilteredSheets()).ToList();
                var root = new TreeNode("All sheets (" + sheets.Count + ")")
                {
                    Tag = string.Empty
                };
                _sheetTree.Nodes.Add(root);
                foreach (ViewSheet sheet in sheets)
                {
                    TreeNode parent = root;
                    string currentPath = string.Empty;
                    foreach (string segment in FolderSegments(sheet))
                    {
                        currentPath = currentPath.Length == 0
                            ? segment
                            : currentPath + "/" + segment;
                        TreeNode existing = parent.Nodes.Cast<TreeNode>()
                            .FirstOrDefault(node => node.Tag is string &&
                                string.Equals((string)node.Tag, currentPath, StringComparison.CurrentCultureIgnoreCase));
                        if (existing == null)
                        {
                            existing = new TreeNode(segment) { Tag = currentPath };
                            parent.Nodes.Add(existing);
                        }
                        parent = existing;
                    }
                    var leaf = new TreeNode(sheet.SheetNumber + "  -  " + sheet.Name)
                    {
                        Tag = sheet,
                        Checked = _checkedSheetIds.Contains(sheet.Id.Value)
                    };
                    parent.Nodes.Add(leaf);
                    if (_activeSheetId != ElementId.InvalidElementId &&
                        sheet.Id.Value == _activeSheetId.Value)
                    {
                        leaf.NodeFont = new Font(_sheetTree.Font, FontStyle.Bold);
                    }
                }
                UpdateFolderCheckStates(root);
                root.Expand();
                _selectedTreeNode = FindFolderNode(root, selectedPath) ?? root;
                _sheetTree.SelectedNode = _selectedTreeNode;
                _sheetTree.EndUpdate();
            }
            finally
            {
                _updatingTree = false;
            }
        }

        private void SyncTreeChecks()
        {
            _updatingTree = true;
            try
            {
                foreach (TreeNode root in _sheetTree.Nodes)
                {
                    foreach (TreeNode node in EnumerateNodes(root))
                    {
                        ViewSheet sheet = node.Tag as ViewSheet;
                        if (sheet != null)
                        {
                            node.Checked = _checkedSheetIds.Contains(sheet.Id.Value);
                        }
                    }
                    UpdateFolderCheckStates(root);
                }
            }
            finally
            {
                _updatingTree = false;
            }
        }

        private void SetTreeNodeChecked(TreeNode node, bool value)
        {
            _updatingTree = true;
            try
            {
                foreach (TreeNode current in EnumerateNodes(node))
                {
                    current.Checked = value;
                    ViewSheet sheet = current.Tag as ViewSheet;
                    if (sheet != null)
                    {
                        if (value)
                        {
                            _checkedSheetIds.Add(sheet.Id.Value);
                        }
                        else
                        {
                            _checkedSheetIds.Remove(sheet.Id.Value);
                        }
                    }
                }
                TreeNode root = node;
                while (root.Parent != null)
                {
                    root = root.Parent;
                }
                UpdateFolderCheckStates(root);
            }
            finally
            {
                _updatingTree = false;
            }
        }

        private static IEnumerable<TreeNode> EnumerateNodes(TreeNode root)
        {
            yield return root;
            foreach (TreeNode child in root.Nodes)
            {
                foreach (TreeNode descendant in EnumerateNodes(child))
                {
                    yield return descendant;
                }
            }
        }

        private static bool UpdateFolderCheckStates(TreeNode node)
        {
            if (node.Tag is ViewSheet)
            {
                return node.Checked;
            }
            bool any = false;
            bool all = node.Nodes.Count > 0;
            foreach (TreeNode child in node.Nodes)
            {
                bool childChecked = UpdateFolderCheckStates(child);
                any |= childChecked;
                all &= childChecked;
            }
            node.Checked = all && any;
            return node.Checked;
        }

        private static TreeNode FindFolderNode(TreeNode root, string path)
        {
            if (string.Equals(root.Tag as string, path, StringComparison.CurrentCultureIgnoreCase))
            {
                return root;
            }
            foreach (TreeNode child in root.Nodes)
            {
                if (!(child.Tag is string))
                {
                    continue;
                }
                TreeNode match = FindFolderNode(child, path);
                if (match != null)
                {
                    return match;
                }
            }
            return null;
        }

        private string FolderPath(ViewSheet sheet)
        {
            return string.Join("/", FolderSegments(sheet));
        }

        private IList<string> FolderSegments(ViewSheet sheet)
        {
            IList<string> browserPath;
            if (sheet != null &&
                _browserFolderPaths.TryGetValue(sheet.Id.Value, out browserPath) &&
                browserPath != null && browserPath.Count > 0)
            {
                return browserPath;
            }
            string sheetNumber = sheet == null ? string.Empty : sheet.SheetNumber;
            string number = (sheetNumber ?? string.Empty).Trim();
            string[] parts = Regex.Split(number, "[-_./\\\\]+")
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
            if (parts.Length > 1)
            {
                return parts.Take(parts.Length - 1).ToList();
            }
            Match prefix = Regex.Match(number, "^[^0-9]+", RegexOptions.CultureInvariant);
            string value = prefix.Success ? prefix.Value.Trim(' ', '-', '_', '.', '/') : string.Empty;
            return string.IsNullOrEmpty(value)
                ? new List<string> { "Other" }
                : new List<string> { value };
        }

        private SheetRenumberSort SelectedSort
        {
            get
            {
                return _sort.SelectedIndex == 1
                    ? SheetRenumberSort.Name
                    : SheetRenumberSort.Number;
            }
        }

        private static bool IsRowChecked(DataGridViewRow row)
        {
            object value = row.Cells["Use"].Value;
            return value is bool && (bool)value;
        }

        private static string FormatSequenceValue(decimal value, int padding)
        {
            long whole = Decimal.ToInt64(value);
            string digits = Math.Abs(whole).ToString(
                padding > 0 ? new string('0', padding) : "0",
                CultureInfo.InvariantCulture);
            return whole < 0 ? "-" + digits : digits;
        }

        private static ComboBox CreateComboBox()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
        }

        private static TextBox CreateTextBox()
        {
            return new TextBox { Dock = DockStyle.Fill };
        }

        private static NumericUpDown CreateNumericBox(decimal minimum, decimal maximum, decimal value)
        {
            return new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = minimum,
                Maximum = maximum,
                Value = value,
                ThousandsSeparator = true
            };
        }

        private static void AddSetting(
            TableLayoutPanel panel,
            string label,
            Control control,
            int column,
            int row)
        {
            panel.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left
            }, column, row);
            panel.Controls.Add(control, column + 1, row);
        }
    }
}
