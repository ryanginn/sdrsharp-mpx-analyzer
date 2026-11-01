using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Level readouts to the right of the spectrum: MPX power, deviation, pilot, RDS and RDS lock
    public sealed class MetersView : Control
    {
        const float VALUE_FONT_SCALE = 1.4f;
        const float MAX_DEV_KHZ = (float)(MpxChain.DEVIATION / 1000.0);

        readonly Font valueFont;
        readonly ToolTip toolTip = new ToolTip { InitialDelay = 300, ReshowDelay = 100 };
        readonly List<(RectangleF rect, string tip)> tips = new List<(RectangleF, string)>();
        string shownTip;

        MpxMeasurements meas;
        bool valid;
        public bool LevelPercent;
        public float PowerLimitDBr;

        public MetersView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            valueFont = new Font(Font.FontFamily, Font.Size * VALUE_FONT_SCALE, FontStyle.Bold);
        }

        public int PreferredWidth
        {
            get
            {
                using var g = CreateGraphics();
                float w = Math.Max(g.MeasureString("-00.0 dBr", valueFont).Width, g.MeasureString("Deviation (1 s peak)", Font).Width);
                w = Math.Max(w, g.MeasureString("RDS lock    Yes +00°", Font).Width);
                return (int)Math.Ceiling(w) + 12;
            }
        }

        public void UpdateData(MpxChain chain, bool enabled)
        {
            valid = enabled && chain.GetMeasurements(out meas);
            Invalidate();
        }

        // Level in kHz, or in percent where 100% = 75kHz. Subcarrier levels get an extra decimal in kHz
        static string FormatLevel(float khz, bool percent, bool subcarrier)
        {
            if (percent) { return string.Format("{0:0.0} %", 100.0f * khz / MAX_DEV_KHZ); }
            return string.Format(subcarrier ? "{0:0.00} kHz" : "{0:0.0} kHz", khz);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            tips.Clear();

            float x = 4, w = Width - 8, y = 4;
            float lineH = Font.Height + 2;
            float valueH = valueFont.Height + 1;
            Color dim = Theme.Dim, normal = Theme.Text;

            void Label(string text)
            {
                using var b = new SolidBrush(dim);
                g.DrawString(text, Font, b, x, y);
                y += lineH;
            }
            void Value(string text, Color col)
            {
                using var b = new SolidBrush(col);
                g.DrawString(text, valueFont, b, x, y);
                y += valueH;
            }
            // Label on the left, value right aligned
            void Row(string label, string value, Color col, string tip = null)
            {
                using (var b = new SolidBrush(dim)) { g.DrawString(label, Font, b, x, y); }
                var size = g.MeasureString(value, Font);
                using (var b = new SolidBrush(col)) { g.DrawString(value, Font, b, x + w - size.Width, y); }
                if (tip != null) { tips.Add((new RectangleF(x, y, w, lineH), tip)); }
                y += lineH;
            }
            void Gap() => y += lineH * 0.5f;

            // MPX power (ITU-R BS.412), 60s average with a 1s short term value
            Label(valid && meas.PowerSeconds < 59.95f ? string.Format("MPX power ({0:0} s)", meas.PowerSeconds) : "MPX power (60 s)");
            float powerY = y;
            Value(valid ? string.Format("{0:+0.0;-0.0;0.0} dBr", meas.PowerDBr) : "--.- dBr", valid ? (meas.PowerDBr > PowerLimitDBr ? Theme.Warn : normal) : dim);
            tips.Add((new RectangleF(x, powerY, w, valueH), string.Format("MPX power (ITU-R BS.412): 0 dBr = power of a sine at ±19 kHz deviation.\nAmber above the {0:+0.0;-0.0;0.0} dBr limit (set in the plugin panel).", PowerLimitDBr)));
            Row("1 s", valid ? string.Format("{0:+0.0;-0.0;0.0} dBr", meas.PowerShortDBr) : "--.- dBr", dim);
            Gap();

            // Peak deviation over 1s with max hold
            string noLevel = LevelPercent ? "--.- %" : "--.- kHz";
            float resp = valid ? meas.PeakResponseSec : 0.0f;
            float devY = y;
            Label(resp <= 0.0f ? "Deviation (1 s peak)" : resp < 1e-3f ? string.Format("Deviation ({0:0} µs)", resp * 1e6f) : string.Format("Deviation ({0:0.#} ms)", resp * 1e3f));
            tips.Add((new RectangleF(x, devY, w, lineH + valueH), resp <= 0.0f
                ? "Peak deviation over the last second (true sample peak)"
                : string.Format("Peak deviation over the last second, quasi-peak with {0:0} µs response time:\npeaks shorter than that read lower", resp * 1e6f)));
            Value(valid ? FormatLevel(meas.PeakDevKHz, LevelPercent, false) : noLevel, valid ? (meas.PeakDevKHz > MAX_DEV_KHZ ? Theme.Over : normal) : dim);
            Row("Max", valid ? FormatLevel(meas.MaxDevKHz, LevelPercent, false) : noLevel, valid && meas.MaxDevKHz > MAX_DEV_KHZ ? Theme.Over : dim);
            Gap();
            using (var p = new Pen(Theme.Grid)) { g.DrawLine(p, x, y, x + w, y); }
            y += 4;

            // Subcarrier levels, with the other unit in the tooltip
            if (valid && meas.PilotPresent)
            {
                Row("Pilot", FormatLevel(meas.PilotKHz, LevelPercent, true), normal, "19 kHz pilot: " + FormatLevel(meas.PilotKHz, !LevelPercent, true));
            }
            else
            {
                Row("Pilot", valid ? "none" : noLevel, dim);
            }
            if (valid && meas.RdsPresent)
            {
                Row("RDS", FormatLevel(meas.RdsKHz, LevelPercent, true), normal, "57 kHz RDS: " + FormatLevel(meas.RdsKHz, !LevelPercent, true));
            }
            else
            {
                Row("RDS", valid ? "none" : noLevel, dim);
            }

            // RDS carrier locked to the 3rd harmonic of the pilot (only meaningful with both present)
            if (valid && meas.PilotPresent && meas.RdsPresent && meas.LockValid)
            {
                // EN 50067: in phase or quadrature, within +/-10 degrees
                float offset = Math.Abs(meas.LockPhaseDeg);
                bool inTolerance = Math.Min(offset, Math.Abs(90.0f - offset)) <= 10.0f;
                string tip = string.Format("RDS 57 kHz carrier vs 3rd harmonic of the 19 kHz pilot\n" +
                                           "Phase: {0:+0.0;-0.0;0.0}° (EN 50067: 0° or 90°, ±10°)\n" +
                                           "Stability: {1:0.000} over {2:0} s (locked above {3:0.00})",
                                           meas.LockPhaseDeg, meas.LockCoherence, meas.LockSeconds, MpxChain.LOCK_MIN_COHERENCE);
                if (meas.Locked)
                {
                    Row("RDS lock", string.Format("Yes {0:+0;-0;0}°", meas.LockPhaseDeg), inTolerance ? Theme.Good : Theme.Warn, tip);
                }
                else
                {
                    Row("RDS lock", "No", Theme.Warn, tip);
                }
            }
            else if (valid && meas.PilotPresent && meas.RdsPresent)
            {
                Row("RDS lock", "--", dim);
            }
            else
            {
                Row("RDS lock", valid ? "n/a" : "--", dim);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string tip = null;
            foreach (var (rect, t) in tips)
            {
                if (rect.Contains(e.Location)) { tip = t; break; }
            }
            if (tip != shownTip)
            {
                shownTip = tip;
                if (tip == null) { toolTip.Hide(this); } else { toolTip.Show(tip, this, e.X + 12, e.Y + 16); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            shownTip = null;
            toolTip.Hide(this);
            base.OnMouseLeave(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                toolTip.Dispose();
                valueFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
