using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.MPXAnalyzer.Dsp;
using SDRSharp.MPXAnalyzer.Gui;
using SDRSharp.Radio;

namespace SDRSharp.MPXAnalyzer
{
    // State shared by the plugin's panels: the DSP chain on SDR#'s IQ hook, the settings, and the GUI refresh.
    //
    // Current SDR# gets two plugins from this DLL, like its own FM MPX Spectrum: "MPX Analyzer" (settings and
    // recording) and "MPX Spectrum" (a dock panel with the spectrum and meters). Older SDR# (no dock panels) gets
    // the settings plugin, which shows the spectrum in a floating window. Whichever is created first sets this up.
    internal sealed class MpxCore
    {
        static MpxCore instance;
        static int refs;

        readonly ISharpControl control;
        readonly MpxChain chain;
        readonly MpxIQHook hook;
        readonly MpxSettings settings;
        readonly List<MpxDisplay> displays = new List<MpxDisplay>();
        readonly List<ScopeDisplay> scopes = new List<ScopeDisplay>();
        System.Windows.Forms.Timer timer;
        MpxWindow window;
        ScopeWindow scopeWindow;

        // Retune detection, and the frequency for file names (read on the GUI thread, used on the DSP thread)
        long frequency;
        int themeTicks;
        long measFrequency = -1;
        int measBandwidth = -1;
        double measRate = -1;

        public static MpxCore Acquire(ISharpControl control)
        {
            if (instance == null) { instance = new MpxCore(control); }
            refs++;
            return instance;
        }

        public static void Release()
        {
            if (instance == null || --refs > 0) { return; }
            instance.Shutdown();
            instance = null;
        }

        MpxCore(ISharpControl control)
        {
            this.control = control;
            settings = MpxSettings.Load();
            chain = new MpxChain();
            chain.SetAveraging(settings.AvgFrames);
            chain.SetPeakResponse(settings.PeakResponseUs * 1e-6);
            chain.SetAnalysis(false);   // Until a display is visible
            hook = new MpxIQHook(chain);
            control.RegisterStreamHook(hook, ProcessorType.DecimatedAndFilteredIQ);
        }

        void Shutdown()
        {
            timer?.Stop();
            control.UnregisterStreamHook(hook);
            chain.StopRecording();
            if (window != null)
            {
                SaveWindowBounds();
                window.CloseForGood();
                window = null;
            }
            if (scopeWindow != null)
            {
                SaveScopeWindowBounds();
                scopeWindow.CloseForGood();
                scopeWindow = null;
            }
            settings.Save();
        }

        public ISharpControl Control => control;
        public MpxChain Chain => chain;
        public MpxIQHook Hook => hook;
        public MpxSettings Settings => settings;
        public ControlPanel Panel { get; set; }

        // SDR# loaded the MPX Spectrum dock panel plugin. If not (SDR# that only loads the plugins listed in
        // Plugins.xml, e.g. 1716), the spectrum is shown in a floating window instead
        public bool SpectrumPanelLoaded { get; set; }

        // SDR# loaded the MPX Scope plugin. If not, the scope is shown in a floating window instead
        public bool ScopePanelLoaded { get; set; }

        public void SaveSettings() => settings.Save();

        // ---- GUI refresh ----

        // Called on the GUI thread when a panel is created
        public void StartGui()
        {
            if (timer != null) { return; }
            Theme.Refresh(control);
            timer = new System.Windows.Forms.Timer { Interval = 33 };     // ~30 fps
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        public void AddDisplay(MpxDisplay d)
        {
            StartGui();
            displays.Add(d);
        }

        public void RemoveDisplay(MpxDisplay d) => displays.Remove(d);

        public void AddScope(ScopeDisplay d)
        {
            StartGui();
            scopes.Add(d);
        }

        public void RemoveScope(ScopeDisplay d) => scopes.Remove(d);

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

            // Follow SDR#'s theme and spectrum display settings (checked about once a second)
            if (++themeTicks >= 30)
            {
                themeTicks = 0;
                if (Theme.Refresh(control))
                {
                    foreach (var d in displays) { d.ApplyTheme(); }
                    foreach (var d in scopes) { d.ApplyTheme(); }
                    window?.ApplyTheme();
                    scopeWindow?.ApplyTheme();
                }
            }

            Panel?.UpdateStatus();

            // The spectrum and measurements are only computed while a display is on screen
            bool shown = false;
            foreach (var d in displays)
            {
                if (d.Visible && d.IsHandleCreated)
                {
                    d.UpdateView();
                    shown = true;
                }
            }
            // Scopes refresh themselves (faster), they only need the analysis running
            foreach (var d in scopes)
            {
                if (d.Visible && d.IsHandleCreated) { shown = true; }
            }
            chain.SetAnalysis(shown);
        }

        // ---- Floating window (older SDR#, which has no dock panels) ----

        public bool WindowVisible => window != null && window.Visible;

        public void ShowWindow(bool show, IWin32Window owner = null)
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
                if (!window.Visible)
                {
                    if (owner != null) { window.Show(owner); } else { window.Show(); }
                }
                window.Activate();
            }
            else if (window != null && window.Visible)
            {
                SaveWindowBounds();
                window.Hide();
            }
        }

        // Called when the user closes the window
        public void OnWindowClosed()
        {
            SaveWindowBounds();
            settings.ShowWindow = false;
            settings.Save();
        }

        void SaveWindowBounds()
        {
            if (window == null || window.WindowState != FormWindowState.Normal) { return; }
            settings.WindowX = window.Left;
            settings.WindowY = window.Top;
            settings.WindowWidth = window.Width;
            settings.WindowHeight = window.Height;
        }

        // ---- Floating scope window (older SDR# without the MPX Scope plugin) ----

        public bool ScopeWindowVisible => scopeWindow != null && scopeWindow.Visible;

        public void ShowScopeWindow(bool show, IWin32Window owner = null)
        {
            settings.ShowScopeWindow = show;
            settings.Save();
            if (show)
            {
                if (scopeWindow == null || scopeWindow.IsDisposed)
                {
                    scopeWindow = new ScopeWindow(this);
                    var b = new System.Drawing.Rectangle(settings.ScopeWindowX, settings.ScopeWindowY, settings.ScopeWindowWidth, settings.ScopeWindowHeight);
                    if (settings.ScopeWindowX != int.MinValue && IsOnScreen(b))
                    {
                        scopeWindow.StartPosition = FormStartPosition.Manual;
                        scopeWindow.Bounds = b;
                    }
                    else
                    {
                        scopeWindow.Size = b.Size;
                    }
                }
                if (!scopeWindow.Visible)
                {
                    if (owner != null) { scopeWindow.Show(owner); } else { scopeWindow.Show(); }
                }
                scopeWindow.Activate();
            }
            else if (scopeWindow != null && scopeWindow.Visible)
            {
                SaveScopeWindowBounds();
                scopeWindow.Hide();
            }
        }

        // Called when the user closes the scope window
        public void OnScopeWindowClosed()
        {
            SaveScopeWindowBounds();
            settings.ShowScopeWindow = false;
            settings.Save();
        }

        void SaveScopeWindowBounds()
        {
            if (scopeWindow == null || scopeWindow.WindowState != FormWindowState.Normal) { return; }
            settings.ScopeWindowX = scopeWindow.Left;
            settings.ScopeWindowY = scopeWindow.Top;
            settings.ScopeWindowWidth = scopeWindow.Width;
            settings.ScopeWindowHeight = scopeWindow.Height;
        }

        static bool IsOnScreen(System.Drawing.Rectangle r)
        {
            foreach (var s in Screen.AllScreens)
            {
                if (s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(r.X, r.Y, Math.Min(r.Width, 200), 40))) { return true; }
            }
            return false;
        }

        // ---- Shared settings actions (from the panel or a display) ----

        public void SetPeakHold(bool on)
        {
            settings.PeakHold = on;
            chain.ResetPeak();
            settings.Save();
            Panel?.SyncFromSettings();
        }

        public void ToggleRecording(IWin32Window owner)
        {
            if (chain.IsRecording)
            {
                chain.StopRecording();
            }
            else
            {
                Panel?.CommitFolder();
                if (!StartRecording(out string error))
                {
                    MessageBox.Show(owner, error, "MPX Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            Panel?.UpdateStatus();
        }

        // ---- SDR# state ----

        // Tuned frequency (Hz), as of the last GUI update
        public long Frequency => Interlocked.Read(ref frequency);

        long ReadFrequency()
        {
            try { return control.Frequency; }
            catch { return 0; }
        }

        // SDR#'s IF filter bandwidth (Hz), or 0 if unknown
        public int FilterBandwidth
        {
            get
            {
                try { return control.FilterBandwidth; }
                catch { return 0; }
            }
        }

        public bool IsWfm
        {
            get
            {
                try { return control.DetectorType == DetectorType.WFM; }
                catch { return true; }
            }
        }

        public bool IsPlaying
        {
            get
            {
                try { return control.IsPlaying; }
                catch { return true; }
            }
        }

        // ---- Recording ----

        // Creates the default folder if needed. Returns false if recording can't start
        public bool StartRecording(out string error)
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

        public void StopRecording() => chain.StopRecording();

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
