using System;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // The MPX spectrum with its toolbar and level readouts. Hosted by an SDR# dock panel (current SDR#) or by the
    // floating MpxWindow (older SDR#)
    public sealed class MpxDisplay : UserControl
    {
        readonly MpxCore core;
        readonly Label freqLabel;
        readonly Label infoLabel;
        readonly CheckBox peakHoldCheck;
        readonly Button resetPeakButton;
        readonly Label recTimeLabel;
        readonly Button recordButton;
        readonly SpectrumView spectrum;
        readonly MetersView meters;
        readonly Button resetMeasButton;
        readonly ToolTip toolTip = new ToolTip();
        readonly TableLayoutPanel toolbar;
        readonly Label scaleNote;
        readonly Panel right;
        bool updating;

        internal MpxDisplay(MpxCore core)
        {
            this.core = core;
            Size = new Size(720, 300);
            MinimumSize = new Size(360, 160);

            // Toolbar: frequency, info, peak hold, record button
            toolbar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 7, Padding = new Padding(4, 3, 4, 3) };
            for (int i = 0; i < 7; i++) { toolbar.ColumnStyles.Add(new ColumnStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, 100.0f)); }

            freqLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold) };
            infoLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
            peakHoldCheck = new CheckBox { Text = "Peak hold", AutoSize = true, Anchor = AnchorStyles.Left };
            peakHoldCheck.CheckedChanged += (o, e) =>
            {
                if (updating) { return; }
                core.SetPeakHold(peakHoldCheck.Checked);
            };
            resetPeakButton = new Button { Text = "Reset", AutoSize = true, Anchor = AnchorStyles.Left };
            resetPeakButton.Click += (o, e) => core.Chain.ResetPeak();
            scaleNote = new Label { Text = "0 dB = 75 kHz dev.", AutoSize = true, Anchor = AnchorStyles.Right };
            recTimeLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Right };
            recordButton = new Button { Size = new Size(26, 26), Anchor = AnchorStyles.Right, Margin = new Padding(6, 0, 0, 0) };
            recordButton.Paint += PaintRecordButton;
            recordButton.Click += (o, e) => core.ToggleRecording(FindForm());

            toolbar.Controls.Add(freqLabel, 0, 0);
            toolbar.Controls.Add(infoLabel, 1, 0);
            toolbar.Controls.Add(peakHoldCheck, 2, 0);
            toolbar.Controls.Add(resetPeakButton, 3, 0);
            toolbar.Controls.Add(scaleNote, 4, 0);
            toolbar.Controls.Add(recTimeLabel, 5, 0);
            toolbar.Controls.Add(recordButton, 6, 0);

            // Spectrum on the left, meters on the right
            spectrum = new SpectrumView { Dock = DockStyle.Fill };
            meters = new MetersView { Dock = DockStyle.Fill };
            resetMeasButton = new Button { Text = "Reset", AutoSize = true, Dock = DockStyle.Bottom };
            resetMeasButton.Click += (o, e) => core.Chain.ResetMeasurements();
            toolTip.SetToolTip(resetMeasButton, "Restart the measurements (also done automatically when retuning)");
            right = new Panel { Dock = DockStyle.Right, Width = meters.PreferredWidth, Padding = new Padding(0, 4, 4, 4) };
            right.Controls.Add(meters);
            right.Controls.Add(resetMeasButton);

            Controls.Add(spectrum);
            Controls.Add(right);
            Controls.Add(toolbar);
            ApplyTheme();
        }

        // Colours of our own parts from SDR#'s theme. Called again when SDR#'s theme changes
        internal void ApplyTheme()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            toolbar.BackColor = Theme.Toolbar;
            right.BackColor = Theme.Background;
            freqLabel.ForeColor = Theme.ToolbarText;
            peakHoldCheck.ForeColor = Theme.ToolbarText;
            infoLabel.ForeColor = Theme.ToolbarDim;
            scaleNote.ForeColor = Theme.ToolbarDim;
            recTimeLabel.ForeColor = Theme.Over;
            spectrum.Invalidate();
            meters.Invalidate();
        }

        void PaintRecordButton(object sender, PaintEventArgs e)
        {
            bool recording = core.Chain.IsRecording;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float size = Math.Min(recordButton.Width, recordButton.Height);
            float cx = recordButton.Width / 2.0f, cy = recordButton.Height / 2.0f;
            using var b = new SolidBrush(Theme.Record);
            if (recording)
            {
                float h = size * 0.22f;
                g.FillRectangle(b, cx - h, cy - h, 2 * h, 2 * h);
            }
            else
            {
                float r = size * 0.27f;
                g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
            }
        }

        // Called by the GUI timer while visible
        internal void UpdateView()
        {
            var s = core.Settings;
            var chain = core.Chain;

            freqLabel.Text = string.Format("{0:0.0000} MHz", core.Frequency / 1e6);
            double rate = chain.IqRate;
            infoLabel.Text = rate > 0.0 ? string.Format("IF {0:0} kHz", core.FilterBandwidth / 1000.0) : "";

            updating = true;
            peakHoldCheck.Checked = s.PeakHold;
            updating = false;
            resetPeakButton.Visible = s.PeakHold;

            bool recording = chain.IsRecording;
            recTimeLabel.Text = recording ? "REC " + ControlPanel.FormatTime(chain.RecordedSamples) : "";
            toolTip.SetToolTip(recordButton, recording ? "Stop recording" : "Record MPX");
            recordButton.Invalidate();

            // Why there's nothing to show
            string msg = null;
            if (core.Hook.Error != null) { msg = "Processing error, see the MPX Analyzer panel"; }
            else if (!core.IsPlaying || rate <= 0.0) { msg = "SDR# is not running"; }
            else if (!core.IsWfm) { msg = "Set the demodulator to WFM"; }

            spectrum.MaxFreqKHz = s.MaxFreqKHz;
            spectrum.DbMin = s.DbMin;
            spectrum.DbMax = s.DbMax;
            spectrum.PeakHold = s.PeakHold;
            spectrum.ShowMarkers = s.ShowMarkers;
            spectrum.UpdateData(chain, msg);

            meters.LevelPercent = s.LevelPercent;
            meters.PowerLimitDBr = s.MpxPowerLimitDBr;
            meters.UpdateData(chain, msg == null);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                core.RemoveDisplay(this);
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
