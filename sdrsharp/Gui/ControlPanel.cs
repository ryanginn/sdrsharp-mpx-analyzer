using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer.Gui
{
    // The plugin's section in SDR#'s side panel: display and recording settings
    public sealed class ControlPanel : UserControl
    {
        readonly MpxPlugin plugin;
        readonly TableLayoutPanel table;
        readonly Button showButton;
        readonly Label statusLabel;
        readonly Label warnLabel;
        readonly NumericUpDown avgUpDown;
        readonly NumericUpDown maxFreqUpDown;
        readonly NumericUpDown dbMaxUpDown;
        readonly NumericUpDown dbMinUpDown;
        readonly CheckBox peakHoldCheck;
        readonly ComboBox unitsCombo;
        readonly TextBox folderBox;
        readonly Button folderButton;
        readonly ComboBox typeCombo;
        readonly Button recordButton;
        readonly Label recLabel;
        readonly ToolTip toolTip = new ToolTip();
        bool updating;

        public ControlPanel(MpxPlugin plugin)
        {
            this.plugin = plugin;
            var s = plugin.Settings;

            table = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(2)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));

            showButton = new Button { Text = "Show MPX window", Dock = DockStyle.Fill, AutoSize = true };
            showButton.Click += (o, e) => plugin.ShowWindow(!plugin.WindowVisible, FindForm());
            AddWide(showButton);

            statusLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText };
            AddWide(statusLabel);
            warnLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DarkOrange, Visible = false };
            AddWide(warnLabel);

            avgUpDown = AddNumber("Averaging", 1, 100, s.AvgFrames, "FFT frames averaged (about 94 frames/s)");
            avgUpDown.ValueChanged += (o, e) =>
            {
                if (updating) { return; }
                s.AvgFrames = (int)avgUpDown.Value;
                plugin.Chain.SetAveraging(s.AvgFrames);
                plugin.SaveSettings();
            };

            maxFreqUpDown = AddNumber("Max freq (kHz)", 20, (int)(MpxChain.SAMPLERATE / 2000), (int)s.MaxFreqKHz, "Upper edge of the display");
            maxFreqUpDown.ValueChanged += (o, e) =>
            {
                if (updating) { return; }
                s.MaxFreqKHz = (float)maxFreqUpDown.Value;
                plugin.SaveSettings();
            };

            dbMaxUpDown = AddNumber("dB max", -60, 20, (int)s.DbMax, "Top of the level scale (0 dB = sine at 75 kHz deviation)");
            dbMinUpDown = AddNumber("dB min", -160, -20, (int)s.DbMin, "Bottom of the level scale");
            dbMaxUpDown.Increment = dbMinUpDown.Increment = 5;
            dbMaxUpDown.ValueChanged += (o, e) => DbChanged(true);
            dbMinUpDown.ValueChanged += (o, e) => DbChanged(false);

            peakHoldCheck = new CheckBox { Text = "Peak hold", Checked = s.PeakHold, AutoSize = true, Anchor = AnchorStyles.Left };
            peakHoldCheck.CheckedChanged += (o, e) =>
            {
                if (updating) { return; }
                SetPeakHold(peakHoldCheck.Checked);
            };
            var resetPeak = new Button { Text = "Reset peak", AutoSize = true, Anchor = AnchorStyles.Right };
            resetPeak.Click += (o, e) => plugin.Chain.ResetPeak();
            table.Controls.Add(peakHoldCheck);
            table.Controls.Add(resetPeak);

            unitsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            unitsCombo.Items.AddRange(new object[] { "kHz", "% (100% = 75 kHz)" });
            unitsCombo.SelectedIndex = s.LevelPercent ? 1 : 0;
            unitsCombo.SelectedIndexChanged += (o, e) =>
            {
                s.LevelPercent = unitsCombo.SelectedIndex == 1;
                plugin.SaveSettings();
            };
            AddRow("Level units", unitsCombo, "Deviation, pilot and RDS levels in kHz or percent");

            // Recording
            var folderPanel = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f));
            folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderBox = new TextBox { Text = s.RecPath, Dock = DockStyle.Fill };
            folderBox.Leave += (o, e) => FolderChanged(folderBox.Text);
            folderBox.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { FolderChanged(folderBox.Text); e.SuppressKeyPress = true; } };
            folderButton = new Button { Text = "...", Width = 28, Height = folderBox.PreferredHeight, Margin = new Padding(2, 0, 0, 0) };
            folderButton.Click += (o, e) => BrowseFolder();
            folderPanel.Controls.Add(folderBox, 0, 0);
            folderPanel.Controls.Add(folderButton, 1, 0);
            AddRow("Folder", folderPanel, "Recordings are saved here as mpx_<frequency>Hz_<date>_<time>.wav");

            typeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            typeCombo.Items.AddRange(new object[] { "Int16", "Int32", "Float32" });
            typeCombo.SelectedIndex = (int)s.SampleType;
            typeCombo.SelectedIndexChanged += (o, e) =>
            {
                s.SampleType = (WavSampleType)typeCombo.SelectedIndex;
                plugin.SaveSettings();
            };
            AddRow("Sample type", typeCombo, "Int16/Int32 clip at 75 kHz deviation, Float32 doesn't");

            recordButton = new Button { Text = "Record MPX", Dock = DockStyle.Fill, AutoSize = true };
            recordButton.Click += (o, e) => ToggleRecording(this);
            AddWide(recordButton);
            recLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Idle --:--:--" };
            AddWide(recLabel);

            Controls.Add(table);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(180, 0);
            UpdateStatus();
        }

        void AddRow(string label, Control c, string tip)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left };
            table.Controls.Add(l);
            table.Controls.Add(c);
            if (tip != null)
            {
                toolTip.SetToolTip(l, tip);
                toolTip.SetToolTip(c, tip);
            }
        }

        void AddWide(Control c)
        {
            table.Controls.Add(c);
            table.SetColumnSpan(c, 2);
        }

        NumericUpDown AddNumber(string label, int min, int max, int value, string tip)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Dock = DockStyle.Fill };
            AddRow(label, n, tip);
            return n;
        }

        void DbChanged(bool max)
        {
            if (updating) { return; }
            var s = plugin.Settings;
            s.DbMax = (float)dbMaxUpDown.Value;
            s.DbMin = (float)dbMinUpDown.Value;
            if (s.DbMin > s.DbMax - 10.0f)
            {
                if (max) { s.DbMin = s.DbMax - 10.0f; } else { s.DbMax = s.DbMin + 10.0f; }
            }
            SyncFromSettings();
            plugin.SaveSettings();
        }

        // Peak hold can also be changed from the MPX window
        internal void SetPeakHold(bool on)
        {
            plugin.Settings.PeakHold = on;
            plugin.Chain.ResetPeak();
            plugin.SaveSettings();
            SyncFromSettings();
        }

        void SyncFromSettings()
        {
            var s = plugin.Settings;
            updating = true;
            dbMaxUpDown.Value = Math.Clamp((decimal)s.DbMax, dbMaxUpDown.Minimum, dbMaxUpDown.Maximum);
            dbMinUpDown.Value = Math.Clamp((decimal)s.DbMin, dbMinUpDown.Minimum, dbMinUpDown.Maximum);
            peakHoldCheck.Checked = s.PeakHold;
            updating = false;
        }

        void FolderChanged(string path)
        {
            path = path.Trim();
            if (path == plugin.Settings.RecPath || path.Length == 0)
            {
                folderBox.Text = plugin.Settings.RecPath;
                return;
            }
            plugin.Settings.RecPath = path;
            plugin.SaveSettings();
        }

        void BrowseFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Folder for MPX recordings";
                if (Directory.Exists(plugin.Settings.RecPath)) { dlg.SelectedPath = plugin.Settings.RecPath; }
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    folderBox.Text = dlg.SelectedPath;
                    FolderChanged(dlg.SelectedPath);
                }
            }
        }

        // Shared with the MPX window's record button
        internal void ToggleRecording(IWin32Window owner)
        {
            if (plugin.Chain.IsRecording)
            {
                plugin.StopRecording();
            }
            else
            {
                FolderChanged(folderBox.Text);
                if (!plugin.StartRecording(out string error))
                {
                    MessageBox.Show(owner, error, "MPX Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            UpdateStatus();
        }

        internal static string FormatTime(long samples)
        {
            long seconds = samples / (long)MpxChain.SAMPLERATE;
            return string.Format("{0:00}:{1:00}:{2:00}", seconds / 3600, (seconds / 60) % 60, seconds % 60);
        }

        // Called by the plugin's timer
        internal void UpdateStatus()
        {
            var chain = plugin.Chain;
            showButton.Text = plugin.WindowVisible ? "Hide MPX window" : "Show MPX window";

            double rate = chain.IqRate;
            int bw = plugin.FilterBandwidth;
            statusLabel.Text = (rate > 0.0)
                ? string.Format("IF {0:0} kHz, IQ {1:0.#} kS/s", bw / 1000.0, rate / 1000.0)
                : "Waiting for IQ (press Play)";

            // Conditions that make the MPX unusable or inaccurate
            string warn = null;
            if (plugin.Hook.Error != null)
            {
                warn = "Processing error, restart SDR#:\n" + plugin.Hook.Error.Split('\n')[0];
            }
            else if (!plugin.IsWfm)
            {
                warn = "Set the demodulator to WFM";
            }
            else if (rate > 0.0 && rate < MpxChain.SAMPLERATE)
            {
                warn = string.Format("IQ rate is below 192 kS/s: MPX above {0:0} kHz is lost. Widen the filter bandwidth.", rate / 2000.0);
            }
            else if (bw > 0 && bw < 180000)
            {
                warn = "Filter bandwidth below 180 kHz distorts the MPX. Use 200-250 kHz for measurements.";
            }
            warnLabel.Visible = warn != null;
            if (warn != null) { warnLabel.Text = warn; }
            warnLabel.MaximumSize = new Size(Math.Max(table.ClientSize.Width - 8, 100), 0);
            statusLabel.MaximumSize = warnLabel.MaximumSize;

            bool recording = chain.IsRecording;
            recordButton.Text = recording ? "Stop" : "Record MPX";
            folderBox.Enabled = folderButton.Enabled = typeCombo.Enabled = !recording;
            if (recording)
            {
                recLabel.ForeColor = Color.Red;
                recLabel.Text = "Recording " + FormatTime(chain.RecordedSamples) + "\n" + Path.GetFileName(chain.RecordingPath);
            }
            else
            {
                string err = chain.RecordingError;
                recLabel.ForeColor = (err != null) ? Color.DarkOrange : ForeColor;
                recLabel.Text = err ?? "Idle --:--:--";
            }
            recLabel.MaximumSize = warnLabel.MaximumSize;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { toolTip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
