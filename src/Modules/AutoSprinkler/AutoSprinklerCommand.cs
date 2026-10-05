using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

namespace TungMEP.Modules.AutoSprinkler
{
    [Transaction(TransactionMode.Manual)]
    public class AutoSprinklerCommand : IExternalCommand
    {
        private const string DefaultFamilyFileName = "TungMEP Test Sprinkler.rfa";
        private const string DefaultFamilyName = "TungMEP Test Sprinkler";
        private const string GeneratedPipeComment = "TungMEP Auto Sprinkler";
        private const double Dn25Millimeters = 25.0;
        private const double Dn32Millimeters = 32.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("Tung MEP - Auto Sprinkler", "Run this command in a Revit project, not in the Family Editor.");
                return Result.Cancelled;
            }

            Level level = GetActiveViewLevel(doc);
            if (level == null)
            {
                TaskDialog.Show("Tung MEP - Auto Sprinkler", "Open a floor plan or ceiling plan associated with a Level.");
                return Result.Cancelled;
            }

            SprinklerLineStyleService.EnsureAll(doc);
            GraphicsStyle boundaryStyle = BoundaryStyleService.Find(doc);
            List<CurveElement> boundaryCurves = GetBoundaryCurves(doc, uiDoc, boundaryStyle);
            if (boundaryCurves.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Auto Sprinkler",
                    "No Detail Lines using " + BoundaryStyleService.StyleName + " were found in the active view.\n\n" +
                    "Use DL to draw a closed boundary and assign the dedicated Line Style in Properties.");
                return Result.Cancelled;
            }

            GraphicsStyle mainStyle = SprinklerLineStyleService.Find(doc, SprinklerLineStyleService.MainStyleName);
            CurveElement mainPipeCurve = GetMainPipeCurve(doc, uiDoc, mainStyle);
            Line mainPipeLine = mainPipeCurve == null ? null : mainPipeCurve.GeometryCurve as Line;
            if (mainPipeLine == null || !mainPipeLine.IsBound)
            {
                TaskDialog.Show(
                    "Tung MEP - Auto Sprinkler",
                    "Draw one straight Detail Line using " + SprinklerLineStyleService.MainStyleName + ".\n\n" +
                    "This green line represents the main pipe and controls the direction of every generated branch.");
                return Result.Cancelled;
            }
            XYZ mainStart = mainPipeLine.GetEndPoint(0);
            XYZ mainEnd = mainPipeLine.GetEndPoint(1);

            int openChains;
            double joinTolerance = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
            List<IList<XYZ>> polygons = BuildClosedLoops(boundaryCurves, joinTolerance, out openChains);
            if (polygons.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Auto Sprinkler",
                    "The boundary lines do not form a closed loop. Close all gaps, then run the command again.");
                return Result.Cancelled;
            }

            string boundaryError;
            List<SprinklerRegion> regions = BuildRegions(polygons, out boundaryError);
            if (regions == null)
            {
                TaskDialog.Show("Tung MEP - Auto Sprinkler", boundaryError);
                return Result.Cancelled;
            }

            List<IList<XYZ>> drawnObstacles = GetDrawnObstaclePolygons(doc, doc.ActiveView);
            int mainPipeObstacleIntersections = drawnObstacles.Count(obstacle =>
                SegmentIntersectsPolygon2D(mainStart, mainEnd, obstacle));

            string loadError;
            if (!TryEnsureDefaultSprinklerFamily(doc, out loadError))
            {
                TaskDialog.Show("Tung MEP - Auto Sprinkler", "Could not load the unhosted test sprinkler family.\n\n" + loadError);
                return Result.Cancelled;
            }

            List<FamilySymbol> symbols = GetLevelBasedSprinklerSymbols(doc);
            if (symbols.Count == 0)
            {
                TaskDialog.Show("Tung MEP - Auto Sprinkler", "No level-based Sprinklers family type was found.");
                return Result.Cancelled;
            }
            List<PipeType> pipeTypes = GetPipeTypes(doc);
            List<PipingSystemType> fireProtectionSystemTypes = GetFireProtectionSystemTypes(doc);
            if (pipeTypes.Count == 0 || fireProtectionSystemTypes.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Auto Sprinkler",
                    pipeTypes.Count == 0
                        ? "No Pipe Type was found. Load or create a DN25-capable Pipe Type first."
                        : "No Fire Protection Piping System Type was found. Create a Fire Protection system type before routing sprinkler pipes.");
                return Result.Cancelled;
            }

            LastUsedSelections lastUsed = LastUsedSelectionStore.Load();
            {
                var settings = new SprinklerSettings
                {
                    CoverageRadiusMm = lastUsed.CoverageRadiusMm ?? 1500,
                    MinimumSpacingMm = lastUsed.MinimumSpacingMm ?? 2000,
                    MaximumSpacingMm = lastUsed.MaximumSpacingMm ?? 3000,
                    BoundaryClearanceMm = lastUsed.BoundaryClearanceMm ?? 100,
                    ObstacleClearanceMm = lastUsed.ObstacleClearanceMm ?? 150,
                    HeightMm = lastUsed.HeightMm ?? 2340,
                    BranchOffsetMm = lastUsed.BranchOffsetMm ?? 150,
                    DrawBranchGuides = lastUsed.DrawBranchGuides ?? false
                };
                int wizardStep = 0;
                while (wizardStep < 4)
                {
                    if (wizardStep == 0)
                    {
                        using (var form = new SprinklerSettingsForm(settings))
                        {
                            if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                            {
                                return Result.Cancelled;
                            }
                            SprinklerSettings entered = form.Settings;
                            settings.CoverageRadiusMm = entered.CoverageRadiusMm;
                            settings.MinimumSpacingMm = entered.MinimumSpacingMm;
                            settings.MaximumSpacingMm = entered.MaximumSpacingMm;
                            settings.BoundaryClearanceMm = entered.BoundaryClearanceMm;
                            settings.ObstacleClearanceMm = entered.ObstacleClearanceMm;
                            settings.HeightMm = entered.HeightMm;
                            settings.BranchOffsetMm = entered.BranchOffsetMm;
                            settings.DrawBranchGuides = entered.DrawBranchGuides;
                        }
                        if (settings.MaximumSpacingMm < settings.MinimumSpacingMm)
                        {
                            TaskDialog.Show("Tung MEP - Auto Sprinkler", "Maximum spacing must be greater than or equal to minimum spacing.");
                            continue;
                        }
                        wizardStep = 1;
                        continue;
                    }

                    if (wizardStep == 1)
                    {
                        string rememberedFamily = settings.Symbol == null
                            ? lastUsed.SprinklerFamilyName
                            : settings.Symbol.FamilyName;
                        string rememberedType = settings.Symbol == null
                            ? lastUsed.SprinklerTypeName
                            : settings.Symbol.Name;
                        using (var typePicker = new SprinklerTypePickerForm(
                            symbols,
                            rememberedFamily,
                            rememberedType))
                        {
                            System.Windows.Forms.DialogResult choice = typePicker.ShowDialog();
                            if (choice == System.Windows.Forms.DialogResult.Retry)
                            {
                                wizardStep = 0;
                                continue;
                            }
                            if (choice != System.Windows.Forms.DialogResult.OK ||
                                typePicker.SelectedSymbol == null)
                            {
                                return Result.Cancelled;
                            }
                            settings.Symbol = typePicker.SelectedSymbol;
                        }
                        wizardStep = 2;
                        continue;
                    }

                    if (wizardStep == 2)
                    {
                        string rememberedPipeType = settings.PipeType == null
                            ? lastUsed.PipeTypeName
                            : settings.PipeType.Name;
                        using (var pipeTypePicker = new PipeTypePickerForm(pipeTypes, rememberedPipeType))
                        {
                            System.Windows.Forms.DialogResult choice = pipeTypePicker.ShowDialog();
                            if (choice == System.Windows.Forms.DialogResult.Retry)
                            {
                                wizardStep = 1;
                                continue;
                            }
                            if (choice != System.Windows.Forms.DialogResult.OK ||
                                pipeTypePicker.SelectedPipeType == null)
                            {
                                return Result.Cancelled;
                            }
                            settings.PipeType = pipeTypePicker.SelectedPipeType;
                        }
                        wizardStep = 3;
                        continue;
                    }

                    string rememberedSystemType = settings.PipingSystemType == null
                        ? lastUsed.PipingSystemTypeName
                        : settings.PipingSystemType.Name;
                    using (var systemTypePicker = new PipingSystemTypePickerForm(
                        fireProtectionSystemTypes,
                        rememberedSystemType))
                    {
                        System.Windows.Forms.DialogResult choice = systemTypePicker.ShowDialog();
                        if (choice == System.Windows.Forms.DialogResult.Retry)
                        {
                            wizardStep = 2;
                            continue;
                        }
                        if (choice != System.Windows.Forms.DialogResult.OK ||
                            systemTypePicker.SelectedSystemType == null)
                        {
                            return Result.Cancelled;
                        }
                        settings.PipingSystemType = systemTypePicker.SelectedSystemType;
                    }
                    wizardStep = 4;
                }
                LastUsedSelectionStore.Save(settings);

                double coverageRadius = UnitUtils.ConvertToInternalUnits(settings.CoverageRadiusMm, UnitTypeId.Millimeters);
                double automaticMinimumSpacingMm = Math.Min(
                    settings.MinimumSpacingMm,
                    Math.Max(500.0, Math.Floor(settings.CoverageRadiusMm * 4.0 / 3.0 / 100.0) * 100.0));
                double minimumSpacing = UnitUtils.ConvertToInternalUnits(automaticMinimumSpacingMm, UnitTypeId.Millimeters);
                double maximumSpacing = UnitUtils.ConvertToInternalUnits(settings.MaximumSpacingMm, UnitTypeId.Millimeters);
                double boundaryClearance = UnitUtils.ConvertToInternalUnits(settings.BoundaryClearanceMm, UnitTypeId.Millimeters);
                double obstacleClearance = UnitUtils.ConvertToInternalUnits(settings.ObstacleClearanceMm, UnitTypeId.Millimeters);
                double installHeight = UnitUtils.ConvertToInternalUnits(settings.HeightMm, UnitTypeId.Millimeters);
                double branchOffset = UnitUtils.ConvertToInternalUnits(settings.BranchOffsetMm, UnitTypeId.Millimeters);
                string branchPosition = string.Empty;
                double branchElevation = 0;
                bool categoryUnhidden;
                string visibilityNote;
                EnsureSprinklerCategoryVisible(doc, doc.ActiveView, out categoryUnhidden, out visibilityNote);

                int placed = 0;
                int failed = 0;
                int totalSamples = 0;
                int coveredSamples = 0;
                int branchCount = 0;
                int detourBranchCount = 0;
                int routeFailureCount = 0;
                int expectedGridIntersections = 0;
                int missingGridIntersections = 0;
                int pipesCreated = 0;
                int fittingsCreated = 0;
                int pipeFailures = 0;
                int collectorFallbacks = 0;
                double selectedGridSpacing = 0;
                double selectedColumnSpacing = 0;
                double selectedAlongBranchSpacing = 0;
                string firstFailure = null;
                var placedIds = new List<ElementId>();
                var placedInstances = new List<FamilyInstance>();
                var guideSegments = new List<BranchGuideSegment>();
                var allCenters = new List<XYZ>();

                using (var group = new TransactionGroup(doc, "Tung MEP - Smart sprinkler layout"))
                {
                    group.Start();

                    var placements = new List<SprinklerPlacement>();

                    foreach (SprinklerRegion region in regions)
                    {
                        AlignedBranchPlan plan = AlignedBranchPlanner.Create(
                            region.Outer,
                            mainStart,
                            mainEnd,
                            coverageRadius,
                            minimumSpacing,
                            maximumSpacing,
                            boundaryClearance,
                            drawnObstacles,
                            region.Holes,
                            obstacleClearance);

                        totalSamples += plan.SampleCount;
                        coveredSamples += plan.CoveredSampleCount;
                        branchCount += plan.BranchCount;
                        detourBranchCount += plan.DetourBranchCount;
                        routeFailureCount += plan.RouteFailureCount;
                        expectedGridIntersections += plan.ExpectedGridIntersectionCount;
                        missingGridIntersections += plan.MissingGridIntersectionCount;
                        selectedGridSpacing = selectedGridSpacing == 0
                            ? plan.GridSpacing
                            : Math.Min(selectedGridSpacing, plan.GridSpacing);
                        selectedColumnSpacing = selectedColumnSpacing == 0
                            ? plan.ColumnSpacing
                            : Math.Min(selectedColumnSpacing, plan.ColumnSpacing);
                        selectedAlongBranchSpacing = selectedAlongBranchSpacing == 0
                            ? plan.AlongBranchSpacing
                            : Math.Min(selectedAlongBranchSpacing, plan.AlongBranchSpacing);
                        allCenters.AddRange(plan.Centers);

                        foreach (XYZ center in plan.Centers)
                        {
                            XYZ actualPoint = new XYZ(center.X, center.Y, level.ProjectElevation + installHeight);
                            placements.Add(new SprinklerPlacement(actualPoint, level));
                        }
                    }

                    // Build the pipe routes once from all regions. This joins only
                    // collinear head columns perpendicular to the main and lets a
                    // column continue across separate sprinkler regions. Horizontal
                    // motion is introduced only where an obstacle requires a detour.
                    List<IList<XYZ>> routingObstacles = drawnObstacles
                        .Concat(regions.SelectMany(region => region.Holes))
                        .ToList();
                    guideSegments = AlignedBranchPlanner.CreateGlobalRoutes(
                        allCenters,
                        mainStart,
                        mainEnd,
                        routingObstacles,
                        obstacleClearance,
                        out branchCount,
                        out detourBranchCount,
                        out routeFailureCount);

                    if (placements.Count == 0)
                    {
                        group.Assimilate();
                        TaskDialog.Show(
                            "Tung MEP - Auto Sprinkler",
                            "No valid aligned sprinkler grid was found. Reduce minimum spacing or edge clearance.");
                        return Result.Succeeded;
                    }

                    using (var transaction = new Transaction(doc, "Tung MEP - Place optimized sprinklers"))
                    {
                        transaction.Start();
                        if (!settings.Symbol.IsActive)
                        {
                            settings.Symbol.Activate();
                            doc.Regenerate();
                        }

                        foreach (SprinklerPlacement placement in placements)
                        {
                            try
                            {
                                FamilyInstance instance = PlaceLevelBasedSprinkler(doc, settings.Symbol, placement.Point, placement.Level);
                                SetInstanceHeight(instance, installHeight);
                                placedIds.Add(instance.Id);
                                placedInstances.Add(instance);
                                placed++;
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                if (firstFailure == null)
                                {
                                    firstFailure = ex.Message;
                                }
                            }
                        }

                        if (placed > 0)
                        {
                            // Some sprinkler families update their connector only
                            // after Elevation from Level has regenerated.
                            doc.Regenerate();
                            branchElevation = GetBranchElevation(
                                settings.Symbol,
                                placedInstances,
                                level.ProjectElevation + installHeight,
                                branchOffset,
                                out branchPosition);
                            PipeRouteResult pipeResult = CreatePipeNetwork(
                                doc, level, settings.PipingSystemType, settings.PipeType, guideSegments,
                                placedInstances, branchElevation);
                            pipesCreated = pipeResult.PipesCreated;
                            fittingsCreated = pipeResult.FittingsCreated;
                            pipeFailures = pipeResult.Failures;
                            collectorFallbacks = pipeResult.CollectorFallbacks;
                            if (firstFailure == null && !string.IsNullOrEmpty(pipeResult.FirstFailure))
                            {
                                firstFailure = pipeResult.FirstFailure;
                            }
                        }

                        if (settings.DrawBranchGuides)
                        {
                            GraphicsStyle branchStyle = SprinklerLineStyleService.Find(
                                doc,
                                SprinklerLineStyleService.BranchStyleName);
                            GraphicsStyle detourStyle = SprinklerLineStyleService.Find(
                                doc,
                                SprinklerLineStyleService.DetourStyleName);
                            GraphicsStyle mainConnectionStyle = SprinklerLineStyleService.Find(
                                doc,
                                SprinklerLineStyleService.MainConnectionStyleName);
                            foreach (BranchGuideSegment guide in guideSegments)
                            {
                                try
                                {
                                    DetailCurve detail = doc.Create.NewDetailCurve(
                                        doc.ActiveView,
                                        Line.CreateBound(guide.Start, guide.End));
                                    detail.LineStyle = guide.Kind == BranchGuideKind.MainConnection
                                        ? mainConnectionStyle
                                        : (guide.IsDetour ? detourStyle : branchStyle);
                                }
                                catch (Exception ex)
                                {
                                    routeFailureCount++;
                                    if (firstFailure == null)
                                    {
                                        firstFailure = ex.Message;
                                    }
                                }
                            }
                        }
                        transaction.Commit();
                    }

                    group.Assimilate();
                }

                double coveragePercent = totalSamples == 0 ? 0 : 100.0 * coveredSamples / totalSamples;
                string gridSpacing = selectedGridSpacing <= 0
                    ? "n/a"
                    : UnitUtils.ConvertFromInternalUnits(selectedGridSpacing, UnitTypeId.Millimeters).ToString("0") + " mm";
                string directionalSpacing = selectedColumnSpacing <= 0 || selectedAlongBranchSpacing <= 0
                    ? gridSpacing
                    : UnitUtils.ConvertFromInternalUnits(selectedColumnSpacing, UnitTypeId.Millimeters).ToString("0") +
                      " x " + UnitUtils.ConvertFromInternalUnits(selectedAlongBranchSpacing, UnitTypeId.Millimeters).ToString("0") +
                      " mm (columns x heads along branch)";
                string result =
                    "Placed: " + placed +
                    "\nFailed: " + failed +
                    "\nSampled coverage: " + coveragePercent.ToString("0.00") + "%" +
                    "\nCoverage samples: " + coveredSamples + " / " + totalSamples +
                    "\nAligned grid spacing: " + directionalSpacing +
                    "\nStructured grid intersections: " +
                        Math.Max(0, expectedGridIntersections - missingGridIntersections) +
                        " / " + expectedGridIntersections +
                    "\nGenerated branches: " + branchCount +
                    "\nBranches with automatic detours: " + detourBranchCount +
                    "\nRoute segments not generated: " + routeFailureCount +
                    "\nDN25/DN32 branch pipes created: " + pipesCreated +
                    "\nDN32 segments falling back to DN25: " + collectorFallbacks +
                    "\nPipe fittings created: " + fittingsCreated +
                    "\nPipe segments/fittings failed: " + pipeFailures +
                    "\nBranch elevation: " + branchPosition + " (" + settings.BranchOffsetMm.ToString("0") + " mm from sprinkler)" +
                    "\nPreferred spacing: " + settings.MinimumSpacingMm.ToString("0") + " - " + settings.MaximumSpacingMm.ToString("0") + " mm" +
                    "\nAutomatic coverage floor: " + automaticMinimumSpacingMm.ToString("0") + " mm" +
                    "\nCoverage radius: " + settings.CoverageRadiusMm.ToString("0") + " mm" +
                    "\nMinimum edge clearance: " + settings.BoundaryClearanceMm.ToString("0") + " mm" +
                    "\nDrawn obstacle-line regions: " + drawnObstacles.Count +
                    "\nObstacle-line clearance: " + settings.ObstacleClearanceMm.ToString("0") + " mm" +
                    "\nDrawn obstacles crossing the main pipe guide: " + mainPipeObstacleIntersections +
                    "\nSprinkler regions: " + regions.Count +
                    "\nBoundary voids excluded: " + regions.Sum(region => region.Holes.Count) +
                    "\nOpen boundary chains ignored: " + openChains;
                if (categoryUnhidden)
                {
                    result += "\nSprinklers category was enabled in the active view.";
                }
                if (!string.IsNullOrEmpty(visibilityNote))
                {
                    result += "\n\nVisibility note: " + visibilityNote;
                }
                if (firstFailure != null)
                {
                    result += "\n\nFirst failure: " + firstFailure;
                }
                if (coveragePercent < 99.999)
                {
                    result += "\n\nCoverage warning: the structured grid within the selected " +
                        automaticMinimumSpacingMm.ToString("0") + " - " +
                        settings.MaximumSpacingMm.ToString("0") +
                        " mm spacing range could not keep every sampled boundary/interior point within the " +
                        settings.CoverageRadiusMm.ToString("0") +
                        " mm coverage radius. Required row/column intersections were retained where a complete repair was possible.";
                }
                else if (selectedGridSpacing > 0 &&
                    UnitUtils.ConvertFromInternalUnits(selectedGridSpacing, UnitTypeId.Millimeters) + 0.5 < settings.MinimumSpacingMm)
                {
                    result += "\n\nSpacing adjustment: an additional head row was required for full coverage, " +
                        "so spacing was automatically reduced below the preferred minimum.";
                }
                if (missingGridIntersections > 0)
                {
                    result += "\n\nGrid warning: " + missingGridIntersections +
                        " intended grid intersection(s) could not be moved clear of obstacles without " +
                        "leaving the boundary or exceeding the maximum spacing.";
                }
                if (mainPipeObstacleIntersections > 0)
                {
                    result += "\n\nWarning: the green main pipe guide itself crosses a user-drawn obstacle region. " +
                        "Branch takeoffs were shifted where possible, but the main pipe guide should also be moved or rerouted.";
                }

                uiDoc.Selection.SetElementIds(placedIds);
                TaskDialog.Show("Tung MEP - Auto Sprinkler", result);
                return placed > 0 ? Result.Succeeded : Result.Failed;
            }
        }

        private static void EnsureSprinklerCategoryVisible(
            Document doc,
            View view,
            out bool categoryUnhidden,
            out string visibilityNote)
        {
            categoryUnhidden = false;
            visibilityNote = string.Empty;

            using (var transaction = new Transaction(doc, "Tung MEP - Prepare sprinkler visibility"))
            {
                transaction.Start();
                try
                {
                    Category category = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Sprinklers);
                    View template = view.ViewTemplateId == ElementId.InvalidElementId
                        ? null
                        : doc.GetElement(view.ViewTemplateId) as View;

                    if (template != null)
                    {
                        if (template.CanCategoryBeHidden(category.Id) && template.GetCategoryHidden(category.Id))
                        {
                            template.SetCategoryHidden(category.Id, false);
                            categoryUnhidden = true;
                        }
                    }
                    else if (view.CanCategoryBeHidden(category.Id) && view.GetCategoryHidden(category.Id))
                    {
                        view.SetCategoryHidden(category.Id, false);
                        categoryUnhidden = true;
                    }
                }
                catch (Exception ex)
                {
                    visibilityNote = "Could not change Sprinklers visibility in the active view or its View Template: " + ex.Message;
                }
                transaction.Commit();
            }
        }

        private static List<FamilySymbol> GetLevelBasedSprinklerSymbols(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Sprinklers)
                .Cast<FamilySymbol>()
                .Where(symbol => symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                .OrderBy(symbol => symbol.FamilyName.Equals(DefaultFamilyName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(symbol => symbol.FamilyName)
                .ThenBy(symbol => symbol.Name)
                .ToList();
        }

        private static List<PipeType> GetPipeTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .OrderBy(type => type.Name)
                .ToList();
        }

        private static List<PipingSystemType> GetFireProtectionSystemTypes(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .Where(IsFireProtectionSystemType)
                .OrderBy(type => type.Name.IndexOf("Wet", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenBy(type => type.Name)
                .ToList();
        }

        private static bool IsFireProtectionSystemType(PipingSystemType type)
        {
            return type != null &&
                (type.SystemClassification.ToString().IndexOf("FireProtect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 type.Name.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 type.Name.IndexOf("Sprinkler", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static PipeRouteResult CreatePipeNetwork(
            Document doc,
            Level level,
            PipingSystemType systemType,
            PipeType pipeType,
            IList<BranchGuideSegment> guides,
            IList<FamilyInstance> sprinklers,
            double branchElevation)
        {
            var result = new PipeRouteResult();
            EnsureTeeRoutingPreference(doc, pipeType, Dn25Millimeters);
            double collectorDiameterMm = FindClosestSupportedPipeDiameterMm(
                doc,
                pipeType,
                Dn32Millimeters);
            bool canUseDn32 = Math.Abs(collectorDiameterMm - Dn32Millimeters) <= 1.0;
            var branches = new List<CreatedBranchPipe>();
            foreach (BranchGuideSegment guide in guides)
            {
                XYZ start = AtElevation(guide.Start, branchElevation);
                XYZ end = AtElevation(guide.End, branchElevation);
                bool mainCollector = guide.Kind == BranchGuideKind.MainConnection;
                bool endsAtSprinkler = mainCollector && IsAtSprinklerPoint(end, sprinklers);
                double minimumSplitLength = UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Millimeters);
                if (canUseDn32 && endsAtSprinkler && start.DistanceTo(end) > minimumSplitLength)
                {
                    // Keep the reducer far enough from the head Tee. A 250 mm
                    // DN25 tail is too short for several Revit tee/reducer
                    // families: insertion of the Tee moves the pipe end and the
                    // nearby reducer then prevents Revit from solving the joint.
                    // Prefer 600 mm and use up to 75% of a shorter segment.
                    double dn25TailLength = Math.Min(
                        UnitUtils.ConvertToInternalUnits(600, UnitTypeId.Millimeters),
                        start.DistanceTo(end) * 0.75);
                    XYZ transitionPoint = end + (start - end).Normalize() * dn25TailLength;
                    Pipe collector = TryCreatePipeQuiet(
                        doc, systemType, pipeType, level,
                        start, transitionPoint, collectorDiameterMm);
                    Pipe terminal = collector == null ? null : TryCreatePipeQuiet(
                        doc, systemType, pipeType, level,
                        transitionPoint, end, Dn25Millimeters);
                    bool collectorCreated = false;
                    if (collector != null && terminal != null)
                    {
                        try
                        {
                            doc.Create.NewTransitionFitting(
                                GetEndConnector(collector, transitionPoint),
                                GetEndConnector(terminal, transitionPoint));
                            result.PipesCreated += 2;
                            result.FittingsCreated++;
                            branches.Add(new CreatedBranchPipe(collector, start, transitionPoint));
                            branches.Add(new CreatedBranchPipe(terminal, transitionPoint, end));
                            collectorCreated = true;
                        }
                        catch
                        {
                            doc.Delete(new List<ElementId> { collector.Id, terminal.Id });
                        }
                    }
                    else if (collector != null)
                    {
                        doc.Delete(collector.Id);
                    }
                    if (collectorCreated)
                    {
                        continue;
                    }
                }

                Pipe pipe = null;
                if (mainCollector && canUseDn32 && !endsAtSprinkler)
                {
                    pipe = TryCreatePipeQuiet(
                        doc, systemType, pipeType, level,
                        start, end, collectorDiameterMm);
                    if (pipe != null)
                    {
                        result.PipesCreated++;
                    }
                }
                if (pipe == null)
                {
                    if (mainCollector)
                    {
                        result.CollectorFallbacks++;
                    }
                    pipe = TryCreatePipe(
                        doc, systemType, pipeType, level,
                        start, end, Dn25Millimeters, result, true);
                }
                if (pipe != null)
                {
                    branches.Add(new CreatedBranchPipe(pipe, start, end));
                }
            }

            doc.Regenerate();
            List<HeadDrop> drops = CreateHeadDrops(
                doc, level, systemType, pipeType, sprinklers, branchElevation, result);
            // Reducers and family connections change the available connector
            // geometry. Regenerate before asking Revit to size/orient Tees.
            doc.Regenerate();
            ConnectBranchJoints(doc, branches, drops, result);
            return result;
        }

        private static Pipe TryCreatePipeQuiet(
            Document doc,
            PipingSystemType systemType,
            PipeType pipeType,
            Level level,
            XYZ start,
            XYZ end,
            double diameterMillimeters)
        {
            try
            {
                if (start.DistanceTo(end) < UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters))
                {
                    return null;
                }
                Pipe pipe = Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, start, end);
                if (ConfigureGeneratedPipe(pipe, diameterMillimeters))
                {
                    return pipe;
                }
                doc.Delete(pipe.Id);
            }
            catch
            {
                // DN32 is an optimization. The caller creates a complete DN25
                // route when this Pipe Type or its reducer cannot support it.
            }
            return null;
        }

        private static Pipe TryCreatePipe(
            Document doc,
            PipingSystemType systemType,
            PipeType pipeType,
            Level level,
            XYZ start,
            XYZ end,
            double diameterMillimeters,
            PipeRouteResult result,
            bool isRouteSegment)
        {
            try
            {
                if (start.DistanceTo(end) < UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters))
                {
                    return null;
                }
                Pipe pipe = Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, start, end);
                if (!ConfigureGeneratedPipe(pipe, diameterMillimeters))
                {
                    doc.Delete(pipe.Id);
                    AddPipeFailure(result, "The selected Pipe Type does not support DN" +
                        diameterMillimeters.ToString("0") + ".", isRouteSegment);
                    return null;
                }
                result.PipesCreated++;
                return pipe;
            }
            catch (Exception ex)
            {
                AddPipeFailure(result, ex.Message, isRouteSegment);
                return null;
            }
        }

        private static List<HeadDrop> CreateHeadDrops(
            Document doc,
            Level level,
            PipingSystemType systemType,
            PipeType pipeType,
            IEnumerable<FamilyInstance> sprinklers,
            double branchElevation,
            PipeRouteResult result)
        {
            var drops = new List<HeadDrop>();
            foreach (FamilyInstance sprinkler in sprinklers)
            {
                Connector headConnector = GetSprinklerPipeConnector(sprinkler);
                if (headConnector == null)
                {
                    AddPipeFailure(result, "The selected sprinkler type has no piping connector, so its branch pipe cannot be connected.", false);
                    continue;
                }
                XYZ headPoint = headConnector.Origin;
                XYZ branchPoint = new XYZ(headPoint.X, headPoint.Y, branchElevation);
                double verticalDistance = Math.Abs(branchPoint.Z - headPoint.Z);
                if (verticalDistance < UnitUtils.ConvertToInternalUnits(2, UnitTypeId.Millimeters))
                {
                    AddPipeFailure(result, "Branch elevation must be above or below the sprinkler connector to create a connection.", false);
                    continue;
                }
                double headDiameterMm = UnitUtils.ConvertFromInternalUnits(headConnector.Radius * 2.0, UnitTypeId.Millimeters);
                if (headDiameterMm < 1)
                {
                    AddPipeFailure(result, "The sprinkler piping connector does not expose a valid nominal diameter.", false);
                    continue;
                }

                double direction = branchPoint.Z > headPoint.Z ? 1.0 : -1.0;
                Pipe branchDrop;
                if (Math.Abs(headDiameterMm - Dn25Millimeters) < 0.5)
                {
                    branchDrop = TryCreatePipeFromFamilyConnector(
                        doc,
                        level,
                        systemType,
                        pipeType,
                        headConnector,
                        branchPoint,
                        Dn25Millimeters,
                        result);
                }
                else
                {
                    double smallDiameterMm = FindClosestSupportedPipeDiameterMm(
                        doc,
                        pipeType,
                        headDiameterMm);
                    if (Math.Abs(smallDiameterMm - headDiameterMm) > 0.6)
                    {
                        AddPipeFailure(result, "The selected Pipe Type has no pipe size matching the DN" +
                            headDiameterMm.ToString("0.#") + " sprinkler connector.", false);
                        continue;
                    }
                    double reducerLength = Math.Min(
                        UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters),
                        verticalDistance / 2.0);
                    XYZ transitionPoint = new XYZ(
                        headPoint.X,
                        headPoint.Y,
                        branchPoint.Z - direction * reducerLength);
                    Pipe smallDrop = TryCreatePipeFromFamilyConnector(
                        doc,
                        level,
                        systemType,
                        pipeType,
                        headConnector,
                        transitionPoint,
                        smallDiameterMm,
                        result);
                    if (smallDrop == null)
                    {
                        continue;
                    }
                    Pipe largeDrop = TryCreatePipe(
                        doc,
                        systemType,
                        pipeType,
                        level,
                        transitionPoint,
                        branchPoint,
                        Dn25Millimeters,
                        result,
                        false);
                    if (largeDrop == null)
                    {
                        doc.Delete(smallDrop.Id);
                        result.PipesCreated--;
                        continue;
                    }
                    try
                    {
                        doc.Create.NewTransitionFitting(
                            GetEndConnector(smallDrop, transitionPoint),
                            GetEndConnector(largeDrop, transitionPoint));
                        result.FittingsCreated++;
                    }
                    catch (Exception ex)
                    {
                        doc.Delete(new List<ElementId> { smallDrop.Id, largeDrop.Id });
                        result.PipesCreated -= 2;
                        AddPipeFailure(result, "Could not create DN" +
                            smallDiameterMm.ToString("0.#") + " to DN25 reducer: " + ex.Message, false);
                        continue;
                    }
                    branchDrop = largeDrop;
                }
                if (branchDrop != null)
                {
                    drops.Add(new HeadDrop(branchPoint, GetEndConnector(branchDrop, branchPoint)));
                }
            }
            return drops;
        }

        private static Pipe TryCreatePipeFromFamilyConnector(
            Document doc,
            Level level,
            PipingSystemType systemType,
            PipeType pipeType,
            Connector familyConnector,
            XYZ end,
            double diameterMillimeters,
            PipeRouteResult result)
        {
            Pipe pipe = TryCreatePipe(
                doc,
                systemType,
                pipeType,
                level,
                familyConnector.Origin,
                end,
                diameterMillimeters,
                result,
                false);
            if (pipe == null)
            {
                return null;
            }
            try
            {
                Connector pipeConnector = GetEndConnector(pipe, familyConnector.Origin);
                if (pipeConnector == null)
                {
                    throw new InvalidOperationException("Could not find the pipe connector at the sprinkler.");
                }
                familyConnector.ConnectTo(pipeConnector);
                return pipe;
            }
            catch (Exception ex)
            {
                doc.Delete(pipe.Id);
                result.PipesCreated--;
                AddPipeFailure(result, "Could not connect the sprinkler connector to its nipple: " +
                    ex.Message, false);
                return null;
            }
        }

        private static double FindClosestSupportedPipeDiameterMm(
            Document doc,
            PipeType pipeType,
            double requestedDiameterMm)
        {
            var sizes = new List<double>();
            RoutingPreferenceManager manager = pipeType.RoutingPreferenceManager;
            int ruleCount = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
            for (int ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
            {
                RoutingPreferenceRule rule = manager.GetRule(
                    RoutingPreferenceRuleGroupType.Segments,
                    ruleIndex);
                PipeSegment segment = doc.GetElement(rule.MEPPartId) as PipeSegment;
                if (segment == null)
                {
                    continue;
                }
                sizes.AddRange(segment.GetSizes().Select(size =>
                    UnitUtils.ConvertFromInternalUnits(size.NominalDiameter, UnitTypeId.Millimeters)));
            }
            return sizes.Count == 0
                ? requestedDiameterMm
                : sizes.OrderBy(size => Math.Abs(size - requestedDiameterMm)).First();
        }

        private static bool ConfigureGeneratedPipe(Pipe pipe, double diameterMillimeters)
        {
            Parameter diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diameter == null || diameter.IsReadOnly ||
                !diameter.Set(UnitUtils.ConvertToInternalUnits(diameterMillimeters, UnitTypeId.Millimeters)))
            {
                return false;
            }
            Parameter comment = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comment != null && !comment.IsReadOnly)
            {
                comment.Set(GeneratedPipeComment);
            }
            return true;
        }

        private static Connector GetSprinklerPipeConnector(FamilyInstance sprinkler)
        {
            if (sprinkler == null || sprinkler.MEPModel == null || sprinkler.MEPModel.ConnectorManager == null)
            {
                return null;
            }
            return sprinkler.MEPModel.ConnectorManager.Connectors
                .Cast<Connector>()
                .Where(Pipe.IsPipingConnector)
                .Where(connector => connector.Radius > 1e-8)
                .OrderBy(connector => connector.ConnectorType == ConnectorType.End ? 0 : 1)
                .ThenBy(connector => Math.Abs(connector.CoordinateSystem.BasisZ.Z) > 0.9 ? 0 : 1)
                .FirstOrDefault();
        }

        private static void ConnectBranchJoints(
            Document doc,
            IList<CreatedBranchPipe> branches,
            IList<HeadDrop> drops,
            PipeRouteResult result)
        {
            var endpoints = new Dictionary<string, List<Connector>>();
            foreach (CreatedBranchPipe branch in branches)
            {
                AddConnector(endpoints, GetEndConnector(branch.Pipe, branch.Start));
                AddConnector(endpoints, GetEndConnector(branch.Pipe, branch.End));
            }
            var dropConnectors = drops
                .Where(drop => drop.Connector != null)
                .ToDictionary(drop => ConnectorKey(drop.Point), drop => drop.Connector);
            foreach (KeyValuePair<string, List<Connector>> endpoint in endpoints)
            {
                List<Connector> connectors = endpoint.Value;
                try
                {
                    Connector dropConnector;
                    if (dropConnectors.TryGetValue(endpoint.Key, out dropConnector))
                    {
                        if (connectors.Count == 3)
                        {
                            doc.Create.NewCrossFitting(
                                connectors[0], connectors[1], connectors[2], dropConnector);
                        }
                        else if (connectors.Count == 2)
                        {
                            CreateOrderedTee(doc, new[] { connectors[0], connectors[1], dropConnector });
                        }
                        else if (connectors.Count == 1)
                        {
                            doc.Create.NewElbowFitting(connectors[0], dropConnector);
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else if (connectors.Count == 4)
                    {
                        doc.Create.NewCrossFitting(
                            connectors[0], connectors[1], connectors[2], connectors[3]);
                    }
                    else if (connectors.Count == 3)
                    {
                        CreateOrderedTee(doc, connectors.Take(3).ToArray());
                    }
                    else if (connectors.Count == 2)
                    {
                        if (AreParallel(connectors[0], connectors[1]))
                        {
                            doc.Create.NewUnionFitting(connectors[0], connectors[1]);
                        }
                        else
                        {
                            doc.Create.NewElbowFitting(connectors[0], connectors[1]);
                        }
                    }
                    else
                    {
                        continue;
                    }
                    result.FittingsCreated++;
                    doc.Regenerate();
                }
                catch (Exception ex)
                {
                    AddPipeFailure(result, ex.Message, false);
                }
            }
        }

        private static void CreateOrderedTee(Document doc, IList<Connector> connectors)
        {
            if (connectors == null || connectors.Count != 3)
            {
                throw new InvalidOperationException("A Tee requires exactly three pipe connectors.");
            }
            Exception lastFailure = null;
            for (int first = 0; first < connectors.Count; first++)
            {
                for (int second = first + 1; second < connectors.Count; second++)
                {
                    if (!AreParallel(connectors[first], connectors[second]))
                    {
                        continue;
                    }
                    int branch = Enumerable.Range(0, connectors.Count)
                        .First(index => index != first && index != second);
                    // Revit normally accepts either run order, but some routed
                    // families solve only in one orientation. Isolate each
                    // attempt so a failed insertion cannot leave shortened pipes
                    // or a pending fitting failure behind.
                    int[] runOrder = { first, second };
                    for (int order = 0; order < 2; order++)
                    {
                        int runFirst = runOrder[order];
                        int runSecond = runOrder[1 - order];
                        using (var attempt = new SubTransaction(doc))
                        {
                            attempt.Start();
                            try
                            {
                                doc.Create.NewTeeFitting(
                                    connectors[runFirst],
                                    connectors[runSecond],
                                    connectors[branch]);
                                attempt.Commit();
                                return;
                            }
                            catch (Exception ex)
                            {
                                lastFailure = ex;
                                if (attempt.GetStatus() == TransactionStatus.Started)
                                {
                                    attempt.RollBack();
                                }
                            }
                        }
                        doc.Regenerate();
                    }
                }
            }
            if (lastFailure != null)
            {
                throw new InvalidOperationException(
                    "Could not insert Tee after retrying both run directions: " +
                    lastFailure.Message,
                    lastFailure);
            }
            throw new InvalidOperationException(
                "Cannot insert Tee because the two run connectors are not collinear.");
        }

        private static void EnsureTeeRoutingPreference(
            Document doc,
            PipeType selectedPipeType,
            double diameterMm)
        {
            if (doc == null || selectedPipeType == null)
            {
                return;
            }
            double diameter = UnitUtils.ConvertToInternalUnits(diameterMm, UnitTypeId.Millimeters);
            RoutingPreferenceManager selectedManager = selectedPipeType.RoutingPreferenceManager;
            if (FindTeeRule(doc, selectedManager, diameter) != null)
            {
                return;
            }

            RoutingPreferenceRule sourceRule = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .Where(type => type.Id != selectedPipeType.Id)
                .Select(type => FindTeeRule(doc, type.RoutingPreferenceManager, diameter))
                .FirstOrDefault(rule => rule != null);
            if (sourceRule == null)
            {
                FamilySymbol loadedTee = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(symbol => IsTeeSymbol(symbol));
                if (loadedTee == null)
                {
                    return;
                }
                var loadedTeeRule = new RoutingPreferenceRule(
                    loadedTee.Id,
                    "TungMEP DN25 Tee fallback");
                loadedTeeRule.AddCriterion(new PrimarySizeCriterion(diameter, diameter));
                selectedManager.AddRule(
                    RoutingPreferenceRuleGroupType.Junctions,
                    loadedTeeRule,
                    0);
                return;
            }

            var copiedRule = new RoutingPreferenceRule(
                sourceRule.MEPPartId,
                "TungMEP DN25 Tee fallback");
            for (int index = 0; index < sourceRule.NumberOfCriteria; index++)
            {
                PrimarySizeCriterion size = sourceRule.GetCriterion(index) as PrimarySizeCriterion;
                if (size != null)
                {
                    copiedRule.AddCriterion(new PrimarySizeCriterion(
                        size.MinimumSize,
                        size.MaximumSize));
                }
            }
            selectedManager.AddRule(
                RoutingPreferenceRuleGroupType.Junctions,
                copiedRule,
                0);
        }

        private static RoutingPreferenceRule FindTeeRule(
            Document doc,
            RoutingPreferenceManager manager,
            double diameter)
        {
            if (manager == null)
            {
                return null;
            }
            int count = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions);
            for (int index = 0; index < count; index++)
            {
                RoutingPreferenceRule rule = manager.GetRule(
                    RoutingPreferenceRuleGroupType.Junctions,
                    index);
                if (IsTeeRule(doc, rule) && RuleSupportsSize(rule, diameter))
                {
                    return rule;
                }
            }
            return null;
        }

        private static bool IsTeeRule(Document doc, RoutingPreferenceRule rule)
        {
            FamilySymbol symbol = rule == null ? null : doc.GetElement(rule.MEPPartId) as FamilySymbol;
            return IsTeeSymbol(symbol);
        }

        private static bool IsTeeSymbol(FamilySymbol symbol)
        {
            if (symbol == null)
            {
                return false;
            }
            Parameter partType = symbol.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
            if (partType != null)
            {
                int value = partType.AsInteger();
                return value == (int)PartType.Tee || value == (int)PartType.LateralTee;
            }
            string name = symbol.FamilyName + " " + symbol.Name;
            return name.IndexOf("tee", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool RuleSupportsSize(RoutingPreferenceRule rule, double diameter)
        {
            for (int index = 0; index < rule.NumberOfCriteria; index++)
            {
                PrimarySizeCriterion size = rule.GetCriterion(index) as PrimarySizeCriterion;
                if (size != null &&
                    (diameter < size.MinimumSize - 1e-8 || diameter > size.MaximumSize + 1e-8))
                {
                    return false;
                }
            }
            return true;
        }

        private static void AddConnector(IDictionary<string, List<Connector>> endpoints, Connector connector)
        {
            if (connector == null || connector.IsConnected)
            {
                return;
            }
            string key = ConnectorKey(connector.Origin);
            List<Connector> values;
            if (!endpoints.TryGetValue(key, out values))
            {
                values = new List<Connector>();
                endpoints.Add(key, values);
            }
            values.Add(connector);
        }

        private static bool IsAtSprinklerPoint(
            XYZ point,
            IEnumerable<FamilyInstance> sprinklers)
        {
            double tolerance = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
            foreach (FamilyInstance sprinkler in sprinklers)
            {
                Connector connector = GetSprinklerPipeConnector(sprinkler);
                LocationPoint location = sprinkler.Location as LocationPoint;
                XYZ head = connector != null
                    ? connector.Origin
                    : (location == null ? null : location.Point);
                if (head != null && Math.Sqrt(
                    Math.Pow(point.X - head.X, 2) +
                    Math.Pow(point.Y - head.Y, 2)) <= tolerance)
                {
                    return true;
                }
            }
            return false;
        }

        private static string ConnectorKey(XYZ point)
        {
            return Math.Round(point.X, 7) + ":" + Math.Round(point.Y, 7) + ":" + Math.Round(point.Z, 7);
        }

        private static Connector GetEndConnector(Pipe pipe, XYZ point)
        {
            if (pipe == null)
            {
                return null;
            }
            return pipe.ConnectorManager.Connectors
                .Cast<Connector>()
                .OrderBy(connector => connector.Origin.DistanceTo(point))
                .FirstOrDefault();
        }

        private static bool AreParallel(Connector first, Connector second)
        {
            if (first == null || second == null)
            {
                return false;
            }
            double dot = first.CoordinateSystem.BasisZ.DotProduct(second.CoordinateSystem.BasisZ);
            return Math.Abs(dot) > 0.999;
        }

        private static XYZ AtElevation(XYZ point, double elevation)
        {
            return new XYZ(point.X, point.Y, elevation);
        }

        private static double GetBranchElevation(
            FamilySymbol symbol,
            IEnumerable<FamilyInstance> sprinklers,
            double fallbackElevation,
            double branchOffset,
            out string position)
        {
            string name = (symbol.FamilyName + " " + symbol.Name).ToLowerInvariant();
            List<double> connectorElevations = sprinklers
                .Select(GetSprinklerPipeConnector)
                .Where(connector => connector != null)
                .Select(connector => connector.Origin.Z)
                .ToList();
            double sprinklerElevation = connectorElevations.Count == 0
                ? fallbackElevation
                : connectorElevations.Average();
            if (name.Contains("pendent") || name.Contains("pendant"))
            {
                position = connectorElevations.Count == 0
                    ? "above Pendent sprinkler (fallback elevation; no piping connector found)"
                    : "above Pendent sprinkler connector";
                return sprinklerElevation + branchOffset;
            }
            if (name.Contains("upright"))
            {
                position = connectorElevations.Count == 0
                    ? "below Upright sprinkler (fallback elevation; no piping connector found)"
                    : "below Upright sprinkler connector";
                return sprinklerElevation - branchOffset;
            }
            position = connectorElevations.Count == 0
                ? "at fallback elevation (type is neither Pendent nor Upright)"
                : "at sprinkler connector elevation (type is neither Pendent nor Upright)";
            return sprinklerElevation;
        }

        private static void AddPipeFailure(PipeRouteResult result, string message, bool segmentFailure)
        {
            result.Failures++;
            if (segmentFailure)
            {
                result.SegmentFailures++;
            }
            if (string.IsNullOrEmpty(result.FirstFailure))
            {
                result.FirstFailure = message;
            }
        }

        private static bool TryEnsureDefaultSprinklerFamily(Document doc, out string error)
        {
            bool alreadyLoaded = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Any(symbol => symbol.FamilyName.Equals(DefaultFamilyName, StringComparison.OrdinalIgnoreCase));

            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            DirectoryInfo payloadParent = Directory.GetParent(assemblyDirectory);
            var familyCandidates = new List<string>
            {
                Path.Combine(assemblyDirectory, "content", "AutoSprinkler", DefaultFamilyFileName)
            };
            if (payloadParent != null)
            {
                familyCandidates.Add(Path.Combine(
                    payloadParent.FullName, "content", "AutoSprinkler", DefaultFamilyFileName));
            }
            string familyPath = familyCandidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrEmpty(familyPath))
            {
                error = "Bundled family file was not found beside the TungMEP payload.";
                return false;
            }

            try
            {
                using (var transaction = new Transaction(doc, "Tung MEP - Load unhosted test sprinkler"))
                {
                    transaction.Start();
                    FailureHandlingOptions failureOptions = transaction.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new IgnoreLoadFamilyWarnings());
                    transaction.SetFailureHandlingOptions(failureOptions);

                    Family family;
                    bool loaded = doc.LoadFamily(familyPath, new OverwriteFamilyLoadOptions(), out family);
                    if ((!loaded || family == null) && !alreadyLoaded)
                    {
                        transaction.RollBack();
                        error = "Revit rejected the bundled family file:\n" + familyPath;
                        return false;
                    }
                    transaction.Commit();
                }

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message + "\n\nFamily file:\n" + familyPath;
                return false;
            }
        }

        private static List<CurveElement> GetBoundaryCurves(Document doc, UIDocument uiDoc, GraphicsStyle style)
        {
            List<CurveElement> selected = uiDoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id) as CurveElement)
                .Where(curve => IsBoundaryDetailCurve(curve, style, doc.ActiveView.Id))
                .ToList();
            if (selected.Count > 0)
            {
                return selected;
            }

            return new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfClass(typeof(CurveElement))
                .Cast<CurveElement>()
                .Where(curve => IsBoundaryDetailCurve(curve, style, doc.ActiveView.Id))
                .ToList();
        }

        private static List<IList<XYZ>> GetDrawnObstaclePolygons(Document doc, View view)
        {
            var obstacles = new List<IList<XYZ>>();
            GraphicsStyle obstacleStyle = SprinklerLineStyleService.Find(
                doc,
                SprinklerLineStyleService.ObstacleStyleName);
            if (obstacleStyle != null)
            {
                List<CurveElement> obstacleCurves = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(CurveElement))
                    .Cast<CurveElement>()
                    .Where(curve => IsObstacleDetailCurve(curve, obstacleStyle, view.Id))
                    .ToList();
                int ignoredOpenObstacleChains;
                double obstacleJoinTolerance = UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters);
                obstacles.AddRange(BuildClosedLoops(
                    obstacleCurves,
                    obstacleJoinTolerance,
                    out ignoredOpenObstacleChains));
                obstacles.AddRange(BuildOpenObstacleBounds(
                    obstacleCurves,
                    obstacleJoinTolerance,
                    UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters)));
            }
            return obstacles;
        }

        private static bool IsObstacleDetailCurve(
            CurveElement curve,
            GraphicsStyle expectedStyle,
            ElementId viewId)
        {
            if (curve == null || !(curve is DetailCurve) || curve.OwnerViewId != viewId)
            {
                return false;
            }
            GraphicsStyle style = curve.LineStyle as GraphicsStyle;
            if (style == null)
            {
                return false;
            }
            return expectedStyle != null && style.Id == expectedStyle.Id;
        }

        private static IList<XYZ> GetStructuralFramingPolygon(
            Element framing,
            Autodesk.Revit.DB.Transform transform,
            double minimumElevation,
            double maximumElevation)
        {
            LocationCurve location = framing == null ? null : framing.Location as LocationCurve;
            Curve curve = location == null ? null : location.Curve;
            if (curve == null || !curve.IsBound || !(curve is Line))
            {
                return null;
            }
            XYZ start = transform.OfPoint(curve.GetEndPoint(0));
            XYZ end = transform.OfPoint(curve.GetEndPoint(1));
            BoundingBoxXYZ box = framing.get_BoundingBox(null);
            if (box != null)
            {
                Autodesk.Revit.DB.Transform boxTransform = transform.Multiply(
                    box.Transform ?? Autodesk.Revit.DB.Transform.Identity);
                var elevations = new List<double>();
                for (int x = 0; x <= 1; x++)
                {
                    for (int y = 0; y <= 1; y++)
                    {
                        for (int z = 0; z <= 1; z++)
                        {
                            elevations.Add(boxTransform.OfPoint(new XYZ(
                                x == 0 ? box.Min.X : box.Max.X,
                                y == 0 ? box.Min.Y : box.Max.Y,
                                z == 0 ? box.Min.Z : box.Max.Z)).Z);
                        }
                    }
                }
                if (elevations.Max() < minimumElevation || elevations.Min() > maximumElevation)
                {
                    return null;
                }
            }

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters))
            {
                return null;
            }
            double width = GetFramingWidth(framing);
            double halfWidth = width / 2.0;
            double perpendicularX = -dy / length;
            double perpendicularY = dx / length;
            var offset = new XYZ(
                perpendicularX * halfWidth,
                perpendicularY * halfWidth,
                0);
            return new List<XYZ>
            {
                start + offset,
                end + offset,
                end - offset,
                start - offset
            };
        }

        private static double GetFramingWidth(Element framing)
        {
            BuiltInParameter[] widthParameters =
            {
                BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH,
                BuiltInParameter.FAMILY_WIDTH_PARAM
            };
            Element type = framing.Document.GetElement(framing.GetTypeId());
            foreach (BuiltInParameter builtIn in widthParameters)
            {
                Parameter parameter = framing.get_Parameter(builtIn) ??
                    (type == null ? null : type.get_Parameter(builtIn));
                if (parameter != null && parameter.StorageType == StorageType.Double &&
                    parameter.AsDouble() > UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters))
                {
                    return parameter.AsDouble();
                }
            }
            return UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);
        }

        private static List<IList<XYZ>> BuildOpenObstacleBounds(
            IList<CurveElement> curves,
            double closedTolerance,
            double recoveryTolerance)
        {
            var recovered = new List<IList<XYZ>>();
            var remaining = new HashSet<int>(Enumerable.Range(0, curves.Count));
            while (remaining.Count > 0)
            {
                int seed = remaining.First();
                remaining.Remove(seed);
                var groupIndices = new List<int> { seed };
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    CurveElement current = curves[queue.Dequeue()];
                    List<int> connected = remaining
                        .Where(index => ObstacleCurvesTouch(
                            current,
                            curves[index],
                            recoveryTolerance))
                        .ToList();
                    foreach (int index in connected)
                    {
                        remaining.Remove(index);
                        groupIndices.Add(index);
                        queue.Enqueue(index);
                    }
                }

                List<CurveElement> group = groupIndices.Select(index => curves[index]).ToList();
                int ignored;
                if (BuildClosedLoops(group, closedTolerance, out ignored).Count > 0)
                {
                    continue;
                }
                List<XYZ> points = group
                    .SelectMany(curve => curve.GeometryCurve.Tessellate())
                    .ToList();
                if (points.Count == 0)
                {
                    continue;
                }
                double minimumX = points.Min(point => point.X);
                double maximumX = points.Max(point => point.X);
                double minimumY = points.Min(point => point.Y);
                double maximumY = points.Max(point => point.Y);
                if (maximumX - minimumX < closedTolerance ||
                    maximumY - minimumY < closedTolerance)
                {
                    continue;
                }
                double z = points.Average(point => point.Z);
                recovered.Add(new List<XYZ>
                {
                    new XYZ(minimumX, minimumY, z),
                    new XYZ(maximumX, minimumY, z),
                    new XYZ(maximumX, maximumY, z),
                    new XYZ(minimumX, maximumY, z)
                });
            }
            return recovered;
        }

        private static bool ObstacleCurvesTouch(
            CurveElement first,
            CurveElement second,
            double tolerance)
        {
            Curve firstCurve = first == null ? null : first.GeometryCurve;
            Curve secondCurve = second == null ? null : second.GeometryCurve;
            if (firstCurve == null || secondCurve == null ||
                !firstCurve.IsBound || !secondCurve.IsBound)
            {
                return false;
            }
            XYZ[] firstEnds = { firstCurve.GetEndPoint(0), firstCurve.GetEndPoint(1) };
            XYZ[] secondEnds = { secondCurve.GetEndPoint(0), secondCurve.GetEndPoint(1) };
            return firstEnds.Any(firstEnd => secondEnds.Any(secondEnd =>
                IsNear2D(firstEnd, secondEnd, tolerance)));
        }

        private static IList<XYZ> BoundingBoxToPolygon(
            BoundingBoxXYZ box,
            Autodesk.Revit.DB.Transform transform,
            double minimumElevation,
            double maximumElevation)
        {
            if (box == null || box.Max.X - box.Min.X < 1e-6 || box.Max.Y - box.Min.Y < 1e-6)
            {
                return null;
            }
            Autodesk.Revit.DB.Transform boxTransform = box.Transform ?? Autodesk.Revit.DB.Transform.Identity;
            Autodesk.Revit.DB.Transform combinedTransform = transform.Multiply(boxTransform);
            var boxCorners = new List<XYZ>();
            foreach (double x in new[] { box.Min.X, box.Max.X })
            {
                foreach (double y in new[] { box.Min.Y, box.Max.Y })
                {
                    foreach (double zValue in new[] { box.Min.Z, box.Max.Z })
                    {
                        boxCorners.Add(combinedTransform.OfPoint(new XYZ(x, y, zValue)));
                    }
                }
            }
            if (boxCorners.Max(point => point.Z) < minimumElevation ||
                boxCorners.Min(point => point.Z) > maximumElevation)
            {
                return null;
            }
            return new List<XYZ>
            {
                combinedTransform.OfPoint(new XYZ(box.Min.X, box.Min.Y, box.Min.Z)),
                combinedTransform.OfPoint(new XYZ(box.Max.X, box.Min.Y, box.Min.Z)),
                combinedTransform.OfPoint(new XYZ(box.Max.X, box.Max.Y, box.Min.Z)),
                combinedTransform.OfPoint(new XYZ(box.Min.X, box.Max.Y, box.Min.Z))
            };
        }

        private static bool PolygonsMayOverlap(IList<XYZ> first, IList<XYZ> second)
        {
            double firstMinX = first.Min(point => point.X);
            double firstMaxX = first.Max(point => point.X);
            double firstMinY = first.Min(point => point.Y);
            double firstMaxY = first.Max(point => point.Y);
            double secondMinX = second.Min(point => point.X);
            double secondMaxX = second.Max(point => point.X);
            double secondMinY = second.Min(point => point.Y);
            double secondMaxY = second.Max(point => point.Y);
            return firstMinX <= secondMaxX && firstMaxX >= secondMinX &&
                firstMinY <= secondMaxY && firstMaxY >= secondMinY;
        }

        private static bool SegmentIntersectsPolygon2D(XYZ start, XYZ end, IList<XYZ> polygon)
        {
            if (IsPointInPolygon(start, polygon) || IsPointInPolygon(end, polygon))
            {
                return true;
            }
            for (int index = 0; index < polygon.Count; index++)
            {
                if (SegmentsIntersect2D(start, end, polygon[index], polygon[(index + 1) % polygon.Count]))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect2D(XYZ a, XYZ b, XYZ c, XYZ d)
        {
            double abC = Cross2D(a, b, c);
            double abD = Cross2D(a, b, d);
            double cdA = Cross2D(c, d, a);
            double cdB = Cross2D(c, d, b);
            return ((abC > 1e-9 && abD < -1e-9) || (abC < -1e-9 && abD > 1e-9)) &&
                ((cdA > 1e-9 && cdB < -1e-9) || (cdA < -1e-9 && cdB > 1e-9));
        }

        private static double Cross2D(XYZ start, XYZ end, XYZ point)
        {
            return (end.X - start.X) * (point.Y - start.Y) -
                (end.Y - start.Y) * (point.X - start.X);
        }

        private static CurveElement GetMainPipeCurve(Document doc, UIDocument uiDoc, GraphicsStyle style)
        {
            if (style == null)
            {
                return null;
            }
            List<CurveElement> selected = uiDoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id) as CurveElement)
                .Where(curve => IsDetailCurveWithStyle(curve, style, doc.ActiveView.Id))
                .ToList();
            IEnumerable<CurveElement> candidates = selected.Count > 0
                ? selected
                : new FilteredElementCollector(doc, doc.ActiveView.Id)
                    .OfClass(typeof(CurveElement))
                    .Cast<CurveElement>()
                    .Where(curve => IsDetailCurveWithStyle(curve, style, doc.ActiveView.Id));
            return candidates
                .Where(curve => curve.GeometryCurve is Line && curve.GeometryCurve.IsBound)
                .OrderByDescending(curve => curve.GeometryCurve.Length)
                .FirstOrDefault();
        }

        private static bool IsDetailCurveWithStyle(CurveElement curve, GraphicsStyle style, ElementId viewId)
        {
            if (curve == null || !(curve is DetailCurve) || curve.OwnerViewId != viewId)
            {
                return false;
            }
            GraphicsStyle lineStyle = curve.LineStyle as GraphicsStyle;
            return lineStyle != null && lineStyle.Id == style.Id;
        }

        private static bool IsBoundaryDetailCurve(CurveElement curve, GraphicsStyle style, ElementId viewId)
        {
            return IsDetailCurveWithStyle(curve, style, viewId);
        }

        // Closed loops use an even/odd fill rule: outer loops are sprinkler regions,
        // direct child loops are voids, and an island inside a void becomes a region again.
        private static List<SprinklerRegion> BuildRegions(IList<IList<XYZ>> loops, out string error)
        {
            error = string.Empty;
            for (int index = 0; index < loops.Count; index++)
            {
                if (!IsSimplePolygon(loops[index]))
                {
                    error = "A sprinkler boundary intersects or touches itself. " +
                        "Use separate, non-self-intersecting closed loops.";
                    return null;
                }
                for (int other = index + 1; other < loops.Count; other++)
                {
                    if (PolygonsTouchOrIntersect(loops[index], loops[other]))
                    {
                        error = "Two sprinkler boundaries intersect or touch. " +
                            "Use disjoint regions, or place a void loop completely inside its enclosing region.";
                        return null;
                    }
                }
            }

            int[] depths = new int[loops.Count];
            for (int index = 0; index < loops.Count; index++)
            {
                depths[index] = loops.Count(other => other != loops[index] &&
                    IsPointInPolygon(loops[index][0], other));
            }

            var regions = new List<SprinklerRegion>();
            for (int index = 0; index < loops.Count; index++)
            {
                if (depths[index] % 2 != 0)
                {
                    continue;
                }
                var holes = new List<IList<XYZ>>();
                for (int other = 0; other < loops.Count; other++)
                {
                    if (depths[other] == depths[index] + 1 &&
                        IsPointInPolygon(loops[other][0], loops[index]))
                    {
                        holes.Add(loops[other]);
                    }
                }
                regions.Add(new SprinklerRegion(loops[index], holes));
            }
            return regions;
        }

        private static bool IsSimplePolygon(IList<XYZ> polygon)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }
            for (int first = 0; first < polygon.Count; first++)
            {
                XYZ a = polygon[first];
                XYZ b = polygon[(first + 1) % polygon.Count];
                if (IsNear2D(a, b, 1e-8))
                {
                    return false;
                }
                for (int second = first + 1; second < polygon.Count; second++)
                {
                    if (second == first + 1 || (first == 0 && second == polygon.Count - 1))
                    {
                        continue;
                    }
                    XYZ c = polygon[second];
                    XYZ d = polygon[(second + 1) % polygon.Count];
                    if (SegmentsTouchOrIntersect2D(a, b, c, d))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool PolygonsTouchOrIntersect(IList<XYZ> first, IList<XYZ> second)
        {
            for (int firstIndex = 0; firstIndex < first.Count; firstIndex++)
            {
                XYZ a = first[firstIndex];
                XYZ b = first[(firstIndex + 1) % first.Count];
                for (int secondIndex = 0; secondIndex < second.Count; secondIndex++)
                {
                    XYZ c = second[secondIndex];
                    XYZ d = second[(secondIndex + 1) % second.Count];
                    if (SegmentsTouchOrIntersect2D(a, b, c, d))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool SegmentsTouchOrIntersect2D(XYZ a, XYZ b, XYZ c, XYZ d)
        {
            const double tolerance = 1e-9;
            double abC = Cross2D(a, b, c);
            double abD = Cross2D(a, b, d);
            double cdA = Cross2D(c, d, a);
            double cdB = Cross2D(c, d, b);
            if (((abC > tolerance && abD < -tolerance) || (abC < -tolerance && abD > tolerance)) &&
                ((cdA > tolerance && cdB < -tolerance) || (cdA < -tolerance && cdB > tolerance)))
            {
                return true;
            }
            return Math.Abs(abC) <= tolerance && PointOnSegment2D(c, a, b, tolerance) ||
                Math.Abs(abD) <= tolerance && PointOnSegment2D(d, a, b, tolerance) ||
                Math.Abs(cdA) <= tolerance && PointOnSegment2D(a, c, d, tolerance) ||
                Math.Abs(cdB) <= tolerance && PointOnSegment2D(b, c, d, tolerance);
        }

        private static bool PointOnSegment2D(XYZ point, XYZ start, XYZ end, double tolerance)
        {
            return point.X >= Math.Min(start.X, end.X) - tolerance &&
                point.X <= Math.Max(start.X, end.X) + tolerance &&
                point.Y >= Math.Min(start.Y, end.Y) - tolerance &&
                point.Y <= Math.Max(start.Y, end.Y) + tolerance;
        }

        private static List<IList<XYZ>> BuildClosedLoops(IEnumerable<CurveElement> curveElements, double tolerance, out int openChains)
        {
            openChains = 0;
            var loops = new List<IList<XYZ>>();
            var remaining = new List<Curve>();

            foreach (CurveElement element in curveElements)
            {
                Curve curve = element.GeometryCurve;
                if (curve == null)
                {
                    continue;
                }

                if (!curve.IsBound)
                {
                    List<XYZ> closedPoints = CleanPolygon(curve.Tessellate(), tolerance);
                    if (closedPoints.Count >= 3)
                    {
                        loops.Add(closedPoints);
                    }
                }
                else
                {
                    remaining.Add(curve);
                }
            }

            while (remaining.Count > 0)
            {
                Curve first = remaining[0];
                remaining.RemoveAt(0);
                var chain = new List<OrientedCurve> { new OrientedCurve(first, false) };
                XYZ start = first.GetEndPoint(0);
                XYZ current = first.GetEndPoint(1);
                bool closed = IsNear2D(start, current, tolerance);

                while (!closed)
                {
                    int matchIndex = -1;
                    bool reversed = false;
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        Curve candidate = remaining[i];
                        if (IsNear2D(current, candidate.GetEndPoint(0), tolerance))
                        {
                            matchIndex = i;
                            break;
                        }
                        if (IsNear2D(current, candidate.GetEndPoint(1), tolerance))
                        {
                            matchIndex = i;
                            reversed = true;
                            break;
                        }
                    }

                    if (matchIndex < 0)
                    {
                        break;
                    }

                    Curve match = remaining[matchIndex];
                    remaining.RemoveAt(matchIndex);
                    chain.Add(new OrientedCurve(match, reversed));
                    current = reversed ? match.GetEndPoint(0) : match.GetEndPoint(1);
                    closed = IsNear2D(start, current, tolerance);
                }

                if (!closed)
                {
                    openChains++;
                    continue;
                }

                var polygon = new List<XYZ>();
                foreach (OrientedCurve part in chain)
                {
                    List<XYZ> points = part.Curve.Tessellate().ToList();
                    if (part.Reversed)
                    {
                        points.Reverse();
                    }
                    foreach (XYZ point in points)
                    {
                        if (polygon.Count == 0 || !IsNear2D(polygon[polygon.Count - 1], point, tolerance))
                        {
                            polygon.Add(point);
                        }
                    }
                }

                polygon = CleanPolygon(polygon, tolerance);
                if (polygon.Count >= 3)
                {
                    loops.Add(polygon);
                }
            }

            return loops;
        }

        private static List<XYZ> CleanPolygon(IEnumerable<XYZ> points, double tolerance)
        {
            var polygon = new List<XYZ>();
            foreach (XYZ point in points)
            {
                if (polygon.Count == 0 || !IsNear2D(polygon[polygon.Count - 1], point, tolerance))
                {
                    polygon.Add(point);
                }
            }
            if (polygon.Count > 1 && IsNear2D(polygon[0], polygon[polygon.Count - 1], tolerance))
            {
                polygon.RemoveAt(polygon.Count - 1);
            }
            return polygon;
        }

        private static Level GetActiveViewLevel(Document doc)
        {
            ViewPlan plan = doc.ActiveView as ViewPlan;
            return plan == null ? null : plan.GenLevel;
        }

        private static FamilyInstance PlaceLevelBasedSprinkler(Document doc, FamilySymbol symbol, XYZ point, Level level)
        {
            return doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
        }

        private static bool SetInstanceHeight(FamilyInstance instance, double height)
        {
            Parameter elevation = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (TrySetElevationParameter(elevation, height))
            {
                return true;
            }

            string[] parameterNames = { "Elevation from Level", "Offset", "Default Elevation", "Height" };
            foreach (string name in parameterNames)
            {
                Parameter parameter = instance.LookupParameter(name);
                if (TrySetElevationParameter(parameter, height))
                {
                    return true;
                }
            }

            Parameter builtInOffset = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (TrySetElevationParameter(builtInOffset, height))
            {
                return true;
            }

            return false;
        }

        private static bool TrySetElevationParameter(Parameter parameter, double height)
        {
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Double)
            {
                return false;
            }
            if (Math.Abs(parameter.AsDouble() - height) > 1e-8)
            {
                parameter.Set(height);
            }
            return true;
        }

        private static List<XYZ> GetExistingSprinklerPoints(Document doc, Level level, double targetElevation, double elevationTolerance)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Sprinklers)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(instance => instance.LevelId == level.Id)
                .Select(GetActualInstancePoint)
                .Where(point => point != null && Math.Abs(point.Z - targetElevation) <= elevationTolerance)
                .ToList();
        }

        private static XYZ GetActualInstancePoint(FamilyInstance instance)
        {
            LocationPoint location = instance.Location as LocationPoint;
            if (location == null)
            {
                return null;
            }

            double z = location.Point.Z;
            Parameter offset = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            Level level = instance.Document.GetElement(instance.LevelId) as Level;
            if (offset != null && level != null && offset.StorageType == StorageType.Double)
            {
                z = level.ProjectElevation + offset.AsDouble();
            }
            return new XYZ(location.Point.X, location.Point.Y, z);
        }

        private static bool IsPointInPolygon(XYZ point, IList<XYZ> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                XYZ current = polygon[i];
                XYZ previous = polygon[j];
                bool intersects = ((current.Y > point.Y) != (previous.Y > point.Y)) &&
                    point.X < (previous.X - current.X) * (point.Y - current.Y) /
                    (previous.Y - current.Y + 1e-9) + current.X;
                if (intersects)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static double DistanceToPolygon(XYZ point, IList<XYZ> polygon)
        {
            double minimum = double.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                minimum = Math.Min(minimum, DistancePointToSegment2D(point, polygon[i], polygon[(i + 1) % polygon.Count]));
            }
            return minimum;
        }

        private static double DistancePointToSegment2D(XYZ point, XYZ start, XYZ end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-9)
            {
                return Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2));
            }

            double ratio = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            ratio = Math.Max(0, Math.Min(1, ratio));
            double px = point.X - (start.X + ratio * dx);
            double py = point.Y - (start.Y + ratio * dy);
            return Math.Sqrt(px * px + py * py);
        }

        private static bool IsNear2D(XYZ first, XYZ second, double tolerance)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return dx * dx + dy * dy <= tolerance * tolerance;
        }

        private class IgnoreLoadFamilyWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(failure);
                    }
                }
                return FailureProcessingResult.Continue;
            }
        }

        private class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        private class OrientedCurve
        {
            public OrientedCurve(Curve curve, bool reversed)
            {
                Curve = curve;
                Reversed = reversed;
            }

            public Curve Curve { get; private set; }
            public bool Reversed { get; private set; }
        }

        private class SprinklerPlacement
        {
            public SprinklerPlacement(XYZ point, Level level)
            {
                Point = point;
                Level = level;
            }

            public XYZ Point { get; private set; }
            public Level Level { get; private set; }
        }

        private class SprinklerRegion
        {
            public SprinklerRegion(IList<XYZ> outer, IList<IList<XYZ>> holes)
            {
                Outer = outer;
                Holes = holes;
            }

            public IList<XYZ> Outer { get; private set; }
            public IList<IList<XYZ>> Holes { get; private set; }
        }

        private class CreatedBranchPipe
        {
            public CreatedBranchPipe(Pipe pipe, XYZ start, XYZ end)
            {
                Pipe = pipe;
                Start = start;
                End = end;
            }

            public Pipe Pipe { get; private set; }
            public XYZ Start { get; private set; }
            public XYZ End { get; private set; }
        }

        private class HeadDrop
        {
            public HeadDrop(XYZ point, Connector connector)
            {
                Point = point;
                Connector = connector;
            }

            public XYZ Point { get; private set; }
            public Connector Connector { get; private set; }
        }

        private class PipeRouteResult
        {
            public int PipesCreated { get; set; }
            public int FittingsCreated { get; set; }
            public int CollectorFallbacks { get; set; }
            public int Failures { get; set; }
            public int SegmentFailures { get; set; }
            public string FirstFailure { get; set; }
        }
    }
}
