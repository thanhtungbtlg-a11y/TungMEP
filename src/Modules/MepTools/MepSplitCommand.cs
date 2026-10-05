using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;

namespace TungMEP.Modules.MepTools
{
    [Transaction(TransactionMode.Manual)]
    public class LegacyMepSplitCommand : IExternalCommand
    {
        internal const int MaximumPiecesPerCurve = 500;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            if (document == null || document.IsFamilyDocument)
            {
                TaskDialog.Show("Tung MEP - MEP Split", "Open a Revit project before running MEP Split.");
                return Result.Cancelled;
            }

            List<ElementId> sourceIds = CollectPreselectedSources(document, uiDocument);
            if (sourceIds.Count == 0)
            {
                try
                {
                    sourceIds = uiDocument.Selection
                        .PickObjects(
                            ObjectType.Element,
                            new MepSplitSelectionFilter(),
                            "Select straight, unpinned pipes or ducts to split")
                        .Select(reference => reference.ElementId)
                        .GroupBy(id => id.Value)
                        .Select(group => group.First())
                        .ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
            }

            if (sourceIds.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - MEP Split",
                    "No straight, unpinned pipes or ducts were selected.");
                return Result.Cancelled;
            }

            using (var dialog = new MepSplitForm())
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                double minimumSegmentLength = Math.Max(
                    UnitUtils.ConvertToInternalUnits(10.0, UnitTypeId.Millimeters),
                    commandData.Application.Application.ShortCurveTolerance * 2.0);
                double requestedLength = UnitUtils.ConvertToInternalUnits(
                    dialog.LengthMm,
                    UnitTypeId.Millimeters);
                double pointTolerance = Math.Max(
                    UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters),
                    commandData.Application.Application.ShortCurveTolerance * 2.0);
                double connectorTolerance = Math.Max(
                    UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters),
                    commandData.Application.Application.ShortCurveTolerance * 4.0);

                int splitSources = 0;
                int unchangedSources = 0;
                int failedSources = 0;
                int insulatedSourcesSkipped = 0;
                int createdSegments = 0;
                int createdUnions = 0;
                int failedUnions = 0;
                int alreadyConnectedBreaks = 0;
                string firstFailure = string.Empty;

                using (var transaction = new Transaction(document, "Tung MEP - MEP Split"))
                {
                    transaction.Start();
                    foreach (ElementId sourceId in sourceIds)
                    {
                        Element source = document.GetElement(sourceId);
                        Line sourceLine;
                        if (!TryGetSupportedLine(source, out sourceLine))
                        {
                            unchangedSources++;
                            continue;
                        }
                        if (HasInsulationOrLining(document, source.Id))
                        {
                            unchangedSources++;
                            insulatedSourcesSkipped++;
                            continue;
                        }

                        List<XYZ> breakPoints;
                        try
                        {
                            breakPoints = BuildBreakPoints(
                                sourceLine,
                                dialog.Mode,
                                requestedLength,
                                dialog.Quantity,
                                minimumSegmentLength);
                        }
                        catch (Exception ex)
                        {
                            failedSources++;
                            SetFirstFailure(ref firstFailure, source, ex.Message);
                            continue;
                        }

                        if (breakPoints.Count == 0)
                        {
                            unchangedSources++;
                            continue;
                        }

                        using (var sourceTransaction = new SubTransaction(document))
                        {
                            sourceTransaction.Start();
                            try
                            {
                                int sourceCreatedUnions = 0;
                                int sourceFailedUnions = 0;
                                int sourceAlreadyConnectedBreaks = 0;
                                var liveSegmentIds = new List<ElementId> { sourceId };
                                var actualBreakPoints = new List<XYZ>();
                                foreach (XYZ plannedPoint in breakPoints)
                                {
                                    ElementId segmentId;
                                    XYZ pointOnSegment;
                                    if (!TryFindSegmentAtPoint(
                                        document,
                                        liveSegmentIds,
                                        plannedPoint,
                                        minimumSegmentLength,
                                        pointTolerance,
                                        out segmentId,
                                        out pointOnSegment))
                                    {
                                        throw new InvalidOperationException(
                                            "A current pipe or duct segment could not be found at a planned split point.");
                                    }

                                    Element segment = document.GetElement(segmentId);
                                    ElementId newSegmentId = BreakCurve(document, segment, pointOnSegment);
                                    if (newSegmentId == null ||
                                        newSegmentId == ElementId.InvalidElementId ||
                                        document.GetElement(newSegmentId) == null)
                                    {
                                        throw new InvalidOperationException("Revit did not return the new MEP segment.");
                                    }
                                    if (!liveSegmentIds.Any(id => id.Value == newSegmentId.Value))
                                    {
                                        liveSegmentIds.Add(newSegmentId);
                                    }
                                    actualBreakPoints.Add(pointOnSegment);
                                    document.Regenerate();
                                }

                                if (dialog.InsertUnions)
                                {
                                    foreach (XYZ breakPoint in actualBreakPoints)
                                    {
                                        Connector first;
                                        Connector second;
                                        bool alreadyConnected;
                                        if (!TryFindConnectorPair(
                                            document,
                                            liveSegmentIds,
                                            breakPoint,
                                            connectorTolerance,
                                            out first,
                                            out second,
                                            out alreadyConnected))
                                        {
                                            if (alreadyConnected)
                                            {
                                                sourceAlreadyConnectedBreaks++;
                                            }
                                            else
                                            {
                                                sourceFailedUnions++;
                                            }
                                            continue;
                                        }

                                        using (var unionTransaction = new SubTransaction(document))
                                        {
                                            unionTransaction.Start();
                                            try
                                            {
                                                FamilyInstance union = document.Create.NewUnionFitting(first, second);
                                                if (union == null || !union.IsValidObject)
                                                {
                                                    throw new InvalidOperationException(
                                                        "Revit did not create a union fitting.");
                                                }
                                                unionTransaction.Commit();
                                                sourceCreatedUnions++;
                                                document.Regenerate();
                                            }
                                            catch
                                            {
                                                try
                                                {
                                                    unionTransaction.RollBack();
                                                }
                                                catch
                                                {
                                                    // The source subtransaction still protects the split operation.
                                                }
                                                sourceFailedUnions++;
                                            }
                                        }
                                    }
                                }

                                sourceTransaction.Commit();
                                splitSources++;
                                createdSegments += actualBreakPoints.Count;
                                createdUnions += sourceCreatedUnions;
                                failedUnions += sourceFailedUnions;
                                alreadyConnectedBreaks += sourceAlreadyConnectedBreaks;
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    sourceTransaction.RollBack();
                                }
                                catch
                                {
                                    // The parent transaction will still protect the document.
                                }
                                failedSources++;
                                SetFirstFailure(ref firstFailure, source, ex.Message);
                            }
                        }
                    }
                    transaction.Commit();
                }

                string result = "MEP curves split: " + splitSources + "." +
                    "\nNew segments: " + createdSegments + ".";
                if (unchangedSources > 0)
                {
                    result += "\nNo split required / unsupported after selection: " + unchangedSources + ".";
                }
                if (failedSources > 0)
                {
                    result += "\nCurves not split: " + failedSources + ".";
                }
                if (insulatedSourcesSkipped > 0)
                {
                    result += "\nInsulated/lined curves skipped: " + insulatedSourcesSkipped +
                        " (remove insulation/lining, split, then reapply it).";
                }
                if (dialog.InsertUnions)
                {
                    result += "\nUnion fittings created: " + createdUnions + ".";
                    if (alreadyConnectedBreaks > 0)
                    {
                        result += "\nBreaks already connected: " + alreadyConnectedBreaks + ".";
                    }
                    if (failedUnions > 0)
                    {
                        result += "\nUnion fittings not created: " + failedUnions +
                            " (check the routing preferences for the selected type).";
                    }
                }
                if (!string.IsNullOrEmpty(firstFailure))
                {
                    result += "\n\nFirst failure: " + firstFailure;
                }
                TaskDialog.Show("Tung MEP - MEP Split", result);
                return Result.Succeeded;
            }
        }

        private static List<ElementId> CollectPreselectedSources(
            Document document,
            UIDocument uiDocument)
        {
            return uiDocument.Selection.GetElementIds()
                .Where(id => IsSupportedSource(document.GetElement(id)))
                .GroupBy(id => id.Value)
                .Select(group => group.First())
                .ToList();
        }

        private static bool IsSupportedSource(Element element)
        {
            Line line;
            return TryGetSupportedLine(element, out line);
        }

        private static bool HasInsulationOrLining(Document document, ElementId elementId)
        {
            try
            {
                return InsulationLiningBase.GetInsulationIds(document, elementId).Count > 0 ||
                    InsulationLiningBase.GetLiningIds(document, elementId).Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetSupportedLine(Element element, out Line line)
        {
            line = null;
            if (!(element is Pipe) && !(element is Duct))
            {
                return false;
            }
            if (element.Pinned || element.GroupId != ElementId.InvalidElementId)
            {
                return false;
            }
            LocationCurve location = element.Location as LocationCurve;
            line = location == null ? null : location.Curve as Line;
            return line != null && line.IsBound && line.Length > 1e-8;
        }

        private static List<XYZ> BuildBreakPoints(
            Line line,
            MepSplitMode mode,
            double requestedLength,
            int quantity,
            double minimumSegmentLength)
        {
            double totalLength = line.Length;
            var stations = new List<double>();
            if (mode == MepSplitMode.Quantity)
            {
                if (quantity < 2)
                {
                    return new List<XYZ>();
                }
                if (totalLength / quantity < minimumSegmentLength)
                {
                    throw new InvalidOperationException(
                        "The requested quantity would create segments shorter than 10 mm.");
                }
                if (quantity > MaximumPiecesPerCurve)
                {
                    throw new InvalidOperationException(
                        "A maximum of " + MaximumPiecesPerCurve + " pieces per curve is supported.");
                }
                double pieceLength = totalLength / quantity;
                for (int index = 1; index < quantity; index++)
                {
                    stations.Add(pieceLength * index);
                }
            }
            else if (mode == MepSplitMode.MaximumLength)
            {
                if (requestedLength <= 0.0)
                {
                    throw new InvalidOperationException("Maximum length must be greater than zero.");
                }
                int pieces = (int)Math.Ceiling((totalLength / requestedLength) - 1e-10);
                if (pieces <= 1)
                {
                    return new List<XYZ>();
                }
                if (pieces > MaximumPiecesPerCurve)
                {
                    throw new InvalidOperationException(
                        "The maximum length would create more than " +
                        MaximumPiecesPerCurve + " pieces on one curve.");
                }
                double equalLength = totalLength / pieces;
                if (equalLength < minimumSegmentLength)
                {
                    throw new InvalidOperationException(
                        "The requested maximum length would create segments shorter than 10 mm.");
                }
                for (int index = 1; index < pieces; index++)
                {
                    stations.Add(equalLength * index);
                }
            }
            else
            {
                if (requestedLength < minimumSegmentLength)
                {
                    throw new InvalidOperationException("Exact length must be at least 10 mm.");
                }
                for (double station = requestedLength;
                    station < totalLength - minimumSegmentLength;
                    station += requestedLength)
                {
                    stations.Add(station);
                    if (stations.Count >= MaximumPiecesPerCurve - 1)
                    {
                        throw new InvalidOperationException(
                            "The exact length would create more than " +
                            MaximumPiecesPerCurve + " pieces on one curve.");
                    }
                }
            }

            XYZ start = line.GetEndPoint(0);
            XYZ direction = (line.GetEndPoint(1) - start).Normalize();
            return stations
                .OrderByDescending(station => station)
                .Select(station => start + (direction * station))
                .ToList();
        }

        private static bool TryFindSegmentAtPoint(
            Document document,
            IList<ElementId> liveSegmentIds,
            XYZ plannedPoint,
            double minimumEndDistance,
            double pointTolerance,
            out ElementId segmentId,
            out XYZ pointOnSegment)
        {
            segmentId = ElementId.InvalidElementId;
            pointOnSegment = null;
            double bestDistance = double.MaxValue;
            foreach (ElementId candidateId in liveSegmentIds)
            {
                Element candidate = document.GetElement(candidateId);
                Line line;
                if (!TryGetSupportedLine(candidate, out line))
                {
                    continue;
                }
                IntersectionResult projection = line.Project(plannedPoint);
                if (projection == null || projection.XYZPoint == null)
                {
                    continue;
                }
                XYZ projected = projection.XYZPoint;
                double distance = projected.DistanceTo(plannedPoint);
                if (distance > pointTolerance ||
                    projected.DistanceTo(line.GetEndPoint(0)) < minimumEndDistance ||
                    projected.DistanceTo(line.GetEndPoint(1)) < minimumEndDistance)
                {
                    continue;
                }
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    segmentId = candidateId;
                    pointOnSegment = projected;
                }
            }
            return segmentId != ElementId.InvalidElementId && pointOnSegment != null;
        }

        private static ElementId BreakCurve(Document document, Element segment, XYZ point)
        {
            if (segment is Pipe)
            {
                return PlumbingUtils.BreakCurve(document, segment.Id, point);
            }
            if (segment is Duct)
            {
                return MechanicalUtils.BreakCurve(document, segment.Id, point);
            }
            throw new InvalidOperationException("Only Pipe and Duct elements are supported.");
        }

        private static bool TryFindConnectorPair(
            Document document,
            IList<ElementId> liveSegmentIds,
            XYZ splitPoint,
            double tolerance,
            out Connector first,
            out Connector second,
            out bool alreadyConnected)
        {
            first = null;
            second = null;
            alreadyConnected = false;
            var connectors = new List<Connector>();
            foreach (ElementId id in liveSegmentIds)
            {
                MEPCurve curve = document.GetElement(id) as MEPCurve;
                if (curve == null || curve.ConnectorManager == null)
                {
                    continue;
                }
                foreach (Connector connector in curve.ConnectorManager.Connectors)
                {
                    if (connector != null &&
                        connector.ConnectorType == ConnectorType.End &&
                        connector.Origin.DistanceTo(splitPoint) <= tolerance)
                    {
                        connectors.Add(connector);
                    }
                }
            }

            double bestScore = double.MaxValue;
            for (int firstIndex = 0; firstIndex < connectors.Count; firstIndex++)
            {
                Connector left = connectors[firstIndex];
                for (int secondIndex = firstIndex + 1; secondIndex < connectors.Count; secondIndex++)
                {
                    Connector right = connectors[secondIndex];
                    if (left.Owner == null || right.Owner == null ||
                        left.Owner.Id.Value == right.Owner.Id.Value ||
                        left.Domain != right.Domain ||
                        left.Shape != right.Shape ||
                        left.Origin.DistanceTo(right.Origin) > tolerance)
                    {
                        continue;
                    }
                    try
                    {
                        if (left.IsConnectedTo(right))
                        {
                            alreadyConnected = true;
                            return false;
                        }
                    }
                    catch
                    {
                        // Continue with the open-connector checks below.
                    }
                    if (left.IsConnected || right.IsConnected)
                    {
                        continue;
                    }

                    double alignmentPenalty = GetAlignmentPenalty(left, right);
                    if (alignmentPenalty >= 1.5)
                    {
                        continue;
                    }
                    double score = left.Origin.DistanceTo(splitPoint) +
                        right.Origin.DistanceTo(splitPoint) +
                        left.Origin.DistanceTo(right.Origin) +
                        (alignmentPenalty * tolerance);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        first = left;
                        second = right;
                    }
                }
            }
            return first != null && second != null;
        }

        private static double GetAlignmentPenalty(Connector first, Connector second)
        {
            try
            {
                XYZ firstDirection = first.CoordinateSystem.BasisZ.Normalize();
                XYZ secondDirection = second.CoordinateSystem.BasisZ.Normalize();
                return Math.Abs(firstDirection.DotProduct(secondDirection) + 1.0);
            }
            catch
            {
                return 0.0;
            }
        }

        private static void SetFirstFailure(ref string firstFailure, Element source, string detail)
        {
            if (!string.IsNullOrEmpty(firstFailure))
            {
                return;
            }
            string label = source == null ? "MEP curve" : "Element " + source.Id.Value;
            firstFailure = label + ": " + detail;
        }

        private sealed class MepSplitSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return IsSupportedSource(element);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }
    }

    internal enum MepSplitMode
    {
        MaximumLength,
        ExactLength,
        Quantity
    }

    internal sealed class MepSplitForm : Form
    {
        private readonly ComboBox _mode;
        private readonly Label _lengthLabel;
        private readonly NumericUpDown _length;
        private readonly Label _quantityLabel;
        private readonly NumericUpDown _quantity;
        private readonly CheckBox _insertUnions;
        private readonly Label _description;

        public MepSplitForm()
        {
            Text = "Tung MEP - MEP Split";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(445, 260);

            Controls.Add(new Label
            {
                AutoSize = true,
                Location = new Point(18, 24),
                Text = "Split method"
            });
            _mode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(178, 20),
                Size = new Size(240, 24)
            };
            _mode.Items.Add("Maximum length (equal parts)");
            _mode.Items.Add("Exact length");
            _mode.Items.Add("Quantity");
            _mode.SelectedIndex = 0;
            Controls.Add(_mode);

            _lengthLabel = new Label
            {
                AutoSize = true,
                Location = new Point(18, 68),
                Text = "Maximum length (mm)"
            };
            Controls.Add(_lengthLabel);
            _length = new NumericUpDown
            {
                Location = new Point(178, 64),
                Size = new Size(240, 24),
                DecimalPlaces = 0,
                Minimum = 10,
                Maximum = 1000000,
                Value = 3000,
                ThousandsSeparator = true
            };
            Controls.Add(_length);

            _quantityLabel = new Label
            {
                AutoSize = true,
                Location = new Point(18, 68),
                Text = "Number of pieces"
            };
            Controls.Add(_quantityLabel);
            _quantity = new NumericUpDown
            {
                Location = new Point(178, 64),
                Size = new Size(240, 24),
                Minimum = 2,
                Maximum = MepSplitCommand.MaximumPiecesPerCurve,
                Value = 2
            };
            Controls.Add(_quantity);

            _insertUnions = new CheckBox
            {
                AutoSize = true,
                Location = new Point(178, 108),
                Text = "Insert union fittings at split points",
                Checked = true
            };
            Controls.Add(_insertUnions);

            _description = new Label
            {
                AutoSize = false,
                Location = new Point(18, 143),
                Size = new Size(400, 42)
            };
            Controls.Add(_description);

            var split = new Button
            {
                Text = "Split",
                DialogResult = DialogResult.OK,
                Location = new Point(250, 214),
                Size = new Size(80, 28)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(338, 214),
                Size = new Size(80, 28)
            };
            Controls.Add(split);
            Controls.Add(cancel);
            AcceptButton = split;
            CancelButton = cancel;

            _mode.SelectedIndexChanged += delegate { UpdateMode(); };
            UpdateMode();
        }

        public MepSplitMode Mode { get { return (MepSplitMode)_mode.SelectedIndex; } }
        public double LengthMm { get { return Decimal.ToDouble(_length.Value); } }
        public int Quantity { get { return Decimal.ToInt32(_quantity.Value); } }
        public bool InsertUnions { get { return _insertUnions.Checked; } }

        private void UpdateMode()
        {
            bool quantityMode = Mode == MepSplitMode.Quantity;
            _lengthLabel.Visible = !quantityMode;
            _length.Visible = !quantityMode;
            _quantityLabel.Visible = quantityMode;
            _quantity.Visible = quantityMode;
            if (Mode == MepSplitMode.MaximumLength)
            {
                _lengthLabel.Text = "Maximum length (mm)";
                _description.Text =
                    "Each curve is divided into equal pieces, none longer than the maximum.";
            }
            else if (Mode == MepSplitMode.ExactLength)
            {
                _lengthLabel.Text = "Exact length (mm)";
                _description.Text =
                    "Pieces use the exact length from the curve start; the remainder stays at the end.";
            }
            else
            {
                _description.Text = "Each curve is divided into the specified number of equal pieces.";
            }
        }
    }
}
