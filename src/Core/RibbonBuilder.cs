using System;
using Autodesk.Revit.UI;

namespace TungMEP.Core
{
    internal static class RibbonBuilder
    {
        public const string TabName = "Tung MEP";

        public static void EnsureTab(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch
            {
                // Revit throws when a tab with the same name already exists.
            }
        }

        public static RibbonPanel GetOrCreatePanel(UIControlledApplication application, string panelName)
        {
            foreach (RibbonPanel panel in application.GetRibbonPanels(TabName))
            {
                if (panel.Name.Equals(panelName, StringComparison.OrdinalIgnoreCase))
                {
                    return panel;
                }
            }

            return application.CreateRibbonPanel(TabName, panelName);
        }
    }
}
