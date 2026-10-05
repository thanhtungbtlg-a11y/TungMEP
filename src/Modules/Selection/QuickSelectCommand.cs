using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using TextBox = System.Windows.Forms.TextBox;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.Selection
{
    [Transaction(TransactionMode.Manual)]
    public class QuickSelectCommand : IExternalCommand
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
                TaskDialog.Show("Tung MEP - Quick Select", "Open a Revit document before running Quick Select.");
                return Result.Cancelled;
            }

            using (var dialog = new QuickSelectForm(document, document.ActiveView))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                List<Element> candidates = CollectCandidates(document, document.ActiveView, dialog.EntireProject);
                HashSet<long> current = new HashSet<long>(
                    uiDocument.Selection.GetElementIds().Select(id => id.Value));
                IEnumerable<Element> matches = candidates.Where(element => Matches(element, document, dialog));
                if (dialog.Action == QuickSelectAction.FilterCurrent)
                {
                    matches = matches.Where(element => current.Contains(element.Id.Value));
                }

                var matchedIds = matches
                    .Select(element => element.Id)
                    .GroupBy(id => id.Value)
                    .Select(group => group.First())
                    .ToList();
                ICollection<ElementId> resultIds;
                if (dialog.Action == QuickSelectAction.Add)
                {
                    resultIds = uiDocument.Selection.GetElementIds()
                        .Concat(matchedIds)
                        .GroupBy(id => id.Value)
                        .Select(group => group.First())
                        .ToList();
                }
                else if (dialog.Action == QuickSelectAction.Remove)
                {
                    var removeIds = new HashSet<long>(matchedIds.Select(id => id.Value));
                    resultIds = uiDocument.Selection.GetElementIds()
                        .Where(id => !removeIds.Contains(id.Value))
                        .ToList();
                }
                else
                {
                    resultIds = matchedIds;
                }

                uiDocument.Selection.SetElementIds(resultIds);
                TaskDialog.Show(
                    "Tung MEP - Quick Select",
                    "Matched: " + matchedIds.Count + " element(s).\nCurrent selection: " + resultIds.Count + ".");
                return Result.Succeeded;
            }
        }

        internal static List<Element> CollectCandidates(Document document, View view, bool entireProject)
        {
            FilteredElementCollector collector = entireProject || view == null
                ? new FilteredElementCollector(document)
                : new FilteredElementCollector(document, view.Id);
            return collector
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(element => element != null && element.Category != null)
                .ToList();
        }

        private static bool Matches(Element element, Document document, QuickSelectForm settings)
        {
            if (element.Category == null || element.Category.Id.Value != settings.CategoryId)
            {
                return false;
            }
            if (settings.TypeId != ElementId.InvalidElementId.Value &&
                element.GetTypeId().Value != settings.TypeId)
            {
                return false;
            }
            if (string.IsNullOrEmpty(settings.ParameterName))
            {
                return true;
            }

            Parameter parameter = FindParameter(element, document, settings.ParameterName);
            string actual = GetParameterText(parameter);
            string expected = settings.ParameterValue ?? string.Empty;
            switch (settings.Condition)
            {
                case QuickSelectCondition.NotEqual:
                    return !string.Equals(actual, expected, StringComparison.CurrentCultureIgnoreCase);
                case QuickSelectCondition.Contains:
                    return actual.IndexOf(expected, StringComparison.CurrentCultureIgnoreCase) >= 0;
                case QuickSelectCondition.StartsWith:
                    return actual.StartsWith(expected, StringComparison.CurrentCultureIgnoreCase);
                case QuickSelectCondition.EndsWith:
                    return actual.EndsWith(expected, StringComparison.CurrentCultureIgnoreCase);
                case QuickSelectCondition.HasValue:
                    return parameter != null && parameter.HasValue && !string.IsNullOrWhiteSpace(actual);
                case QuickSelectCondition.IsEmpty:
                    return parameter == null || !parameter.HasValue || string.IsNullOrWhiteSpace(actual);
                default:
                    return string.Equals(actual, expected, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        internal static Parameter FindParameter(Element element, Document document, string name)
        {
            Parameter parameter = element.LookupParameter(name);
            if (parameter != null)
            {
                return parameter;
            }
            ElementId typeId = element.GetTypeId();
            Element type = typeId == ElementId.InvalidElementId ? null : document.GetElement(typeId);
            return type == null ? null : type.LookupParameter(name);
        }

        internal static string GetParameterText(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
            {
                return string.Empty;
            }
            string displayed = parameter.AsValueString();
            if (!string.IsNullOrEmpty(displayed))
            {
                return displayed;
            }
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(CultureInfo.CurrentCulture);
                case StorageType.Double:
                    return parameter.AsDouble().ToString("G", CultureInfo.CurrentCulture);
                case StorageType.ElementId:
                    return parameter.AsElementId().Value.ToString(CultureInfo.CurrentCulture);
                default:
                    return string.Empty;
            }
        }
    }

    internal enum QuickSelectAction
    {
        Replace,
        Add,
        Remove,
        FilterCurrent
    }

    internal enum QuickSelectCondition
    {
        Equal,
        NotEqual,
        Contains,
        StartsWith,
        EndsWith,
        HasValue,
        IsEmpty
    }

    internal sealed class QuickSelectForm : Form
    {
        private readonly Document _document;
        private readonly View _view;
        private readonly ComboBox _scope;
        private readonly ComboBox _category;
        private readonly ComboBox _type;
        private readonly ComboBox _parameter;
        private readonly ComboBox _condition;
        private readonly TextBox _value;
        private readonly ComboBox _action;
        private List<Element> _candidates;

        public QuickSelectForm(Document document, View view)
        {
            _document = document;
            _view = view;
            Text = "Tung MEP - Quick Select";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 330);

            AddLabel("Scope", 16, 22);
            _scope = AddCombo(160, 18, 335);
            _scope.Items.Add("Active view");
            _scope.Items.Add("Entire project");
            _scope.SelectedIndex = 0;

            AddLabel("Category", 16, 61);
            _category = AddCombo(160, 57, 335);
            AddLabel("Family / Type", 16, 100);
            _type = AddCombo(160, 96, 335);
            AddLabel("Parameter", 16, 139);
            _parameter = AddCombo(160, 135, 335);
            AddLabel("Condition", 16, 178);
            _condition = AddCombo(160, 174, 150);
            _condition.Items.AddRange(new object[]
            {
                "Equals", "Not equal", "Contains", "Starts with", "Ends with", "Has value", "Is empty"
            });
            _condition.SelectedIndex = 0;
            _value = new TextBox { Location = new Point(318, 174), Size = new Size(177, 24) };
            Controls.Add(_value);

            AddLabel("Selection action", 16, 217);
            _action = AddCombo(160, 213, 335);
            _action.Items.AddRange(new object[]
            {
                "Replace selection", "Add to selection", "Remove from selection", "Filter current selection"
            });
            _action.SelectedIndex = 0;

            var ok = new Button
            {
                Text = "Select",
                DialogResult = DialogResult.OK,
                Location = new Point(327, 285),
                Size = new Size(80, 28)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(415, 285),
                Size = new Size(80, 28)
            };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            _scope.SelectedIndexChanged += delegate { ReloadCategories(); };
            _category.SelectedIndexChanged += delegate { ReloadTypesAndParameters(); };
            _type.SelectedIndexChanged += delegate { ReloadParameters(); };
            _condition.SelectedIndexChanged += delegate { UpdateValueState(); };
            ReloadCategories();
            UpdateValueState();
        }

        public bool EntireProject { get { return _scope.SelectedIndex == 1; } }
        public long CategoryId
        {
            get
            {
                CategoryChoice item = _category.SelectedItem as CategoryChoice;
                return item == null ? long.MinValue : item.Id;
            }
        }
        public long TypeId
        {
            get
            {
                TypeChoice item = _type.SelectedItem as TypeChoice;
                return item == null ? ElementId.InvalidElementId.Value : item.Id;
            }
        }
        public string ParameterName
        {
            get
            {
                string value = _parameter.SelectedItem as string;
                return string.Equals(value, "<Any parameter>", StringComparison.Ordinal) ? string.Empty : value;
            }
        }
        public QuickSelectCondition Condition { get { return (QuickSelectCondition)_condition.SelectedIndex; } }
        public string ParameterValue { get { return _value.Text.Trim(); } }
        public QuickSelectAction Action { get { return (QuickSelectAction)_action.SelectedIndex; } }

        private void ReloadCategories()
        {
            if (_scope.SelectedIndex < 0)
            {
                return;
            }
            long previous = CategoryId;
            _candidates = QuickSelectCommand.CollectCandidates(_document, _view, EntireProject);
            List<CategoryChoice> categories = _candidates
                .Where(element => element.Category != null)
                .GroupBy(element => element.Category.Id.Value)
                .Select(group => new CategoryChoice
                {
                    Id = group.Key,
                    Name = group.First().Category.Name
                })
                .OrderBy(item => item.Name)
                .ToList();
            _category.BeginUpdate();
            _category.Items.Clear();
            foreach (CategoryChoice item in categories)
            {
                _category.Items.Add(item);
            }
            int selected = categories.FindIndex(item => item.Id == previous);
            _category.SelectedIndex = selected >= 0 ? selected : (categories.Count > 0 ? 0 : -1);
            _category.EndUpdate();
        }

        private void ReloadTypesAndParameters()
        {
            long categoryId = CategoryId;
            long previous = TypeId;
            List<Element> categoryElements = _candidates == null
                ? new List<Element>()
                : _candidates.Where(element => element.Category.Id.Value == categoryId).ToList();
            var types = categoryElements
                .Select(element => element.GetTypeId())
                .Where(id => id != ElementId.InvalidElementId)
                .GroupBy(id => id.Value)
                .Select(group => _document.GetElement(group.First()) as ElementType)
                .Where(type => type != null)
                .Select(type => new TypeChoice
                {
                    Id = type.Id.Value,
                    Name = string.IsNullOrEmpty(type.FamilyName)
                        ? type.Name
                        : type.FamilyName + ": " + type.Name
                })
                .OrderBy(item => item.Name)
                .ToList();
            _type.BeginUpdate();
            _type.Items.Clear();
            _type.Items.Add(new TypeChoice { Id = ElementId.InvalidElementId.Value, Name = "<Any type>" });
            foreach (TypeChoice item in types)
            {
                _type.Items.Add(item);
            }
            TypeChoice matching = types.FirstOrDefault(item => item.Id == previous);
            _type.SelectedItem = matching == null ? _type.Items[0] : matching;
            _type.EndUpdate();
            ReloadParameters();
        }

        private void ReloadParameters()
        {
            string previous = ParameterName;
            long categoryId = CategoryId;
            long typeId = TypeId;
            IEnumerable<Element> elements = (_candidates ?? new List<Element>())
                .Where(element => element.Category.Id.Value == categoryId);
            if (typeId != ElementId.InvalidElementId.Value)
            {
                elements = elements.Where(element => element.GetTypeId().Value == typeId);
            }
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (Element element in elements.Take(500))
            {
                AddParameterNames(element, names);
                ElementId elementTypeId = element.GetTypeId();
                Element type = elementTypeId == ElementId.InvalidElementId
                    ? null
                    : _document.GetElement(elementTypeId);
                AddParameterNames(type, names);
            }
            List<string> ordered = names.OrderBy(name => name).ToList();
            _parameter.BeginUpdate();
            _parameter.Items.Clear();
            _parameter.Items.Add("<Any parameter>");
            foreach (string name in ordered)
            {
                _parameter.Items.Add(name);
            }
            int selected = string.IsNullOrEmpty(previous)
                ? 0
                : ordered.FindIndex(name => string.Equals(name, previous, StringComparison.CurrentCultureIgnoreCase)) + 1;
            _parameter.SelectedIndex = selected > 0 ? selected : 0;
            _parameter.EndUpdate();
        }

        private static void AddParameterNames(Element element, ISet<string> names)
        {
            if (element == null)
            {
                return;
            }
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.Definition != null && !string.IsNullOrWhiteSpace(parameter.Definition.Name))
                {
                    names.Add(parameter.Definition.Name);
                }
            }
        }

        private void UpdateValueState()
        {
            bool needsValue = _condition.SelectedIndex >= 0 &&
                _condition.SelectedIndex <= (int)QuickSelectCondition.EndsWith;
            _value.Enabled = needsValue;
            if (!needsValue)
            {
                _value.Text = string.Empty;
            }
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label { AutoSize = true, Location = new Point(x, y), Text = text });
        }

        private ComboBox AddCombo(int x, int y, int width)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(x, y),
                Size = new Size(width, 24)
            };
            Controls.Add(combo);
            return combo;
        }

        private sealed class CategoryChoice
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public override string ToString() { return Name; }
        }

        private sealed class TypeChoice
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public override string ToString() { return Name; }
        }
    }
}
