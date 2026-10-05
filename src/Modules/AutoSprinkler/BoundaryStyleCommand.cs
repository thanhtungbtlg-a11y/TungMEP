using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TungMEP.Modules.AutoSprinkler
{
    internal static class SprinklerLineStyleService
    {
        public const string MainStyleName = "TungMEP_SprinklerMain";
        public const string MainConnectionStyleName = "TungMEP_SprinklerMainConnection";
        public const string BranchStyleName = "TungMEP_SprinklerBranch";
        public const string DetourStyleName = "TungMEP_SprinklerBranchDetour";
        public const string ObstacleStyleName = "TungMEP_SprinklerObstacle";

        public static void EnsureAll(Document doc)
        {
            BoundaryStyleService.Ensure(doc);
            Ensure(doc, MainStyleName, new Color(0, 190, 0), 5, false);
            Ensure(doc, MainConnectionStyleName, new Color(0, 190, 0), 4, false);
            Ensure(doc, BranchStyleName, new Color(0, 120, 255), 3, false);
            Ensure(doc, DetourStyleName, new Color(255, 40, 40), 4, false);
            Ensure(doc, ObstacleStyleName, new Color(255, 190, 0), 6, true);
        }

        public static GraphicsStyle Find(Document doc, string styleName)
        {
            Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            foreach (Category subcategory in lines.SubCategories)
            {
                if (subcategory.Name.Equals(styleName, StringComparison.OrdinalIgnoreCase))
                {
                    return subcategory.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
            }
            return null;
        }

        private static GraphicsStyle Ensure(
            Document doc,
            string styleName,
            Color color,
            int lineWeight,
            bool dashed)
        {
            GraphicsStyle existing = Find(doc, styleName);
            if (existing != null)
            {
                using (var transaction = new Transaction(doc, "Tung MEP - Update " + styleName))
                {
                    transaction.Start();
                    ApplyAppearance(doc, existing.GraphicsStyleCategory, color, lineWeight, dashed);
                    transaction.Commit();
                }
                return Find(doc, styleName);
            }

            using (var transaction = new Transaction(doc, "Tung MEP - Create " + styleName))
            {
                transaction.Start();
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                Category category = doc.Settings.Categories.NewSubcategory(lines, styleName);
                ApplyAppearance(doc, category, color, lineWeight, dashed);
                transaction.Commit();
            }
            return Find(doc, styleName);
        }

        private static void ApplyAppearance(
            Document doc,
            Category category,
            Color color,
            int lineWeight,
            bool dashed)
        {
            category.LineColor = color;
            category.SetLineWeight(lineWeight, GraphicsStyleType.Projection);
            if (!dashed)
            {
                return;
            }
            LinePatternElement pattern = new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement))
                .Cast<LinePatternElement>()
                .FirstOrDefault(item =>
                    item.Name.IndexOf("Dash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Name.IndexOf("Hidden", StringComparison.OrdinalIgnoreCase) >= 0);
            if (pattern != null)
            {
                category.SetLinePatternId(pattern.Id, GraphicsStyleType.Projection);
            }
        }
    }

    internal static class BoundaryStyleService
    {
        public const string StyleName = "TungMEP_SprinklerBoundary";

        public static GraphicsStyle Ensure(Document doc)
        {
            GraphicsStyle existing = Find(doc);
            if (existing != null)
            {
                return existing;
            }

            using (var transaction = new Transaction(doc, "Tung MEP - Create sprinkler boundary style"))
            {
                transaction.Start();
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                Category boundary = doc.Settings.Categories.NewSubcategory(lines, StyleName);
                boundary.LineColor = new Color(255, 0, 255);
                boundary.SetLineWeight(5, GraphicsStyleType.Projection);

                LinePatternElement dashed = new FilteredElementCollector(doc)
                    .OfClass(typeof(LinePatternElement))
                    .Cast<LinePatternElement>()
                    .FirstOrDefault(pattern => pattern.Name.IndexOf("Dash", StringComparison.OrdinalIgnoreCase) >= 0);
                if (dashed != null)
                {
                    boundary.SetLinePatternId(dashed.Id, GraphicsStyleType.Projection);
                }

                transaction.Commit();
            }

            return Find(doc);
        }

        public static GraphicsStyle Find(Document doc)
        {
            Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            foreach (Category subcategory in lines.SubCategories)
            {
                if (subcategory.Name.Equals(StyleName, StringComparison.OrdinalIgnoreCase))
                {
                    return subcategory.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
            }

            return null;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class CreateBoundaryStyleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            SprinklerLineStyleService.EnsureAll(doc);
            GraphicsStyle boundaryStyle = BoundaryStyleService.Find(doc);
            GraphicsStyle mainStyle = SprinklerLineStyleService.Find(doc, SprinklerLineStyleService.MainStyleName);
            if (boundaryStyle == null || mainStyle == null)
            {
                message = "Could not create the sprinkler line styles.";
                return Result.Failed;
            }

            TaskDialog.Show(
                "Tung MEP - Sprinkler Styles",
                "Sprinkler line styles are ready.\n\n" +
                "1. Draw closed boundaries with DL and " + BoundaryStyleService.StyleName + ".\n" +
                "2. Draw one straight main pipe guide with DL and " + SprinklerLineStyleService.MainStyleName + ".\n" +
                "3. Optional: draw obstacle outlines with " + SprinklerLineStyleService.ObstacleStyleName +
                    ". Only this line style is treated as an obstacle; model/link columns and beams are ignored.\n" +
                "4. Run Auto Sprinkler. Blue and red branch guides are generated automatically.");
            return Result.Succeeded;
        }
    }
}
