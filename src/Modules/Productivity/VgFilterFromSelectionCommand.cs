using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using TextBox = System.Windows.Forms.TextBox;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.Productivity
{
    [Transaction(TransactionMode.Manual)]
    public class VgFilterFromSelectionCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            View view = document == null ? null : document.ActiveView;
            if (document == null || view == null || document.IsFamilyDocument)
            {
                TaskDialog.Show("Tung MEP - VG Filter from Selection", "Open a Revit project view first.");
                return Result.Cancelled;
            }

            List<Element> selected;
            try
            {
                selected = CollectSelection(uiDocument, document);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            if (selected.Count == 0)
            {
                return Result.Cancelled;
            }
            List<ElementId> categories = ParameterFilterUtilities.RemoveUnfilterableCategories(
                    selected.Where(element => element.Category != null)
                        .Select(element => element.Category.Id)
                        .GroupBy(id => id.Value)
                        .Select(group => group.First())
                        .ToList())
                .ToList();
            if (categories.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - VG Filter from Selection",
                    "The selected categories cannot be used by a Revit Parameter Filter.");
                return Result.Cancelled;
            }
            selected = selected.Where(element => element.Category != null &&
                categories.Any(category => category.Value == element.Category.Id.Value)).ToList();
            List<SelectionParameterOption> parameters =
                SelectionParameterSupport.CollectCommonOptions(
                    document,
                    selected,
                    false);
            if (parameters.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - VG Filter from Selection",
                    "No filterable parameter with a value is common to the selected categories.");
                return Result.Cancelled;
            }

            VgFilterSettings settings;
            using (var form = new VgFilterFromSelectionForm(
                parameters,
                BuildDefaultName(selected[0], parameters[0])))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                settings = form.Settings;
            }

            FilterRule rule = SelectionParameterSupport.CreateRule(
                settings.Parameter,
                settings.Comparison);
            if (rule == null)
            {
                TaskDialog.Show(
                    "Tung MEP - VG Filter from Selection",
                    "The selected parameter/operator combination cannot create a Revit filter rule.");
                return Result.Cancelled;
            }

            string filterName = UniqueFilterName(document, settings.FilterName);
            ParameterFilterElement filter = null;
            bool applied = false;
            string applyIssue = null;
            using (var transaction = new Transaction(
                document,
                "Tung MEP - Create VG Parameter Filter"))
            {
                transaction.Start();
                try
                {
                    var elementFilter = new ElementParameterFilter(rule);
                    filter = ParameterFilterElement.Create(
                        document,
                        filterName,
                        categories,
                        elementFilter);
                    if (settings.ApplyToActiveView)
                    {
                        try
                        {
                            if (!view.IsFilterApplied(filter.Id))
                            {
                                view.AddFilter(filter.Id);
                            }
                            view.SetFilterVisibility(filter.Id, settings.Visible);
                            if (settings.Halftone)
                            {
                                var overrides = new OverrideGraphicSettings();
                                overrides.SetHalftone(true);
                                view.SetFilterOverrides(filter.Id, overrides);
                            }
                            applied = true;
                        }
                        catch (Exception exception)
                        {
                            applyIssue = exception.Message;
                        }
                    }
                    transaction.Commit();
                }
                catch (Exception exception)
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        transaction.RollBack();
                    }
                    TaskDialog.Show(
                        "Tung MEP - VG Filter from Selection",
                        "Could not create the Parameter Filter.\n\n" + exception.Message);
                    return Result.Cancelled;
                }
            }

            string report = "Created Parameter Filter: " + filterName + ".\n" +
                "Categories: " + categories.Count + ".\n" +
                "Rule: " + settings.Parameter.Name + " " +
                OperatorText(settings.Comparison) + " " +
                settings.Parameter.DisplayValue + ".\n" +
                "Applied to active view: " + (applied ? "Yes" : "No") + ".";
            if (!string.IsNullOrEmpty(applyIssue))
            {
                report += "\n\nThe filter was created, but Revit could not apply it to this view: " + applyIssue;
            }
            TaskDialog.Show("Tung MEP - VG Filter from Selection", report);
            return Result.Succeeded;
        }

        private static List<Element> CollectSelection(
            UIDocument uiDocument,
            Document document)
        {
            List<Element> selected = uiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .Where(element => element != null && element.Category != null &&
                    element.ViewSpecific == false)
                .ToList();
            if (selected.Count > 0)
            {
                return selected;
            }
            return uiDocument.Selection.PickObjects(
                    ObjectType.Element,
                    new FilterableModelElementSelectionFilter(),
                    "Select one or more model objects whose categories and parameter value define the VG Filter")
                .Select(reference => document.GetElement(reference.ElementId))
                .Where(element => element != null)
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static string BuildDefaultName(
            Element element,
            SelectionParameterOption parameter)
        {
            string category = element.Category == null ? "Objects" : element.Category.Name;
            return Sanitize("TungMEP - " + category + " - " + parameter.Name + " - " +
                parameter.DisplayValue, 120);
        }

        private static string UniqueFilterName(Document document, string requested)
        {
            string baseName = string.IsNullOrWhiteSpace(requested)
                ? "TungMEP Parameter Filter"
                : requested.Trim();
            var existing = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>()
                    .Select(filter => filter.Name),
                StringComparer.CurrentCultureIgnoreCase);
            if (!existing.Contains(baseName))
            {
                return baseName;
            }
            for (int index = 2; index < 10000; index++)
            {
                string candidate = baseName + " (" + index + ")";
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }
            return baseName + " - " + DateTime.Now.ToString("yyyyMMddHHmmss");
        }

        private static string Sanitize(string value, int maximumLength)
        {
            string result = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return result.Length <= maximumLength
                ? result
                : result.Substring(0, maximumLength).Trim();
        }

        private static string OperatorText(SelectionParameterOperator comparison)
        {
            return comparison == SelectionParameterOperator.NotEquals ? "does not equal" :
                comparison == SelectionParameterOperator.Contains ? "contains" :
                comparison == SelectionParameterOperator.BeginsWith ? "begins with" :
                comparison == SelectionParameterOperator.GreaterThan ? "is greater than" :
                comparison == SelectionParameterOperator.LessThan ? "is less than" : "equals";
        }

        private sealed class FilterableModelElementSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element != null && element.Category != null && !element.ViewSpecific;
            }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }
    }

    internal sealed class VgFilterSettings
    {
        public SelectionParameterOption Parameter { get; set; }
        public SelectionParameterOperator Comparison { get; set; }
        public string FilterName { get; set; }
        public bool ApplyToActiveView { get; set; }
        public bool Visible { get; set; }
        public bool Halftone { get; set; }
    }

    internal sealed class VgFilterFromSelectionForm : Form
    {
        private readonly ComboBox _parameter;
        private readonly ComboBox _comparison;
        private readonly TextBox _name;
        private readonly Label _value;
        private readonly CheckBox _apply;
        private readonly CheckBox _visible;
        private readonly CheckBox _halftone;
        private readonly Button _ok;

        public VgFilterFromSelectionForm(
            IList<SelectionParameterOption> parameters,
            string defaultName)
        {
            Text = "Tung MEP - VG Filter from Selection";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(720, 330);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 24), Text = "Property / parameter" });
            _parameter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(175, 20), Size = new Size(515, 24) };
            foreach (SelectionParameterOption parameter in parameters)
            {
                _parameter.Items.Add(parameter);
            }
            _parameter.SelectedIndex = 0;
            _parameter.SelectedIndexChanged += delegate { UpdateParameter(); };
            Controls.Add(_parameter);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 68), Text = "Rule" });
            _comparison = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(175, 64), Size = new Size(220, 24) };
            Controls.Add(_comparison);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 111), Text = "Selected value" });
            _value = new Label { AutoSize = false, BorderStyle = BorderStyle.FixedSingle, Location = new Point(175, 105), Size = new Size(515, 31), Padding = new Padding(6), AutoEllipsis = true };
            Controls.Add(_value);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 157), Text = "Filter name" });
            _name = new TextBox { Location = new Point(175, 153), Size = new Size(515, 24), Text = defaultName ?? string.Empty };
            _name.TextChanged += delegate { ValidateForm(); };
            Controls.Add(_name);
            _apply = new CheckBox { AutoSize = true, Location = new Point(20, 202), Text = "Add filter to active view", Checked = true };
            _visible = new CheckBox { AutoSize = true, Location = new Point(235, 202), Text = "Elements visible", Checked = true };
            _halftone = new CheckBox { AutoSize = true, Location = new Point(420, 202), Text = "Halftone override", Checked = false };
            Controls.Add(_apply);
            Controls.Add(_visible);
            Controls.Add(_halftone);
            Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(16, 235),
                Size = new Size(674, 38),
                Text = "The rule uses the current value of the selected object's parameter. The resulting native Parameter Filter remains editable in VG > Filters."
            });
            _ok = new Button { Text = "Create", DialogResult = DialogResult.OK, Location = new Point(522, 286), Size = new Size(80, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(610, 286), Size = new Size(80, 28) };
            Controls.Add(_ok);
            Controls.Add(cancel);
            AcceptButton = _ok;
            CancelButton = cancel;
            UpdateParameter();
        }

        public VgFilterSettings Settings
        {
            get
            {
                OperatorOption comparison = _comparison.SelectedItem as OperatorOption;
                return new VgFilterSettings
                {
                    Parameter = _parameter.SelectedItem as SelectionParameterOption,
                    Comparison = comparison == null
                        ? SelectionParameterOperator.Equals
                        : comparison.Value,
                    FilterName = _name.Text,
                    ApplyToActiveView = _apply.Checked,
                    Visible = _visible.Checked,
                    Halftone = _halftone.Checked
                };
            }
        }

        private void UpdateParameter()
        {
            SelectionParameterOption parameter =
                _parameter.SelectedItem as SelectionParameterOption;
            _comparison.Items.Clear();
            AddOperator("Equals", SelectionParameterOperator.Equals);
            AddOperator("Does not equal", SelectionParameterOperator.NotEquals);
            if (parameter != null && parameter.StorageType == StorageType.String)
            {
                AddOperator("Contains", SelectionParameterOperator.Contains);
                AddOperator("Begins with", SelectionParameterOperator.BeginsWith);
            }
            else if (parameter != null &&
                (parameter.StorageType == StorageType.Double ||
                 parameter.StorageType == StorageType.Integer))
            {
                AddOperator("Greater than", SelectionParameterOperator.GreaterThan);
                AddOperator("Less than", SelectionParameterOperator.LessThan);
            }
            _comparison.SelectedIndex = 0;
            _value.Text = parameter == null ? string.Empty : parameter.DisplayValue;
            ValidateForm();
        }

        private void AddOperator(string text, SelectionParameterOperator value)
        {
            _comparison.Items.Add(new OperatorOption(text, value));
        }

        private void ValidateForm()
        {
            if (_ok != null)
            {
                _ok.Enabled = _parameter.SelectedItem != null &&
                    !string.IsNullOrWhiteSpace(_name.Text);
            }
        }

        private sealed class OperatorOption
        {
            public OperatorOption(string text, SelectionParameterOperator value)
            {
                Text = text;
                Value = value;
            }
            public string Text { get; private set; }
            public SelectionParameterOperator Value { get; private set; }
            public override string ToString() { return Text; }
        }
    }
}
