using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TungMEP.Modules.Dimensions
{
    internal enum DimensionReferenceKind
    {
        Center,
        Face
    }

    internal sealed class DimensionCandidateReference
    {
        internal DimensionCandidateReference(
            Reference reference,
            XYZ anchor,
            XYZ direction,
            XYZ normal,
            ElementId sourceElementId,
            ElementId sourceCategoryId,
            string sourceCategoryName,
            string stableKey,
            DimensionReferenceKind kind)
        {
            Reference = reference;
            Anchor = anchor;
            Direction = direction;
            Normal = normal;
            SourceElementId = sourceElementId;
            SourceCategoryId = sourceCategoryId;
            SourceCategoryName = sourceCategoryName ?? string.Empty;
            StableKey = stableKey;
            Kind = kind;
        }

        public Reference Reference { get; private set; }
        public XYZ Anchor { get; private set; }
        public XYZ Direction { get; private set; }
        public XYZ Normal { get; private set; }
        public ElementId SourceElementId { get; private set; }
        public ElementId SourceId { get { return SourceElementId; } }
        public ElementId SourceCategoryId { get; private set; }
        public ElementId CategoryId { get { return SourceCategoryId; } }
        public string SourceCategoryName { get; private set; }
        public string CategoryName { get { return SourceCategoryName; } }
        public string StableKey { get; private set; }
        public DimensionReferenceKind Kind { get; private set; }
    }

    internal sealed class DimensionReferenceSkip
    {
        internal DimensionReferenceSkip(
            ElementId sourceElementId,
            ElementId sourceCategoryId,
            string sourceCategoryName,
            string reason)
        {
            SourceElementId = sourceElementId ?? ElementId.InvalidElementId;
            SourceCategoryId = sourceCategoryId ?? ElementId.InvalidElementId;
            SourceCategoryName = sourceCategoryName ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public ElementId SourceElementId { get; private set; }
        public ElementId SourceId { get { return SourceElementId; } }
        public ElementId SourceCategoryId { get; private set; }
        public ElementId CategoryId { get { return SourceCategoryId; } }
        public string SourceCategoryName { get; private set; }
        public string CategoryName { get { return SourceCategoryName; } }
        public string Reason { get; private set; }
    }

    /// <summary>
    /// Resolves real, dimensionable Revit references. GeometryInstance.GetInstanceGeometry()
    /// is deliberately not used: it returns copied geometry whose references are not safe for
    /// creation of persistent dimensions.
    /// </summary>
    internal sealed class AutoDimensionReferenceProvider
    {
        private const double VectorEpsilon = 1e-9;
        // Native linear dimensions require genuinely parallel references.  Keep this
        // much tighter than a visual/selection tolerance so slightly rotated systems
        // are never mixed into one ReferenceArray.
        private const double DirectionDotTolerance = 0.999999999999;
        private const double PerpendicularDotTolerance = 0.000001;
        // A scan line is a hand-drawn selection gesture, not the final native
        // dimension direction.  Allow about five degrees of mouse imprecision while
        // preserving the exact reference directions on the returned candidates.
        private const double ScanGestureDirectionDotTolerance = 0.996194698092;
        private const double ScanGesturePerpendicularDotTolerance = 0.087155742748;

        private readonly Document _document;
        private readonly View _view;
        private readonly Options _geometryOptions;
        private readonly XYZ _viewNormal;
        private readonly XYZ _viewRight;
        private readonly XYZ _viewUp;
        private readonly double _positionTolerance;
        private readonly double _boundsTolerance;

        public AutoDimensionReferenceProvider(Document document, View activeView)
        {
            if (document == null)
            {
                throw new ArgumentNullException("document");
            }
            if (activeView == null)
            {
                throw new ArgumentNullException("activeView");
            }
            if (!BelongsToDocument(activeView, document))
            {
                throw new ArgumentException(
                    "The active view must belong to the supplied document.",
                    "activeView");
            }

            _document = document;
            _view = activeView;
            _viewNormal = NormalizeOrFallback(activeView.ViewDirection, XYZ.BasisZ);
            _viewRight = NormalizeOrFallback(activeView.RightDirection, XYZ.BasisX);
            _viewUp = NormalizeOrFallback(activeView.UpDirection, XYZ.BasisY);
            _positionTolerance = UnitUtils.ConvertToInternalUnits(
                1.0,
                UnitTypeId.Millimeters);
            _boundsTolerance = UnitUtils.ConvertToInternalUnits(
                5.0,
                UnitTypeId.Millimeters);

            _geometryOptions = new Options();
            _geometryOptions.ComputeReferences = true;
            _geometryOptions.IncludeNonVisibleObjects = true;
            _geometryOptions.View = activeView;
        }

        public IList<DimensionCandidateReference> Collect(
            IEnumerable<ElementId> sourceIds,
            XYZ measurementDirection,
            Line scanLine,
            out IList<DimensionReferenceSkip> skipped)
        {
            if (sourceIds == null)
            {
                throw new ArgumentNullException("sourceIds");
            }
            return Collect(
                sourceIds
                    .Where(id => id != null && id != ElementId.InvalidElementId)
                    .Select(id => _document.GetElement(id)),
                measurementDirection,
                scanLine,
                out skipped);
        }

        public IList<DimensionCandidateReference> Collect(
            IEnumerable<Element> sourceElements,
            XYZ measurementDirection,
            Line scanLine,
            out IList<DimensionReferenceSkip> skipped)
        {
            if (sourceElements == null)
            {
                throw new ArgumentNullException("sourceElements");
            }

            var results = new List<DimensionCandidateReference>();
            var skipList = new List<DimensionReferenceSkip>();
            var stableKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Element element in sourceElements)
            {
                IList<DimensionCandidateReference> elementResults;
                string reason;
                if (!TryCollect(
                    element,
                    measurementDirection,
                    scanLine,
                    out elementResults,
                    out reason))
                {
                    skipList.Add(CreateSkip(element, reason));
                    continue;
                }

                foreach (DimensionCandidateReference candidate in elementResults)
                {
                    if (stableKeys.Add(candidate.StableKey))
                    {
                        results.Add(candidate);
                    }
                }
            }
            skipped = skipList;
            return results;
        }

        public IList<DimensionCandidateReference> Collect(
            IEnumerable<Element> sourceElements,
            Line scanLine,
            XYZ measurementDirection,
            out IList<DimensionReferenceSkip> skipped)
        {
            return Collect(
                sourceElements,
                measurementDirection,
                scanLine,
                out skipped);
        }

        public bool TryCollect(
            Element element,
            XYZ measurementDirection,
            Line scanLine,
            out IList<DimensionCandidateReference> candidates,
            out string skipReason)
        {
            var result = new List<DimensionCandidateReference>();
            candidates = result;
            skipReason = string.Empty;
            if (element == null || !element.IsValidObject)
            {
                skipReason = "The element is missing or is no longer valid.";
                return false;
            }
            if (!BelongsToDocument(element, _document))
            {
                skipReason = "The element belongs to a different document.";
                return false;
            }

            SupportedReferenceSource source = GetSupportedSource(element);
            if (source == SupportedReferenceSource.None)
            {
                skipReason = "The category is not supported by Auto Dimension.";
                return false;
            }

            XYZ measure = NormalizeInView(measurementDirection);
            if (IsZero(measure) && scanLine != null)
            {
                measure = NormalizeInView(GetLineDirection(scanLine));
            }
            if (IsZero(measure))
            {
                skipReason = "The measurement direction is zero or perpendicular to the active view.";
                return false;
            }

            var stableKeys = new HashSet<string>(StringComparer.Ordinal);
            var positionKeys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                switch (source)
                {
                    case SupportedReferenceSource.Grid:
                        CollectGrid(
                            (Grid)element,
                            measure,
                            scanLine,
                            result,
                            stableKeys,
                            positionKeys);
                        break;
                    case SupportedReferenceSource.Wall:
                        CollectWall(
                            (Wall)element,
                            measure,
                            scanLine,
                            result,
                            stableKeys,
                            positionKeys);
                        break;
                    case SupportedReferenceSource.Pipe:
                    case SupportedReferenceSource.Duct:
                        CollectMepCurve(
                            element as MEPCurve,
                            measure,
                            scanLine,
                            result,
                            stableKeys,
                            positionKeys);
                        break;
                    case SupportedReferenceSource.StructuralColumn:
                    case SupportedReferenceSource.Sprinkler:
                    case SupportedReferenceSource.MechanicalEquipment:
                        CollectFamilyInstance(
                            element as FamilyInstance,
                            measure,
                            scanLine,
                            result,
                            stableKeys,
                            positionKeys);
                        break;
                }
            }
            catch (Exception ex)
            {
                skipReason = "Reference extraction failed: " + ex.Message;
                return false;
            }

            if (result.Count == 0)
            {
                skipReason = scanLine == null
                    ? "No compatible stable center or face reference was found for the measurement direction."
                    : "No compatible stable reference crosses the scan line in the active view.";
                return false;
            }
            return true;
        }

        private void CollectGrid(
            Grid grid,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            IList<Curve> curves = null;
            try
            {
                curves = grid.GetCurvesInView(
                    DatumExtentType.ViewSpecific,
                    _view);
            }
            catch
            {
            }

            if (TryCollectGridCurves(
                grid,
                measure,
                scanLine,
                curves,
                candidates,
                stableKeys,
                positionKeys))
            {
                return;
            }

            curves = null;
            try
            {
                curves = grid.GetCurvesInView(
                    DatumExtentType.Model,
                    _view);
            }
            catch
            {
            }

            if (TryCollectGridCurves(
                grid,
                measure,
                scanLine,
                curves,
                candidates,
                stableKeys,
                positionKeys))
            {
                return;
            }

            Curve modelCurve = null;
            try
            {
                modelCurve = grid.Curve;
            }
            catch
            {
            }
            if (modelCurve != null)
            {
                TryCollectGridCurves(
                    grid,
                    measure,
                    scanLine,
                    new[] { modelCurve },
                    candidates,
                    stableKeys,
                    positionKeys);
            }
        }

        private bool TryCollectGridCurves(
            Grid grid,
            XYZ measure,
            Line scanLine,
            IEnumerable<Curve> curves,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            if (curves == null)
            {
                return false;
            }

            foreach (Curve curve in curves)
            {
                if (!(curve is Line))
                {
                    continue;
                }

                XYZ anchor;
                if (!TryGetCurveScanAnchor(
                    curve,
                    Transform.Identity,
                    scanLine,
                    out anchor))
                {
                    continue;
                }

                XYZ direction = GetCurveDirection(curve, Transform.Identity);
                if (TryAddCandidate(
                    grid,
                    new Reference(grid),
                    anchor,
                    direction,
                    CrossNormal(direction),
                    DimensionReferenceKind.Center,
                    measure,
                    candidates,
                    stableKeys,
                    positionKeys))
                {
                    return true;
                }
            }
            return false;
        }

        private void CollectWall(
            Wall wall,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            LocationCurve location = wall.Location as LocationCurve;
            Curve curve = location == null ? null : location.Curve;
            if (curve != null && !(curve is Line))
            {
                return;
            }
            XYZ anchor;
            if (curve != null && TryGetCurveScanAnchor(
                curve,
                Transform.Identity,
                scanLine,
                out anchor))
            {
                XYZ direction = GetCurveDirection(curve, Transform.Identity);
                TryAddCandidate(
                    wall,
                    new Reference(wall),
                    anchor,
                    direction,
                    CrossNormal(direction),
                    DimensionReferenceKind.Center,
                    measure,
                    candidates,
                    stableKeys,
                    positionKeys);
            }

            CollectWallSideFaces(
                wall,
                ShellLayerType.Exterior,
                measure,
                scanLine,
                candidates,
                stableKeys,
                positionKeys);
            CollectWallSideFaces(
                wall,
                ShellLayerType.Interior,
                measure,
                scanLine,
                candidates,
                stableKeys,
                positionKeys);
        }

        private void CollectWallSideFaces(
            Wall wall,
            ShellLayerType side,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            IList<Reference> references;
            try
            {
                references = HostObjectUtils.GetSideFaces(wall, side);
            }
            catch
            {
                return;
            }
            foreach (Reference reference in references)
            {
                PlanarFace face = wall.GetGeometryObjectFromReference(reference) as PlanarFace;
                if (face == null)
                {
                    continue;
                }
                TryAddPlanarFace(
                    wall,
                    face,
                    reference,
                    Transform.Identity,
                    measure,
                    scanLine,
                    candidates,
                    stableKeys,
                    positionKeys);
            }
        }

        private void CollectMepCurve(
            MEPCurve mepCurve,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            if (mepCurve == null)
            {
                return;
            }
            LocationCurve location = mepCurve.Location as LocationCurve;
            Line centerLine = location == null ? null : location.Curve as Line;
            if (centerLine == null)
            {
                return;
            }

            CollectMepCenterReferences(
                mepCurve,
                centerLine,
                measure,
                scanLine,
                candidates,
                stableKeys,
                positionKeys);

            CollectGeometryPlanarFaces(
                mepCurve,
                measure,
                scanLine,
                candidates,
                stableKeys,
                positionKeys);
        }

        private void CollectMepCenterReferences(
            MEPCurve mepCurve,
            Line locationLine,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            int countBefore = candidates.Count;
            GeometryElement geometry = null;
            try
            {
                geometry = mepCurve.get_Geometry(_geometryOptions);
            }
            catch
            {
                // The LocationCurve reference below remains a valid fallback when exposed.
            }
            if (geometry != null)
            {
                CollectMepCenterCurves(
                    mepCurve,
                    geometry,
                    Transform.Identity,
                    locationLine,
                    measure,
                    scanLine,
                    candidates,
                    stableKeys,
                    positionKeys);
            }
            if (candidates.Count > countBefore || locationLine.Reference == null)
            {
                return;
            }

            XYZ anchor;
            if (!TryGetCurveScanAnchor(
                locationLine,
                Transform.Identity,
                scanLine,
                out anchor))
            {
                return;
            }
            XYZ direction = NormalizeInView(locationLine.Direction);
            TryAddCandidate(
                mepCurve,
                locationLine.Reference,
                anchor,
                direction,
                CrossNormal(direction),
                DimensionReferenceKind.Center,
                measure,
                candidates,
                stableKeys,
                positionKeys);
        }

        private void CollectMepCenterCurves(
            MEPCurve mepCurve,
            GeometryElement geometry,
            Transform transform,
            Line locationLine,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            foreach (GeometryObject geometryObject in geometry)
            {
                Line rawLine = geometryObject as Line;
                if (rawLine != null && rawLine.Reference != null &&
                    IsMepCenterCurve(rawLine, transform, locationLine))
                {
                    XYZ anchor;
                    if (TryGetCurveScanAnchor(rawLine, transform, scanLine, out anchor))
                    {
                        XYZ direction = GetCurveDirection(rawLine, transform);
                        TryAddCandidate(
                            mepCurve,
                            rawLine.Reference,
                            anchor,
                            direction,
                            CrossNormal(direction),
                            DimensionReferenceKind.Center,
                            measure,
                            candidates,
                            stableKeys,
                            positionKeys);
                    }
                    continue;
                }

                GeometryInstance geometryInstance = geometryObject as GeometryInstance;
                if (geometryInstance == null)
                {
                    continue;
                }
                GeometryElement symbolGeometry = null;
                try
                {
                    symbolGeometry = geometryInstance.GetSymbolGeometry();
                }
                catch
                {
                    continue;
                }
                if (symbolGeometry != null)
                {
                    CollectMepCenterCurves(
                        mepCurve,
                        symbolGeometry,
                        transform.Multiply(geometryInstance.Transform),
                        locationLine,
                        measure,
                        scanLine,
                        candidates,
                        stableKeys,
                        positionKeys);
                }
            }
        }

        private bool IsMepCenterCurve(
            Line geometryLine,
            Transform transform,
            Line locationLine)
        {
            XYZ geometryDirection = GetCurveDirection(geometryLine, transform);
            XYZ locationDirection = NormalizeInView(locationLine.Direction);
            if (IsZero(geometryDirection) || IsZero(locationDirection) ||
                Math.Abs(geometryDirection.DotProduct(locationDirection)) <
                DirectionDotTolerance)
            {
                return false;
            }
            XYZ midpoint;
            try
            {
                midpoint = transform.OfPoint(geometryLine.Evaluate(0.5, true));
            }
            catch
            {
                return false;
            }
            IntersectionResult projection = locationLine.Project(midpoint);
            if (projection == null || projection.XYZPoint == null)
            {
                return false;
            }
            double tolerance = UnitUtils.ConvertToInternalUnits(
                2.0,
                UnitTypeId.Millimeters);
            return ProjectInView(midpoint - projection.XYZPoint).GetLength() <= tolerance;
        }

        private void CollectFamilyInstance(
            FamilyInstance instance,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            if (instance == null)
            {
                return;
            }

            FamilyInstanceReferenceType[] preferredTypes =
            {
                FamilyInstanceReferenceType.CenterLeftRight,
                FamilyInstanceReferenceType.CenterFrontBack,
                FamilyInstanceReferenceType.CenterElevation,
                FamilyInstanceReferenceType.StrongReference,
                FamilyInstanceReferenceType.Left,
                FamilyInstanceReferenceType.Right,
                FamilyInstanceReferenceType.Front,
                FamilyInstanceReferenceType.Back,
                FamilyInstanceReferenceType.Bottom,
                FamilyInstanceReferenceType.Top
            };
            var discoveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FamilyInstanceReferenceType referenceType in preferredTypes)
            {
                IList<Reference> references;
                try
                {
                    references = instance.GetReferences(referenceType);
                }
                catch
                {
                    continue;
                }
                foreach (Reference reference in references)
                {
                    Reference resolvedReference = reference;
                    string referenceName = SafeReferenceName(instance, reference);
                    if (!string.IsNullOrWhiteSpace(referenceName))
                    {
                        discoveredNames.Add(referenceName);
                        Reference namedReference = SafeGetReferenceByName(instance, referenceName);
                        if (namedReference != null)
                        {
                            resolvedReference = namedReference;
                        }
                    }
                    AddFamilyReference(
                        instance,
                        resolvedReference,
                        referenceType,
                        referenceName,
                        measure,
                        scanLine,
                        candidates,
                        stableKeys,
                        positionKeys);
                }
            }

            string[] conventionalNames =
            {
                "Center (Left/Right)",
                "Center (Front/Back)",
                "Center (Elevation)",
                "Center Left/Right",
                "Center Front/Back",
                "Center Elevation",
                "Centerline",
                "Center Line",
                "Reference Line",
                "Left",
                "Right",
                "Front",
                "Back",
                "Top",
                "Bottom"
            };
            foreach (string name in conventionalNames.Concat(discoveredNames))
            {
                Reference reference = SafeGetReferenceByName(instance, name);
                if (reference == null)
                {
                    continue;
                }
                AddNamedFamilyReference(
                    instance,
                    reference,
                    name,
                    measure,
                    scanLine,
                    candidates,
                    stableKeys,
                    positionKeys);
            }

            CollectGeometryPlanarFaces(
                instance,
                measure,
                scanLine,
                candidates,
                stableKeys,
                positionKeys);
        }

        private void AddFamilyReference(
            FamilyInstance instance,
            Reference reference,
            FamilyInstanceReferenceType referenceType,
            string referenceName,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            if (reference == null)
            {
                return;
            }

            if (referenceType == FamilyInstanceReferenceType.StrongReference)
            {
                if (TryAddResolvedGeometryReference(
                    instance,
                    reference,
                    measure,
                    scanLine,
                    candidates,
                    stableKeys,
                    positionKeys))
                {
                    return;
                }
            }

            XYZ normal = GetFamilyReferenceNormal(instance, referenceType, referenceName);
            if (IsZero(normal))
            {
                return;
            }
            XYZ anchor;
            if (!TryGetFamilyPlaneScanAnchor(
                instance,
                normal,
                referenceType,
                referenceName,
                scanLine,
                out anchor))
            {
                return;
            }
            DimensionReferenceKind kind = IsCenterReference(referenceType, referenceName)
                ? DimensionReferenceKind.Center
                : DimensionReferenceKind.Face;
            TryAddCandidate(
                instance,
                reference,
                anchor,
                DirectionFromNormal(normal),
                normal,
                kind,
                measure,
                candidates,
                stableKeys,
                positionKeys);
        }

        private void AddNamedFamilyReference(
            FamilyInstance instance,
            Reference reference,
            string referenceName,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            if (TryAddResolvedGeometryReference(
                instance,
                reference,
                measure,
                scanLine,
                candidates,
                stableKeys,
                positionKeys))
            {
                return;
            }
            XYZ normal = GetFamilyReferenceNormal(
                instance,
                FamilyInstanceReferenceType.NotAReference,
                referenceName);
            if (IsZero(normal))
            {
                return;
            }
            XYZ anchor;
            if (!TryGetFamilyPlaneScanAnchor(
                instance,
                normal,
                FamilyInstanceReferenceType.NotAReference,
                referenceName,
                scanLine,
                out anchor))
            {
                return;
            }
            TryAddCandidate(
                instance,
                reference,
                anchor,
                DirectionFromNormal(normal),
                normal,
                IsCenterReference(FamilyInstanceReferenceType.NotAReference, referenceName)
                    ? DimensionReferenceKind.Center
                    : DimensionReferenceKind.Face,
                measure,
                candidates,
                stableKeys,
                positionKeys);
        }

        private bool TryAddResolvedGeometryReference(
            FamilyInstance instance,
            Reference reference,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            GeometryObject geometry;
            try
            {
                geometry = instance.GetGeometryObjectFromReference(reference);
            }
            catch
            {
                return false;
            }
            PlanarFace face = geometry as PlanarFace;
            if (face != null)
            {
                return TryAddPlanarFace(
                    instance,
                    face,
                    reference,
                    Transform.Identity,
                    measure,
                    scanLine,
                    candidates,
                    stableKeys,
                    positionKeys);
            }
            Edge edge = geometry as Edge;
            Curve curve = edge == null ? geometry as Curve : edge.AsCurve();
            if (curve == null)
            {
                return false;
            }
            XYZ anchor;
            if (!TryGetCurveScanAnchor(
                curve,
                Transform.Identity,
                scanLine,
                out anchor))
            {
                return false;
            }
            XYZ direction = GetCurveDirection(curve, Transform.Identity);
            return TryAddCandidate(
                instance,
                reference,
                anchor,
                direction,
                CrossNormal(direction),
                DimensionReferenceKind.Center,
                measure,
                candidates,
                stableKeys,
                positionKeys);
        }

        private void CollectGeometryPlanarFaces(
            Element element,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            GeometryElement geometry;
            try
            {
                geometry = element.get_Geometry(_geometryOptions);
            }
            catch
            {
                return;
            }
            if (geometry == null)
            {
                return;
            }
            CollectGeometryPlanarFaces(
                element,
                geometry,
                Transform.Identity,
                measure,
                scanLine,
                candidates,
                stableKeys,
                positionKeys);
        }

        private void CollectGeometryPlanarFaces(
            Element element,
            GeometryElement geometry,
            Transform transform,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            foreach (GeometryObject geometryObject in geometry)
            {
                Solid solid = geometryObject as Solid;
                if (solid != null && solid.Faces != null)
                {
                    foreach (Face rawFace in solid.Faces)
                    {
                        PlanarFace face = rawFace as PlanarFace;
                        if (face == null || face.Reference == null)
                        {
                            continue;
                        }
                        TryAddPlanarFace(
                            element,
                            face,
                            face.Reference,
                            transform,
                            measure,
                            scanLine,
                            candidates,
                            stableKeys,
                            positionKeys);
                    }
                    continue;
                }

                GeometryInstance geometryInstance = geometryObject as GeometryInstance;
                if (geometryInstance == null)
                {
                    continue;
                }
                GeometryElement symbolGeometry;
                try
                {
                    // GetSymbolGeometry() without a transform returns original Revit geometry.
                    // Its references may be used by dimensions; GetInstanceGeometry() copies may not.
                    symbolGeometry = geometryInstance.GetSymbolGeometry();
                }
                catch
                {
                    continue;
                }
                if (symbolGeometry == null)
                {
                    continue;
                }
                Transform nestedTransform = transform.Multiply(geometryInstance.Transform);
                CollectGeometryPlanarFaces(
                    element,
                    symbolGeometry,
                    nestedTransform,
                    measure,
                    scanLine,
                    candidates,
                    stableKeys,
                    positionKeys);
            }
        }

        private bool TryAddPlanarFace(
            Element element,
            PlanarFace face,
            Reference reference,
            Transform transform,
            XYZ measure,
            Line scanLine,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            XYZ anchor;
            XYZ worldNormal = NormalizeInView(transform.OfVector(face.FaceNormal));
            if (IsZero(worldNormal) || !TryGetFaceScanAnchor(
                face,
                transform,
                worldNormal,
                scanLine,
                out anchor))
            {
                return false;
            }
            return TryAddCandidate(
                element,
                reference,
                anchor,
                DirectionFromNormal(worldNormal),
                worldNormal,
                DimensionReferenceKind.Face,
                measure,
                candidates,
                stableKeys,
                positionKeys);
        }

        private bool TryAddCandidate(
            Element element,
            Reference reference,
            XYZ anchor,
            XYZ direction,
            XYZ normal,
            DimensionReferenceKind kind,
            XYZ measure,
            ICollection<DimensionCandidateReference> candidates,
            ISet<string> stableKeys,
            ISet<string> positionKeys)
        {
            if (reference == null || anchor == null || !IsFinite(anchor))
            {
                return false;
            }
            direction = NormalizeInView(direction);
            normal = NormalizeInView(normal);
            if (IsZero(normal) && !IsZero(direction))
            {
                normal = CrossNormal(direction);
            }
            if (IsZero(direction) && !IsZero(normal))
            {
                direction = DirectionFromNormal(normal);
            }

            bool compatible = !IsZero(normal)
                ? Math.Abs(normal.DotProduct(measure)) >=
                    ScanGestureDirectionDotTolerance
                : !IsZero(direction) &&
                    Math.Abs(direction.DotProduct(measure)) <=
                        ScanGesturePerpendicularDotTolerance;
            if (!compatible)
            {
                return false;
            }

            string stableKey = GetStableKey(reference, element, kind, candidates.Count);
            if (!stableKeys.Add(stableKey))
            {
                return false;
            }
            long positionBucket = (long)Math.Round(
                anchor.DotProduct(measure) / _positionTolerance,
                MidpointRounding.AwayFromZero);
            string positionKey = element.Id.Value + ":" + kind + ":" + positionBucket;
            if (!positionKeys.Add(positionKey))
            {
                stableKeys.Remove(stableKey);
                return false;
            }

            Category category = element.Category;
            candidates.Add(new DimensionCandidateReference(
                reference,
                anchor,
                direction,
                normal,
                element.Id,
                category == null ? ElementId.InvalidElementId : category.Id,
                category == null ? string.Empty : category.Name,
                stableKey,
                kind));
            return true;
        }

        private bool TryGetCurveScanAnchor(
            Curve curve,
            Transform transform,
            Line scanLine,
            out XYZ anchor)
        {
            anchor = null;
            if (curve == null)
            {
                return false;
            }
            if (scanLine == null)
            {
                try
                {
                    anchor = transform.OfPoint(curve.Evaluate(0.5, true));
                    return IsFinite(anchor);
                }
                catch
                {
                    return false;
                }
            }

            XYZ scanStart;
            XYZ scanEnd;
            if (!TryGetBoundLineEndpoints(scanLine, out scanStart, out scanEnd))
            {
                return false;
            }
            IList<XYZ> points;
            try
            {
                points = curve.Tessellate()
                    .Select(transform.OfPoint)
                    .ToList();
            }
            catch
            {
                return false;
            }
            for (int index = 1; index < points.Count; index++)
            {
                double firstParameter;
                double secondParameter;
                if (!TryViewSegmentIntersection(
                    scanStart,
                    scanEnd,
                    points[index - 1],
                    points[index],
                    out firstParameter,
                    out secondParameter))
                {
                    continue;
                }
                anchor = points[index - 1] +
                    ((points[index] - points[index - 1]) * secondParameter);
                return true;
            }
            return false;
        }

        private bool TryGetFaceScanAnchor(
            PlanarFace face,
            Transform transform,
            XYZ worldNormal,
            Line scanLine,
            out XYZ anchor)
        {
            anchor = null;
            XYZ worldOrigin = transform.OfPoint(face.Origin);
            if (scanLine == null)
            {
                anchor = worldOrigin;
                return true;
            }

            XYZ scanStart;
            XYZ scanEnd;
            if (!TryGetBoundLineEndpoints(scanLine, out scanStart, out scanEnd))
            {
                return false;
            }
            XYZ scanVector = ProjectInView(scanEnd - scanStart);
            double denominator = worldNormal.DotProduct(scanVector);
            if (Math.Abs(denominator) <= VectorEpsilon)
            {
                return false;
            }
            double parameter = worldNormal.DotProduct(worldOrigin - scanStart) / denominator;
            if (parameter < -VectorEpsilon || parameter > 1.0 + VectorEpsilon)
            {
                return false;
            }
            parameter = Math.Max(0.0, Math.Min(1.0, parameter));
            anchor = scanStart + (scanVector * parameter);
            return IsPointWithinProjectedFaceBounds(face, transform, anchor);
        }

        private bool TryGetFamilyPlaneScanAnchor(
            FamilyInstance instance,
            XYZ normal,
            FamilyInstanceReferenceType referenceType,
            string referenceName,
            Line scanLine,
            out XYZ anchor)
        {
            XYZ origin = GetFamilyReferencePlaneOrigin(
                instance,
                normal,
                referenceType,
                referenceName);
            anchor = origin;
            if (scanLine == null)
            {
                return origin != null;
            }

            XYZ scanStart;
            XYZ scanEnd;
            if (!TryGetBoundLineEndpoints(scanLine, out scanStart, out scanEnd))
            {
                return false;
            }
            normal = NormalizeInView(normal);
            XYZ scanVector = ProjectInView(scanEnd - scanStart);
            double denominator = normal.DotProduct(scanVector);
            if (Math.Abs(denominator) <= VectorEpsilon)
            {
                return false;
            }
            double parameter = normal.DotProduct(origin - scanStart) / denominator;
            if (parameter < -VectorEpsilon || parameter > 1.0 + VectorEpsilon)
            {
                return false;
            }
            parameter = Math.Max(0.0, Math.Min(1.0, parameter));
            anchor = scanStart + (scanVector * parameter);
            return IsPointWithinProjectedElementBounds(instance, anchor);
        }

        private XYZ GetFamilyReferencePlaneOrigin(
            FamilyInstance instance,
            XYZ normal,
            FamilyInstanceReferenceType referenceType,
            string referenceName)
        {
            XYZ center = GetElementAnchor(instance);
            if (center == null || IsCenterReference(referenceType, referenceName))
            {
                return center;
            }

            int extreme = GetFamilyReferenceExtreme(referenceType, referenceName);
            if (extreme == 0)
            {
                return center;
            }
            normal = NormalizeOrFallback(normal, XYZ.BasisX);
            BoundingBoxXYZ bounds = null;
            try
            {
                bounds = instance.get_BoundingBox(_view) ?? instance.get_BoundingBox(null);
            }
            catch
            {
                return center;
            }
            if (bounds == null)
            {
                return center;
            }

            List<double> coordinates = CreateBoundingBoxCorners(bounds)
                .Select(rawCorner => bounds.Transform.OfPoint(rawCorner).DotProduct(normal))
                .ToList();
            if (coordinates.Count == 0)
            {
                return center;
            }
            double target = extreme < 0 ? coordinates.Min() : coordinates.Max();
            return center + (normal * (target - center.DotProduct(normal)));
        }

        private static int GetFamilyReferenceExtreme(
            FamilyInstanceReferenceType referenceType,
            string referenceName)
        {
            switch (referenceType)
            {
                case FamilyInstanceReferenceType.Left:
                case FamilyInstanceReferenceType.Back:
                case FamilyInstanceReferenceType.Bottom:
                    return -1;
                case FamilyInstanceReferenceType.Right:
                case FamilyInstanceReferenceType.Front:
                case FamilyInstanceReferenceType.Top:
                    return 1;
            }
            string name = (referenceName ?? string.Empty).ToLowerInvariant();
            if (name.Contains("left") || name.Contains("back") || name.Contains("bottom"))
            {
                return -1;
            }
            if (name.Contains("right") || name.Contains("front") || name.Contains("top"))
            {
                return 1;
            }
            return 0;
        }

        private bool IsPointWithinProjectedFaceBounds(
            PlanarFace face,
            Transform transform,
            XYZ point)
        {
            bool found = false;
            double minimumU = double.MaxValue;
            double maximumU = double.MinValue;
            double minimumV = double.MaxValue;
            double maximumV = double.MinValue;
            try
            {
                foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
                {
                    foreach (Curve edgeCurve in loop)
                    {
                        foreach (XYZ rawPoint in edgeCurve.Tessellate())
                        {
                            XYZ worldPoint = transform.OfPoint(rawPoint);
                            ViewPoint projected = ProjectPoint(worldPoint);
                            minimumU = Math.Min(minimumU, projected.U);
                            maximumU = Math.Max(maximumU, projected.U);
                            minimumV = Math.Min(minimumV, projected.V);
                            maximumV = Math.Max(maximumV, projected.V);
                            found = true;
                        }
                    }
                }
            }
            catch
            {
                return true;
            }
            if (!found)
            {
                return true;
            }
            ViewPoint candidate = ProjectPoint(point);
            return candidate.U >= minimumU - _boundsTolerance &&
                candidate.U <= maximumU + _boundsTolerance &&
                candidate.V >= minimumV - _boundsTolerance &&
                candidate.V <= maximumV + _boundsTolerance;
        }

        private bool IsPointWithinProjectedElementBounds(Element element, XYZ point)
        {
            BoundingBoxXYZ bounds = null;
            try
            {
                bounds = element.get_BoundingBox(_view) ?? element.get_BoundingBox(null);
            }
            catch
            {
                // A missing bounding box is not sufficient reason to reject a valid family reference.
            }
            if (bounds == null)
            {
                return true;
            }

            XYZ[] corners = CreateBoundingBoxCorners(bounds);
            double minimumU = double.MaxValue;
            double maximumU = double.MinValue;
            double minimumV = double.MaxValue;
            double maximumV = double.MinValue;
            foreach (XYZ rawCorner in corners)
            {
                XYZ corner = bounds.Transform.OfPoint(rawCorner);
                ViewPoint projected = ProjectPoint(corner);
                minimumU = Math.Min(minimumU, projected.U);
                maximumU = Math.Max(maximumU, projected.U);
                minimumV = Math.Min(minimumV, projected.V);
                maximumV = Math.Max(maximumV, projected.V);
            }
            ViewPoint candidate = ProjectPoint(point);
            return candidate.U >= minimumU - _boundsTolerance &&
                candidate.U <= maximumU + _boundsTolerance &&
                candidate.V >= minimumV - _boundsTolerance &&
                candidate.V <= maximumV + _boundsTolerance;
        }

        private bool TryViewSegmentIntersection(
            XYZ firstStart,
            XYZ firstEnd,
            XYZ secondStart,
            XYZ secondEnd,
            out double firstParameter,
            out double secondParameter)
        {
            ViewPoint a = ProjectPoint(firstStart);
            ViewPoint b = ProjectPoint(firstEnd);
            ViewPoint c = ProjectPoint(secondStart);
            ViewPoint d = ProjectPoint(secondEnd);
            double firstU = b.U - a.U;
            double firstV = b.V - a.V;
            double secondU = d.U - c.U;
            double secondV = d.V - c.V;
            double denominator = (firstU * secondV) - (firstV * secondU);
            firstParameter = 0.0;
            secondParameter = 0.0;
            if (Math.Abs(denominator) <= VectorEpsilon)
            {
                return false;
            }
            double betweenU = c.U - a.U;
            double betweenV = c.V - a.V;
            firstParameter = ((betweenU * secondV) - (betweenV * secondU)) / denominator;
            secondParameter = ((betweenU * firstV) - (betweenV * firstU)) / denominator;
            return firstParameter >= -VectorEpsilon &&
                firstParameter <= 1.0 + VectorEpsilon &&
                secondParameter >= -VectorEpsilon &&
                secondParameter <= 1.0 + VectorEpsilon;
        }

        private XYZ GetFamilyReferenceNormal(
            FamilyInstance instance,
            FamilyInstanceReferenceType referenceType,
            string referenceName)
        {
            switch (referenceType)
            {
                case FamilyInstanceReferenceType.Left:
                case FamilyInstanceReferenceType.CenterLeftRight:
                case FamilyInstanceReferenceType.Right:
                    return SafeHandOrientation(instance);
                case FamilyInstanceReferenceType.Front:
                case FamilyInstanceReferenceType.CenterFrontBack:
                case FamilyInstanceReferenceType.Back:
                    return SafeFacingOrientation(instance);
                case FamilyInstanceReferenceType.Bottom:
                case FamilyInstanceReferenceType.CenterElevation:
                case FamilyInstanceReferenceType.Top:
                    return SafeLocalUpOrientation(instance);
            }

            string name = (referenceName ?? string.Empty).ToLowerInvariant();
            if (name.Contains("left") || name.Contains("right"))
            {
                return SafeHandOrientation(instance);
            }
            if (name.Contains("front") || name.Contains("back"))
            {
                return SafeFacingOrientation(instance);
            }
            if (name.Contains("elevation") || name.Contains("top") || name.Contains("bottom"))
            {
                return SafeLocalUpOrientation(instance);
            }
            return XYZ.Zero;
        }

        private static bool IsCenterReference(
            FamilyInstanceReferenceType referenceType,
            string referenceName)
        {
            if (referenceType == FamilyInstanceReferenceType.CenterLeftRight ||
                referenceType == FamilyInstanceReferenceType.CenterFrontBack ||
                referenceType == FamilyInstanceReferenceType.CenterElevation)
            {
                return true;
            }
            string name = (referenceName ?? string.Empty).ToLowerInvariant();
            return name.Contains("center") || name.Contains("centre");
        }

        private static Reference SafeGetReferenceByName(
            FamilyInstance instance,
            string referenceName)
        {
            if (string.IsNullOrWhiteSpace(referenceName))
            {
                return null;
            }
            try
            {
                return instance.GetReferenceByName(referenceName);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeReferenceName(
            FamilyInstance instance,
            Reference reference)
        {
            try
            {
                return instance.GetReferenceName(reference) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private XYZ GetElementAnchor(Element element)
        {
            LocationPoint locationPoint = element.Location as LocationPoint;
            if (locationPoint != null)
            {
                return locationPoint.Point;
            }
            LocationCurve locationCurve = element.Location as LocationCurve;
            if (locationCurve != null && locationCurve.Curve != null)
            {
                try
                {
                    return locationCurve.Curve.Evaluate(0.5, true);
                }
                catch
                {
                    // Fall through to the bounding box.
                }
            }
            BoundingBoxXYZ bounds = null;
            try
            {
                bounds = element.get_BoundingBox(_view) ?? element.get_BoundingBox(null);
            }
            catch
            {
                // Use the view origin if no element location is available.
            }
            if (bounds != null)
            {
                return bounds.Transform.OfPoint((bounds.Min + bounds.Max) * 0.5);
            }
            return _view.Origin;
        }

        private XYZ GetCurveDirection(Curve curve, Transform transform)
        {
            try
            {
                Transform derivatives = curve.ComputeDerivatives(0.5, true);
                return NormalizeInView(transform.OfVector(derivatives.BasisX));
            }
            catch
            {
                try
                {
                    return NormalizeInView(
                        transform.OfPoint(curve.GetEndPoint(1)) -
                        transform.OfPoint(curve.GetEndPoint(0)));
                }
                catch
                {
                    return XYZ.Zero;
                }
            }
        }

        private XYZ CrossNormal(XYZ direction)
        {
            direction = NormalizeInView(direction);
            return IsZero(direction)
                ? XYZ.Zero
                : NormalizeInView(direction.CrossProduct(_viewNormal));
        }

        private XYZ DirectionFromNormal(XYZ normal)
        {
            normal = NormalizeInView(normal);
            return IsZero(normal)
                ? XYZ.Zero
                : NormalizeInView(_viewNormal.CrossProduct(normal));
        }

        private XYZ NormalizeInView(XYZ vector)
        {
            return NormalizeOrZero(ProjectInView(vector));
        }

        private XYZ ProjectInView(XYZ vector)
        {
            if (vector == null)
            {
                return XYZ.Zero;
            }
            return vector - (_viewNormal * vector.DotProduct(_viewNormal));
        }

        private ViewPoint ProjectPoint(XYZ point)
        {
            XYZ relative = point - _view.Origin;
            return new ViewPoint(
                relative.DotProduct(_viewRight),
                relative.DotProduct(_viewUp));
        }

        private static bool TryGetBoundLineEndpoints(
            Line line,
            out XYZ start,
            out XYZ end)
        {
            start = null;
            end = null;
            if (line == null || !line.IsBound)
            {
                return false;
            }
            try
            {
                start = line.GetEndPoint(0);
                end = line.GetEndPoint(1);
                return start != null && end != null &&
                    start.DistanceTo(end) > VectorEpsilon;
            }
            catch
            {
                return false;
            }
        }

        private static XYZ GetLineDirection(Line line)
        {
            if (line == null)
            {
                return XYZ.Zero;
            }
            try
            {
                return line.Direction;
            }
            catch
            {
                XYZ start;
                XYZ end;
                return TryGetBoundLineEndpoints(line, out start, out end)
                    ? end - start
                    : XYZ.Zero;
            }
        }

        private static XYZ SafeHandOrientation(FamilyInstance instance)
        {
            try
            {
                return instance.HandOrientation;
            }
            catch
            {
                return XYZ.Zero;
            }
        }

        private static XYZ SafeFacingOrientation(FamilyInstance instance)
        {
            try
            {
                return instance.FacingOrientation;
            }
            catch
            {
                return XYZ.Zero;
            }
        }

        private static XYZ SafeLocalUpOrientation(FamilyInstance instance)
        {
            try
            {
                Transform transform = instance.GetTransform();
                if (transform != null && transform.BasisZ != null &&
                    transform.BasisZ.GetLength() > VectorEpsilon)
                {
                    return transform.BasisZ;
                }
            }
            catch
            {
                // Level-based families use global Z when no usable instance transform exists.
            }
            return XYZ.BasisZ;
        }

        private string GetStableKey(
            Reference reference,
            Element element,
            DimensionReferenceKind kind,
            int fallbackIndex)
        {
            try
            {
                string representation = reference.ConvertToStableRepresentation(_document);
                if (!string.IsNullOrWhiteSpace(representation))
                {
                    return representation;
                }
            }
            catch
            {
                // Some valid element references have no serializable geometric path.
            }
            return "element:" + element.Id.Value +
                ":" + kind +
                ":" + reference.ElementReferenceType +
                ":" + fallbackIndex;
        }

        private static DimensionReferenceSkip CreateSkip(Element element, string reason)
        {
            if (element == null)
            {
                return new DimensionReferenceSkip(
                    ElementId.InvalidElementId,
                    ElementId.InvalidElementId,
                    string.Empty,
                    reason);
            }
            Category category = element.Category;
            return new DimensionReferenceSkip(
                element.Id,
                category == null ? ElementId.InvalidElementId : category.Id,
                category == null ? string.Empty : category.Name,
                reason);
        }

        private static SupportedReferenceSource GetSupportedSource(Element element)
        {
            if (element is Grid)
            {
                return SupportedReferenceSource.Grid;
            }
            if (element is Wall)
            {
                return SupportedReferenceSource.Wall;
            }
            if (IsCategory(element, BuiltInCategory.OST_PipeCurves))
            {
                return SupportedReferenceSource.Pipe;
            }
            if (IsCategory(element, BuiltInCategory.OST_DuctCurves))
            {
                return SupportedReferenceSource.Duct;
            }
            if (IsCategory(element, BuiltInCategory.OST_StructuralColumns) ||
                IsCategory(element, BuiltInCategory.OST_Columns))
            {
                return SupportedReferenceSource.StructuralColumn;
            }
            if (IsCategory(element, BuiltInCategory.OST_Sprinklers))
            {
                return SupportedReferenceSource.Sprinkler;
            }
            if (IsCategory(element, BuiltInCategory.OST_MechanicalEquipment))
            {
                return SupportedReferenceSource.MechanicalEquipment;
            }
            return SupportedReferenceSource.None;
        }

        private static bool IsCategory(Element element, BuiltInCategory category)
        {
            return element != null && element.Category != null &&
                element.Category.Id.Value == (long)category;
        }

        private static bool BelongsToDocument(Element element, Document document)
        {
            if (element == null || document == null ||
                element.Id == null || element.Id == ElementId.InvalidElementId)
            {
                return false;
            }
            try
            {
                Element resolved = document.GetElement(element.Id);
                return resolved != null &&
                    string.Equals(
                        resolved.UniqueId,
                        element.UniqueId,
                        StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static XYZ[] CreateBoundingBoxCorners(BoundingBoxXYZ bounds)
        {
            XYZ minimum = bounds.Min;
            XYZ maximum = bounds.Max;
            return new[]
            {
                new XYZ(minimum.X, minimum.Y, minimum.Z),
                new XYZ(maximum.X, minimum.Y, minimum.Z),
                new XYZ(minimum.X, maximum.Y, minimum.Z),
                new XYZ(maximum.X, maximum.Y, minimum.Z),
                new XYZ(minimum.X, minimum.Y, maximum.Z),
                new XYZ(maximum.X, minimum.Y, maximum.Z),
                new XYZ(minimum.X, maximum.Y, maximum.Z),
                new XYZ(maximum.X, maximum.Y, maximum.Z)
            };
        }

        private static XYZ NormalizeOrFallback(XYZ value, XYZ fallback)
        {
            XYZ normalized = NormalizeOrZero(value);
            return IsZero(normalized) ? fallback : normalized;
        }

        private static XYZ NormalizeOrZero(XYZ value)
        {
            if (value == null || !IsFinite(value))
            {
                return XYZ.Zero;
            }
            double length = value.GetLength();
            return length <= VectorEpsilon ? XYZ.Zero : value / length;
        }

        private static bool IsZero(XYZ value)
        {
            return value == null || value.GetLength() <= VectorEpsilon;
        }

        private static bool IsFinite(XYZ value)
        {
            return value != null &&
                IsFinite(value.X) &&
                IsFinite(value.Y) &&
                IsFinite(value.Z);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private enum SupportedReferenceSource
        {
            None,
            Grid,
            Wall,
            StructuralColumn,
            Pipe,
            Duct,
            Sprinkler,
            MechanicalEquipment
        }

        private struct ViewPoint
        {
            public ViewPoint(double u, double v)
                : this()
            {
                U = u;
                V = v;
            }

            public double U { get; private set; }
            public double V { get; private set; }
        }
    }
}
