using System;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.MPXAnalyzer.Gui;

namespace SDRSharp.MPXAnalyzer
{
    // FM MPX spectrum analyzer and recorder for SDR#: the settings and recording panel.
    //
    // The plugin taps SDR#'s decimated and filtered IQ, i.e. the Radio's own channel after its IF filter, so it
    // always follows the frequency and filter bandwidth set in SDR#'s main window. It FM-demodulates that IQ
    // itself, so the MPX scaling (+/-1.0 = 75kHz deviation) doesn't depend on SDR#'s audio settings.
    //
    // Built twice: for .NET 8 (current SDR#), where the spectrum is docked in SDR#'s main window like its own FM MPX
    // Spectrum (a "front control"), and for .NET Framework 4.6 (older 32-bit SDR#, e.g. 1716), where it's a
    // floating window opened from this panel.
#if NETCOREAPP
    public class MpxPlugin : ISharpPlugin, ICanLazyLoadGui, IExtendedNameProvider
#else
    public class MpxPlugin : ISharpPlugin
#endif
    {
        MpxCore core;
        ControlPanel gui;
#if NETCOREAPP
        MpxDisplay display;
#endif

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
            core.StartGui();
            gui = new ControlPanel(core);
            core.Panel = gui;
#if !NETCOREAPP
            if (core.Settings.ShowWindow) { gui.HandleCreated += (s, e) => gui.BeginInvoke(new Action(() => core.ShowWindow(true, gui.FindForm()))); }
#endif
        }

        public void Initialize(ISharpControl control)
        {
            core = MpxCore.Acquire(control);
#if NETCOREAPP
            // The spectrum goes in SDR#'s main dock area, below the spectrum/waterfall by default, where the IF,
            // Audio and FM MPX spectra are. SDR# handles docking, tabbing, floating and closing it
            core.StartGui();
            display = new MpxDisplay(core);
            core.AddDisplay(display);
            control.RegisterFrontControl(display, PluginPosition.Bottom);
#endif
        }

        public void Close()
        {
            if (core == null) { return; }
            if (core.Panel == gui) { core.Panel = null; }
#if NETCOREAPP
            if (display != null) { core.RemoveDisplay(display); }
#endif
            MpxCore.Release();
            core = null;
        }

        internal MpxCore Core => core;
    }

}
