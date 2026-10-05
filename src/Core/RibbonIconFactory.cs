using System.Windows;
using System.Windows.Media;

namespace TungMEP.Core
{
    internal static class RibbonIconFactory
    {
        private static readonly Brush FireRed = CreateBrush(220, 55, 42);
        private static readonly Brush DeepBlue = CreateBrush(20, 77, 125);
        private static readonly Brush White = Brushes.White;

        public static ImageSource CreateAutoSprinklerIcon()
        {
            var drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                context.DrawRoundedRectangle(DeepBlue, null, new Rect(1, 1, 30, 30), 5, 5);
                context.DrawEllipse(FireRed, new Pen(White, 1.6), new Point(16, 10), 5.4, 5.4);
                context.DrawLine(new Pen(White, 2.2), new Point(16, 15.5), new Point(16, 20));
                context.DrawLine(new Pen(White, 1.6), new Point(8, 20), new Point(24, 20));
                context.DrawLine(new Pen(White, 1.6), new Point(10, 20), new Point(7, 25));
                context.DrawLine(new Pen(White, 1.6), new Point(16, 20), new Point(16, 26));
                context.DrawLine(new Pen(White, 1.6), new Point(22, 20), new Point(25, 25));
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
                var pen = new Pen(White, 2.2);
                context.DrawLine(pen, new Point(7, 9), new Point(25, 9));
                context.DrawLine(pen, new Point(7, 16), new Point(25, 16));
                context.DrawLine(pen, new Point(7, 23), new Point(19, 23));
                context.DrawEllipse(FireRed, new Pen(White, 1), new Point(22.5, 23), 3.5, 3.5);
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
