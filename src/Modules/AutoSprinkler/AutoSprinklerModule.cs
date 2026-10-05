using Autodesk.Revit.UI;
using TungMEP.Core;

namespace TungMEP.Modules.AutoSprinkler
{
    internal class AutoSprinklerModule : IToolModule
    {
        public void Register(UIControlledApplication application, string assemblyPath)
        {
            RibbonPanel panel = RibbonBuilder.GetOrCreatePanel(application, "Fire Protection");
            var styleButton = new PushButtonData(
                "TungMEP_SprinklerBoundaryStyle",
                "Setup\nStyles",
                assemblyPath,
                typeof(CreateBoundaryStyleCommand).FullName)
            {
                ToolTip = "Create Detail Line styles for the sprinkler boundary, main pipe, and generated branch guides."
            };

            var button = new PushButtonData(
                "TungMEP_AutoSprinkler",
                "Auto\nSprinkler",
                assemblyPath,
                typeof(AutoSprinklerCommand).FullName)
            {
                ToolTip = "Place aligned sprinkler heads and route branches from a main pipe guide."
            };

            PushButton setupStyles = panel.AddItem(styleButton) as PushButton;
            PushButton autoSprinkler = panel.AddItem(button) as PushButton;
            if (setupStyles != null)
            {
                setupStyles.LargeImage = RibbonIconFactory.CreateSetupStylesIcon();
                setupStyles.Image = RibbonIconFactory.CreateSetupStylesIcon();
            }
            if (autoSprinkler != null)
            {
                autoSprinkler.LargeImage = RibbonIconFactory.CreateAutoSprinklerIcon();
                autoSprinkler.Image = RibbonIconFactory.CreateAutoSprinklerIcon();
            }
        }
    }
}
