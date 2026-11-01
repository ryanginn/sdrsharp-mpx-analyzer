using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Colours matching SDR#'s dark theme and its spectrum displays (IF / Audio / FM MPX Spectrum)
    static class Theme
    {
        public static readonly Color Background = Color.Black;
        public static readonly Color Toolbar = Color.FromArgb(24, 24, 24);
        public static readonly Color PlotBackground = Color.Black;
        public static readonly Color Text = Color.FromArgb(220, 220, 220);
        public static readonly Color AxisText = Color.FromArgb(200, 200, 200);
        public static readonly Color Dim = Color.FromArgb(140, 140, 140);
        public static readonly Color Grid = Color.FromArgb(64, 64, 64);
        public static readonly Color PlotBorder = Color.FromArgb(160, 160, 160);
        public static readonly Color Separator = Color.FromArgb(64, 64, 64);
        public static readonly Color Marker = Color.FromArgb(170, 255, 200, 0);
        public static readonly Color Trace = Color.FromArgb(252, 252, 252);
        public static readonly Color Peak = Color.FromArgb(220, 255, 140, 60);
        public static readonly Color Warn = Color.FromArgb(255, 166, 0);
        public static readonly Color Over = Color.FromArgb(255, 51, 51);
        public static readonly Color Good = Color.FromArgb(77, 217, 77);
        public static readonly Color Record = Color.FromArgb(230, 40, 40);
        public static readonly Color ButtonBack = Color.FromArgb(45, 45, 48);
        public static readonly Color ButtonBorder = Color.FromArgb(80, 80, 80);

        // Fill under the trace: a fixed vertical gradient over the plot height, like SDR#'s spectra
        public static Brush SpectrumFill(RectangleF plot)
        {
            var brush = new LinearGradientBrush(new RectangleF(plot.X, plot.Y - 1, plot.Width, plot.Height + 2), Color.White, Color.Black, LinearGradientMode.Vertical);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(205, 212, 218),
                    Color.FromArgb(110, 150, 190),
                    Color.FromArgb(30, 110, 200),
                    Color.FromArgb(6, 45, 115),
                    Color.FromArgb(0, 0, 45)
                },
                Positions = new[] { 0.0f, 0.3f, 0.55f, 0.8f, 1.0f }
            };
            return brush;
        }

        public static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = ButtonBack;
            b.ForeColor = Text;
            b.FlatAppearance.BorderColor = ButtonBorder;
            b.UseVisualStyleBackColor = false;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark title bar (Windows 10 20H1 and later; ignored elsewhere)
        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));   // Older Windows 10 builds
                }
            }
            catch { }
        }
    }
}
