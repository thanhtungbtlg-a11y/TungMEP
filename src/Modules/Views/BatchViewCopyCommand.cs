using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Button = System.Windows.Forms.Button;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.Views
{
    [Transaction(TransactionMode.Manual)]
    public class BatchViewCopyCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            if (document == null)
            {
                return Result.Cancelled;
            }

            List<View> availableViews = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(IsEligibleSource)
                .OrderBy(view => GetViewTypeLabel(view), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (availableViews.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Batch View Copy",
                    "No duplicable view candidates were found in the current project.");
                return Result.Cancelled;
            }

            BatchViewCopySettings settings;
            using (var dialog = new BatchViewCopyForm(
                availableViews,
                document.ActiveView == null ? ElementId.InvalidElementId : document.ActiveView.Id))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                settings = dialog.GetSettings();
            }

            var existingNames = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Select(view => view.Name),
                StringComparer.OrdinalIgnoreCase);
            var result = new BatchViewCopyResult();

            using (var transaction = new Transaction(document, "Tung MEP - Batch View Copy"))
            {
                transaction.Start();
                foreach (View source in settings.SourceViews)
                {
                    ViewDuplicateOption option = settings.DuplicateOption;
                    if (source.ViewType == ViewType.DraftingView)
                    {
                        option = ViewDuplicateOption.WithDetailing;
                        result.DraftingViewsForced++;
                    }
                    else if (source is ViewSchedule)
                    {
                        option = ViewDuplicateOption.Duplicate;
                        result.SchedulesForced++;
                    }

                    if (!source.IsValidObject || !source.CanViewBeDuplicated(option))
                    {
                        result.Skipped += settings.CopiesPerView;
                        result.AddDetail(
                            "Skipped: " + SafeViewName(source) +
                            " does not support " + GetDuplicateOptionLabel(option) + ".");
                        continue;
                    }

                    for (int copyIndex = 0; copyIndex < settings.CopiesPerView; copyIndex++)
                    {
                        using (var subTransaction = new SubTransaction(document))
                        {
                            subTransaction.Start();
                            try
                            {
                                ElementId duplicateId = source.Duplicate(option);
                                View duplicate = document.GetElement(duplicateId) as View;
                                if (duplicate == null || !duplicate.IsValidObject)
                                {
                                    throw new InvalidOperationException(
                                        "Revit did not return a valid duplicated view.");
                                }

                                string requestedName = BuildRequestedName(source.Name, settings);
                                string uniqueName = MakeUniqueName(requestedName, existingNames);
                                duplicate.Name = uniqueName;
                                subTransaction.Commit();

                                existingNames.Add(uniqueName);
                                result.Created++;
                            }
                            catch (Exception ex)
                            {
                                if (subTransaction.GetStatus() == TransactionStatus.Started)
                                {
                                    subTransaction.RollBack();
                                }
                                result.Failed++;
                                result.AddDetail(
                                    "Failed: " + SafeViewName(source) +
                                    " (copy " + (copyIndex + 1) + ") - " + ex.Message);
                            }
                        }
                    }
                }
                transaction.Commit();
            }

            var report = new StringBuilder();
            report.AppendLine("Created: " + result.Created);
            report.AppendLine("Skipped: " + result.Skipped);
            report.AppendLine("Failed: " + result.Failed);
            if (result.DraftingViewsForced > 0)
            {
                report.AppendLine(
                    "Drafting views forced to Duplicate with detailing: " +
                    result.DraftingViewsForced);
            }
            if (result.SchedulesForced > 0)
            {
                report.AppendLine(
                    "Schedules forced to Duplicate: " + result.SchedulesForced);
            }
            if (result.Details.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Details:");
                foreach (string detail in result.Details.Take(15))
                {
                    report.AppendLine("- " + detail);
                }
                if (result.DetailCount > result.Details.Count)
                {
                    report.AppendLine(
                        "- ... and " + (result.DetailCount - result.Details.Count) +
                        " more issue(s).");
                }
            }
            TaskDialog.Show("Tung MEP - Batch View Copy", report.ToString().TrimEnd());
            return Result.Succeeded;
        }

        private static bool IsEligibleSource(View view)
        {
            if (view == null || view.IsTemplate || view is ViewSheet)
            {
                return false;
            }
            if (view.ViewType == ViewType.Legend ||
                view.ViewType == ViewType.Internal ||
                view.ViewType == ViewType.ProjectBrowser ||
                view.ViewType == ViewType.SystemBrowser)
            {
                return false;
            }
            try
            {
                if (view.ViewType == ViewType.DraftingView)
                {
                    return view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing);
                }
                return view.CanViewBeDuplicated(ViewDuplicateOption.Duplicate) ||
                    view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing) ||
                    view.CanViewBeDuplicated(ViewDuplicateOption.AsDependent);
            }
            catch
            {
                return false;
            }
        }

        private static string BuildRequestedName(
            string sourceName,
            BatchViewCopySettings settings)
        {
            string coreName = sourceName ?? string.Empty;
            if (!string.IsNullOrEmpty(settings.FindText))
            {
                coreName = ReplaceOrdinalIgnoreCase(
                    coreName,
                    settings.FindText,
                    settings.ReplaceText ?? string.Empty);
            }
            return (settings.Prefix ?? string.Empty) +
                coreName +
                (settings.Suffix ?? string.Empty);
        }

        private static string ReplaceOrdinalIgnoreCase(
            string input,
            string find,
            string replacement)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(find))
            {
                return input;
            }

            var builder = new StringBuilder();
            int startIndex = 0;
            while (startIndex < input.Length)
            {
                int matchIndex = input.IndexOf(
                    find,
                    startIndex,
                    StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    builder.Append(input, startIndex, input.Length - startIndex);
                    break;
                }
                builder.Append(input, startIndex, matchIndex - startIndex);
                builder.Append(replacement);
                startIndex = matchIndex + find.Length;
            }
            return builder.ToString();
        }

        private static string MakeUniqueName(
            string requestedName,
            HashSet<string> existingNames)
        {
            string baseName = string.IsNullOrWhiteSpace(requestedName)
                ? "View Copy"
                : requestedName.Trim();
            string candidate = baseName;
            int suffix = 2;
            while (existingNames.Contains(candidate))
            {
                candidate = baseName + " (" + suffix + ")";
                suffix++;
            }
            return candidate;
        }

        private static string SafeViewName(View view)
        {
            try
            {
                return view == null ? "<missing view>" : view.Name;
            }
            catch
            {
                return "<invalid view>";
            }
        }

        internal static string GetViewTypeLabel(View view)
        {
            return view == null ? string.Empty : view.ViewType.ToString();
        }

        private static string GetDuplicateOptionLabel(ViewDuplicateOption option)
        {
            switch (option)
            {
                case ViewDuplicateOption.WithDetailing:
                    return "Duplicate with detailing";
                case ViewDuplicateOption.AsDependent:
                    return "Duplicate as dependent";
                default:
                    return "Duplicate";
            }
        }

        private sealed class BatchViewCopyResult
        {
            public BatchViewCopyResult()
            {
                Details = new List<string>();
            }

            public int Created { get; set; }
            public int Skipped { get; set; }
            public int Failed { get; set; }
            public int DraftingViewsForced { get; set; }
            public int SchedulesForced { get; set; }
            public int DetailCount { get; private set; }
            public List<string> Details { get; private set; }

            public void AddDetail(string detail)
            {
                DetailCount++;
                if (Details.Count < 15)
                {
                    Details.Add(detail);
                }
            }
        }
    }

    internal sealed class BatchViewCopySettings
    {
        public IList<View> SourceViews { get; set; }
        public ViewDuplicateOption DuplicateOption { get; set; }
        public int CopiesPerView { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }
        public string FindText { get; set; }
        public string ReplaceText { get; set; }
    }

    internal sealed class BatchViewCopyForm : Form
    {
        private readonly List<ViewChoice> _allChoices;
        private readonly TextBox _searchInput;
        private readonly CheckedListBox _viewList;
        private readonly ComboBox _modeInput;
        private readonly NumericUpDown _copiesInput;
        private readonly TextBox _prefixInput;
        private readonly TextBox _suffixInput;
        private readonly TextBox _findInput;
        private readonly TextBox _replaceInput;

        public BatchViewCopyForm(IList<View> views, ElementId activeViewId)
        {
            _allChoices = views
                .Select(view => new ViewChoice(
                    view,
                    activeViewId != null && view.Id.Value == activeViewId.Value))
                .ToList();

            Text = "Tung MEP - Batch View Copy";
            Width = 780;
            Height = 610;
            MinimumSize = new Size(700, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 6
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var searchPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2
            };
            searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchPanel.Controls.Add(
                new Label { Text = "Search", AutoSize = true, Anchor = AnchorStyles.Left },
                0,
                0);
            _searchInput = new TextBox { Dock = DockStyle.Fill };
            searchPanel.Controls.Add(_searchInput, 1, 0);
            root.Controls.Add(searchPanel, 0, 0);

            _viewList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                HorizontalScrollbar = true
            };
            root.Controls.Add(_viewList, 0, 1);

            var selectionButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            var selectVisible = new Button { Text = "Select visible", AutoSize = true };
            var clearVisible = new Button { Text = "Clear visible", AutoSize = true };
            selectVisible.Click += delegate { SetVisibleChoicesChecked(true); };
            clearVisible.Click += delegate { SetVisibleChoicesChecked(false); };
            selectionButtons.Controls.Add(selectVisible);
            selectionButtons.Controls.Add(clearVisible);
            root.Controls.Add(selectionButtons, 0, 2);

            var settingsGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 4,
                RowCount = 3,
                Padding = new Padding(0, 6, 0, 0)
            };
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            _modeInput = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _modeInput.Items.Add(new DuplicateModeChoice(
                "Duplicate",
                ViewDuplicateOption.Duplicate));
            _modeInput.Items.Add(new DuplicateModeChoice(
                "With detailing",
                ViewDuplicateOption.WithDetailing));
            _modeInput.Items.Add(new DuplicateModeChoice(
                "As dependent",
                ViewDuplicateOption.AsDependent));
            _modeInput.SelectedIndex = 1;

            _copiesInput = new NumericUpDown
            {
                Dock = DockStyle.Left,
                Width = 90,
                Minimum = 1,
                Maximum = 100,
                Value = 1
            };
            _prefixInput = new TextBox { Dock = DockStyle.Fill };
            _suffixInput = new TextBox { Dock = DockStyle.Fill };
            _findInput = new TextBox { Dock = DockStyle.Fill };
            _replaceInput = new TextBox { Dock = DockStyle.Fill };

            AddSetting(settingsGrid, "Mode", _modeInput, 0, 0);
            AddSetting(settingsGrid, "Copies", _copiesInput, 2, 0);
            AddSetting(settingsGrid, "Prefix", _prefixInput, 0, 1);
            AddSetting(settingsGrid, "Suffix", _suffixInput, 2, 1);
            AddSetting(settingsGrid, "Find", _findInput, 0, 2);
            AddSetting(settingsGrid, "Replace", _replaceInput, 2, 2);
            root.Controls.Add(settingsGrid, 0, 3);

            root.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Text = "Drafting Views always use Duplicate with detailing. " +
                    "Schedules always use Duplicate. Unsupported view/mode combinations are skipped."
            }, 0, 4);

            var actionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0)
            };
            var okButton = new Button { Text = "Copy", Width = 90 };
            var cancelButton = new Button
            {
                Text = "Cancel",
                Width = 90,
                DialogResult = DialogResult.Cancel
            };
            okButton.Click += HandleAccept;
            actionPanel.Controls.Add(okButton);
            actionPanel.Controls.Add(cancelButton);
            root.Controls.Add(actionPanel, 0, 5);

            _viewList.ItemCheck += HandleItemCheck;
            _searchInput.TextChanged += delegate { RefreshVisibleChoices(); };
            Controls.Add(root);
            AcceptButton = okButton;
            CancelButton = cancelButton;
            RefreshVisibleChoices();
        }

        public BatchViewCopySettings GetSettings()
        {
            return new BatchViewCopySettings
            {
                SourceViews = _allChoices
                    .Where(choice => choice.IsChecked)
                    .Select(choice => choice.View)
                    .ToList(),
                DuplicateOption = ((DuplicateModeChoice)_modeInput.SelectedItem).Option,
                CopiesPerView = Decimal.ToInt32(_copiesInput.Value),
                Prefix = _prefixInput.Text,
                Suffix = _suffixInput.Text,
                FindText = _findInput.Text,
                ReplaceText = _replaceInput.Text
            };
        }

        private static void AddSetting(
            TableLayoutPanel panel,
            string label,
            System.Windows.Forms.Control input,
            int column,
            int row)
        {
            panel.Controls.Add(
                new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left },
                column,
                row);
            panel.Controls.Add(input, column + 1, row);
        }

        private void HandleAccept(object sender, EventArgs e)
        {
            if (!_allChoices.Any(choice => choice.IsChecked))
            {
                MessageBox.Show(
                    this,
                    "Select at least one view to copy.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void HandleItemCheck(object sender, ItemCheckEventArgs e)
        {
            ViewChoice choice = _viewList.Items[e.Index] as ViewChoice;
            if (choice != null)
            {
                choice.IsChecked = e.NewValue == CheckState.Checked;
            }
        }

        private void RefreshVisibleChoices()
        {
            string query = _searchInput.Text.Trim();
            _viewList.BeginUpdate();
            try
            {
                _viewList.Items.Clear();
                foreach (ViewChoice choice in _allChoices)
                {
                    if (!choice.Matches(query))
                    {
                        continue;
                    }
                    _viewList.Items.Add(choice, choice.IsChecked);
                }
            }
            finally
            {
                _viewList.EndUpdate();
            }
        }

        private void SetVisibleChoicesChecked(bool isChecked)
        {
            for (int index = 0; index < _viewList.Items.Count; index++)
            {
                ViewChoice choice = _viewList.Items[index] as ViewChoice;
                if (choice != null)
                {
                    choice.IsChecked = isChecked;
                    _viewList.SetItemChecked(index, isChecked);
                }
            }
        }

        private sealed class ViewChoice
        {
            private readonly string _displayText;

            public ViewChoice(View view, bool isChecked)
            {
                View = view;
                IsChecked = isChecked;
                _displayText = "[" + BatchViewCopyCommand.GetViewTypeLabel(view) + "] " + view.Name;
            }

            public View View { get; private set; }
            public bool IsChecked { get; set; }

            public bool Matches(string query)
            {
                return string.IsNullOrEmpty(query) ||
                    _displayText.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
            }

            public override string ToString()
            {
                return _displayText;
            }
        }

        private sealed class DuplicateModeChoice
        {
            private readonly string _label;

            public DuplicateModeChoice(string label, ViewDuplicateOption option)
            {
                _label = label;
                Option = option;
            }

            public ViewDuplicateOption Option { get; private set; }

            public override string ToString()
            {
                return _label;
            }
        }
    }
}
