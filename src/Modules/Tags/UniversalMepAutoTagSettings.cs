using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.Tags
{
    internal sealed class UniversalTagCategoryDefinition
    {
        private UniversalTagCategoryDefinition(
            string key,
            string displayName,
            BuiltInCategory modelCategory,
            BuiltInCategory tagCategory,
            int order)
        {
            Key = key;
            DisplayName = displayName;
            ModelCategory = modelCategory;
            TagCategory = tagCategory;
            Order = order;
        }

        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public BuiltInCategory ModelCategory { get; private set; }
        public BuiltInCategory TagCategory { get; private set; }
        public int Order { get; private set; }
        public long ModelCategoryId { get { return (long)ModelCategory; } }
        public long TagCategoryId { get { return (long)TagCategory; } }

        public static readonly IList<UniversalTagCategoryDefinition> All =
            new List<UniversalTagCategoryDefinition>
            {
                new UniversalTagCategoryDefinition(
                    "plumbing-fixtures", "Plumbing Fixtures",
                    BuiltInCategory.OST_PlumbingFixtures,
                    BuiltInCategory.OST_PlumbingFixtureTags, 10),
                new UniversalTagCategoryDefinition(
                    "pipe-accessories", "Pipe Accessories",
                    BuiltInCategory.OST_PipeAccessory,
                    BuiltInCategory.OST_PipeAccessoryTags, 20),
                new UniversalTagCategoryDefinition(
                    "mechanical-equipment", "Mechanical Equipment",
                    BuiltInCategory.OST_MechanicalEquipment,
                    BuiltInCategory.OST_MechanicalEquipmentTags, 30),
                new UniversalTagCategoryDefinition(
                    "air-terminals", "Air Terminals",
                    BuiltInCategory.OST_DuctTerminal,
                    BuiltInCategory.OST_DuctTerminalTags, 40),
                new UniversalTagCategoryDefinition(
                    "duct-accessories", "Duct Accessories",
                    BuiltInCategory.OST_DuctAccessory,
                    BuiltInCategory.OST_DuctAccessoryTags, 50),
                new UniversalTagCategoryDefinition(
                    "sprinklers", "Sprinklers",
                    BuiltInCategory.OST_Sprinklers,
                    BuiltInCategory.OST_SprinklerTags, 60),
                new UniversalTagCategoryDefinition(
                    "electrical-equipment", "Electrical Equipment",
                    BuiltInCategory.OST_ElectricalEquipment,
                    BuiltInCategory.OST_ElectricalEquipmentTags, 70),
                new UniversalTagCategoryDefinition(
                    "electrical-fixtures", "Electrical Fixtures",
                    BuiltInCategory.OST_ElectricalFixtures,
                    BuiltInCategory.OST_ElectricalFixtureTags, 80),
                new UniversalTagCategoryDefinition(
                    "lighting-fixtures", "Lighting Fixtures",
                    BuiltInCategory.OST_LightingFixtures,
                    BuiltInCategory.OST_LightingFixtureTags, 90),
                new UniversalTagCategoryDefinition(
                    "fire-alarm-devices", "Fire Alarm Devices",
                    BuiltInCategory.OST_FireAlarmDevices,
                    BuiltInCategory.OST_FireAlarmDeviceTags, 100),
                new UniversalTagCategoryDefinition(
                    "specialty-equipment", "Specialty Equipment",
                    BuiltInCategory.OST_SpecialityEquipment,
                    BuiltInCategory.OST_SpecialityEquipmentTags, 110),
                new UniversalTagCategoryDefinition(
                    "generic-models", "Generic Models",
                    BuiltInCategory.OST_GenericModel,
                    BuiltInCategory.OST_GenericModelTags, 120)
            }.OrderBy(item => item.Order).ToList();

        public static UniversalTagCategoryDefinition Find(string key)
        {
            return All.FirstOrDefault(item => string.Equals(
                item.Key,
                key,
                StringComparison.OrdinalIgnoreCase));
        }

        public static UniversalTagCategoryDefinition FromModelCategory(Element element)
        {
            if (element == null || element.Category == null)
            {
                return null;
            }
            long categoryId = element.Category.Id.Value;
            return All.FirstOrDefault(item => item.ModelCategoryId == categoryId);
        }

        public static UniversalTagCategoryDefinition FromTagSymbol(FamilySymbol symbol)
        {
            if (symbol == null || symbol.Category == null)
            {
                return null;
            }
            long categoryId = symbol.Category.Id.Value;
            return All.FirstOrDefault(item => item.TagCategoryId == categoryId);
        }
    }

    internal sealed class UniversalTagCategorySelection
    {
        public string CategoryKey { get; set; }
        public bool Enabled { get; set; }
        public long TagTypeId { get; set; }
        public string TagTypeName { get; set; }

        public UniversalTagCategorySelection Clone()
        {
            return new UniversalTagCategorySelection
            {
                CategoryKey = CategoryKey ?? string.Empty,
                Enabled = Enabled,
                TagTypeId = TagTypeId,
                TagTypeName = TagTypeName ?? string.Empty
            };
        }
    }

    internal sealed class UniversalMepAutoTagSettings
    {
        public AutoTagScope Scope { get; set; }
        public AutoTagSidePreference SidePreference { get; set; }
        public long SelectedArrowheadTypeId { get; set; }
        public string SelectedArrowheadTypeName { get; set; }
        public bool AddLeader { get; set; }
        public bool SkipAlreadyTagged { get; set; }
        public bool PreviewBeforeApply { get; set; }
        public bool OptimizeDocumentationLayout { get; set; }
        public double OffsetPaperMm { get; set; }
        public double TagGapPaperMm { get; set; }
        public double ClearancePaperMm { get; set; }
        public int MaximumLaneSteps { get; set; }
        public List<UniversalTagCategorySelection> Categories { get; set; }

        public static UniversalMepAutoTagSettings CreateDefault()
        {
            return new UniversalMepAutoTagSettings
            {
                Scope = AutoTagScope.SelectionOrPick,
                SidePreference = AutoTagSidePreference.Auto,
                SelectedArrowheadTypeId = ElementId.InvalidElementId.Value,
                SelectedArrowheadTypeName = string.Empty,
                AddLeader = true,
                SkipAlreadyTagged = true,
                PreviewBeforeApply = true,
                OptimizeDocumentationLayout = true,
                OffsetPaperMm = 12.0,
                TagGapPaperMm = 4.0,
                ClearancePaperMm = 2.0,
                MaximumLaneSteps = 8,
                Categories = UniversalTagCategoryDefinition.All.Select(item =>
                    new UniversalTagCategorySelection
                    {
                        CategoryKey = item.Key,
                        Enabled = item.Key == "plumbing-fixtures",
                        TagTypeId = ElementId.InvalidElementId.Value,
                        TagTypeName = string.Empty
                    }).ToList()
            };
        }

        public UniversalMepAutoTagSettings Clone()
        {
            return new UniversalMepAutoTagSettings
            {
                Scope = Scope,
                SidePreference = SidePreference,
                SelectedArrowheadTypeId = SelectedArrowheadTypeId,
                SelectedArrowheadTypeName = SelectedArrowheadTypeName ?? string.Empty,
                AddLeader = AddLeader,
                SkipAlreadyTagged = SkipAlreadyTagged,
                PreviewBeforeApply = PreviewBeforeApply,
                OptimizeDocumentationLayout = OptimizeDocumentationLayout,
                OffsetPaperMm = OffsetPaperMm,
                TagGapPaperMm = TagGapPaperMm,
                ClearancePaperMm = ClearancePaperMm,
                MaximumLaneSteps = MaximumLaneSteps,
                Categories = (Categories ?? new List<UniversalTagCategorySelection>())
                    .Where(item => item != null)
                    .Select(item => item.Clone())
                    .ToList()
            };
        }

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(AutoTagScope), Scope))
            {
                Scope = AutoTagScope.SelectionOrPick;
            }
            if (!Enum.IsDefined(typeof(AutoTagSidePreference), SidePreference))
            {
                SidePreference = AutoTagSidePreference.Auto;
            }
            SelectedArrowheadTypeName = SelectedArrowheadTypeName ?? string.Empty;
            OffsetPaperMm = ClampFinite(OffsetPaperMm, 1.0, 200.0, 12.0);
            TagGapPaperMm = ClampFinite(TagGapPaperMm, 0.5, 100.0, 4.0);
            ClearancePaperMm = ClampFinite(ClearancePaperMm, 0.0, 100.0, 2.0);
            MaximumLaneSteps = Math.Max(1, Math.Min(30, MaximumLaneSteps));

            var existing = (Categories ?? new List<UniversalTagCategorySelection>())
                .Where(item => item != null && UniversalTagCategoryDefinition.Find(item.CategoryKey) != null)
                .GroupBy(item => item.CategoryKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            Categories = new List<UniversalTagCategorySelection>();
            foreach (UniversalTagCategoryDefinition definition in UniversalTagCategoryDefinition.All)
            {
                UniversalTagCategorySelection selection;
                if (!existing.TryGetValue(definition.Key, out selection))
                {
                    selection = new UniversalTagCategorySelection
                    {
                        CategoryKey = definition.Key,
                        Enabled = false,
                        TagTypeId = ElementId.InvalidElementId.Value,
                        TagTypeName = string.Empty
                    };
                }
                selection.CategoryKey = definition.Key;
                selection.TagTypeName = selection.TagTypeName ?? string.Empty;
                Categories.Add(selection);
            }
        }

        public IList<UniversalTagCategorySelection> EnabledCategories
        {
            get
            {
                return (Categories ?? new List<UniversalTagCategorySelection>())
                    .Where(item => item != null && item.Enabled)
                    .ToList();
            }
        }

        public UniversalTagCategorySelection FindCategory(string key)
        {
            return (Categories ?? new List<UniversalTagCategorySelection>())
                .FirstOrDefault(item => item != null && string.Equals(
                    item.CategoryKey,
                    key,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static double ClampFinite(
            double value,
            double minimum,
            double maximum,
            double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return fallback;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class UniversalAutoTagTypeOption
    {
        public UniversalAutoTagTypeOption(
            FamilySymbol symbol,
            UniversalTagCategoryDefinition category)
        {
            IdValue = symbol == null ? ElementId.InvalidElementId.Value : symbol.Id.Value;
            CategoryKey = category == null ? string.Empty : category.Key;
            FamilyName = symbol == null || symbol.Family == null
                ? string.Empty
                : symbol.Family.Name;
            TypeName = symbol == null ? string.Empty : symbol.Name;
        }

        public long IdValue { get; private set; }
        public string CategoryKey { get; private set; }
        public string FamilyName { get; private set; }
        public string TypeName { get; private set; }
        public string DisplayName
        {
            get
            {
                return string.IsNullOrEmpty(FamilyName)
                    ? TypeName
                    : FamilyName + " : " + TypeName;
            }
        }
        public override string ToString() { return DisplayName; }
    }
}
