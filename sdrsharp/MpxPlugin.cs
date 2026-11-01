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
    // Built twice: for .NET 8 (current SDR#), where the spectrum is a separate dock panel (MpxSpectrumPlugin), and
    // for .NET Framework 4.6 (older 32-bit SDR#, e.g. 1716), where it's a floating window opened from this panel.
#if NETCOREAPP
    public class MpxPlugin : ISharpPlugin, ICanLazyLoadGui, IExtendedNameProvider
#else
    public class MpxPlugin : ISharpPlugin
#endif
    {
        MpxCore core;
        ControlPanel gui;

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
        }

        public void Close()
        {
            if (core == null) { return; }
            if (core.Panel == gui) { core.Panel = null; }
            MpxCore.Release();
            core = null;
        }

        internal MpxCore Core => core;
    }

#if NETCOREAPP
    // The spectrum and meters as an SDR# dock panel, like SDR#'s own FM MPX Spectrum: opened from SDR#'s menu,
    // docked at the bottom by default, and can be moved, tabbed, pinned or floated like the other panels
    public class MpxSpectrumPlugin : ISharpPlugin, ICanLazyLoadGui, IExtendedNameProvider, IPreferedDockPosition
    {
        MpxCore core;
        MpxDisplay gui;

        public string DisplayName => "MPX Spectrum";

        public string Category => "Analysis";

        public string MenuItemName => DisplayName;

        public DockStyle DockPosition => DockStyle.Bottom;

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
            gui = new MpxDisplay(core);
            core.AddDisplay(gui);
        }

        public void Initialize(ISharpControl control)
        {
            core = MpxCore.Acquire(control);
        }

        public void Close()
        {
            if (core == null) { return; }
            if (gui != null) { core.RemoveDisplay(gui); }
            MpxCore.Release();
            core = null;
        }

        internal MpxCore Core => core;
    }
#endif
}
