using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // MPX spectrum plot: level and frequency grid, markers for the usual MPX components, average and peak traces
    // and a cursor readout
    public sealed class SpectrumView : Control
    {
        static readonly (double freq, string label)[] MARKERS =
        {
            (19000.0, "Pilot"),
            (38000.0, "38k"),
            (57000.0, "RDS"),
            (67000.0, "SCA"),
            (92000.0, "SCA")
        };

        readonly float[] avg = new float[MpxChain.FFT_BINS];
        readonly float[] peak = new float[MpxChain.FFT_BINS];
        bool valid;
        string message = "No signal";
        Point? mouse;
        PointF[] trace = new PointF[0];

        public float MaxFreqKHz = 96.0f;
        public float DbMin = -100.0f;
        public float DbMax = 0.0f;
        public bool PeakHold;
        public bool ShowMarkers = true;

        public SpectrumView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
        }

        // Fetches the latest spectrum from the chain. `msg` is shown when there's none
        public void UpdateData(MpxChain chain, string msg)
        {
            valid = msg == null && chain.GetSpectrum(avg, peak);
            message = msg ?? "No signal";
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouse = null;
            Invalidate();
            base.OnMouseLeave(e);
        }

        static float ToDB(float p) => 10.0f * (float)Math.Log10(Math.Max(p, 1e-20f));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            float textH = Font.Height;
            float labelW = g.MeasureString("-100", Font).Width;

            var plot = RectangleF.FromLTRB(labelW + 6, textH * 1.5f, Width - 10, Height - textH - 4);
            if (plot.Width < 20 || plot.Height < 20) { return; }

            using (var bg = new SolidBrush(Theme.PlotBackground)) { g.FillRectangle(bg, plot); }

            double span = MaxFreqKHz * 1000.0;
            float dbRange = Math.Max(DbMax - DbMin, 1.0f);
            float FToX(double f) => plot.Left + (float)(f / span) * plot.Width;
            float DbToY(float db) => Math.Clamp(plot.Bottom - (db - DbMin) / dbRange * plot.Height, plot.Top, plot.Bottom);

            using var textBrush = new SolidBrush(ForeColor);
            using var axisBrush = new SolidBrush(Theme.AxisText);
            using var gridPen = new Pen(Theme.Grid);
            using var borderPen = new Pen(Theme.PlotBorder);

            // Unit, above the level axis (SDR#'s spectra show "dBFS" there)
            g.DrawString("dB", Font, axisBrush, 2, 0);

            // Level grid
            float dbStep = dbRange > 100.0f ? 20.0f : 10.0f;
            for (float db = (float)Math.Ceiling(DbMin / dbStep) * dbStep; db <= DbMax + 0.01f; db += dbStep)
            {
                float y = DbToY(db);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                string s = db.ToString("0");
                var ts = g.MeasureString(s, Font);
                g.DrawString(s, Font, axisBrush, plot.Left - ts.Width - 2, y - ts.Height * 0.5f);
            }

            // Frequency grid
            double fStep = span > 50000.0 ? 10000.0 : 5000.0;
            for (double f = 0.0; f <= span + 1.0; f += fStep)
            {
                float x = FToX(f);
                g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                string s = (f / 1000.0).ToString("0") + (f > 0.0 ? " k" : "");
                var ts = g.MeasureString(s, Font);
                g.DrawString(s, Font, axisBrush, Math.Min(x - ts.Width * 0.5f, plot.Right - ts.Width), plot.Bottom + 2);
            }

            var clip = g.Clip;
            g.SetClip(plot);

            // Markers for the well known MPX components
            if (ShowMarkers)
            using (var markerPen = new Pen(Theme.Marker) { DashStyle = DashStyle.Dash })
            using (var markerBrush = new SolidBrush(Theme.Marker))
            {
                foreach (var (freq, label) in MARKERS)
                {
                    if (freq > span) { continue; }
                    float x = FToX(freq);
                    g.DrawLine(markerPen, x, plot.Top, x, plot.Bottom);
                    g.DrawString(label, Font, markerBrush, x + 3, plot.Top + 2);
                }
            }

            if (valid)
            {
                // Gradient fill below the average trace (as in SDR#'s spectra), then the lines on top
                ComputeTrace(avg, plot, span, DbToY);
                var area = new PointF[trace.Length + 2];
                Array.Copy(trace, area, trace.Length);
                area[trace.Length] = new PointF(trace[trace.Length - 1].X, plot.Bottom);
                area[trace.Length + 1] = new PointF(trace[0].X, plot.Bottom);
                using (var fill = Theme.SpectrumFill(plot)) { g.FillPolygon(fill, area); }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (PeakHold)
                {
                    var avgTrace = (PointF[])trace.Clone();
                    ComputeTrace(peak, plot, span, DbToY);
                    using (var peakPen = new Pen(Theme.Peak)) { g.DrawLines(peakPen, trace); }
                    trace = avgTrace;
                }
                using (var avgPen = new Pen(Theme.Trace)) { g.DrawLines(avgPen, trace); }
                g.SmoothingMode = SmoothingMode.None;
            }
            else
            {
                var ts = g.MeasureString(message, Font);
                g.DrawString(message, Font, textBrush, plot.Left + (plot.Width - ts.Width) / 2, plot.Top + (plot.Height - ts.Height) / 2);
            }

            // Cursor readout
            if (valid && mouse.HasValue && plot.Contains(mouse.Value))
            {
                var m = mouse.Value;
                double binHz = MpxChain.SAMPLERATE / MpxChain.FFT_SIZE;
                double f = (m.X - plot.Left) / plot.Width * span;
                int bin = Math.Clamp((int)Math.Round(f / binHz), 0, MpxChain.FFT_BINS - 1);
                using (var cursorPen = new Pen(Color.FromArgb(128, ForeColor))) { g.DrawLine(cursorPen, m.X, plot.Top, m.X, plot.Bottom); }

                string text = string.Format("{0:0.00} kHz\nAvg:  {1:0.0} dB", bin * binHz / 1000.0, ToDB(avg[bin]));
                if (PeakHold) { text += string.Format("\nPeak: {0:0.0} dB", ToDB(peak[bin])); }
                var ts = g.MeasureString(text, Font);
                float bx = m.X + 12, by = m.Y + 12;
                if (bx + ts.Width + 6 > plot.Right) { bx = m.X - ts.Width - 12; }
                if (by + ts.Height + 6 > plot.Bottom) { by = m.Y - ts.Height - 12; }
                var box = new RectangleF(bx, by, ts.Width + 6, ts.Height + 4);
                using (var boxBrush = new SolidBrush(Color.FromArgb(230, 45, 45, 50))) { g.FillRectangle(boxBrush, box); }
                g.DrawRectangle(gridPen, box.X, box.Y, box.Width, box.Height);
                g.DrawString(text, Font, textBrush, bx + 3, by + 2);
            }

            g.Clip = clip;
            g.DrawRectangle(borderPen, plot.X, plot.Y, plot.Width, plot.Height);
        }

        // One point per pixel column, showing the highest bin within it so narrow carriers (pilot) never disappear
        void ComputeTrace(float[] spec, RectangleF plot, double span, Func<float, float> dbToY)
        {
            double binHz = MpxChain.SAMPLERATE / MpxChain.FFT_SIZE;
            int width = Math.Max((int)plot.Width, 1);
            if (trace.Length != width + 1) { trace = new PointF[width + 1]; }
            for (int px = 0; px <= width; px++)
            {
                int b0 = Math.Clamp((int)(span * px / width / binHz), 0, MpxChain.FFT_BINS - 1);
                int b1 = Math.Clamp((int)(span * (px + 1) / width / binHz), b0, MpxChain.FFT_BINS - 1);
                float p = spec[b0];
                for (int b = b0 + 1; b < b1; b++) { p = Math.Max(p, spec[b]); }
                trace[px] = new PointF(plot.Left + px, dbToY(ToDB(p)));
            }
        }
    }
}
