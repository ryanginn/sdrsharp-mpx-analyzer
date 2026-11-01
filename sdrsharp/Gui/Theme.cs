using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.PanView;
using SDRSharp.Radio;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Colours and spectrum style taken from SDR#'s current theme and display settings, so the MPX window looks
    // like SDR#'s own spectrum displays whichever theme is selected. Refresh() picks up changes while running.
    static class Theme
    {
        public static bool Dark = true;
        public static Color Background = Color.Black;       // Spectrum and meters
        public static Color Toolbar = Color.FromArgb(24, 24, 24);
        public static Color Text = Color.FromArgb(220, 220, 220);
        public static Color AxisText = Color.FromArgb(200, 200, 200);
        public static Color Dim = Color.FromArgb(140, 140, 140);
        public static Color Grid = Color.FromArgb(64, 64, 64);
        public static Color PlotBorder = Color.FromArgb(160, 160, 160);
        public static Color Trace = Color.White;            // SDR#'s spectrum envelope colour
        public static Color Peak = Color.Yellow;            // SDR#'s spectrum peak colour
        public static Color Fill = Color.DodgerBlue;        // SDR#'s spectrum fill colour (solid fill style)
        public static Color Marker = Color.FromArgb(170, 255, 200, 0);
        public static Color Warn = Color.FromArgb(255, 166, 0);
        public static Color Over = Color.FromArgb(255, 51, 51);
        public static Color Good = Color.FromArgb(77, 217, 77);
        public static readonly Color Record = Color.FromArgb(230, 40, 40);
        public static SpectrumStyle Style = SpectrumStyle.StaticGradient;
        static ColorBlend gradient = DefaultGradient();
        static string key;

        static ColorBlend DefaultGradient() => new ColorBlend
        {
            Colors = new[] { Color.White, Color.LightBlue, Color.DodgerBlue, Color.FromArgb(0, 0, 80), Color.Black },
            Positions = new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f }
        };

        static Color Valid(Color c, Color fallback) => (c.IsEmpty || c.A == 0) ? fallback : c;

        static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // Reads SDR#'s theme. Returns true if anything changed since the last call
        public static bool Refresh(ISharpControl control)
        {
            bool dark = true;
            Color fore = Color.Empty, back = Color.Empty, panel = Color.Empty;
            ColorBlend blend = null;
            SpectrumStyle style = SpectrumStyle.StaticGradient;
            string name = "";
            try
            {
                dark = control.ThemeIsDark;
                name = control.ThemeName ?? "";
                fore = control.ThemeForeColor;
                back = control.ThemeBackColor;
                panel = control.ThemePanelColor;
                blend = control.SpectrumGradient;
                style = control.FFTSpectrumStyle;
            }
            catch { }

            // SDR#'s spectrum colours (Display settings)
            Color env = Color.White, peak = Color.Yellow, fill = Color.DodgerBlue;
            try
            {
                env = Utils.GetColorSetting("spectrumAnalyzer.envelopeColor", Color.White);
                peak = Utils.GetColorSetting("spectrumAnalyzer.peakColor", Color.Yellow);
                fill = Utils.GetColorSetting("spectrumAnalyzer.fillColor", Color.DodgerBlue);
            }
            catch { }

            string newKey = string.Join("|", dark, name, fore.ToArgb(), back.ToArgb(), panel.ToArgb(), style, env.ToArgb(), peak.ToArgb(), fill.ToArgb(),
                                        blend == null ? "" : string.Join(",", Array.ConvertAll(blend.Colors, c => c.ToArgb())));
            if (newKey == key) { return false; }
            key = newKey;

            Dark = dark;
            Background = Valid(back, dark ? Color.Black : Color.White);
            Toolbar = Valid(panel, dark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(240, 240, 240));
            Text = Valid(fore, dark ? Color.FromArgb(220, 220, 220) : Color.Black);
            AxisText = Mix(Background, Text, 0.85f);
            Dim = Mix(Background, Text, 0.55f);
            Grid = Mix(Background, Text, 0.22f);
            PlotBorder = Mix(Background, Text, 0.6f);
            Trace = Valid(env, dark ? Color.White : Color.Black);
            Peak = Valid(peak, Color.Yellow);
            Fill = Valid(fill, Color.DodgerBlue);
            Style = style;
            gradient = (blend != null && blend.Colors != null && blend.Colors.Length >= 2) ? blend : DefaultGradient();

            // Status colours that stay readable on light themes
            Marker = dark ? Color.FromArgb(170, 255, 200, 0) : Color.FromArgb(200, 190, 120, 0);
            Warn = dark ? Color.FromArgb(255, 166, 0) : Color.FromArgb(200, 110, 0);
            Over = dark ? Color.FromArgb(255, 51, 51) : Color.FromArgb(210, 0, 0);
            Good = dark ? Color.FromArgb(77, 217, 77) : Color.FromArgb(0, 140, 0);
            return true;
        }

        // Fill under the spectrum trace, following SDR#'s spectrum style. `traceTop` is the highest point of the
        // trace (for the dynamic gradient). Returns null when the style has no fill
        public static Brush SpectrumFill(RectangleF plot, float traceTop)
        {
            switch (Style)
            {
                case SpectrumStyle.SolidFill:
                    return new SolidBrush(Fill);
                case SpectrumStyle.StaticGradient:
                case SpectrumStyle.DynamicGradient:
                    // Static: the gradient spans the plot height. Dynamic: it spans from the trace's peak down
                    float top = (Style == SpectrumStyle.DynamicGradient) ? Math.Min(traceTop, plot.Bottom - 2) : plot.Top;
                    var rect = RectangleF.FromLTRB(plot.Left, top - 1, plot.Right, plot.Bottom + 1);
                    var brush = new LinearGradientBrush(rect, Color.White, Color.Black, LinearGradientMode.Vertical);
                    brush.InterpolationColors = gradient;
                    return brush;
                default:
                    return null;    // Dots, simple curve, min/max: line only
            }
        }

        // Standard controls (buttons, check boxes) and the title bar, as SDR# does for its own windows
        public static void ApplyTo(Form form)
        {
            try { ThemeService.ApplyThemeToGui(form, Dark, true); } catch { }
            try { if (form.IsHandleCreated) { ThemeService.UseImmersiveDarkMode(form.Handle, Dark); } } catch { }
        }
    }
}
