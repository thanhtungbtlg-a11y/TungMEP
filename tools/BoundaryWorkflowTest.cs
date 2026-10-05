using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace TungMEP.Tools
{
    public class BoundaryWorkflowTestApplication : IExternalApplication
    {
        private const string ModelPath = @"C:\Users\NITRO 5\Downloads\tung_mep\scan\Snowdon Towers Sample Architectural.rvt";
        private const string AddinPath = @"C:\Users\NITRO 5\Downloads\tung_mep\bin\net48\TungMEP.Addin.v5.dll";
        private const string FamilyPath = @"C:\Users\NITRO 5\Downloads\tung_mep\bin\net48\content\AutoSprinkler\TungMEP Test Sprinkler.rfa";
        private const string ReportPath = @"C:\Users\NITRO 5\Downloads\tung_mep\tools\BoundaryWorkflowTest.txt";

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
            Document doc = null;
            try
            {
                doc = app.OpenDocumentFile(ModelPath);
                ViewPlan view = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .First(candidate => !candidate.IsTemplate && candidate.GenLevel != null);
                Level level = view.GenLevel;
                var detailCurves = new List<CurveElement>();
                double mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
                double z = level.ProjectElevation;
                var vertices = new List<XYZ>
                {
                    new XYZ(0 * mm, 1200 * mm, z),
                    new XYZ(1800 * mm, 10000 * mm, z),
                    new XYZ(11800 * mm, 10000 * mm, z),
                    new XYZ(15200 * mm, 6100 * mm, z),
                    new XYZ(14100 * mm, 3300 * mm, z),
                    new XYZ(8000 * mm, 0 * mm, z),
                    new XYZ(0 * mm, 2100 * mm, z)
                };

                using (var transaction = new Transaction(doc, "Test irregular DL boundary"))
                {
                    transaction.Start();
                    Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                    Category boundary = null;
                    foreach (Category subcategory in lines.SubCategories)
                    {
                        if (subcategory.Name == "TungMEP_SprinklerBoundary")
                        {
                            boundary = subcategory;
                            break;
                        }
                    }
                    if (boundary == null)
                    {
                        boundary = doc.Settings.Categories.NewSubcategory(lines, "TungMEP_SprinklerBoundary");
                    }
                    GraphicsStyle style = boundary.GetGraphicsStyle(GraphicsStyleType.Projection);

                    for (int i = 0; i < vertices.Count; i++)
                    {
                        DetailCurve detail = doc.Create.NewDetailCurve(
                            view,
                            Line.CreateBound(vertices[i], vertices[(i + 1) % vertices.Count]));
                        detail.LineStyle = style;
                        detailCurves.Add(detail);
                    }
                    transaction.Commit();
                }

                Assembly addin = Assembly.LoadFrom(AddinPath);
                Type commandType = addin.GetType("TungMEP.Modules.AutoSprinkler.AutoSprinklerCommand", true);
                MethodInfo buildLoops = commandType.GetMethod("BuildClosedLoops", BindingFlags.NonPublic | BindingFlags.Static);
                object[] loopArgs = { detailCurves, 5 * mm, 0 };
                var loops = (IEnumerable)buildLoops.Invoke(null, loopArgs);
                var loopList = loops.Cast<IList<XYZ>>().ToList();

                Type plannerType = addin.GetType("TungMEP.Modules.AutoSprinkler.SmartCoveragePlanner", true);
                MethodInfo createPlan = plannerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
                object plan = createPlan.Invoke(null, new object[]
                {
                    loopList[0],
                    1500 * mm,
                    2000 * mm,
                    3000 * mm,
                    250 * mm,
                    new List<XYZ>()
                });
                Type planType = plan.GetType();
                var generated = (List<XYZ>)planType.GetProperty("NewCenters").GetValue(plan, null);
                double coveragePercent = (double)planType.GetProperty("CoveragePercent").GetValue(plan, null);
                double minimumSpacing = (double)planType.GetProperty("MinimumNearestSpacing").GetValue(plan, null);
                double maximumSpacing = (double)planType.GetProperty("MaximumNearestSpacing").GetValue(plan, null);

                FamilySymbol symbol;
                var placedInstances = new List<FamilyInstance>();
                using (var transaction = new Transaction(doc, "Test smart sprinkler placement"))
                {
                    transaction.Start();
                    Family family;
                    doc.LoadFamily(FamilyPath, out family);
                    symbol = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .OfCategory(BuiltInCategory.OST_Sprinklers)
                        .Cast<FamilySymbol>()
                        .First(candidate => candidate.FamilyName == "TungMEP Test Sprinkler");
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    foreach (XYZ point in generated)
                    {
                        FamilyInstance instance = doc.Create.NewFamilyInstance(
                            new XYZ(point.X, point.Y, level.ProjectElevation),
                            symbol,
                            level,
                            StructuralType.NonStructural);
                        Parameter offset = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                        if (offset != null && !offset.IsReadOnly)
                        {
                            offset.Set(0);
                        }
                        placedInstances.Add(instance);
                    }
                    transaction.Commit();
                }

                MethodInfo ensureVisibility = commandType.GetMethod("EnsureSprinklerCategoryVisible", BindingFlags.NonPublic | BindingFlags.Static);
                object[] visibilityArgs = { doc, view, false, string.Empty };
                ensureVisibility.Invoke(null, visibilityArgs);
                bool categoryVisible = !view.GetCategoryHidden(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Sprinklers).Id);
                double minimumEdgeDistance = generated.Min(point => DistanceToPolygon(point, loopList[0]));
                bool allVisible = placedInstances.All(instance => instance.get_BoundingBox(view) != null);

                File.WriteAllText(
                    ReportPath,
                    "PASS" + Environment.NewLine +
                    "View: " + view.Name + Environment.NewLine +
                    "Level: " + level.Name + Environment.NewLine +
                    "Irregular detail lines: " + detailCurves.Count + Environment.NewLine +
                    "Closed loops: " + loopList.Count + Environment.NewLine +
                    "Open chains: " + loopArgs[2] + Environment.NewLine +
                    "Optimized heads: " + generated.Count + Environment.NewLine +
                    "Sampled coverage percent: " + coveragePercent.ToString("0.00") + Environment.NewLine +
                    "Minimum nearest spacing mm: " + UnitUtils.ConvertFromInternalUnits(minimumSpacing, UnitTypeId.Millimeters).ToString("0") + Environment.NewLine +
                    "Maximum nearest spacing mm: " + UnitUtils.ConvertFromInternalUnits(maximumSpacing, UnitTypeId.Millimeters).ToString("0") + Environment.NewLine +
                    "Minimum edge distance mm: " + UnitUtils.ConvertFromInternalUnits(minimumEdgeDistance, UnitTypeId.Millimeters).ToString("0") + Environment.NewLine +
                    "Family placement type: " + symbol.Family.FamilyPlacementType + Environment.NewLine +
                    "Sprinklers category visible: " + categoryVisible + Environment.NewLine +
                    "All instances have view bounding boxes: " + allVisible);
            }
            catch (Exception ex)
            {
                File.WriteAllText(ReportPath, "FAIL" + Environment.NewLine + ex);
            }
            finally
            {
                if (doc != null)
                {
                    doc.Close(false);
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

        private static double DistanceToPolygon(XYZ point, IList<XYZ> polygon)
        {
            double minimum = double.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                XYZ start = polygon[i];
                XYZ end = polygon[(i + 1) % polygon.Count];
                double dx = end.X - start.X;
                double dy = end.Y - start.Y;
                double lengthSquared = dx * dx + dy * dy;
                double ratio = lengthSquared < 1e-9
                    ? 0
                    : ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
                ratio = Math.Max(0, Math.Min(1, ratio));
                double px = point.X - (start.X + ratio * dx);
                double py = point.Y - (start.Y + ratio * dy);
                minimum = Math.Min(minimum, Math.Sqrt(px * px + py * py));
            }
            return minimum;
        }
    }
}
