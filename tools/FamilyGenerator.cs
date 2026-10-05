using System;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace TungMEP.Tools
{
    public class FamilyGeneratorApplication : IExternalApplication
    {
        private const string TemplatePath = @"C:\ProgramData\Autodesk\RVT 2024\Family Templates\English\Metric Generic Model.rft";
        private const string OutputPath = @"C:\Users\NITRO 5\Downloads\tung_mep\content\TungMEP Test Sprinkler.rfa";
        private const string ReportPath = @"C:\Users\NITRO 5\Downloads\tung_mep\content\TungMEP Test Sprinkler.txt";

        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized;
            return Result.Succeeded;
        }

        private static void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs args)
        {
            Application app = sender as Application;
            Document familyDoc = null;
            try
            {
                familyDoc = app.NewFamilyDocument(TemplatePath);
                using (var transaction = new Transaction(familyDoc, "Create Tung MEP test sprinkler"))
                {
                    transaction.Start();
                    familyDoc.OwnerFamily.FamilyCategory = familyDoc.Settings.Categories.get_Item(BuiltInCategory.OST_Sprinklers);

                    double symbolRadius = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
                    double diagonal = symbolRadius / Math.Sqrt(2.0);
                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    SketchPlane sketchPlane = SketchPlane.Create(familyDoc, plane);

                    familyDoc.FamilyCreate.NewSymbolicCurve(
                        Arc.Create(XYZ.Zero, symbolRadius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY),
                        sketchPlane);
                    familyDoc.FamilyCreate.NewSymbolicCurve(
                        Arc.Create(XYZ.Zero, symbolRadius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY),
                        sketchPlane);
                    familyDoc.FamilyCreate.NewSymbolicCurve(
                        Line.CreateBound(new XYZ(-diagonal, -diagonal, 0), new XYZ(diagonal, diagonal, 0)),
                        sketchPlane);
                    familyDoc.FamilyCreate.NewSymbolicCurve(
                        Line.CreateBound(new XYZ(-diagonal, diagonal, 0), new XYZ(diagonal, -diagonal, 0)),
                        sketchPlane);

                    transaction.Commit();
                }

                var saveOptions = new SaveAsOptions { OverwriteExistingFile = true };
                familyDoc.SaveAs(OutputPath, saveOptions);
                string report =
                    "Category: " + familyDoc.OwnerFamily.FamilyCategory.Name + Environment.NewLine +
                    "Placement type: " + familyDoc.OwnerFamily.FamilyPlacementType + Environment.NewLine +
                    "Symbol: 200 mm circle with diagonal cross" + Environment.NewLine +
                    "Output: " + OutputPath;
                File.WriteAllText(ReportPath, report);
            }
            catch (Exception ex)
            {
                File.WriteAllText(ReportPath, "GENERATOR ERROR" + Environment.NewLine + ex);
            }
            finally
            {
                if (familyDoc != null)
                {
                    familyDoc.Close(false);
                }

                try
                {
                    var uiApplication = new UIApplication(app);
                    RevitCommandId exitCommand = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
                    if (uiApplication.CanPostCommand(exitCommand))
                    {
                        uiApplication.PostCommand(exitCommand);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
