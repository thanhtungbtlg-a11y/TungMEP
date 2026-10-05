using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;
using View = Autodesk.Revit.DB.View;

namespace TungMEP.Modules.Views
{
    [Transaction(TransactionMode.Manual)]
    public class DraftingToLegendCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document document = uiDocument == null ? null : uiDocument.Document;
            if (document == null)
            {
                return Result.Cancelled;
            }

            View source;
            Viewport sourceViewport;
            bool userCancelled;
            if (!TryResolveDraftingSource(
                uiDocument,
                document,
                out source,
                out sourceViewport,
                out userCancelled))
            {
                if (userCancelled)
                {
                    return Result.Cancelled;
                }
                TaskDialog.Show(
                    "Tung MEP - Drafting to Legend",
                    "Open a Sheet and click the Drafting View viewport to replace, or open the Drafting View itself and run the command.");
                return Result.Cancelled;
            }
            if (sourceViewport == null || !sourceViewport.IsValidObject)
            {
                TaskDialog.Show(
                    "Tung MEP - Drafting to Legend",
                    "The Drafting View '" + source.Name + "' is not placed on a Sheet.\n\n" +
                    "Place it on a Sheet first, then run this command to replace that viewport with a Legend.");
                return Result.Cancelled;
            }

            ViewSheet sheet = document.GetElement(sourceViewport.SheetId) as ViewSheet;
            if (sheet == null)
            {
                TaskDialog.Show("Tung MEP - Drafting to Legend", "The source Drafting Viewport is not on a valid Sheet.");
                return Result.Cancelled;
            }

            List<View> legends = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && view.ViewType == ViewType.Legend)
                .Where(view => CountViewOwnedElements(document, view.Id) == 0)
                .OrderByDescending(view => view.Name.Equals(
                    "TungMEP - Blank Legend",
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(view => view.Name)
                .ToList();
            if (legends.Count == 0)
            {
                TaskDialog.Show(
                    "Tung MEP - Drafting to Legend",
                    "This project has no blank Legend to use as an API seed.\n\n" +
                    "Revit API cannot create a Legend directly. Create one empty Legend once with " +
                    "View > Legends > Legend, name it 'TungMEP - Blank Legend', leave it empty, then run this tool. " +
                    "The blank seed and all existing Legends will remain unchanged.");
                return Result.Cancelled;
            }

            var existingNames = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(view => view.Id.Value != source.Id.Value)
                    .Select(view => view.Name),
                StringComparer.OrdinalIgnoreCase);
            using (var dialog = new LegendCreateForm(
                source.Name,
                source.Scale,
                existingNames,
                legends[0].Name))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return Result.Cancelled;
                }

                View legend = null;
                int copied = 0;
                int skipped = 0;
                string renamedSourceName = null;
                ViewportPlacementState placement = CaptureViewportPlacement(sourceViewport);
                ElementId replacementViewportId = ElementId.InvalidElementId;
                try
                {
                    using (var transaction = new Transaction(document, "Tung MEP - Drafting to Legend"))
                    {
                        transaction.Start();
                        ElementId legendId = legends[0].Duplicate(ViewDuplicateOption.Duplicate);
                        legend = document.GetElement(legendId) as View;
                        if (legend == null || !legend.IsValidObject)
                        {
                            throw new InvalidOperationException("Revit did not return a valid duplicated Legend view.");
                        }

                        if (string.Equals(
                            dialog.LegendName,
                            source.Name,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            renamedSourceName = MakeUniqueViewName(
                                document,
                                source.Name + " - Drafting",
                                source.Id);
                            source.Name = renamedSourceName;
                        }
                        legend.Name = dialog.LegendName;
                        legend.Scale = dialog.LegendScale;

                        List<ElementId> sourceIds = CollectCopyableContents(document, source.Id);
                        ICollection<ElementId> copiedIds;
                        if (TryCopyAll(document, source, legend, sourceIds, out copiedIds))
                        {
                            copied = copiedIds.Count;
                        }
                        else
                        {
                            foreach (ElementId sourceId in sourceIds)
                            {
                                ICollection<ElementId> individual;
                                if (TryCopyAll(
                                    document,
                                    source,
                                    legend,
                                    new List<ElementId> { sourceId },
                                    out individual))
                                {
                                    copied += individual.Count;
                                }
                                else
                                {
                                    skipped++;
                                }
                            }
                        }

                        if (skipped > 0)
                        {
                            throw new InvalidOperationException(
                                skipped + " Drafting View element(s) are not supported in a Legend. " +
                                "The Sheet viewport was kept unchanged.");
                        }
                        if (!Viewport.CanAddViewToSheet(document, sheet.Id, legend.Id))
                        {
                            throw new InvalidOperationException("Revit cannot place the new Legend on Sheet " + sheet.SheetNumber + ".");
                        }

                        document.Regenerate();
                        Viewport replacementViewport = Viewport.Create(
                            document,
                            sheet.Id,
                            legend.Id,
                            placement.BoxCenter);
                        ApplyViewportPlacement(replacementViewport, placement);
                        replacementViewportId = replacementViewport.Id;

                        if (sourceViewport.Pinned)
                        {
                            sourceViewport.Pinned = false;
                        }
                        document.Delete(sourceViewport.Id);
                        document.Regenerate();
                        replacementViewport = document.GetElement(replacementViewportId) as Viewport;
                        if (replacementViewport == null || !replacementViewport.IsValidObject)
                        {
                            throw new InvalidOperationException("The replacement Legend viewport became invalid.");
                        }
                        replacementViewport.SetBoxCenter(placement.BoxCenter);
                        replacementViewport.Pinned = placement.WasPinned;
                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    message = string.Empty;
                    TaskDialog.Show(
                        "Tung MEP - Drafting to Legend",
                        "Could not safely replace the Drafting View. No partial Legend was kept, and the original Sheet placement remains.\n\n" +
                        ex.Message);
                    return Result.Cancelled;
                }

                bool copiedToClipboard = false;
                try
                {
                    Clipboard.SetText(legend.Name);
                    copiedToClipboard = true;
                }
                catch
                {
                    // Clipboard can be temporarily locked by another Windows application.
                }

                uiDocument.ActiveView = sheet;
                if (replacementViewportId != ElementId.InvalidElementId)
                {
                    uiDocument.Selection.SetElementIds(new List<ElementId> { replacementViewportId });
                }
                string result = "Created Legend: " + legend.Name +
                    "\nBlank seed used: " + legends[0].Name +
                    "\nScale: 1:" + legend.Scale +
                    "\nCopied elements: " + copied +
                    "\nReplaced on Sheet: " + sheet.SheetNumber + " - " + sheet.Name;
                if (!string.IsNullOrEmpty(renamedSourceName))
                {
                    result += "\nSource Drafting View renamed: " + renamedSourceName;
                }
                result += copiedToClipboard
                    ? "\nLegend name copied to clipboard."
                    : "\nThe Legend was created, but Windows Clipboard was busy.";
                result += "\n\nThe blank seed and source Drafting View were kept; only the old Sheet viewport was replaced.";
                TaskDialog.Show("Tung MEP - Drafting to Legend", result);
                return Result.Succeeded;
            }
        }

        private static bool TryResolveDraftingSource(
            UIDocument uiDocument,
            Document document,
            out View source,
            out Viewport sourceViewport,
            out bool userCancelled)
        {
            source = null;
            sourceViewport = null;
            userCancelled = false;

            foreach (ElementId selectedId in uiDocument.Selection.GetElementIds())
            {
                Viewport selectedViewport = document.GetElement(selectedId) as Viewport;
                if (TryGetDraftingView(document, selectedViewport, out source))
                {
                    sourceViewport = selectedViewport;
                    return true;
                }

                View selectedView = document.GetElement(selectedId) as View;
                if (IsDraftingView(selectedView))
                {
                    source = selectedView;
                    sourceViewport = FindPlacedViewport(document, source.Id);
                    return true;
                }
            }

            View activeView = document.ActiveView;
            if (IsDraftingView(activeView))
            {
                source = activeView;
                sourceViewport = FindPlacedViewport(document, source.Id);
                return true;
            }

            if (activeView is ViewSheet)
            {
                try
                {
                    Reference picked = uiDocument.Selection.PickObject(
                        ObjectType.Element,
                        new DraftingViewportSelectionFilter(document),
                        "Click the Drafting View viewport to convert and replace");
                    sourceViewport = document.GetElement(picked.ElementId) as Viewport;
                    return TryGetDraftingView(document, sourceViewport, out source);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    userCancelled = true;
                    return false;
                }
            }
            return false;
        }

        private static bool TryGetDraftingView(
            Document document,
            Viewport viewport,
            out View draftingView)
        {
            draftingView = viewport == null ? null : document.GetElement(viewport.ViewId) as View;
            return IsDraftingView(draftingView);
        }

        private static bool IsDraftingView(View view)
        {
            return view != null && !view.IsTemplate && view.ViewType == ViewType.DraftingView;
        }

        private static Viewport FindPlacedViewport(Document document, ElementId viewId)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .FirstOrDefault(viewport => viewport.ViewId.Value == viewId.Value);
        }

        private static ViewportPlacementState CaptureViewportPlacement(Viewport viewport)
        {
            var state = new ViewportPlacementState
            {
                BoxCenter = viewport.GetBoxCenter(),
                TypeId = viewport.GetTypeId(),
                Rotation = viewport.Rotation,
                WasPinned = viewport.Pinned
            };
            try
            {
                state.LabelOffset = viewport.LabelOffset;
                state.LabelLineLength = viewport.LabelLineLength;
                state.HasLabelPlacement = true;
            }
            catch
            {
                state.HasLabelPlacement = false;
            }
            return state;
        }

        private static void ApplyViewportPlacement(
            Viewport viewport,
            ViewportPlacementState placement)
        {
            if (placement.TypeId != ElementId.InvalidElementId &&
                viewport.GetTypeId().Value != placement.TypeId.Value)
            {
                try
                {
                    viewport.ChangeTypeId(placement.TypeId);
                }
                catch
                {
                    // Keep the project's default Legend viewport type if the source type is incompatible.
                }
            }
            viewport.Rotation = placement.Rotation;
            viewport.SetBoxCenter(placement.BoxCenter);
            if (placement.HasLabelPlacement)
            {
                try
                {
                    viewport.LabelOffset = placement.LabelOffset;
                    viewport.LabelLineLength = placement.LabelLineLength;
                }
                catch
                {
                    // A viewport type without a title has no visible label placement to preserve.
                }
            }
        }

        private static string MakeUniqueViewName(
            Document document,
            string suggestedName,
            ElementId excludedViewId)
        {
            var names = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(view => excludedViewId == null || view.Id.Value != excludedViewId.Value)
                    .Select(view => view.Name),
                StringComparer.OrdinalIgnoreCase);
            string candidate = suggestedName;
            int suffix = 2;
            while (names.Contains(candidate))
            {
                candidate = suggestedName + " " + suffix;
                suffix++;
            }
            return candidate;
        }

        private static int CountViewOwnedElements(Document document, ElementId viewId)
        {
            return CollectUserContentIds(document, viewId).Count;
        }

        private static List<ElementId> CollectUserContentIds(Document document, ElementId viewId)
        {
            return new FilteredElementCollector(document, viewId)
                .WhereElementIsNotElementType()
                .Where(element => element.ViewSpecific && element.OwnerViewId == viewId)
                .Where(element => element.Id.Value != viewId.Value)
                .Where(element => !(element is View) && !(element is SketchPlane))
                .Where(element => element.Category != null)
                .Select(element => element.Id)
                .ToList();
        }

        private static List<ElementId> CollectCopyableContents(Document document, ElementId viewId)
        {
            return new FilteredElementCollector(document, viewId)
                .WhereElementIsNotElementType()
                .Where(element => element.ViewSpecific && element.OwnerViewId == viewId)
                .Where(element => element.Category != null)
                .Where(element => element.GroupId == ElementId.InvalidElementId || element is Group)
                .Where(element => !(element is SketchPlane) && !(element is View))
                .Select(element => element.Id)
                .ToList();
        }

        private static bool TryCopyAll(
            Document document,
            View source,
            View destination,
            ICollection<ElementId> sourceIds,
            out ICollection<ElementId> copiedIds)
        {
            copiedIds = new List<ElementId>();
            if (sourceIds == null || sourceIds.Count == 0)
            {
                return true;
            }
            using (var subTransaction = new SubTransaction(document))
            {
                subTransaction.Start();
                try
                {
                    copiedIds = ElementTransformUtils.CopyElements(
                        source,
                        sourceIds,
                        destination,
                        Autodesk.Revit.DB.Transform.Identity,
                        new CopyPasteOptions());
                    subTransaction.Commit();
                    return true;
                }
                catch
                {
                    subTransaction.RollBack();
                    copiedIds = new List<ElementId>();
                    return false;
                }
            }
        }

        private sealed class DraftingViewportSelectionFilter : ISelectionFilter
        {
            private readonly Document _document;

            public DraftingViewportSelectionFilter(Document document)
            {
                _document = document;
            }

            public bool AllowElement(Element element)
            {
                View draftingView;
                return TryGetDraftingView(_document, element as Viewport, out draftingView);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return false;
            }
        }

        private sealed class ViewportPlacementState
        {
            public XYZ BoxCenter { get; set; }
            public ElementId TypeId { get; set; }
            public ViewportRotation Rotation { get; set; }
            public bool WasPinned { get; set; }
            public bool HasLabelPlacement { get; set; }
            public XYZ LabelOffset { get; set; }
            public double LabelLineLength { get; set; }
        }
    }

    internal class LegendCreateForm : Form
    {
        private readonly TextBox _nameInput;
        private readonly ComboBox _scaleInput;
        private readonly HashSet<string> _existingNames;

        public LegendCreateForm(
            string suggestedName,
            int currentScale,
            HashSet<string> existingNames,
            string seedLegendName)
        {
            _existingNames = existingNames;
            Text = "Tung MEP - New Legend";
            Width = 470;
            Height = 225;
            MinimumSize = new Size(470, 225);
            MaximumSize = new Size(470, 225);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 4
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _nameInput = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = MakeUniqueName(suggestedName)
            };
            _scaleInput = new ComboBox
            {
                Dock = DockStyle.Left,
                Width = 150,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            int[] standardScales =
            {
                1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 250, 500, 1000
            };
            foreach (int scale in standardScales.Concat(new[] { Math.Max(1, currentScale) }).Distinct().OrderBy(value => value))
            {
                _scaleInput.Items.Add(new ScaleItem(scale));
            }
            _scaleInput.SelectedItem = _scaleInput.Items
                .Cast<ScaleItem>()
                .First(item => item.Value == Math.Max(1, currentScale));

            layout.Controls.Add(new Label { Text = "Blank seed", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                TabStop = false,
                Text = seedLegendName
            }, 1, 0);
            layout.Controls.Add(new Label { Text = "Name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            layout.Controls.Add(_nameInput, 1, 1);
            layout.Controls.Add(new Label { Text = "Scale", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            layout.Controls.Add(_scaleInput, 1, 2);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill
            };
            var ok = new Button { Text = "OK", Width = 85 };
            var cancel = new Button { Text = "Cancel", Width = 85, DialogResult = DialogResult.Cancel };
            ok.Click += delegate
            {
                string name = _nameInput.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show(this, "Enter a Legend name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (_existingNames.Contains(name))
                {
                    MessageBox.Show(this, "A view with this name already exists.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _nameInput.Focus();
                    _nameInput.SelectAll();
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 3);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string LegendName
        {
            get { return _nameInput.Text.Trim(); }
        }

        public int LegendScale
        {
            get { return ((ScaleItem)_scaleInput.SelectedItem).Value; }
        }

        private string MakeUniqueName(string suggested)
        {
            string candidate = suggested;
            int suffix = 2;
            while (_existingNames.Contains(candidate))
            {
                candidate = suggested + " " + suffix;
                suffix++;
            }
            return candidate;
        }

        private class ScaleItem
        {
            public ScaleItem(int value)
            {
                Value = value;
            }

            public int Value { get; private set; }

            public override string ToString()
            {
                return "1 : " + Value;
            }
        }
    }
}
