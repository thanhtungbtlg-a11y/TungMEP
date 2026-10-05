using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using TextBox = System.Windows.Forms.TextBox;

namespace TungMEP.Modules.Productivity
{
    [Transaction(TransactionMode.Manual)]
    public class ScheduleFromSelectionCommand : IExternalCommand
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
                TaskDialog.Show("Tung MEP - Schedule from Selection", "Open a Revit project first.");
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
            List<ElementId> categoryIds = selected
                .Where(element => element.Category != null)
                .Select(element => element.Category.Id)
                .GroupBy(id => id.Value)
                .Select(group => group.First())
                .ToList();
            if (categoryIds.Count != 1)
            {
                TaskDialog.Show(
                    "Tung MEP - Schedule from Selection",
                    "Select objects from one Revit category. A native schedule has one schedule category; create a separate schedule for each category.");
                return Result.Cancelled;
            }
            ElementId categoryId = categoryIds[0];
            if (!ViewSchedule.IsValidCategoryForSchedule(categoryId))
            {
                TaskDialog.Show(
                    "Tung MEP - Schedule from Selection",
                    "The selected category cannot create a native Revit schedule.");
                return Result.Cancelled;
            }

            List<SelectionParameterOption> commonParameters =
                SelectionParameterSupport.CollectCommonOptions(
                    document,
                    selected,
                    true);
            if (commonParameters.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Schedule from Selection",
                    "The selected objects do not share a filterable parameter with the same value.");
                return Result.Cancelled;
            }
            string categoryName = selected[0].Category == null
                ? "Objects"
                : selected[0].Category.Name;
            ScheduleFromSelectionSettings settings;
            using (var form = new ScheduleFromSelectionForm(
                commonParameters,
                "TungMEP - " + categoryName + " Schedule"))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }
                settings = form.Settings;
            }

            string scheduleName = UniqueScheduleName(document, settings.ScheduleName);
            ViewSchedule schedule = null;
            int fieldsAdded = 0;
            bool filterAdded = false;
            using (var transaction = new Transaction(
                document,
                "Tung MEP - Create Schedule from Selection"))
            {
                transaction.Start();
                try
                {
                    schedule = ViewSchedule.CreateSchedule(document, categoryId);
                    schedule.Name = scheduleName;
                    ScheduleDefinition definition = schedule.Definition;
                    definition.IsItemized = settings.ItemizeEveryInstance;
                    IList<SchedulableField> available = definition.GetSchedulableFields();
                    SchedulableField selectedField = available.FirstOrDefault(field =>
                        field.ParameterId.Value == settings.Parameter.ParameterId.Value);
                    if (selectedField == null)
                    {
                        throw new InvalidOperationException(
                            "The selected parameter is filterable but is not schedulable for this category.");
                    }

                    ScheduleField propertyField = definition.AddField(selectedField);
                    fieldsAdded++;
                    if (settings.FilterBySelectedValue &&
                        definition.CanFilterByValue(propertyField.FieldId))
                    {
                        ScheduleFilter filter = CreateScheduleFilter(
                            propertyField.FieldId,
                            settings.Parameter);
                        if (filter != null)
                        {
                            definition.AddFilter(filter);
                            filterAdded = true;
                        }
                    }

                    string[] preferredNames =
                    {
                        "Family and Type",
                        "Family",
                        "Type",
                        "System Name",
                        "System Type",
                        "Level",
                        "Size",
                        "Diameter",
                        "Count"
                    };
                    var usedParameterIds = new HashSet<long>
                    {
                        selectedField.ParameterId.Value
                    };
                    foreach (string preferredName in preferredNames)
                    {
                        SchedulableField field = available.FirstOrDefault(candidate =>
                            !usedParameterIds.Contains(candidate.ParameterId.Value) &&
                            string.Equals(
                                candidate.GetName(document),
                                preferredName,
                                StringComparison.CurrentCultureIgnoreCase));
                        if (field == null)
                        {
                            continue;
                        }
                        definition.AddField(field);
                        usedParameterIds.Add(field.ParameterId.Value);
                        fieldsAdded++;
                        if (fieldsAdded >= 8)
                        {
                            break;
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
                        "Tung MEP - Schedule from Selection",
                        "Could not create the schedule.\n\n" + exception.Message);
                    return Result.Cancelled;
                }
            }

            bool opened = false;
            try
            {
                uiDocument.ActiveView = schedule;
                opened = true;
            }
            catch
            {
            }
            TaskDialog.Show(
                "Tung MEP - Schedule from Selection",
                "Created schedule: " + scheduleName + ".\n" +
                "Category: " + categoryName + ".\n" +
                "Property: " + settings.Parameter.Name + " = " +
                settings.Parameter.DisplayValue + ".\n" +
                "Fields added: " + fieldsAdded + ".\n" +
                "Filtered to the selected value: " + (filterAdded ? "Yes" : "No") + ".\n" +
                "Schedule opened: " + (opened ? "Yes" : "No") + ".");
            return Result.Succeeded;
        }

        private static ScheduleFilter CreateScheduleFilter(
            ScheduleFieldId fieldId,
            SelectionParameterOption parameter)
        {
            if (parameter.StorageType == StorageType.String)
            {
                return new ScheduleFilter(
                    fieldId,
                    ScheduleFilterType.Equal,
                    parameter.StringValue ?? string.Empty);
            }
            if (parameter.StorageType == StorageType.Double)
            {
                return new ScheduleFilter(
                    fieldId,
                    ScheduleFilterType.Equal,
                    parameter.DoubleValue);
            }
            if (parameter.StorageType == StorageType.Integer)
            {
                return new ScheduleFilter(
                    fieldId,
                    ScheduleFilterType.Equal,
                    parameter.IntegerValue);
            }
            if (parameter.StorageType == StorageType.ElementId)
            {
                return new ScheduleFilter(
                    fieldId,
                    ScheduleFilterType.Equal,
                    parameter.ElementIdValue);
            }
            return null;
        }

        private static List<Element> CollectSelection(
            UIDocument uiDocument,
            Document document)
        {
            List<Element> selected = uiDocument.Selection.GetElementIds()
                .Select(document.GetElement)
                .Where(element => element != null && element.Category != null &&
                    !element.ViewSpecific)
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
            if (selected.Count > 0)
            {
                return selected;
            }
            return uiDocument.Selection.PickObjects(
                    ObjectType.Element,
                    new ScheduleModelElementSelectionFilter(),
                    "Select objects from one category that share the property value for the new schedule")
                .Select(reference => document.GetElement(reference.ElementId))
                .Where(element => element != null)
                .GroupBy(element => element.Id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static string UniqueScheduleName(Document document, string requested)
        {
            string baseName = string.IsNullOrWhiteSpace(requested)
                ? "TungMEP Schedule"
                : requested.Trim();
            var existing = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(ViewSchedule))
                    .Cast<ViewSchedule>()
                    .Select(view => view.Name),
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

        private sealed class ScheduleModelElementSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element != null && element.Category != null &&
                    !element.ViewSpecific;
            }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }
    }

    internal sealed class ScheduleFromSelectionSettings
    {
        public SelectionParameterOption Parameter { get; set; }
        public string ScheduleName { get; set; }
        public bool FilterBySelectedValue { get; set; }
        public bool ItemizeEveryInstance { get; set; }
    }

    internal sealed class ScheduleFromSelectionForm : Form
    {
        private readonly ComboBox _parameter;
        private readonly TextBox _name;
        private readonly CheckBox _filter;
        private readonly CheckBox _itemize;
        private readonly Label _value;
        private readonly Button _ok;

        public ScheduleFromSelectionForm(
            IList<SelectionParameterOption> parameters,
            string defaultName)
        {
            Text = "Tung MEP - Schedule from Selection";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(720, 285);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 24), Text = "Shared property" });
            _parameter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(165, 20), Size = new Size(525, 24) };
            foreach (SelectionParameterOption parameter in parameters)
            {
                _parameter.Items.Add(parameter);
            }
            _parameter.SelectedIndex = 0;
            _parameter.SelectedIndexChanged += delegate { UpdateValue(); };
            Controls.Add(_parameter);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 68), Text = "Shared value" });
            _value = new Label { AutoSize = false, BorderStyle = BorderStyle.FixedSingle, Location = new Point(165, 62), Size = new Size(525, 31), Padding = new Padding(6), AutoEllipsis = true };
            Controls.Add(_value);
            Controls.Add(new Label { AutoSize = true, Location = new Point(16, 112), Text = "Schedule name" });
            _name = new TextBox { Location = new Point(165, 108), Size = new Size(525, 24), Text = defaultName ?? string.Empty };
            _name.TextChanged += delegate { ValidateForm(); };
            Controls.Add(_name);
            _filter = new CheckBox { AutoSize = true, Location = new Point(20, 154), Text = "Filter schedule to this shared property value", Checked = true };
            _itemize = new CheckBox { AutoSize = true, Location = new Point(365, 154), Text = "Itemize every instance", Checked = true };
            Controls.Add(_filter);
            Controls.Add(_itemize);
            Controls.Add(new Label
            {
                AutoSize = false,
                Location = new Point(16, 188),
                Size = new Size(674, 38),
                Text = "Only parameters with the same value on every selected object are listed. The native schedule remains fully editable after creation."
            });
            _ok = new Button { Text = "Create", DialogResult = DialogResult.OK, Location = new Point(522, 240), Size = new Size(80, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(610, 240), Size = new Size(80, 28) };
            Controls.Add(_ok);
            Controls.Add(cancel);
            AcceptButton = _ok;
            CancelButton = cancel;
            UpdateValue();
        }

        public ScheduleFromSelectionSettings Settings
        {
            get
            {
                return new ScheduleFromSelectionSettings
                {
                    Parameter = _parameter.SelectedItem as SelectionParameterOption,
                    ScheduleName = _name.Text,
                    FilterBySelectedValue = _filter.Checked,
                    ItemizeEveryInstance = _itemize.Checked
                };
            }
        }

        private void UpdateValue()
        {
            SelectionParameterOption option =
                _parameter.SelectedItem as SelectionParameterOption;
            _value.Text = option == null ? string.Empty : option.DisplayValue;
            ValidateForm();
        }

        private void ValidateForm()
        {
            if (_ok != null)
            {
                _ok.Enabled = _parameter.SelectedItem != null &&
                    !string.IsNullOrWhiteSpace(_name.Text);
            }
        }
    }
}
