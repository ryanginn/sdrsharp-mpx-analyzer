using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using SDRSharp.Common;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Same order as SDR#'s SpectrumStyle
    enum SpecStyle { Dots, SimpleCurve, SolidFill, StaticGradient, DynamicGradient, MinMax }

    // Colours and spectrum style taken from SDR#, so the MPX window looks like SDR#'s own spectrum displays.
    // Current SDR# has themes (ISharpControl.ThemeIsDark, ThemeForeColor...); older builds (.NET Framework, e.g.
    // 1716) don't, and use standard window colours with a black spectrum. Everything is read by reflection so one
    // code base works with both. Refresh() picks up changes while running.
    static class Theme
    {
        public static bool Dark = true;
        public static bool HasThemes;                       // SDR# has the theme API
        public static Color Background = Color.Black;       // Spectrum and meters
        public static Color Text = Color.FromArgb(220, 220, 220);
        public static Color Toolbar = Color.FromArgb(24, 24, 24);
        public static Color ToolbarText = Color.FromArgb(220, 220, 220);
        public static Color ToolbarDim = Color.FromArgb(140, 140, 140);
        public static Color AxisText = Color.FromArgb(200, 200, 200);
        public static Color Dim = Color.FromArgb(140, 140, 140);
        public static Color Grid = Color.FromArgb(64, 64, 64);
        public static Color PlotBorder = Color.FromArgb(160, 160, 160);
        public static Color Trace = Color.White;            // SDR#'s spectrum envelope colour
        public static Color Peak = Color.Yellow;            // SDR#'s spectrum peak colour
        public static Color Fill = Color.FromArgb(100, Color.DodgerBlue);        // SDR#'s spectrum fill colour (solid fill style)
        public static Color Marker = Color.FromArgb(170, 255, 200, 0);
        public static Color Warn = Color.FromArgb(255, 166, 0);
        public static Color Over = Color.FromArgb(255, 51, 51);
        public static Color Good = Color.FromArgb(77, 217, 77);
        public static readonly Color Record = Color.FromArgb(230, 40, 40);
        public static SpecStyle Style = SpecStyle.StaticGradient;
        static ColorBlend gradient = ParseGradient(DEFAULT_GRADIENT);
        static string key;

        const string DEFAULT_GRADIENT = "FFFFFF,ADD8E6,1E90FF,000050,000000";

        // ---- Reading SDR# ----

        static object GetProp(ISharpControl control, string name)
        {
            try
            {
                var p = typeof(ISharpControl).GetProperty(name) ?? control.GetType().GetProperty(name);
                return p?.GetValue(control, null);
            }
            catch { return null; }
        }

        static Type RadioUtils => Type.GetType("SDRSharp.Radio.Utils, SDRSharp.Radio", false);

        // SDR# setting as text (SDRSharp.config / SDRSharp.exe.config), or null
        static string GetSetting(string name)
        {
            try
            {
                var m = RadioUtils?.GetMethod("GetStringSetting", new[] { typeof(string), typeof(string) });
                return m?.Invoke(null, new object[] { name, null }) as string;
            }
            catch { return null; }
        }

        // Colour setting; `defaultAlpha` applies when the setting has no alpha (as SDR# does it: its solid spectrum fill
        // is drawn at alpha 100, i.e. the fill colour at ~40% over the background)
        static Color GetColorSetting(string name, Color fallback, byte defaultAlpha)
        {
            try
            {
                var m = RadioUtils?.GetMethod("GetColorSetting", new[] { typeof(string), typeof(Color), typeof(byte) });
                if (m != null) { return (Color)m.Invoke(null, new object[] { name, fallback, defaultAlpha }); }
            }
            catch { }
            return ParseColor(GetSetting(name), fallback, defaultAlpha);
        }

        // "RRGGBB" or "AARRGGBB"
        static Color ParseColor(string s, Color fallback, byte defaultAlpha = 255)
        {
            if (string.IsNullOrWhiteSpace(s)) { return Color.FromArgb(defaultAlpha, fallback); }
            s = s.Trim().TrimStart('#');
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) { return Color.FromArgb(defaultAlpha, fallback); }
            return (s.Length <= 6) ? Color.FromArgb(defaultAlpha, (int)((v >> 16) & 0xFF), (int)((v >> 8) & 0xFF), (int)(v & 0xFF)) : Color.FromArgb((int)v);
        }

        // SDR#'s gradient setting format: colours from top to bottom, e.g. "FFFFFF,ADD8E6,1E90FF,000050,000000"
        static ColorBlend ParseGradient(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) { return null; }
            var parts = s.Split(',');
            if (parts.Length < 2) { return null; }
            var blend = new ColorBlend(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                blend.Colors[i] = ParseColor(parts[i], Color.Black);
                blend.Positions[i] = (float)i / (parts.Length - 1);
            }
            return blend;
        }

        static Color Valid(object c, Color fallback) => (c is Color col && !col.IsEmpty && col.A != 0) ? col : fallback;

        static Color Mix(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // Reads SDR#'s theme and display settings. Returns true if anything changed since the last call
        public static bool Refresh(ISharpControl control)
        {
            object dark = GetProp(control, "ThemeIsDark");
            bool hasThemes = dark is bool;
            bool isDark = !hasThemes || (bool)dark;      // Older SDR#: dark spectrum on standard windows
            object fore = GetProp(control, "ThemeForeColor");
            object back = GetProp(control, "ThemeBackColor");
            object panel = GetProp(control, "ThemePanelColor");
            object name = GetProp(control, "ThemeName");
            var blend = GetProp(control, "SpectrumGradient") as ColorBlend ?? ParseGradient(GetSetting("spectrumAnalyzer.gradient"));

            object styleObj = GetProp(control, "FFTSpectrumStyle");
            int style = (int)SpecStyle.StaticGradient;
            if (styleObj != null) { try { style = Convert.ToInt32(styleObj, CultureInfo.InvariantCulture); } catch { } }
            else if (int.TryParse(GetSetting("spectrumStyle"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)) { style = s; }
            if (!Enum.IsDefined(typeof(SpecStyle), style)) { style = (int)SpecStyle.StaticGradient; }

            Color env = GetColorSetting("spectrumAnalyzer.envelopeColor", Color.White, 255);
            Color peak = GetColorSetting("spectrumAnalyzer.peakColor", Color.Yellow, 255);
            Color fill = GetColorSetting("spectrumAnalyzer.fillColor", Color.DodgerBlue, 100);

            string newKey = string.Join("|", hasThemes, isDark, name, Valid(fore, Color.Empty).ToArgb(), Valid(back, Color.Empty).ToArgb(),
                                        Valid(panel, Color.Empty).ToArgb(), style, env.ToArgb(), peak.ToArgb(), fill.ToArgb(),
                                        blend == null ? "" : string.Join(",", Array.ConvertAll(blend.Colors, c => c.ToArgb().ToString())));
            if (newKey == key) { return false; }
            key = newKey;

            HasThemes = hasThemes;
            Dark = isDark;
            if (hasThemes)
            {
                Background = Valid(back, isDark ? Color.Black : Color.White);
                Text = Valid(fore, isDark ? Color.FromArgb(220, 220, 220) : Color.Black);
                Toolbar = Valid(panel, isDark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(240, 240, 240));
                ToolbarText = Text;
            }
            else
            {
                // Classic SDR#: black spectrum, standard Windows controls around it
                Background = Color.Black;
                Text = Color.FromArgb(220, 220, 220);
                Toolbar = SystemColors.Control;
                ToolbarText = SystemColors.ControlText;
            }
            ToolbarDim = Mix(Toolbar, ToolbarText, 0.55f);
            AxisText = Mix(Background, Text, 0.85f);
            Dim = Mix(Background, Text, 0.55f);
            Grid = Mix(Background, Text, 0.22f);
            PlotBorder = Mix(Background, Text, 0.6f);
            Trace = env;
            Peak = peak;
            Fill = fill;
            Style = (SpecStyle)style;
            gradient = (blend != null && blend.Colors != null && blend.Colors.Length >= 2) ? blend : ParseGradient(DEFAULT_GRADIENT);

            // Status colours that stay readable on light backgrounds
            bool darkPlot = Background.GetBrightness() < 0.5f;
            Marker = darkPlot ? Color.FromArgb(170, 255, 200, 0) : Color.FromArgb(200, 190, 120, 0);
            Warn = darkPlot ? Color.FromArgb(255, 166, 0) : Color.FromArgb(200, 110, 0);
            Over = darkPlot ? Color.FromArgb(255, 51, 51) : Color.FromArgb(210, 0, 0);
            Good = darkPlot ? Color.FromArgb(77, 217, 77) : Color.FromArgb(0, 140, 0);
            return true;
        }

        // Fill under the spectrum trace, following SDR#'s spectrum style. `traceTop` is the highest point of the
        // trace (for the dynamic gradient). Returns null when the style has no fill
        public static Brush SpectrumFill(RectangleF plot, float traceTop)
        {
            switch (Style)
            {
                case SpecStyle.SolidFill:
                    return new SolidBrush(Fill);
                case SpecStyle.StaticGradient:
                case SpecStyle.DynamicGradient:
                    // Static: the gradient spans the plot height. Dynamic: it spans from the trace's peak down
                    float top = (Style == SpecStyle.DynamicGradient) ? Math.Min(traceTop, plot.Bottom - 2) : plot.Top;
                    var rect = RectangleF.FromLTRB(plot.Left, top - 1, plot.Right, plot.Bottom + 1);
                    var brush = new LinearGradientBrush(rect, Color.White, Color.Black, LinearGradientMode.Vertical);
                    brush.InterpolationColors = gradient;
                    return brush;
                default:
                    return null;    // Dots, simple curve, min/max: line only
            }
        }

        // Standard controls and the title bar, themed by SDR#'s ThemeService as SDR# does for its own windows
        // (current SDR# only; older builds use standard Windows controls)
        public static void ApplyTo(Form form)
        {
            if (!HasThemes) { return; }
            try
            {
                var svc = Type.GetType("SDRSharp.PanView.ThemeService, SDRSharp.PanView", false);
                svc?.GetMethod("ApplyThemeToGui", new[] { typeof(Form), typeof(bool), typeof(bool) })?.Invoke(null, new object[] { form, Dark, true });
                if (form.IsHandleCreated)
                {
                    svc?.GetMethod("UseImmersiveDarkMode", new[] { typeof(IntPtr), typeof(bool) })?.Invoke(null, new object[] { form.Handle, Dark });
                }
            }
            catch { }
        }
    }
}
