using Autodesk.Revit.UI;

namespace TungMEP.Core
{
    internal interface IToolModule
    {
        void Register(UIControlledApplication application, string assemblyPath);
    }
}
