using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WinForms = System.Windows.Forms;

namespace TungMEP.Modules.Views
{
    [Transaction(TransactionMode.Manual)]
    public class ViewSheetAutomationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            if (document == null) return Result.Cancelled;

            List<Level> levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().OrderBy(level => level.Elevation).ToList();
            List<ViewFamilyType> familyTypes = new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
            List<View> templates = new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Where(view => view.IsTemplate).ToList();
            List<Element> zones = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToList();
            List<FamilySymbol> titleBlocks = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().OfType<FamilySymbol>().ToList();
            if (levels.Count == 0)
            {
                TaskDialog.Show("Tung MEP - View & Sheet Automation", "This project does not contain any Levels.");
                return Result.Cancelled;
            }

            ViewSheetAutomationSettings settings;
            IList<ElementId> selectedLevelIds;
            using (var form = new ViewSheetAutomationForm(
                levels,
                familyTypes.Where(type => type.ViewFamily == ViewFamily.FloorPlan),
                familyTypes.Where(type => type.ViewFamily == ViewFamily.CeilingPlan),
                familyTypes.Where(type => type.ViewFamily == ViewFamily.Section),
                templates.Where(view => view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.EngineeringPlan),
                templates.Where(view => view.ViewType == ViewType.CeilingPlan),
                templates.Where(view => view.ViewType == ViewType.Section),
                zones,
                titleBlocks,
                new FilteredElementCollector(document)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Select(view => view.Name),
                new FilteredElementCollector(document)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Select(sheet => sheet.SheetNumber),
                ViewSheetAutomationSettingsStore.Load()))
            {
                if (form.ShowDialog() != WinForms.DialogResult.OK) return Result.Cancelled;
                settings = form.Settings;
                selectedLevelIds = form.SelectedLevelIds;
            }
            ViewSheetAutomationSettingsStore.Save(settings);
            List<Level> selectedLevels = selectedLevelIds.Select(document.GetElement).OfType<Level>().OrderBy(level => level.Elevation).ToList();
            Element zone = settings.ScopeBoxId < 0 ? null : document.GetElement(new ElementId(settings.ScopeBoxId));
            BoundingBoxXYZ zoneBounds = zone == null ? null : zone.get_BoundingBox(null);
            string zoneName = zone == null ? "Project" : zone.Name;
            var created = new List<CreatedView>();
            var sheets = new List<ViewSheet>();
            int failed = 0;
            string firstFailure = null;

            using (var group = new TransactionGroup(document, "Tung MEP - View & Sheet Automation"))
            {
                group.Start();
                using (var transaction = new Transaction(document, "Tung MEP - Create Views and Sheets"))
                {
                    transaction.Start();
                    int index = 1;
                    foreach (Level level in selectedLevels)
                    {
                        if (settings.CreatePlans)
                            TryCreate(document, delegate { return CreatePlan(document, level, settings.PlanTypeId, settings.PlanTemplateId, settings, zone, zoneName, "PLAN", index++); }, level, "PLAN", created, ref failed, ref firstFailure);
                        if (settings.CreateRcps)
                            TryCreate(document, delegate { return CreatePlan(document, level, settings.RcpTypeId, settings.RcpTemplateId, settings, zone, zoneName, "RCP", index++); }, level, "RCP", created, ref failed, ref firstFailure);
                        if (settings.CreateSectionX)
                            TryCreate(document, delegate { return CreateSection(document, level, NextLevelElevation(levels, level), settings, zoneBounds, zoneName, XYZ.BasisX, "SECTION X", index++); }, level, "SECTION X", created, ref failed, ref firstFailure);
                        if (settings.CreateSectionY)
                            TryCreate(document, delegate { return CreateSection(document, level, NextLevelElevation(levels, level), settings, zoneBounds, zoneName, XYZ.BasisY, "SECTION Y", index++); }, level, "SECTION Y", created, ref failed, ref firstFailure);
                    }

                    if (settings.CreateSheets && created.Count > 0)
                    {
                        FamilySymbol titleBlock = document.GetElement(new ElementId(settings.TitleBlockTypeId)) as FamilySymbol;
                        if (titleBlock == null) throw new InvalidOperationException("The selected Title Block Type is unavailable.");
                        if (!titleBlock.IsActive) { titleBlock.Activate(); document.Regenerate(); }
                        int sheetSequence = settings.SheetNumberStart;
                        for (int start = 0; start < created.Count; start += settings.ViewsPerSheet)
                        {
                            List<CreatedView> batch = created.Skip(start).Take(settings.ViewsPerSheet).ToList();
                            ViewSheet sheet = ViewSheet.Create(document, titleBlock.Id);
                            sheet.SheetNumber = NextSheetNumber(document, settings.SheetNumberPrefix, ref sheetSequence, settings.SheetNumberPadding, sheet.Id);
                            sheet.Name = MakeSheetName(settings.SheetNamePattern, batch, zoneName);
                            document.Regenerate();
                            PlaceViewports(document, sheet, batch.Select(item => item.View).ToList());
                            sheets.Add(sheet);
                        }
                    }
                    transaction.Commit();
                }
                group.Assimilate();
            }

            var report = new StringBuilder();
            report.AppendLine("Views created: " + created.Count + ".");
            report.AppendLine("Sheets created: " + sheets.Count + ".");
            report.AppendLine("Viewports placed and evenly distributed: " + (settings.CreateSheets ? created.Count : 0) + ".");
            report.AppendLine("Failed outputs: " + failed + ".");
            report.AppendLine("Zone: " + zoneName + ".");
            if (!string.IsNullOrEmpty(firstFailure)) report.AppendLine("First failure: " + firstFailure);
            TaskDialog.Show("Tung MEP - View & Sheet Automation", report.ToString());
            return created.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }

        private static View CreatePlan(Document document, Level level, long typeId, long templateId, ViewSheetAutomationSettings settings, Element zone, string zoneName, string outputType, int index)
        {
            ViewPlan view = ViewPlan.Create(document, new ElementId(typeId), level.Id);
            ConfigureView(document, view, templateId, settings.ViewScale);
            ApplyScopeBox(view, zone);
            view.Name = UniqueViewName(document, Expand(settings.ViewNamePattern, level.Name, outputType, zoneName, index));
            return view;
        }

        private static View CreateSection(Document document, Level level, double topElevation, ViewSheetAutomationSettings settings, BoundingBoxXYZ zoneBox, string zoneName, XYZ viewDirection, string outputType, int index)
        {
            if (zoneBox == null) throw new InvalidOperationException("The selected Scope Box has no usable 3D bounds.");
            WorldBounds bounds = ToWorldBounds(zoneBox);
            double padding = UnitUtils.ConvertToInternalUnits(settings.SectionPaddingMm, UnitTypeId.Millimeters);
            double bottom = level.Elevation;
            double top = Math.Max(topElevation, bottom + UnitUtils.ConvertToInternalUnits(1000.0, UnitTypeId.Millimeters));
            XYZ center = new XYZ((bounds.Min.X + bounds.Max.X) * 0.5, (bounds.Min.Y + bounds.Max.Y) * 0.5, bottom);
            XYZ up = XYZ.BasisZ;
            XYZ direction = viewDirection.Normalize();
            XYZ right = direction.CrossProduct(up).Normalize();
            double horizontal = Math.Abs(direction.X) > 0.5 ? bounds.Max.Y - bounds.Min.Y : bounds.Max.X - bounds.Min.X;
            double depth = Math.Abs(direction.X) > 0.5 ? bounds.Max.X - bounds.Min.X : bounds.Max.Y - bounds.Min.Y;
            var sectionBox = new BoundingBoxXYZ
            {
                Transform = new Transform(Transform.Identity)
                {
                    Origin = center,
                    BasisX = right,
                    BasisY = up,
                    BasisZ = direction
                },
                Min = new XYZ(-horizontal * 0.5 - padding, -padding, -depth * 0.5 - padding),
                Max = new XYZ(horizontal * 0.5 + padding, top - bottom + padding, depth * 0.5 + padding)
            };
            ViewSection view = ViewSection.CreateSection(document, new ElementId(settings.SectionTypeId), sectionBox);
            ConfigureView(document, view, settings.SectionTemplateId, settings.ViewScale);
            view.Name = UniqueViewName(document, Expand(settings.ViewNamePattern, level.Name, outputType, zoneName, index));
            return view;
        }

        private static void ConfigureView(Document document, View view, long templateId, int scale)
        {
            try { view.Scale = scale; } catch { }
            if (templateId >= 0)
            {
                View template = document.GetElement(new ElementId(templateId)) as View;
                if (template != null && template.IsTemplate) view.ViewTemplateId = template.Id;
            }
        }

        private static void ApplyScopeBox(View view, Element zone)
        {
            if (zone == null) return;
            Parameter parameter = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            if (parameter != null && !parameter.IsReadOnly)
            {
                try { parameter.Set(zone.Id); } catch { }
            }
        }

        private static void TryCreate(Document document, Func<View> creator, Level level, string type, IList<CreatedView> target, ref int failed, ref string firstFailure)
        {
            using (var subTransaction = new SubTransaction(document))
            {
                subTransaction.Start();
                try
                {
                    View view = creator();
                    subTransaction.Commit();
                    target.Add(new CreatedView { View = view, Level = level, OutputType = type });
                }
                catch (Exception exception)
                {
                    if (subTransaction.GetStatus() == TransactionStatus.Started) subTransaction.RollBack();
                    failed++;
                    if (string.IsNullOrEmpty(firstFailure)) firstFailure = type + " / " + level.Name + ": " + exception.Message;
                }
            }
        }

        private static double NextLevelElevation(IList<Level> allLevels, Level current)
        {
            Level next = allLevels.Where(level => level.Elevation > current.Elevation + 1e-6).OrderBy(level => level.Elevation).FirstOrDefault();
            return next == null ? current.Elevation + UnitUtils.ConvertToInternalUnits(4000.0, UnitTypeId.Millimeters) : next.Elevation;
        }

        private static void PlaceViewports(Document document, ViewSheet sheet, IList<View> views)
        {
            XYZ min; XYZ max; GetSheetBounds(document, sheet, out min, out max);
            double width = max.X - min.X; double height = max.Y - min.Y;
            List<XYZ> positions = GridPositions(min, max, views.Count);
            for (int i = 0; i < views.Count; i++)
            {
                View view = views[i];
                if (!Viewport.CanAddViewToSheet(document, sheet.Id, view.Id)) continue;
                Viewport.Create(document, sheet.Id, view.Id, positions[i]);
            }
        }

        private static List<XYZ> GridPositions(XYZ min, XYZ max, int count)
        {
            double cx = (min.X + max.X) * 0.5; double cy = (min.Y + max.Y) * 0.5;
            double dx = (max.X - min.X) * 0.23; double dy = (max.Y - min.Y) * 0.23;
            if (count <= 1) return new List<XYZ> { new XYZ(cx, cy, 0) };
            if (count == 2) return new List<XYZ> { new XYZ(cx - dx, cy, 0), new XYZ(cx + dx, cy, 0) };
            if (count == 3) return new List<XYZ> { new XYZ(cx - dx, cy + dy, 0), new XYZ(cx + dx, cy + dy, 0), new XYZ(cx, cy - dy, 0) };
            return new List<XYZ> { new XYZ(cx - dx, cy + dy, 0), new XYZ(cx + dx, cy + dy, 0), new XYZ(cx - dx, cy - dy, 0), new XYZ(cx + dx, cy - dy, 0) };
        }

        private static void GetSheetBounds(Document document, ViewSheet sheet, out XYZ min, out XYZ max)
        {
            FamilyInstance titleBlock = new FilteredElementCollector(document, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();
            BoundingBoxXYZ box = titleBlock == null ? null : titleBlock.get_BoundingBox(sheet);
            if (box != null) { min = box.Min; max = box.Max; return; }
            min = new XYZ(0, 0, 0); max = new XYZ(2.5, 1.8, 0);
        }

        private static string NextSheetNumber(Document document, string prefix, ref int sequence, int padding, ElementId newSheetId)
        {
            var existing = new HashSet<string>(new FilteredElementCollector(document).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(sheet => sheet.Id != newSheetId).Select(sheet => sheet.SheetNumber), StringComparer.OrdinalIgnoreCase);
            string candidate;
            do { candidate = (prefix ?? string.Empty) + sequence.ToString(new string('0', padding)); sequence++; } while (existing.Contains(candidate));
            return candidate;
        }

        private static string MakeSheetName(string pattern, IList<CreatedView> views, string zone)
        {
            string first = views.Count == 0 ? "MEP Views" : views[0].View.Name;
            string level = views.Count == 0 || views[0].Level == null ? string.Empty : views[0].Level.Name;
            return (pattern ?? "{FirstView}").Replace("{FirstView}", first).Replace("{Level}", level).Replace("{Zone}", zone).Trim();
        }

        private static string Expand(string pattern, string level, string type, string zone, int index)
        {
            string name = (pattern ?? "{Level} - {Type} - {Zone}").Replace("{Level}", level).Replace("{Type}", type).Replace("{Zone}", zone).Replace("{Index}", index.ToString("000")).Trim();
            return name.Trim(' ', '-');
        }

        private static string UniqueViewName(Document document, string desired)
        {
            var names = new HashSet<string>(new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>().Select(view => view.Name), StringComparer.OrdinalIgnoreCase);
            string baseName = string.IsNullOrWhiteSpace(desired) ? "TungMEP View" : desired;
            string candidate = baseName; int suffix = 2;
            while (names.Contains(candidate)) candidate = baseName + " (" + suffix++ + ")";
            return candidate;
        }

        private static WorldBounds ToWorldBounds(BoundingBoxXYZ box)
        {
            Transform transform = box.Transform ?? Transform.Identity;
            var points = new List<XYZ>();
            for (int x = 0; x <= 1; x++) for (int y = 0; y <= 1; y++) for (int z = 0; z <= 1; z++)
                points.Add(transform.OfPoint(new XYZ(x == 0 ? box.Min.X : box.Max.X, y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z)));
            return new WorldBounds
            {
                Min = new XYZ(points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z)),
                Max = new XYZ(points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z))
            };
        }

        private sealed class CreatedView { public View View; public Level Level; public string OutputType; }
        private sealed class WorldBounds { public XYZ Min; public XYZ Max; }
    }
}
