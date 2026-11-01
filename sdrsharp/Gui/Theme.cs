using System.Drawing;

namespace SDRSharp.MPXAnalyzer.Gui
{
    static class Theme
    {
        public static readonly Color Background = Color.FromArgb(20, 20, 22);
        public static readonly Color PlotBackground = Color.FromArgb(32, 32, 36);
        public static readonly Color Text = Color.FromArgb(225, 225, 225);
        public static readonly Color Dim = Color.FromArgb(140, 140, 145);
        public static readonly Color Grid = Color.FromArgb(55, 55, 60);
        public static readonly Color Marker = Color.FromArgb(150, 255, 200, 0);
        public static readonly Color Avg = Color.FromArgb(0, 200, 255);
        public static readonly Color AvgFill = Color.FromArgb(51, 0, 200, 255);
        public static readonly Color Peak = Color.FromArgb(200, 255, 120, 60);
        public static readonly Color Warn = Color.FromArgb(255, 166, 0);
        public static readonly Color Over = Color.FromArgb(255, 51, 51);
        public static readonly Color Good = Color.FromArgb(77, 217, 77);
        public static readonly Color Record = Color.FromArgb(230, 40, 40);
    }
}
