using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.Productivity
{
    internal enum SelectionParameterOperator
    {
        Equals,
        NotEquals,
        Contains,
        BeginsWith,
        GreaterThan,
        LessThan
    }

    internal sealed class SelectionParameterOption
    {
        public ElementId ParameterId { get; set; }
        public string Name { get; set; }
        public StorageType StorageType { get; set; }
        public string StringValue { get; set; }
        public double DoubleValue { get; set; }
        public int IntegerValue { get; set; }
        public ElementId ElementIdValue { get; set; }
        public string DisplayValue { get; set; }
        public bool IsTypeParameter { get; set; }

        public override string ToString()
        {
            return Name + (IsTypeParameter ? " [Type]" : " [Instance]") +
                " = " + DisplayValue;
        }
    }

    internal static class SelectionParameterSupport
    {
        public static List<SelectionParameterOption> CollectCommonOptions(
            Document document,
            IList<Element> elements,
            bool requireSameValue)
        {
            if (document == null || elements == null || elements.Count == 0)
            {
                return new List<SelectionParameterOption>();
            }
            List<ElementId> categories = elements
                .Where(element => element != null && element.Category != null)
                .Select(element => element.Category.Id)
                .GroupBy(id => id.Value)
                .Select(group => group.First())
                .ToList();
            categories = ParameterFilterUtilities.RemoveUnfilterableCategories(categories)
                .ToList();
            if (categories.Count == 0)
            {
                return new List<SelectionParameterOption>();
            }

            ICollection<ElementId> filterable =
                ParameterFilterUtilities.GetFilterableParametersInCommon(
                    document,
                    categories);
            var result = new List<SelectionParameterOption>();
            foreach (ElementId parameterId in filterable)
            {
                bool isType;
                Parameter parameter = FindParameter(
                    document,
                    elements[0],
                    parameterId,
                    out isType);
                if (parameter == null || !parameter.HasValue ||
                    parameter.StorageType == StorageType.None)
                {
                    continue;
                }
                SelectionParameterOption option = CreateOption(
                    document,
                    parameter,
                    parameterId,
                    isType);
                if (option == null)
                {
                    continue;
                }
                if (requireSameValue && elements.Skip(1).Any(element =>
                {
                    bool unused;
                    Parameter other = FindParameter(
                        document,
                        element,
                        parameterId,
                        out unused);
                    return other == null || !SameValue(option, other);
                }))
                {
                    continue;
                }
                result.Add(option);
            }
            return result
                .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(option => option.IsTypeParameter ? 1 : 0)
                .ToList();
        }

        public static FilterRule CreateRule(
            SelectionParameterOption option,
            SelectionParameterOperator comparison)
        {
            if (option == null)
            {
                return null;
            }
            ElementId parameterId = option.ParameterId;
            if (option.StorageType == StorageType.String)
            {
                string value = option.StringValue ?? string.Empty;
                if (comparison == SelectionParameterOperator.NotEquals)
                {
                    return ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, value);
                }
                if (comparison == SelectionParameterOperator.Contains)
                {
                    return ParameterFilterRuleFactory.CreateContainsRule(parameterId, value);
                }
                if (comparison == SelectionParameterOperator.BeginsWith)
                {
                    return ParameterFilterRuleFactory.CreateBeginsWithRule(parameterId, value);
                }
                return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, value);
            }
            if (option.StorageType == StorageType.Double)
            {
                double tolerance = 1e-6;
                if (comparison == SelectionParameterOperator.NotEquals)
                {
                    return ParameterFilterRuleFactory.CreateNotEqualsRule(
                        parameterId,
                        option.DoubleValue,
                        tolerance);
                }
                if (comparison == SelectionParameterOperator.GreaterThan)
                {
                    return ParameterFilterRuleFactory.CreateGreaterRule(
                        parameterId,
                        option.DoubleValue,
                        tolerance);
                }
                if (comparison == SelectionParameterOperator.LessThan)
                {
                    return ParameterFilterRuleFactory.CreateLessRule(
                        parameterId,
                        option.DoubleValue,
                        tolerance);
                }
                return ParameterFilterRuleFactory.CreateEqualsRule(
                    parameterId,
                    option.DoubleValue,
                    tolerance);
            }
            if (option.StorageType == StorageType.Integer)
            {
                if (comparison == SelectionParameterOperator.NotEquals)
                {
                    return ParameterFilterRuleFactory.CreateNotEqualsRule(
                        parameterId,
                        option.IntegerValue);
                }
                if (comparison == SelectionParameterOperator.GreaterThan)
                {
                    return ParameterFilterRuleFactory.CreateGreaterRule(
                        parameterId,
                        option.IntegerValue);
                }
                if (comparison == SelectionParameterOperator.LessThan)
                {
                    return ParameterFilterRuleFactory.CreateLessRule(
                        parameterId,
                        option.IntegerValue);
                }
                return ParameterFilterRuleFactory.CreateEqualsRule(
                    parameterId,
                    option.IntegerValue);
            }
            if (option.StorageType == StorageType.ElementId)
            {
                return comparison == SelectionParameterOperator.NotEquals
                    ? ParameterFilterRuleFactory.CreateNotEqualsRule(
                        parameterId,
                        option.ElementIdValue)
                    : ParameterFilterRuleFactory.CreateEqualsRule(
                        parameterId,
                        option.ElementIdValue);
            }
            return null;
        }

        public static Parameter FindParameter(
            Document document,
            Element element,
            ElementId parameterId,
            out bool isType)
        {
            isType = false;
            Parameter parameter = FindOwnParameter(element, parameterId);
            if (parameter != null)
            {
                return parameter;
            }
            ElementId typeId = element == null ? ElementId.InvalidElementId : element.GetTypeId();
            Element type = typeId == ElementId.InvalidElementId
                ? null
                : document.GetElement(typeId);
            parameter = FindOwnParameter(type, parameterId);
            isType = parameter != null;
            return parameter;
        }

        private static Parameter FindOwnParameter(Element element, ElementId parameterId)
        {
            if (element == null || parameterId == null)
            {
                return null;
            }
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter != null && parameter.Id.Value == parameterId.Value)
                {
                    return parameter;
                }
            }
            return null;
        }

        private static SelectionParameterOption CreateOption(
            Document document,
            Parameter parameter,
            ElementId parameterId,
            bool isType)
        {
            string display = parameter.AsValueString();
            var option = new SelectionParameterOption
            {
                ParameterId = parameterId,
                Name = parameter.Definition == null
                    ? "Parameter " + parameterId.Value.ToString(CultureInfo.InvariantCulture)
                    : parameter.Definition.Name,
                StorageType = parameter.StorageType,
                IsTypeParameter = isType
            };
            if (parameter.StorageType == StorageType.String)
            {
                option.StringValue = parameter.AsString() ?? string.Empty;
                option.DisplayValue = string.IsNullOrEmpty(display)
                    ? option.StringValue
                    : display;
            }
            else if (parameter.StorageType == StorageType.Double)
            {
                option.DoubleValue = parameter.AsDouble();
                option.DisplayValue = string.IsNullOrEmpty(display)
                    ? option.DoubleValue.ToString("0.###", CultureInfo.CurrentCulture)
                    : display;
            }
            else if (parameter.StorageType == StorageType.Integer)
            {
                option.IntegerValue = parameter.AsInteger();
                option.DisplayValue = string.IsNullOrEmpty(display)
                    ? option.IntegerValue.ToString(CultureInfo.CurrentCulture)
                    : display;
            }
            else if (parameter.StorageType == StorageType.ElementId)
            {
                option.ElementIdValue = parameter.AsElementId();
                Element valueElement = option.ElementIdValue == ElementId.InvalidElementId
                    ? null
                    : document.GetElement(option.ElementIdValue);
                option.DisplayValue = valueElement == null
                    ? (string.IsNullOrEmpty(display)
                        ? option.ElementIdValue.Value.ToString(CultureInfo.CurrentCulture)
                        : display)
                    : valueElement.Name;
            }
            else
            {
                return null;
            }
            option.DisplayValue = option.DisplayValue ?? string.Empty;
            return option;
        }

        private static bool SameValue(
            SelectionParameterOption option,
            Parameter parameter)
        {
            if (parameter.StorageType != option.StorageType || !parameter.HasValue)
            {
                return false;
            }
            if (option.StorageType == StorageType.String)
            {
                return string.Equals(
                    option.StringValue ?? string.Empty,
                    parameter.AsString() ?? string.Empty,
                    StringComparison.Ordinal);
            }
            if (option.StorageType == StorageType.Double)
            {
                return Math.Abs(option.DoubleValue - parameter.AsDouble()) <= 1e-8;
            }
            if (option.StorageType == StorageType.Integer)
            {
                return option.IntegerValue == parameter.AsInteger();
            }
            return option.StorageType == StorageType.ElementId &&
                option.ElementIdValue.Value == parameter.AsElementId().Value;
        }
    }
}
