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
    // The spectrum is a second plugin in this DLL (MpxSpectrumPlugin), which SDR# docks, closes and lists in its
    // menu exactly like its own FM MPX Spectrum. SDR# builds that load every plugin in the DLL (current SDR#) get
    // that. SDR# builds that only load what Plugins.xml lists (e.g. 1716) only create this plugin, and then the
    // spectrum is a floating window opened from this panel.
    //
    // Built twice: for .NET 8 (current SDR#, SDRSharp.dotnet8/9.exe, 64-bit and 32-bit) and for .NET Framework 4.6
    // (old SDR# with a single SDRSharp.exe, e.g. 1716).
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
            // No dock panel (SDR# didn't load MpxSpectrumPlugin): reopen the floating window if it was open
            gui.HandleCreated += (s, e) => gui.BeginInvoke(new Action(() =>
            {
                if (!core.SpectrumPanelLoaded && core.Settings.ShowWindow) { core.ShowWindow(true, gui.FindForm()); }
                if (!core.ScopePanelLoaded && core.Settings.ShowScopeWindow) { core.ShowScopeWindow(true, gui.FindForm()); }
            }));
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

    // The spectrum and meters as their own plugin panel. SDR# gives every plugin panel the same dock window as its
    // built-in FFT panels (FM MPX Spectrum etc.): dockable anywhere including below the waterfall, tabbable,
    // floatable, closable, and shown/hidden from the Plugins menu
#if NETCOREAPP
    public class MpxSpectrumPlugin : ISharpPlugin, ICanLazyLoadGui, IExtendedNameProvider, IPreferedDockPosition
#else
    public class MpxSpectrumPlugin : ISharpPlugin
#endif
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
            core.SpectrumPanelLoaded = true;
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

    // The oscilloscope (MPX or decoded L / R / L+R / L-R) with the +/- modulation and demodulated peaks, as its own
    // dock panel like the spectrum. Old SDR# that only loads what Plugins.xml lists shows it as a side panel when
    // it's listed there too; otherwise the settings panel opens it in a floating window
#if NETCOREAPP
    public class MpxScopePlugin : ISharpPlugin, ICanLazyLoadGui, IExtendedNameProvider, IPreferedDockPosition
#else
    public class MpxScopePlugin : ISharpPlugin
#endif
    {
        MpxCore core;
        ScopeDisplay gui;

        public string DisplayName => "MPX Scope";

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
            gui = new ScopeDisplay(core);
            core.AddScope(gui);
        }

        public void Initialize(ISharpControl control)
        {
            core = MpxCore.Acquire(control);
            core.ScopePanelLoaded = true;
        }

        public void Close()
        {
            if (core == null) { return; }
            if (gui != null) { core.RemoveScope(gui); }
            MpxCore.Release();
            core = null;
        }

        internal MpxCore Core => core;
    }
}
