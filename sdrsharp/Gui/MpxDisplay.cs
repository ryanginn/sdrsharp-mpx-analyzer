using System;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // The MPX spectrum with its level readouts and controls. Docked in SDR#'s main window next to its own spectra
    // (current SDR#) or hosted by the floating MpxWindow (older SDR#).
    //
    // Layout: spectrum on the left using the full height; on the right the record button, the meters, and peak
    // hold / reset at the bottom. Frequency and filter aren't repeated here, SDR# shows them already
    public sealed class MpxDisplay : UserControl
    {
        readonly MpxCore core;
        readonly CheckBox peakHoldCheck;
        readonly Button resetPeakButton;
        readonly Label recTimeLabel;
        readonly Button recordButton;
        readonly SpectrumView spectrum;
        readonly MetersView meters;
        readonly Button resetMeasButton;
        readonly ToolTip toolTip = new ToolTip();
        readonly Panel right;
        readonly TableLayoutPanel recRow;
        readonly TableLayoutPanel peakRow;
        bool updating;
        string recordTip;

        internal MpxDisplay(MpxCore core)
        {
            this.core = core;
            Text = "MPX Spectrum";
            Name = "MPX Spectrum";
            Size = new Size(720, 260);
            MinimumSize = new Size(360, 150);

            spectrum = new SpectrumView { Dock = DockStyle.Fill };

            // Right click on the spectrum toggles peak hold (also shows immediately, without waiting for the timer)
            spectrum.MouseUp += (o, e) =>
            {
                if (e.Button != MouseButtons.Right) { return; }
                core.SetPeakHold(!core.Settings.PeakHold);
                updating = true;
                peakHoldCheck.Checked = core.Settings.PeakHold;
                updating = false;
                resetPeakButton.Visible = core.Settings.PeakHold;
                spectrum.PeakHold = core.Settings.PeakHold;
                spectrum.Invalidate();
            };
            meters = new MetersView { Dock = DockStyle.Fill };
            int rightWidth = meters.PreferredWidth;

            // Top of the meters column: record button and elapsed time
            recRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            recRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            recRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            recTimeLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
            recordButton = new Button { Size = new Size(24, 24), Anchor = AnchorStyles.Right, Margin = new Padding(2) };
            recordButton.Paint += PaintRecordButton;
            recordButton.Click += (o, e) => core.ToggleRecording(FindForm());
            SetRecordTip("Record MPX");
            recRow.Controls.Add(recTimeLabel, 0, 0);
            recRow.Controls.Add(recordButton, 1, 0);

            // Bottom: peak hold, then the measurements reset
            peakRow = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            peakRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            peakRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            peakHoldCheck = new CheckBox { Text = "Peak hold", AutoSize = true, Anchor = AnchorStyles.Left };
            peakHoldCheck.CheckedChanged += (o, e) =>
            {
                if (updating) { return; }
                core.SetPeakHold(peakHoldCheck.Checked);
            };
            resetPeakButton = new Button { Text = "Clear", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(2) };
            resetPeakButton.Click += (o, e) => core.Chain.ResetPeak();
            toolTip.SetToolTip(resetPeakButton, "Clear the peak hold trace");
            peakRow.Controls.Add(peakHoldCheck, 0, 0);
            peakRow.Controls.Add(resetPeakButton, 1, 0);

            // Just as wide as the readings need (and the peak hold row)
            rightWidth = Math.Max(rightWidth, peakHoldCheck.PreferredSize.Width + resetPeakButton.PreferredSize.Width + 12);

            resetMeasButton = new Button { Text = "Reset", AutoSize = true, Dock = DockStyle.Bottom };
            resetMeasButton.Click += (o, e) => core.Chain.ResetMeasurements();
            toolTip.SetToolTip(resetMeasButton, "Restart the measurements (also done automatically when retuning)");

            right = new Panel { Dock = DockStyle.Right, Width = rightWidth + 4, Padding = new Padding(0, 2, 4, 4) };
            right.Controls.Add(meters);
            right.Controls.Add(recRow);
            right.Controls.Add(peakRow);
            right.Controls.Add(resetMeasButton);

            Controls.Add(spectrum);
            Controls.Add(right);
            ApplyTheme();
        }

        // Colours of our own parts from SDR#'s theme. Called again when SDR#'s theme changes
        internal void ApplyTheme()
        {
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            right.BackColor = Theme.Background;
            recRow.BackColor = peakRow.BackColor = Theme.Background;
            peakHoldCheck.ForeColor = Theme.Text;
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

        // Only when the text changes: SetToolTip() on every timer tick makes WinForms re-show the tip, and it can
        // get stuck on screen (tooltips are topmost windows, so it then stays above every other window)
        void SetRecordTip(string tip)
        {
            if (tip == recordTip) { return; }
            recordTip = tip;
            toolTip.SetToolTip(recordButton, tip);
        }

        // A tip that's up when the display is hidden (SDR# panel switched, window closed) would be left behind
        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible) { toolTip.Hide(recordButton); }
            base.OnVisibleChanged(e);
        }

        // Called by the GUI timer while visible
        internal void UpdateView()
        {
            var s = core.Settings;
            var chain = core.Chain;
            double rate = chain.IqRate;

            updating = true;
            peakHoldCheck.Checked = s.PeakHold;
            updating = false;
            resetPeakButton.Visible = s.PeakHold;

            bool recording = chain.IsRecording;
            recTimeLabel.Text = recording ? "REC " + ControlPanel.FormatTime(chain.RecordedSamples) : "";
            SetRecordTip(recording ? "Stop recording" : "Record MPX");
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
