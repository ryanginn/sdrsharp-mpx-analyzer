using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.MPXAnalyzer.Dsp;
using SDRSharp.MPXAnalyzer.Gui;
using SDRSharp.Radio;

namespace SDRSharp.MPXAnalyzer
{
    // FM MPX spectrum analyzer and recorder for SDR#.
    //
    // The plugin taps SDR#'s decimated and filtered IQ, i.e. the Radio's own channel after its IF filter, so it
    // always follows the frequency and filter bandwidth set in SDR#'s main window. It FM-demodulates that IQ
    // itself, so the MPX scaling (+/-1.0 = 75kHz deviation) doesn't depend on SDR#'s audio settings.
    public class MpxPlugin : ISharpPlugin, ICanLazyLoadGui, IExtendedNameProvider
    {
        ISharpControl control;
        MpxChain chain;
        MpxIQHook hook;
        MpxSettings settings;
        ControlPanel gui;
        MpxWindow window;
        System.Windows.Forms.Timer timer;

        // Retune detection, and the frequency for file names (read on the GUI thread, used on the DSP thread)
        long frequency;
        long measFrequency = -1;
        int measBandwidth = -1;
        double measRate = -1;

        public string DisplayName => "MPX Analyzer";

        public string Category => "Analysis";

        public string MenuItemName => DisplayName;

        public bool IsActive => gui != null && gui.Visible;

        public UserControl Gui
        {
            get
            {
                LoadGui();
                return gui;
            }
        }

        public void LoadGui()
        {
            if (gui != null) { return; }
            gui = new ControlPanel(this);

            // GUI refresh (~30 fps), on the GUI thread
            timer = new System.Windows.Forms.Timer { Interval = 33 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            if (settings.ShowWindow) { gui.HandleCreated += (s, e) => gui.BeginInvoke(new Action(() => ShowWindow(true, gui.FindForm()))); }
        }

        void Tick()
        {
            Interlocked.Exchange(ref frequency, ReadFrequency());
            long freq = Frequency;

            // Measurements from a different station or filter setting shouldn't be mixed
            int bw = FilterBandwidth;
            double rate = chain.IqRate;
            if (freq != measFrequency || bw != measBandwidth || rate != measRate)
            {
                chain.ResetMeasurements();
                measFrequency = freq;
                measBandwidth = bw;
                measRate = rate;
            }

            gui.UpdateStatus();
            if (window != null && window.Visible) { window.UpdateView(); }
        }

        public void Initialize(ISharpControl control)
        {
            this.control = control;
            settings = MpxSettings.Load();
            chain = new MpxChain();
            chain.SetAveraging(settings.AvgFrames);
            chain.SetPeakResponse(settings.PeakResponseUs * 1e-6);
            chain.SetAnalysis(false);   // Until the window is shown
            hook = new MpxIQHook(chain);
            control.RegisterStreamHook(hook, ProcessorType.DecimatedAndFilteredIQ);
        }

        public void Close()
        {
            timer?.Stop();
            if (control != null && hook != null) { control.UnregisterStreamHook(hook); }
            chain?.StopRecording();
            if (window != null)
            {
                SaveWindowBounds();
                window.CloseForGood();
                window = null;
            }
            settings?.Save();
        }

        // ---- Used by the GUI ----

        internal ISharpControl Control => control;
        internal MpxChain Chain => chain;
        internal MpxIQHook Hook => hook;
        internal MpxSettings Settings => settings;
        internal ControlPanel Panel => gui;

        internal void SaveSettings() => settings.Save();

        internal bool WindowVisible => window != null && window.Visible;

        internal void ShowWindow(bool show, IWin32Window owner = null)
        {
            settings.ShowWindow = show;
            settings.Save();
            if (show)
            {
                if (window == null || window.IsDisposed)
                {
                    window = new MpxWindow(this);
                    var b = new System.Drawing.Rectangle(settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight);
                    if (settings.WindowX != int.MinValue && IsOnScreen(b))
                    {
                        window.StartPosition = FormStartPosition.Manual;
                        window.Bounds = b;
                    }
                    else
                    {
                        window.Size = b.Size;
                    }
                }
                chain.SetAnalysis(true);
                if (!window.Visible)
                {
                    if (owner != null) { window.Show(owner); } else { window.Show(); }
                }
                window.Activate();
            }
            else
            {
                chain.SetAnalysis(false);
                if (window != null && window.Visible)
                {
                    SaveWindowBounds();
                    window.Hide();
                }
            }
        }

        // Called when the user closes the window
        internal void OnWindowClosed()
        {
            SaveWindowBounds();
            settings.ShowWindow = false;
            settings.Save();
            chain.SetAnalysis(false);
        }

        void SaveWindowBounds()
        {
            if (window == null || window.WindowState != FormWindowState.Normal) { return; }
            settings.WindowX = window.Left;
            settings.WindowY = window.Top;
            settings.WindowWidth = window.Width;
            settings.WindowHeight = window.Height;
        }

        static bool IsOnScreen(System.Drawing.Rectangle r)
        {
            foreach (var s in Screen.AllScreens)
            {
                if (s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(r.X, r.Y, Math.Min(r.Width, 200), 40))) { return true; }
            }
            return false;
        }

        // Tuned frequency (Hz), as of the last GUI update
        internal long Frequency => Interlocked.Read(ref frequency);

        long ReadFrequency()
        {
            try { return control.Frequency; }
            catch { return 0; }
        }

        // SDR#'s IF filter bandwidth (Hz), or 0 if unknown
        internal int FilterBandwidth
        {
            get
            {
                try { return control.FilterBandwidth; }
                catch { return 0; }
            }
        }

        internal bool IsWfm
        {
            get
            {
                try { return control.DetectorType == DetectorType.WFM; }
                catch { return true; }
            }
        }

        internal bool IsPlaying
        {
            get
            {
                try { return control.IsPlaying; }
                catch { return true; }
            }
        }

        internal bool RecordFolderValid => !string.IsNullOrWhiteSpace(settings.RecPath) && Directory.Exists(settings.RecPath);

        // Creates the default folder if needed. Returns false if recording can't start
        internal bool StartRecording(out string error)
        {
            error = null;
            Interlocked.Exchange(ref frequency, ReadFrequency());
            try
            {
                if (!Directory.Exists(settings.RecPath)) { Directory.CreateDirectory(settings.RecPath); }
            }
            catch (Exception e)
            {
                error = "Can't create the recording folder: " + e.Message;
                return false;
            }
            if (!chain.StartRecording(GenFileName, settings.SampleType))
            {
                error = chain.RecordingError;
                return false;
            }
            return true;
        }

        internal void StopRecording() => chain.StopRecording();

        // mpx_<frequency>Hz_<YYYYMMDD>_<HHMMSS>.wav, without overwriting an existing file
        string GenFileName()
        {
            string name = string.Format("mpx_{0}Hz_{1:yyyyMMdd}_{1:HHmmss}", Frequency, DateTime.Now);
            string basePath = Path.Combine(settings.RecPath, name);
            string path = basePath + ".wav";
            for (int i = 1; File.Exists(path); i++) { path = basePath + "_" + i + ".wav"; }
            return path;
        }
    }
}
