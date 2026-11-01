using System;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // Floating window with the MPX spectrum and the level readouts
    public sealed class MpxWindow : Form
    {
        readonly MpxPlugin plugin;
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
        bool closingForGood;
        bool updating;

        public MpxWindow(MpxPlugin plugin)
        {
            this.plugin = plugin;
            Text = "MPX Spectrum";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            MinimumSize = new Size(480, 300);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            // Toolbar: frequency, info, peak hold, record button
            toolbar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 7, Padding = new Padding(4, 3, 4, 3) };
            for (int i = 0; i < 7; i++) { toolbar.ColumnStyles.Add(new ColumnStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, 100.0f)); }

            freqLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold) };
            infoLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
            peakHoldCheck = new CheckBox { Text = "Peak hold", AutoSize = true, Anchor = AnchorStyles.Left };
            peakHoldCheck.CheckedChanged += (o, e) =>
            {
                if (updating) { return; }
                plugin.Panel?.SetPeakHold(peakHoldCheck.Checked);
            };
            resetPeakButton = new Button { Text = "Reset", AutoSize = true, Anchor = AnchorStyles.Left };
            resetPeakButton.Click += (o, e) => plugin.Chain.ResetPeak();
            scaleNote = new Label { Text = "0 dB = 75 kHz dev.", AutoSize = true, Anchor = AnchorStyles.Right };
            recTimeLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Right };
            recordButton = new Button { Size = new Size(26, 26), Anchor = AnchorStyles.Right, Margin = new Padding(6, 0, 0, 0) };
            recordButton.Paint += PaintRecordButton;
            recordButton.Click += (o, e) => plugin.Panel?.ToggleRecording(this);

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
            resetMeasButton.Click += (o, e) => plugin.Chain.ResetMeasurements();
            toolTip.SetToolTip(resetMeasButton, "Restart the measurements (also done automatically when retuning)");
            right = new Panel { Dock = DockStyle.Right, Width = meters.PreferredWidth, Padding = new Padding(0, 4, 4, 4) };
            right.Controls.Add(meters);
            right.Controls.Add(resetMeasButton);

            Controls.Add(spectrum);
            Controls.Add(right);
            Controls.Add(toolbar);
            ApplyTheme();
        }

        // SDR#'s theme for the standard controls, then the colours of our own parts. Called again when SDR#'s
        // theme changes
        internal void ApplyTheme()
        {
            Theme.ApplyTo(this);
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTo(this);
        }

        void PaintRecordButton(object sender, PaintEventArgs e)
        {
            bool recording = plugin.Chain.IsRecording;
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

        // Called by the plugin's timer while visible
        internal void UpdateView()
        {
            var s = plugin.Settings;
            var chain = plugin.Chain;

            freqLabel.Text = string.Format("{0:0.0000} MHz", plugin.Frequency / 1e6);
            double rate = chain.IqRate;
            infoLabel.Text = rate > 0.0 ? string.Format("IF {0:0} kHz", plugin.FilterBandwidth / 1000.0) : "";

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
            if (plugin.Hook.Error != null) { msg = "Processing error, see the plugin panel"; }
            else if (!plugin.IsPlaying || rate <= 0.0) { msg = "SDR# is not running"; }
            else if (!plugin.IsWfm) { msg = "Set the demodulator to WFM"; }

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

        // Closing by the user only hides the window, so it can be shown again from the plugin panel
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closingForGood && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                plugin.OnWindowClosed();
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        internal void CloseForGood()
        {
            closingForGood = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { toolTip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
