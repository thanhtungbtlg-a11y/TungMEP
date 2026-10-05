using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.Revit.UI;
using TungMEP.Modules.AutoSprinkler;

namespace TungMEP.Core
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                RibbonBuilder.EnsureTab(application);
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                foreach (IToolModule module in GetModules())
                {
                    module.Register(application, assemblyPath);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Tung MEP", "Cannot initialize add-in:\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static IEnumerable<IToolModule> GetModules()
        {
            yield return new AutoSprinklerModule();
            // Register future MEP modules here.
        }
    }
}
