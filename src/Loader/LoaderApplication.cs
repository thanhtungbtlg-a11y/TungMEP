using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace TungMEP.Loader
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                const string tabName = "Tung MEP";
                try
                {
                    application.CreateRibbonTab(tabName);
                }
                catch
                {
                    // The tab already exists in this Revit session.
                }

                RibbonPanel panel = application.GetRibbonPanels(tabName)
                    .FirstOrDefault(candidate => candidate.Name.Equals("Fire Protection", StringComparison.OrdinalIgnoreCase))
                    ?? application.CreateRibbonPanel(tabName, "Fire Protection");
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                AddButton(
                    panel,
                    "TungMEP_SprinklerBoundaryStyle",
                    "Setup\nStyles",
                    typeof(SetupStylesProxyCommand),
                    "Create Detail Line styles for the sprinkler boundary, main pipe, and branch routes.",
                    LoaderIcons.CreateSetupStylesIcon());
                AddButton(
                    panel,
                    "TungMEP_AutoSprinkler",
                    "Auto\nSprinkler",
                    typeof(AutoSprinklerProxyCommand),
                    "Place sprinkler heads and create DN25 branch pipes from the main guide.",
                    LoaderIcons.CreateAutoSprinklerIcon());

                RibbonPanel utilityPanel = application.GetRibbonPanels(tabName)
                    .FirstOrDefault(candidate => candidate.Name.Equals("Utilities", StringComparison.OrdinalIgnoreCase))
                    ?? application.CreateRibbonPanel(tabName, "Utilities");
                AddButton(
                    utilityPanel,
                    "TungMEP_GridMode",
                    "Grid\nMode",
                    typeof(GridModeProxyCommand),
                    "Toggle selected grids, or all grids visible in the active view, between 2D and 3D extents.",
                    LoaderIcons.CreateGridModeIcon());
                AddButton(
                    utilityPanel,
                    "TungMEP_DraftingToLegend",
                    "Draft to\nLegend",
                    typeof(DraftingToLegendProxyCommand),
                    "Click a Drafting Viewport on a Sheet, create an independent Legend, and replace it at the same Sheet position.",
                    LoaderIcons.CreateDraftingToLegendIcon());
                AddButton(
                    utilityPanel,
                    "TungMEP_ProductivityTools",
                    "More\nTools",
                    typeof(ProductivityToolsProxyCommand),
                    "Open TungMEP productivity tools. New payload tools can be added here without changing the ribbon loader.",
                    LoaderIcons.CreateProductivityToolsIcon());

                RibbonPanel controlPanel = application.GetRibbonPanels(tabName)
                    .FirstOrDefault(candidate => candidate.Name.Equals("TungMEP Control", StringComparison.OrdinalIgnoreCase))
                    ?? application.CreateRibbonPanel(tabName, "TungMEP Control");
                AddButton(
                    controlPanel,
                    "TungMEP_RepeatLast",
                    "Repeat Last\nTungMEP",
                    typeof(RepeatLastTungMepCommand),
                    "Run the most recently completed TungMEP command again without opening its launcher. Assign a Revit keyboard shortcut for fastest access.",
                    LoaderIcons.CreateRepeatLastIcon());
                AddButton(
                    controlPanel,
                    "TungMEP_Reload",
                    "Reload\nTungMEP",
                    typeof(ReloadPayloadCommand),
                    "Load the newest TungMEP payload built into the payload folder. Revit itself remains open.",
                    LoaderIcons.CreateReloadIcon());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Tung MEP", "Cannot initialize TungMEP loader:\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static void AddButton(
            RibbonPanel panel,
            string name,
            string text,
            Type commandType,
            string toolTip,
            ImageSource icon)
        {
            var data = new PushButtonData(name, text, Assembly.GetExecutingAssembly().Location, commandType.FullName)
            {
                ToolTip = toolTip
            };
            PushButton button = panel.AddItem(data) as PushButton;
            if (button != null)
            {
                button.LargeImage = icon;
                button.Image = icon;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SetupStylesProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.AutoSprinkler.CreateBoundaryStyleCommand",
                commandData,
                ref message,
                elements);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class AutoSprinklerProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.AutoSprinkler.AutoSprinklerCommand",
                commandData,
                ref message,
                elements);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class GridModeProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.Grids.GridModeCommand",
                commandData,
                ref message,
                elements);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class DraftingToLegendProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.Views.DraftingToLegendCommand",
                commandData,
                ref message,
                elements);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepAlignProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.MepTools.MepAlignCommand",
                commandData,
                ref message,
                elements);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class MepCopyProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.MepTools.MepCopyCommand",
                commandData,
                ref message,
                elements);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class QuickSelectProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.Selection.QuickSelectCommand",
                commandData,
                ref message,
                elements);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ProductivityToolsProxyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return PayloadRuntime.Execute(
                "TungMEP.Modules.Productivity.ProductivityToolsCommand",
                commandData,
                ref message,
                elements,
                false);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class RepeatLastTungMepCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string commandTypeName = LastTungMepCommandStore.Load();
            if (string.IsNullOrWhiteSpace(commandTypeName))
            {
                TaskDialog.Show(
                    "Tung MEP - Repeat Last",
                    "No previous TungMEP command has been recorded yet.\n\nRun a TungMEP tool once, then use Repeat Last TungMEP.");
                return Result.Cancelled;
            }
            return PayloadRuntime.Execute(
                commandTypeName,
                commandData,
                ref message,
                elements,
                false);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ReloadPayloadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string payloadPath;
            string error;
            if (!PayloadRuntime.Reload(out payloadPath, out error))
            {
                TaskDialog.Show("Tung MEP - Reload", "Could not load the latest TungMEP payload.\n\n" + error);
                return Result.Failed;
            }
            TaskDialog.Show("Tung MEP - Reload", "Loaded:\n" + Path.GetFileName(payloadPath) + "\n\nThe TungMEP command buttons now use this payload.");
            return Result.Succeeded;
        }
    }

    internal static class PayloadRuntime
    {
        private static readonly object SyncRoot = new object();
        private static Assembly _payloadAssembly;
        private static string _payloadPath;

        public static Result Execute(
            string commandTypeName,
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements,
            bool rememberCommand = true)
        {
            string path;
            string error;
            if (!EnsureLoaded(false, out path, out error))
            {
                message = error;
                return Result.Failed;
            }
            try
            {
                Type commandType = _payloadAssembly.GetType(commandTypeName, true);
                IExternalCommand command = Activator.CreateInstance(commandType) as IExternalCommand;
                if (command == null)
                {
                    message = "The payload command does not implement IExternalCommand: " + commandTypeName;
                    return Result.Failed;
                }
                Result result = command.Execute(commandData, ref message, elements);
                if (rememberCommand && result != Result.Failed)
                {
                    LastTungMepCommandStore.Save(commandTypeName);
                }
                return result;
            }
            catch (Exception ex)
            {
                message = "Could not execute " + commandTypeName + " from " + Path.GetFileName(path) + ":\n" + ex;
                return Result.Failed;
            }
        }

        public static bool Reload(out string payloadPath, out string error)
        {
            return EnsureLoaded(true, out payloadPath, out error);
        }

        private static bool EnsureLoaded(bool force, out string payloadPath, out string error)
        {
            payloadPath = ResolvePayloadPath();
            error = string.Empty;
            if (string.IsNullOrEmpty(payloadPath))
            {
                error = "No TungMEP payload was found beside the loader. Build or deploy a payload first.";
                return false;
            }
            lock (SyncRoot)
            {
                if (!force && _payloadAssembly != null &&
                    string.Equals(_payloadPath, payloadPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                try
                {
                    Assembly loaded = Assembly.LoadFrom(payloadPath);
                    if (loaded.GetType("TungMEP.Modules.AutoSprinkler.AutoSprinklerCommand", false) == null)
                    {
                        error = "The selected DLL is not a TungMEP payload: " + payloadPath;
                        return false;
                    }
                    _payloadAssembly = loaded;
                    _payloadPath = payloadPath;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        private static string ResolvePayloadPath()
        {
            string loaderDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string payloadDirectory = Path.Combine(loaderDirectory, "payload");
            string currentFile = Path.Combine(payloadDirectory, "current.txt");
            if (File.Exists(currentFile))
            {
                string fileName = File.ReadAllText(currentFile).Trim();
                string candidate = Path.Combine(payloadDirectory, fileName);
                if (string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
                    fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return Directory.Exists(payloadDirectory)
                ? Directory.GetFiles(payloadDirectory, "TungMEP.Payload.*.dll")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
        }
    }

    internal static class LastTungMepCommandStore
    {
        private const string SettingsDirectoryName = "TungMEP";
        private const string FileName = "LastCommand.txt";

        public static string Load()
        {
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                {
                    return string.Empty;
                }
                string value = File.ReadAllText(path).Trim();
                return IsAllowed(value) ? value : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void Save(string commandTypeName)
        {
            if (!IsAllowed(commandTypeName))
            {
                return;
            }
            try
            {
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, commandTypeName.Trim());
            }
            catch
            {
                // Command history must never block a TungMEP tool.
            }
        }

        private static bool IsAllowed(string commandTypeName)
        {
            return !string.IsNullOrWhiteSpace(commandTypeName) &&
                commandTypeName.StartsWith(
                    "TungMEP.Modules.",
                    StringComparison.Ordinal) &&
                commandTypeName.IndexOfAny(new[] { '\r', '\n' }) < 0;
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SettingsDirectoryName,
                FileName);
        }
    }

    internal static class LoaderIcons
    {
        private static readonly Brush FireRed = CreateBrush(220, 55, 42);
        private static readonly Brush DeepBlue = CreateBrush(20, 77, 125);

        public static ImageSource CreateAutoSprinklerIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                context.DrawEllipse(FireRed, new Pen(Brushes.White, 1.6), new Point(16, 10), 5.4, 5.4);
                context.DrawLine(new Pen(Brushes.White, 2.2), new Point(16, 15.5), new Point(16, 20));
                context.DrawLine(new Pen(Brushes.White, 1.6), new Point(8, 20), new Point(24, 20));
                context.DrawLine(new Pen(Brushes.White, 1.6), new Point(10, 20), new Point(7, 25));
                context.DrawLine(new Pen(Brushes.White, 1.6), new Point(16, 20), new Point(16, 26));
                context.DrawLine(new Pen(Brushes.White, 1.6), new Point(22, 20), new Point(25, 25));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateSetupStylesIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var pen = new Pen(Brushes.White, 2.2);
                context.DrawLine(pen, new Point(7, 9), new Point(25, 9));
                context.DrawLine(pen, new Point(7, 16), new Point(25, 16));
                context.DrawLine(pen, new Point(7, 23), new Point(19, 23));
                context.DrawEllipse(FireRed, new Pen(Brushes.White, 1), new Point(22.5, 23), 3.5, 3.5);
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateReloadIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var pen = new Pen(Brushes.White, 2.4);
                context.DrawLine(pen, new Point(10, 13), new Point(16, 7));
                context.DrawLine(pen, new Point(16, 7), new Point(23, 13));
                context.DrawLine(pen, new Point(23, 13), new Point(20, 13));
                context.DrawLine(pen, new Point(23, 13), new Point(23, 10));
                context.DrawLine(pen, new Point(22, 19), new Point(16, 25));
                context.DrawLine(pen, new Point(16, 25), new Point(9, 19));
                context.DrawLine(pen, new Point(9, 19), new Point(12, 19));
                context.DrawLine(pen, new Point(9, 19), new Point(9, 22));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateGridModeIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var gridPen = new Pen(Brushes.White, 1.8);
                context.DrawLine(gridPen, new Point(6, 10), new Point(26, 10));
                context.DrawLine(gridPen, new Point(6, 22), new Point(26, 22));
                context.DrawLine(gridPen, new Point(10, 6), new Point(10, 26));
                context.DrawLine(gridPen, new Point(22, 6), new Point(22, 26));
                context.DrawEllipse(FireRed, new Pen(Brushes.White, 1), new Point(10, 10), 3, 3);
                context.DrawEllipse(FireRed, new Pen(Brushes.White, 1), new Point(22, 22), 3, 3);
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateDraftingToLegendIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var whitePen = new Pen(Brushes.White, 1.8);
                context.DrawRectangle(null, whitePen, new Rect(5, 6, 9, 20));
                context.DrawRectangle(null, whitePen, new Rect(18, 6, 9, 20));
                context.DrawLine(whitePen, new Point(7, 10), new Point(12, 10));
                context.DrawLine(whitePen, new Point(7, 14), new Point(12, 14));
                context.DrawLine(whitePen, new Point(20, 10), new Point(25, 10));
                context.DrawLine(whitePen, new Point(20, 14), new Point(25, 14));
                var arrowPen = new Pen(FireRed, 2.3);
                context.DrawLine(arrowPen, new Point(13, 20), new Point(19, 20));
                context.DrawLine(arrowPen, new Point(19, 20), new Point(16.5, 17.5));
                context.DrawLine(arrowPen, new Point(19, 20), new Point(16.5, 22.5));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateMepAlignIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var pipePen = new Pen(Brushes.White, 3.2);
                context.DrawLine(pipePen, new Point(6, 9), new Point(26, 9));
                context.DrawLine(pipePen, new Point(6, 23), new Point(26, 23));
                var alignPen = new Pen(FireRed, 2.0);
                context.DrawLine(alignPen, new Point(16, 12), new Point(16, 20));
                context.DrawLine(alignPen, new Point(13, 15), new Point(16, 12));
                context.DrawLine(alignPen, new Point(19, 15), new Point(16, 12));
                context.DrawLine(alignPen, new Point(13, 17), new Point(16, 20));
                context.DrawLine(alignPen, new Point(19, 17), new Point(16, 20));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateMepCopyIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var whitePen = new Pen(Brushes.White, 2.6);
                context.DrawLine(whitePen, new Point(6, 22), new Point(21, 22));
                context.DrawLine(whitePen, new Point(6, 12), new Point(21, 12));
                var arrowPen = new Pen(FireRed, 2.2);
                context.DrawLine(arrowPen, new Point(25, 23), new Point(25, 8));
                context.DrawLine(arrowPen, new Point(25, 8), new Point(21.5, 12));
                context.DrawLine(arrowPen, new Point(25, 8), new Point(28.5, 12));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateQuickSelectIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var whitePen = new Pen(Brushes.White, 1.8);
                context.DrawRectangle(null, whitePen, new Rect(6, 6, 20, 5));
                context.DrawRectangle(null, whitePen, new Rect(6, 14, 20, 5));
                context.DrawRectangle(null, whitePen, new Rect(6, 22, 20, 5));
                context.DrawEllipse(FireRed, new Pen(Brushes.White, 0.8), new Point(10, 16.5), 3.2, 3.2);
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateProductivityToolsIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var tilePen = new Pen(Brushes.White, 1.6);
                context.DrawRectangle(null, tilePen, new Rect(6, 6, 7, 7));
                context.DrawRectangle(null, tilePen, new Rect(19, 6, 7, 7));
                context.DrawRectangle(null, tilePen, new Rect(6, 19, 7, 7));
                context.DrawLine(new Pen(FireRed, 2.4), new Point(19, 22.5), new Point(26, 22.5));
                context.DrawLine(new Pen(FireRed, 2.4), new Point(22.5, 19), new Point(22.5, 26));
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        public static ImageSource CreateRepeatLastIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                var arc = new StreamGeometry();
                using (StreamGeometryContext geometry = arc.Open())
                {
                    geometry.BeginFigure(new Point(8, 18), false, false);
                    geometry.ArcTo(
                        new Point(22, 9),
                        new Size(10, 10),
                        0,
                        false,
                        SweepDirection.Clockwise,
                        true,
                        false);
                }
                arc.Freeze();
                context.DrawGeometry(null, new Pen(Brushes.White, 2.4), arc);
                context.DrawLine(new Pen(Brushes.White, 2.4), new Point(21.5, 9), new Point(16.5, 7));
                context.DrawLine(new Pen(Brushes.White, 2.4), new Point(21.5, 9), new Point(20, 14));
                context.DrawEllipse(FireRed, new Pen(Brushes.White, 1), new Point(10, 21), 3.5, 3.5);
            }
            drawing.Freeze();
            return new DrawingImage(drawing);
        }

        private static Brush CreateBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
