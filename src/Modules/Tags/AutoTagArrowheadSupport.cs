using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.Tags
{
    internal static class AutoTagArrowheadSupport
    {
        public static List<AutoTagArrowheadOption> Collect(Document document)
        {
            return new FilteredElementCollector(document)
                .WhereElementIsElementType()
                .Cast<ElementType>()
                .Where(IsArrowheadType)
                .OrderBy(type => GetPreferenceRank(type.Name))
                .ThenBy(type => type.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(type => new AutoTagArrowheadOption(type))
                .ToList();
        }

        public static bool IsArrowheadType(ElementType type)
        {
            if (type == null)
            {
                return false;
            }
            try
            {
                return type.get_Parameter(BuiltInParameter.ARROW_TYPE) != null &&
                    type.get_Parameter(BuiltInParameter.ARROW_SIZE) != null;
            }
            catch
            {
                return false;
            }
        }

        public static FamilySymbol EnsureTagTypeWithArrowhead(
            Document document,
            FamilySymbol source,
            ElementType arrowhead)
        {
            if (document == null || source == null || !IsArrowheadType(arrowhead))
            {
                throw new InvalidOperationException("The selected Arrowhead Type is invalid.");
            }
            Parameter sourceParameter = source.get_Parameter(
                BuiltInParameter.LEADER_ARROWHEAD);
            if (sourceParameter == null || sourceParameter.StorageType != StorageType.ElementId)
            {
                throw new InvalidOperationException(
                    "This tag family does not expose a Leader Arrowhead type parameter.");
            }
            if (sourceParameter.AsElementId() == arrowhead.Id)
            {
                return source;
            }

            string baseName = source.Name + " - TungMEP " + arrowhead.Name;
            List<FamilySymbol> familySymbols = new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(symbol => symbol.Family != null && source.Family != null &&
                    symbol.Family.Id == source.Family.Id)
                .ToList();
            FamilySymbol existing = familySymbols.FirstOrDefault(symbol =>
            {
                Parameter parameter = symbol.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD);
                return string.Equals(
                        symbol.Name,
                        baseName,
                        StringComparison.CurrentCultureIgnoreCase) &&
                    parameter != null &&
                    parameter.StorageType == StorageType.ElementId &&
                    parameter.AsElementId() == arrowhead.Id;
            });
            if (existing != null)
            {
                return existing;
            }

            string uniqueName = baseName;
            int suffix = 2;
            while (familySymbols.Any(symbol => string.Equals(
                symbol.Name,
                uniqueName,
                StringComparison.CurrentCultureIgnoreCase)))
            {
                uniqueName = baseName + " " + suffix++;
            }
            FamilySymbol duplicate = source.Duplicate(uniqueName) as FamilySymbol;
            if (duplicate == null)
            {
                throw new InvalidOperationException(
                    "Could not duplicate the selected Tag Type for the arrowhead setting.");
            }
            Parameter duplicateParameter = duplicate.get_Parameter(
                BuiltInParameter.LEADER_ARROWHEAD);
            if (duplicateParameter == null || duplicateParameter.IsReadOnly ||
                !duplicateParameter.Set(arrowhead.Id))
            {
                throw new InvalidOperationException(
                    "Could not assign the selected Arrowhead Type to the duplicated Tag Type.");
            }
            return duplicate;
        }

        private static int GetPreferenceRank(string name)
        {
            string normalized = (name ?? string.Empty).ToLowerInvariant();
            if (normalized.Contains("filled") || normalized.Contains("fill"))
            {
                return 0;
            }
            if (normalized.Contains("arrow"))
            {
                return 1;
            }
            return 2;
        }
    }
}
