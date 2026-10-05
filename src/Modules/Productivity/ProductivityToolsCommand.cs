using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;

namespace TungMEP.Modules.Productivity
{
    [Transaction(TransactionMode.Manual)]
    public class ProductivityToolsCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            var tools = new List<ProductivityToolDefinition>
            {
                new ProductivityToolDefinition(
                    "Align Objects by BOP",
                    "Move selected Pipes, Ducts, Conduits, Cable Trays, Sprinklers, Plumbing Fixtures, and other MEP families vertically to one common bottom elevation.",
                    "TungMEP.Modules.MepTools.AlignObjectsByBopCommand"),
                new ProductivityToolDefinition(
                    "Equal Spacing",
                    "Distribute three or more selected model objects at equal center spacing along view X, view Y, world Z, or the automatically detected row direction.",
                    "TungMEP.Modules.MepTools.EqualSpacingCommand"),
                new ProductivityToolDefinition(
                    "Grid Bubble Arrange",
                    "Automatically stagger overlapping visible Grid bubbles with view-specific leaders while keeping the model Grid lines and Grid references fixed.",
                    "TungMEP.Modules.Grids.GridBubbleArrangeCommand"),
                new ProductivityToolDefinition(
                    "VG Filter from Selection",
                    "Create a native Revit Parameter Filter from selected categories and the current value of any common filterable property, with optional active-view VG application.",
                    "TungMEP.Modules.Productivity.VgFilterFromSelectionCommand"),
                new ProductivityToolDefinition(
                    "Schedule from Selection",
                    "Create and open a native Revit Schedule for one selected category, using any common same-value property as an optional schedule filter.",
                    "TungMEP.Modules.Productivity.ScheduleFromSelectionCommand"),
                new ProductivityToolDefinition(
                    "Auto Dimension",
                    "Create native Revit dimension chains by scan line or use the smart sprinkler preset.",
                    "TungMEP.Modules.Dimensions.AutoDimensionCommand"),
                new ProductivityToolDefinition(
                    "Smart Auto Tag MEP",
                    "Create native Pipe tags, update an optional BOP-FFL parameter, and arrange short leaders in clear lanes without moving existing tags.",
                    "TungMEP.Modules.Tags.AutoTagCommand"),
                new ProductivityToolDefinition(
                    "Universal Auto Tag MEP",
                    "Tag multiple family-based MEP categories in one run, choose a native Tag Family Type per category, avoid duplicates, and arrange leaders in clear lanes.",
                    "TungMEP.Modules.Tags.UniversalMepAutoTagCommand"),
                new ProductivityToolDefinition(
                    "Smart Annotation Arrange",
                    "Align, distribute, compact, smart-rail, or reflow existing Independent Tags and Text Notes with customizable leader and spacing rules.",
                    "TungMEP.Modules.Tags.AnnotationArrangeCommand"),
                new ProductivityToolDefinition(
                    "Smart MEP Annotation Pack",
                    "Tag Pipes, Ducts, and Mechanical Equipment; group matching parallel runs into native multi-leader tags and arrange them on model-aware smart rails.",
                    "TungMEP.Modules.Tags.SmartMepAnnotationCommand"),
                new ProductivityToolDefinition(
                    "MEP SLIPT BY LEVEL",
                    "Split straight Pipe, Duct, Conduit, and Cable Tray elements at selected Level planes while protecting fittings and accessories touching those boundaries.",
                    "TungMEP.Modules.MepTools.MepSplitCommand"),
                new ProductivityToolDefinition(
                    "Batch View Copy",
                    "Duplicate multiple views with naming rules and native Revit duplicate modes.",
                    "TungMEP.Modules.Views.BatchViewCopyCommand"),
                new ProductivityToolDefinition(
                    "View & Sheet Automation",
                    "Preview final View names, Sheet numbers, Sheet names, and viewport batches before creating Plan, RCP, and zone-based Section outputs.",
                    "TungMEP.Modules.Views.ViewSheetAutomationCommand"),
                new ProductivityToolDefinition(
                    "Sheet Renumber",
                    "Use preselected Sheets, browse/search a hierarchical Sheet tree, and preview safe batch numbering with prefix, suffix, padding, start, and increment.",
                    "TungMEP.Modules.Views.SheetRenumberCommand")
            };

            using (var dialog = new ProductivityToolsForm(
                tools,
                ProductivityToolHistoryStore.Load()))
            {
                if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedTool == null)
                {
                    return Result.Cancelled;
                }

                Type commandType = Assembly.GetExecutingAssembly().GetType(
                    dialog.SelectedTool.CommandTypeName,
                    false);
                IExternalCommand command = commandType == null
                    ? null
                    : Activator.CreateInstance(commandType) as IExternalCommand;
                if (command == null)
                {
                    message = "This TungMEP tool is not available in the current payload: " +
                        dialog.SelectedTool.Name;
                    return Result.Failed;
                }
                Result result = command.Execute(commandData, ref message, elements);
                if (result != Result.Failed)
                {
                    ProductivityToolHistoryStore.Save(
                        dialog.SelectedTool.CommandTypeName);
                }
                return result;
            }
        }
    }

    internal sealed class ProductivityToolDefinition
    {
        public ProductivityToolDefinition(string name, string description, string commandTypeName)
        {
            Name = name;
            Description = description;
            CommandTypeName = commandTypeName;
        }

        public string Name { get; private set; }
        public string Description { get; private set; }
        public string CommandTypeName { get; private set; }

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class ProductivityToolsForm : Form
    {
        private readonly ListBox _tools;
        private readonly Label _description;
        private readonly Button _run;

        public ProductivityToolsForm(
            IList<ProductivityToolDefinition> tools,
            string initialCommandTypeName)
        {
            Text = "Tung MEP - More Tools";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 340);

            Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Location = new Point(16, 14),
                Text = "Productivity tools"
            });

            _tools = new ListBox
            {
                Location = new Point(16, 43),
                Size = new Size(220, 238),
                IntegralHeight = false
            };
            foreach (ProductivityToolDefinition tool in tools)
            {
                _tools.Items.Add(tool);
            }
            _tools.SelectedIndexChanged += delegate { UpdateSelection(); };
            _tools.DoubleClick += delegate
            {
                if (_tools.SelectedItem != null)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            Controls.Add(_tools);

            _description = new Label
            {
                AutoSize = false,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(250, 43),
                Padding = new Padding(12),
                Size = new Size(292, 238)
            };
            Controls.Add(_description);

            _run = new Button
            {
                Text = "Run",
                DialogResult = DialogResult.OK,
                Enabled = false,
                Location = new Point(374, 298),
                Size = new Size(80, 28)
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(462, 298),
                Size = new Size(80, 28)
            };
            Controls.Add(_run);
            Controls.Add(cancel);
            AcceptButton = _run;
            CancelButton = cancel;

            int initialIndex = -1;
            if (!string.IsNullOrEmpty(initialCommandTypeName))
            {
                for (int index = 0; index < _tools.Items.Count; index++)
                {
                    ProductivityToolDefinition tool =
                        _tools.Items[index] as ProductivityToolDefinition;
                    if (tool != null && string.Equals(
                        tool.CommandTypeName,
                        initialCommandTypeName,
                        StringComparison.Ordinal))
                    {
                        initialIndex = index;
                        break;
                    }
                }
            }
            if (_tools.Items.Count > 0)
            {
                _tools.SelectedIndex = initialIndex >= 0 ? initialIndex : 0;
            }
        }

        public ProductivityToolDefinition SelectedTool
        {
            get { return _tools.SelectedItem as ProductivityToolDefinition; }
        }

        private void UpdateSelection()
        {
            ProductivityToolDefinition tool = SelectedTool;
            _description.Text = tool == null
                ? string.Empty
                : tool.Name + Environment.NewLine + Environment.NewLine + tool.Description;
            _run.Enabled = tool != null;
        }
    }
}
