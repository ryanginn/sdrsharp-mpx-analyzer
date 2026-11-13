using System;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Oscilloscope of the MPX or the decoded audio, with phosphor-like persistence. +/-100% (75kHz) lines; the
    // parts of the trace beyond them are drawn red
    public sealed class ScopeView : Control
    {
        const float RANGE = 1.25f;          // Vertical range, +/- this (1.0 = 75kHz)
        const int FADE_ALPHA = 70;          // Persistence: how much of the old trace each update wipes (0-255)

        float[] samples = new float[0];
        int count;
        bool valid;
        string message = "No signal";
        Bitmap persist;
        Rectangle plot;

        public ScopeSignal Signal = ScopeSignal.Mpx;
        public float TimeMs = 10.0f;
        public bool LevelPercent;

        public ScopeView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        // Fetches the latest samples and draws them into the persistence image. `msg` is shown when there's nothing
        public void UpdateData(MpxChain chain, string msg)
        {
            message = msg ?? "No signal";
            valid = false;
            if (msg == null)
            {
                int want = (int)Math.Round(MpxChain.ScopeRate(Signal) * TimeMs / 1000.0);
                if (samples.Length < want) { samples = new float[want]; }
                count = chain.GetScope(Signal, samples, want, out _);
                valid = count > 1;
            }
            SizePlot();
            DrawTrace();
            Invalidate();
        }

        public void Clear()
        {
            if (persist == null) { return; }
            using (var g = Graphics.FromImage(persist)) { g.Clear(Theme.Background); }
            Invalidate();
        }

        void SizePlot()
        {
            int left = TextRenderer.MeasureText(LevelPercent ? "-100%" : "-75k", Font).Width + 6;
            var r = new Rectangle(left, 4, Math.Max(Width - left - 6, 1), Math.Max(Height - 8, 1));
            if (r != plot || persist == null)
            {
                plot = r;
                persist?.Dispose();
                persist = new Bitmap(plot.Width, plot.Height);
                using (var g = Graphics.FromImage(persist)) { g.Clear(Theme.Background); }
            }
        }

        float Y(float v) => plot.Height * 0.5f * (1.0f - v / RANGE);

        void DrawTrace()
        {
            using var g = Graphics.FromImage(persist);
            using (var fade = new SolidBrush(Color.FromArgb(FADE_ALPHA, Theme.Background))) { g.FillRectangle(fade, 0, 0, persist.Width, persist.Height); }
            if (!valid) { return; }

            int w = persist.Width;
            // SDR#'s spectrum fill colour (blue by default), opaque, so the scope matches the spectra
            using var pen = new Pen(Color.FromArgb(255, Theme.Fill));
            using var overPen = new Pen(Theme.Over);
            float top = Y(1.0f), bottom = Y(-1.0f);

            // Normal colour inside +/-100%, red beyond
            void Draw(Pen p)
            {
                if (count >= w)
                {
                    // Min/max per pixel column, overlapping the neighbours by a sample so the trace is continuous
                    for (int x = 0; x < w; x++)
                    {
                        int a = (int)((long)x * count / w), b = (int)((long)(x + 1) * count / w);
                        a = Math.Max(a - 1, 0);
                        b = Math.Min(Math.Max(b, a + 1), count);
                        float lo = samples[a], hi = samples[a];
                        for (int i = a + 1; i < b; i++)
                        {
                            float v = samples[i];
                            if (v < lo) { lo = v; } else if (v > hi) { hi = v; }
                        }
                        float y0 = Y(hi), y1 = Y(lo);
                        if (y1 - y0 < 1.0f) { y1 = y0 + 1.0f; }
                        g.DrawLine(p, x, y0, x, y1);
                    }
                }
                else
                {
                    var pts = new PointF[count];
                    for (int i = 0; i < count; i++) { pts[i] = new PointF((float)i * (w - 1) / (count - 1), Y(samples[i])); }
                    g.DrawLines(p, pts);
                }
            }

            g.SetClip(new RectangleF(0, top, w, bottom - top));
            Draw(pen);
            g.SetClip(new RectangleF(0, 0, w, top));
            Draw(overPen);
            g.SetClip(new RectangleF(0, bottom, w, persist.Height - bottom));
            Draw(overPen);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            if (persist == null) { SizePlot(); }
            g.DrawImageUnscaled(persist, plot.Left, plot.Top);

            // Grid: 0, +/-50%, +/-100% (and time divisions)
            using var grid = new Pen(Theme.Grid);
            using var limit = new Pen(Color.FromArgb(160, Theme.Over)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var text = new SolidBrush(Theme.AxisText);
            foreach (float v in new[] { 1.0f, 0.5f, 0.0f, -0.5f, -1.0f })
            {
                float y = plot.Top + Y(v);
                g.DrawLine(Math.Abs(v) == 1.0f ? limit : grid, plot.Left, y, plot.Right, y);
                string label = LevelPercent ? string.Format("{0:0}%", v * 100.0f) : string.Format("{0:0.#}k", v * 75.0f);
                var size = g.MeasureString(label, Font);
                g.DrawString(label, Font, text, plot.Left - size.Width - 2, y - size.Height / 2);
            }
            for (int i = 1; i < 10; i++)
            {
                float x = plot.Left + plot.Width * i / 10.0f;
                g.DrawLine(grid, x, plot.Top, x, plot.Top + 4);
                g.DrawLine(grid, x, plot.Bottom - 4, x, plot.Bottom);
            }
            using (var border = new Pen(Theme.PlotBorder)) { g.DrawRectangle(border, plot); }

            // Time per division, top right
            string div = TimeMs / 10.0f >= 1.0f ? string.Format("{0:0.#} ms/div", TimeMs / 10.0f) : string.Format("{0:0} µs/div", TimeMs * 100.0f);
            var ds = g.MeasureString(div, Font);
            g.DrawString(div, Font, text, plot.Right - ds.Width - 4, plot.Top + 4);

            if (!valid)
            {
                using var dim = new SolidBrush(Theme.Dim);
                var ms = g.MeasureString(message, Font);
                g.DrawString(message, Font, dim, plot.Left + (plot.Width - ms.Width) / 2, plot.Top + (plot.Height - ms.Height) / 2);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { persist?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
