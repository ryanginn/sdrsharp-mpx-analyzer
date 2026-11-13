using System;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Peak readouts above the scope: + and - modulation (composite) and the demodulated L and R
    public sealed class ScopeMeters : Control
    {
        const float MAX_DEV_KHZ = (float)(MpxChain.DEVIATION / 1000.0);

        readonly Font valueFont;
        MpxMeasurements meas;
        bool valid;
        public bool LevelPercent;

        public ScopeMeters()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            valueFont = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold);
            Height = Font.Height + valueFont.Height + 8;
        }

        public void UpdateData(MpxChain chain, bool enabled)
        {
            valid = enabled && chain.GetMeasurements(out meas);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Background);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            const string none = "--.-";
            string unit = LevelPercent ? "%" : "kHz";

            string F(float khz) => LevelPercent ? string.Format("{0:0.0}", 100.0f * khz / MAX_DEV_KHZ) : string.Format("{0:0.0}", khz);
            Color ModColor(float khz) => khz > MAX_DEV_KHZ ? Theme.Over : Theme.Text;
            // Audio can use 100% minus the pilot: amber above that, red above 100%
            float audioMax = MAX_DEV_KHZ - (valid && meas.PilotPresent ? meas.PilotKHz : 0.0f);
            Color DemodColor(float khz) => khz > MAX_DEV_KHZ ? Theme.Over : khz > audioMax + 0.05f ? Theme.Warn : Theme.Text;

            string mono = valid && !meas.Stereo ? ", mono" : "";
            string[] labels = { "+ Mod (" + unit + ")", "− Mod (" + unit + ")", "De-mod L" + mono + " (" + unit + ")", "De-mod R" + mono + " (" + unit + ")" };
            string[] values = valid ? new[] { F(meas.PosPeakKHz), F(meas.NegPeakKHz), F(meas.DemodLKHz), F(meas.DemodRKHz) } : new[] { none, none, none, none };
            Color[] colors = valid
                ? new[] { ModColor(meas.PosPeakKHz), ModColor(meas.NegPeakKHz), DemodColor(meas.DemodLKHz), DemodColor(meas.DemodRKHz) }
                : new[] { Theme.Dim, Theme.Dim, Theme.Dim, Theme.Dim };
            float cw = (Width - 8) / (float)labels.Length;
            using var dim = new SolidBrush(Theme.Dim);
            using var border = new Pen(Theme.Grid);
            for (int i = 0; i < labels.Length; i++)
            {
                string label = labels[i], value = values[i];
                Color col = colors[i];
                float x = 4 + i * cw;
                g.DrawRectangle(border, x + 1, 2, cw - 3, Height - 5);
                var ls = g.MeasureString(label, Font);
                g.DrawString(label, Font, dim, x + (cw - ls.Width) / 2, 3);
                var vs = g.MeasureString(value, valueFont);
                using var b = new SolidBrush(col);
                g.DrawString(value, valueFont, b, x + (cw - vs.Width) / 2, 3 + Font.Height + 1);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { valueFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Oscilloscope panel: readouts on top, the scope, and on the right the signal (MPX, L, R, L+R, L-R) and time
    // base. Docked by SDR# like the spectrum (MpxScopePlugin)
    public sealed class ScopeDisplay : UserControl
    {
        static readonly ScopeSignal[] SIGNALS = { ScopeSignal.Mpx, ScopeSignal.Left, ScopeSignal.Right, ScopeSignal.Mid, ScopeSignal.Side };
        static readonly string[] SIGNAL_NAMES = { "MPX", "L", "R", "L+R", "L−R" };
        static readonly string[] SIGNAL_TIPS =
        {
            "The composite (multiplex) signal",
            "Decoded left channel (no de-emphasis)",
            "Decoded right channel (no de-emphasis)",
            "(L+R)/2: the mono part of the deviation",
            "(L−R)/2: the 38 kHz stereo subchannel's part of the deviation",
        };

        readonly MpxCore core;
        readonly ScopeView scope;
        readonly ScopeMeters meters;
        readonly Panel side;
        readonly RadioButton[] radios;
        readonly ComboBox timeCombo;
        readonly Label timeLabel;
        readonly ToolTip toolTip = new ToolTip();

        internal ScopeDisplay(MpxCore core)
        {
            this.core = core;
            Text = "MPX Scope";
            Name = "MPX Scope";
            Size = new Size(720, 260);
            MinimumSize = new Size(360, 150);
            var s = core.Settings;

            scope = new ScopeView { Dock = DockStyle.Fill, Signal = (ScopeSignal)s.ScopeSignal, TimeMs = s.ScopeTimeMs };
            meters = new ScopeMeters { Dock = DockStyle.Top };
            toolTip.SetToolTip(meters, "Peaks over the last second, with the deviation response time set in the plugin panel.\n" +
                                       "+ / − Mod: positive and negative peaks of the composite: clipping shows as flat, equal peaks.\n" +
                                       "De-mod: peaks of the decoded L and R as transmitted (no de-emphasis). Amber above 100% minus\n" +
                                       "the pilot (all the audio can use), red above 100%.");

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
            radios = new RadioButton[SIGNALS.Length];
            for (int i = 0; i < SIGNALS.Length; i++)
            {
                var sig = SIGNALS[i];
                var rb = new RadioButton { Text = SIGNAL_NAMES[i], AutoSize = true, Checked = sig == scope.Signal, Margin = new Padding(3, 1, 3, 1) };
                rb.CheckedChanged += (o, e) =>
                {
                    if (!((RadioButton)o).Checked) { return; }
                    core.Settings.ScopeSignal = (int)sig;
                    core.SaveSettings();
                    scope.Signal = sig;
                    scope.Clear();
                };
                toolTip.SetToolTip(rb, SIGNAL_TIPS[i]);
                radios[i] = rb;
                flow.Controls.Add(rb);
            }
            timeLabel = new Label { Text = "Time", AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
            flow.Controls.Add(timeLabel);
            timeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64, Margin = new Padding(3, 1, 3, 1) };
            foreach (int ms in MpxSettings.SCOPE_TIMES_MS) { timeCombo.Items.Add(ms + " ms"); }
            timeCombo.SelectedIndex = Math.Max(Array.IndexOf(MpxSettings.SCOPE_TIMES_MS, s.ScopeTimeMs), 0);
            timeCombo.SelectedIndexChanged += (o, e) =>
            {
                core.Settings.ScopeTimeMs = MpxSettings.SCOPE_TIMES_MS[timeCombo.SelectedIndex];
                core.SaveSettings();
                scope.TimeMs = core.Settings.ScopeTimeMs;
                scope.Clear();
            };
            toolTip.SetToolTip(timeCombo, "Time across the scope");
            flow.Controls.Add(timeCombo);

            side = new Panel { Dock = DockStyle.Right, Width = Math.Max(flow.PreferredSize.Width, timeCombo.Width + 8) + 4, Padding = new Padding(2) };
            side.Controls.Add(flow);

            Controls.Add(scope);
            Controls.Add(side);
            Controls.Add(meters);
            ApplyTheme();
        }

        internal void ApplyTheme()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            side.BackColor = Theme.Background;
            foreach (var rb in radios) { rb.ForeColor = Theme.Text; }
            timeLabel.ForeColor = Theme.Dim;
            scope.Clear();
            scope.Invalidate();
            meters.Invalidate();
        }

        // Called by the GUI timer while visible
        internal void UpdateView()
        {
            var s = core.Settings;
            string msg = null;
            if (core.Hook.Error != null) { msg = "Processing error, see the MPX Analyzer panel"; }
            else if (!core.IsPlaying || core.Chain.IqRate <= 0.0) { msg = "SDR# is not running"; }
            else if (!core.IsWfm) { msg = "Set the demodulator to WFM"; }

            scope.LevelPercent = meters.LevelPercent = s.LevelPercent;
            scope.UpdateData(core.Chain, msg);
            meters.UpdateData(core.Chain, msg == null);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                core.RemoveScope(this);
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
