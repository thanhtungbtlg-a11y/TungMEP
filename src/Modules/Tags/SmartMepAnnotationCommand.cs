using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WinForms = System.Windows.Forms;

namespace TungMEP.Modules.Tags
{
    [Transaction(TransactionMode.Manual)]
    public class SmartMepAnnotationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            View view = document == null ? null : document.ActiveView;
            if (!IsSupportedView(view))
            {
                TaskDialog.Show("Tung MEP - Smart MEP Annotation Pack",
                    "Open a plan, RCP, section, elevation, or detail view first.");
                return Result.Cancelled;
            }

            List<SmartMepTagTypeOption> tagTypes = CollectTagTypes(document);
            if (tagTypes.Count == 0)
            {
                TaskDialog.Show("Tung MEP - Smart MEP Annotation Pack",
                    "No Pipe, Duct, or Mechanical Equipment Tag Family Type is loaded in this project.");
                return Result.Cancelled;
            }

            SmartMepAnnotationSettings settings;
            using (var form = new SmartMepAnnotationForm(tagTypes, SmartMepAnnotationSettingsStore.Load()))
            {
                if (form.ShowDialog() != WinForms.DialogResult.OK) return Result.Cancelled;
                settings = form.Settings;
            }
            SmartMepAnnotationSettingsStore.Save(settings);

            List<Element> scope;
            try { scope = CollectScope(uiDocument, document, view, settings); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (scope.Count == 0)
            {
                TaskDialog.Show("Tung MEP - Smart MEP Annotation Pack",
                    "No supported MEP element was found in the selected scope.");
                return Result.Cancelled;
            }

            HashSet<long> alreadyTagged = settings.SkipAlreadyTagged
                ? CollectAlreadyTaggedIds(document, view)
                : new HashSet<long>();
            List<MepAnnotationCandidate> candidates = scope
                .Where(element => !alreadyTagged.Contains(element.Id.Value))
                .Select(element => CreateCandidate(element, view))
                .Where(candidate => candidate != null)
                .ToList();
            int skipped = scope.Count - candidates.Count;
            if (candidates.Count == 0)
            {
                TaskDialog.Show("Tung MEP - Smart MEP Annotation Pack",
                    "All selected elements were already tagged or did not have usable geometry.");
                return Result.Succeeded;
            }

            double tolerance = UnitUtils.ConvertToInternalUnits(
                settings.GroupToleranceMm,
                UnitTypeId.Millimeters);
            List<List<MepAnnotationCandidate>> groups = BuildGroups(
                candidates,
                settings.GroupParallelRuns,
                tolerance);
            var createdIds = new List<ElementId>();
            int failed = 0;
            string firstFailure = null;
            string arrangeIssue = null;
            var dimensionResult = new MepDocumentationDimensionResult();
            using (var transactionGroup = new TransactionGroup(document, "Tung MEP - Smart MEP Annotation Pack"))
            {
                transactionGroup.Start();
                using (var transaction = new Transaction(document, "Tung MEP - Create MEP Tags"))
                {
                    transaction.Start();
                    ActivateSelectedTypes(document, settings);
                    foreach (List<MepAnnotationCandidate> group in groups)
                    {
                        using (var subTransaction = new SubTransaction(document))
                        {
                            subTransaction.Start();
                            try
                            {
                                FamilySymbol symbol = ResolveTagType(document, settings, group[0].Kind);
                                if (symbol == null) throw new InvalidOperationException("The selected Tag Type is unavailable.");
                                MepAnnotationCandidate first = group[0];
                                Reference firstReference = new Reference(first.Element);
                                IndependentTag tag = IndependentTag.Create(
                                    document,
                                    symbol.Id,
                                    view.Id,
                                    firstReference,
                                    settings.AddLeaders,
                                    TagOrientation.Horizontal,
                                    first.Source3);
                                if (group.Count > 1)
                                {
                                    tag.AddReferences(group.Skip(1)
                                        .Select(item => new Reference(item.Element))
                                        .ToList());
                                }
                                createdIds.Add(tag.Id);
                                subTransaction.Commit();
                            }
                            catch (Exception exception)
                            {
                                if (subTransaction.GetStatus() == TransactionStatus.Started) subTransaction.RollBack();
                                failed++;
                                if (string.IsNullOrEmpty(firstFailure)) firstFailure = exception.Message;
                            }
                        }
                    }
                    transaction.Commit();
                }

                if (createdIds.Count > 0 && settings.ArrangeTwoSides)
                {
                    var arrange = AnnotationArrangeSettings.CreateDefault();
                    arrange.Mode = settings.OptimizeDocumentationLayout
                        ? AnnotationArrangeMode.DocumentationRail
                        : AnnotationArrangeMode.SmartRail;
                    arrange.Side = AnnotationArrangeSide.BothSidesBySource;
                    arrange.Alignment = AnnotationRailAlignment.NearModelEdge;
                    arrange.LeaderLayout = AnnotationLeaderLayout.OrthogonalElbow;
                    arrange.SortMode = AnnotationSortMode.NestedLeaderFan;
                    arrange.IncludeIndependentTags = true;
                    arrange.IncludeTextNotes = false;
                    arrange.PreserveLeaderEnds = true;
                    arrange.CreateMissingTagLeaders = settings.AddLeaders;
                    arrange.AvoidObstacles = settings.AvoidModel;
                    arrange.AutoCreateColumns = settings.OptimizeDocumentationLayout;
                    arrange.KeepPinnedFixed = true;
                    arrange.PreviewBeforeApply = false;
                    arrange.SpacingPaperMm = settings.ItemSpacingPaperMm;
                    arrange.RailOffsetPaperMm = settings.RailOffsetPaperMm;
                    arrange.ClearancePaperMm = settings.ClearancePaperMm;
                    arrange.OptimizeLeaderRoutes = settings.OptimizeDocumentationLayout;
                    arrange.MaximumSearchSteps = settings.OptimizeDocumentationLayout ? 14 : 4;
                    AnnotationArrangeCommand.ArrangeElements(
                        document,
                        view,
                        createdIds,
                        arrange,
                        out arrangeIssue);
                }

                if (createdIds.Count > 0 &&
                    (settings.CreateGridReferenceDimensions ||
                     settings.CreateElevationDimensions))
                {
                    dimensionResult = MepDocumentationDimensioner.Create(
                        document,
                        view,
                        groups.Select(group => group[0].Element),
                        settings);
                }

                uiDocument.RefreshActiveView();
                if (createdIds.Count == 0)
                {
                    transactionGroup.RollBack();
                }
                else if (settings.Preview)
                {
                    var preview = new TaskDialog("Tung MEP - MEP Annotation Preview")
                    {
                        MainInstruction = "Keep these MEP tags?",
                        MainContent = "Review the tag content, grouping, leader routes, reference dimensions, and documentation-rail layout. OK keeps the result; Cancel restores the model.",
                        CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                        DefaultButton = TaskDialogResult.Ok
                    };
                    if (preview.Show() == TaskDialogResult.Ok) transactionGroup.Assimilate();
                    else { transactionGroup.RollBack(); uiDocument.RefreshActiveView(); return Result.Cancelled; }
                }
                else transactionGroup.Assimilate();
            }

            var report = new StringBuilder();
            report.AppendLine("Native MEP tags created: " + createdIds.Count + ".");
            report.AppendLine("MEP elements represented: " + candidates.Count + ".");
            report.AppendLine("Parallel multi-leader groups: " + groups.Count(group => group.Count > 1) + ".");
            report.AppendLine("Already tagged/invalid elements skipped: " + skipped + ".");
            report.AppendLine("Failed tag groups: " + failed + ".");
            report.AppendLine("Grid reference dimensions created: " + dimensionResult.GridDimensionsCreated + ".");
            report.AppendLine("Elevation-to-level dimensions created: " + dimensionResult.ElevationDimensionsCreated + ".");
            report.AppendLine("Documentation dimensions skipped/failed: " +
                dimensionResult.Skipped + " / " + dimensionResult.Failed + ".");
            if (!string.IsNullOrEmpty(arrangeIssue)) report.AppendLine("Arrangement note: " + arrangeIssue);
            if (!string.IsNullOrEmpty(dimensionResult.FirstIssue)) report.AppendLine("Dimension note: " + dimensionResult.FirstIssue);
            if (!string.IsNullOrEmpty(firstFailure)) report.AppendLine("First failure: " + firstFailure);
            report.AppendLine();
            report.Append("Displayed System / DN / BOP-TOP / Slope fields are controlled by the selected native Revit Tag Family Type.");
            TaskDialog.Show("Tung MEP - Smart MEP Annotation Pack", report.ToString());
            return createdIds.Count > 0 ? Result.Succeeded : Result.Cancelled;
        }

        private static bool IsSupportedView(View view)
        {
            if (view == null || view.IsTemplate) return false;
            return view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.CeilingPlan ||
                view.ViewType == ViewType.EngineeringPlan || view.ViewType == ViewType.Section ||
                view.ViewType == ViewType.Elevation || view.ViewType == ViewType.Detail;
        }

        private static List<SmartMepTagTypeOption> CollectTagTypes(Document document)
        {
            var categories = new Dictionary<long, SmartMepTagKind>
            {
                { (long)BuiltInCategory.OST_PipeTags, SmartMepTagKind.Pipe },
                { (long)BuiltInCategory.OST_DuctTags, SmartMepTagKind.Duct },
                { (long)BuiltInCategory.OST_MechanicalEquipmentTags, SmartMepTagKind.Equipment }
            };
            var result = new List<SmartMepTagTypeOption>();
            foreach (FamilySymbol symbol in new FilteredElementCollector(document).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                SmartMepTagKind kind;
                if (symbol.Category != null && categories.TryGetValue(symbol.Category.Id.Value, out kind))
                    result.Add(new SmartMepTagTypeOption(symbol, kind));
            }
            return result;
        }

        private static List<Element> CollectScope(UIDocument uiDocument, Document document, View view, SmartMepAnnotationSettings settings)
        {
            if (settings.Scope == SmartMepAnnotationScope.ActiveView)
                return new FilteredElementCollector(document, view.Id).WhereElementIsNotElementType().Where(element => Matches(element, settings)).ToList();
            if (settings.Scope == SmartMepAnnotationScope.Rectangle)
                return uiDocument.Selection.PickElementsByRectangle(new MepElementFilter(settings), "Select MEP elements to tag").Where(element => Matches(element, settings)).ToList();
            List<Element> selected = uiDocument.Selection.GetElementIds().Select(document.GetElement).Where(element => Matches(element, settings)).ToList();
            if (selected.Count > 0) return selected;
            return uiDocument.Selection.PickObjects(ObjectType.Element, new MepElementFilter(settings), "Select MEP elements, then Finish")
                .Select(reference => document.GetElement(reference.ElementId)).Where(element => Matches(element, settings)).ToList();
        }

        private static bool Matches(Element element, SmartMepAnnotationSettings settings)
        {
            if (element == null || element.Category == null) return false;
            long category = element.Category.Id.Value;
            return (settings.IncludePipes && category == (long)BuiltInCategory.OST_PipeCurves) ||
                (settings.IncludeDucts && category == (long)BuiltInCategory.OST_DuctCurves) ||
                (settings.IncludeEquipment && category == (long)BuiltInCategory.OST_MechanicalEquipment);
        }

        private static HashSet<long> CollectAlreadyTaggedIds(Document document, View view)
        {
            var result = new HashSet<long>();
            foreach (IndependentTag tag in new FilteredElementCollector(document, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                foreach (ElementId id in tag.GetTaggedLocalElementIds()) result.Add(id.Value);
            return result;
        }

        private static MepAnnotationCandidate CreateCandidate(Element element, View view)
        {
            SmartMepTagKind kind;
            if (element.Category.Id.Value == (long)BuiltInCategory.OST_PipeCurves) kind = SmartMepTagKind.Pipe;
            else if (element.Category.Id.Value == (long)BuiltInCategory.OST_DuctCurves) kind = SmartMepTagKind.Duct;
            else kind = SmartMepTagKind.Equipment;

            XYZ source;
            double direction = -1.0;
            LocationCurve location = element.Location as LocationCurve;
            Curve curve = location == null ? null : location.Curve;
            if (curve != null)
            {
                source = curve.Evaluate(0.5, true);
                XYZ tangent = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                double x = tangent.DotProduct(view.RightDirection);
                double y = tangent.DotProduct(view.UpDirection);
                direction = Math.Atan2(y, x);
                if (direction < 0.0) direction += Math.PI;
                if (direction >= Math.PI) direction -= Math.PI;
            }
            else
            {
                BoundingBoxXYZ box = element.get_BoundingBox(view);
                if (box == null) return null;
                source = box.Transform.OfPoint((box.Min + box.Max) * 0.5);
            }
            return new MepAnnotationCandidate
            {
                Element = element,
                Kind = kind,
                Source3 = source,
                Source2 = new MepPoint2((source - view.Origin).DotProduct(view.RightDirection), (source - view.Origin).DotProduct(view.UpDirection)),
                GroupKey = BuildGroupKey(element, kind, direction)
            };
        }

        private static string BuildGroupKey(Element element, SmartMepTagKind kind, double direction)
        {
            string system = Value(element, BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
            string size = kind == SmartMepTagKind.Pipe
                ? Value(element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                : kind == SmartMepTagKind.Duct
                    ? Value(element, BuiltInParameter.RBS_DUCT_SIZE_FORMATTED_PARAM)
                    : element.GetTypeId().Value.ToString();
            string slope = kind == SmartMepTagKind.Pipe
                ? Value(element, BuiltInParameter.RBS_PIPE_SLOPE)
                : kind == SmartMepTagKind.Duct
                    ? Value(element, BuiltInParameter.RBS_DUCT_SLOPE)
                    : string.Empty;
            int angleBucket = direction < 0.0 ? -1 : (int)Math.Round(direction * 180.0 / Math.PI / 5.0);
            return kind + "|" + system + "|" + size + "|" + slope + "|" + angleBucket;
        }

        private static string Value(Element element, BuiltInParameter parameter)
        {
            Parameter value = element.get_Parameter(parameter);
            if (value == null) return string.Empty;
            string formatted = value.AsValueString();
            return string.IsNullOrEmpty(formatted) ? (value.AsString() ?? string.Empty) : formatted;
        }

        private static List<List<MepAnnotationCandidate>> BuildGroups(IList<MepAnnotationCandidate> candidates, bool groupParallel, double tolerance)
        {
            if (!groupParallel) return candidates.Select(item => new List<MepAnnotationCandidate> { item }).ToList();
            var result = new List<List<MepAnnotationCandidate>>();
            foreach (IGrouping<string, MepAnnotationCandidate> matching in candidates.GroupBy(item => item.GroupKey))
            {
                foreach (MepAnnotationCandidate item in matching.OrderBy(candidate => candidate.Source2.X).ThenBy(candidate => candidate.Source2.Y))
                {
                    List<MepAnnotationCandidate> target = result.LastOrDefault(group =>
                        group.Count < 8 && group[0].GroupKey == item.GroupKey &&
                        group.Min(existing => (existing.Source2 - item.Source2).Length) <= tolerance);
                    if (target == null) { target = new List<MepAnnotationCandidate>(); result.Add(target); }
                    target.Add(item);
                }
            }
            return result;
        }

        private static FamilySymbol ResolveTagType(Document document, SmartMepAnnotationSettings settings, SmartMepTagKind kind)
        {
            long value = kind == SmartMepTagKind.Pipe ? settings.PipeTagTypeId :
                kind == SmartMepTagKind.Duct ? settings.DuctTagTypeId : settings.EquipmentTagTypeId;
            return document.GetElement(new ElementId(value)) as FamilySymbol;
        }

        private static void ActivateSelectedTypes(Document document, SmartMepAnnotationSettings settings)
        {
            foreach (SmartMepTagKind kind in new[] { SmartMepTagKind.Pipe, SmartMepTagKind.Duct, SmartMepTagKind.Equipment })
            {
                FamilySymbol symbol = ResolveTagType(document, settings, kind);
                if (symbol != null && !symbol.IsActive) symbol.Activate();
            }
            document.Regenerate();
        }

        private sealed class MepElementFilter : ISelectionFilter
        {
            private readonly SmartMepAnnotationSettings _settings;
            public MepElementFilter(SmartMepAnnotationSettings settings) { _settings = settings; }
            public bool AllowElement(Element element) { return Matches(element, _settings); }
            public bool AllowReference(Reference reference, XYZ position) { return false; }
        }

        private sealed class MepAnnotationCandidate
        {
            public Element Element;
            public SmartMepTagKind Kind;
            public XYZ Source3;
            public MepPoint2 Source2;
            public string GroupKey;
        }

        private struct MepPoint2
        {
            public MepPoint2(double x, double y) { X = x; Y = y; }
            public double X;
            public double Y;
            public double Length { get { return Math.Sqrt(X * X + Y * Y); } }
            public static MepPoint2 operator -(MepPoint2 left, MepPoint2 right)
            {
                return new MepPoint2(left.X - right.X, left.Y - right.Y);
            }
        }
    }
}
