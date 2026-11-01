using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
#if NETCOREAPP
using System.Runtime.Loader;
#else
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
#endif
using System.Threading;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.MPXAnalyzer;
using SDRSharp.Radio;

// Stand-in for SDR#: answers the ISharpControl calls the plugin makes, and keeps the stream hook it registers.
// DispatchProxy on .NET 8, RealProxy on .NET Framework (older SDR#)
#if NETCOREAPP
public class FakeControl : DispatchProxy
{
    protected override object Invoke(MethodInfo method, object[] args) => FakeSdr.Answer(method, args);

    public static ISharpControl Create() => Create<ISharpControl, FakeControl>();
}
#else
public class FakeControl : RealProxy
{
    FakeControl() : base(typeof(ISharpControl)) { }

    public override IMessage Invoke(IMessage msg)
    {
        var call = (IMethodCallMessage)msg;
        object result = FakeSdr.Answer((MethodInfo)call.MethodBase, call.Args);
        return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
    }

    public static ISharpControl Create() => (ISharpControl)new FakeControl().GetTransparentProxy();
}
#endif

public static class FakeSdr
{
    public static object Hook;
    public static bool Dark = true;
    public static ProcessorType HookType;

    public static object Answer(MethodInfo method, object[] args)
    {
        switch (method.Name)
        {
            case "RegisterStreamHook":
                Hook = args[0];
                HookType = (ProcessorType)args[1];
                return null;
            case "UnregisterStreamHook":
                Hook = null;
                return null;
            case "get_Frequency": return 98800000L;
            case "get_FilterBandwidth": return 250000;
            case "get_DetectorType": return DetectorType.WFM;
            case "get_IsPlaying": return true;
            // Theme, as SDR#'s FluentDark / a light theme would report it
            case "get_ThemeIsDark": return Dark;
            case "get_ThemeName": return Dark ? "FluentDark" : "Light";
            case "get_ThemeForeColor": return Dark ? Color.FromArgb(220, 220, 220) : Color.Black;
            case "get_ThemeBackColor": return Dark ? Color.Black : Color.White;
            case "get_ThemePanelColor": return Dark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(240, 240, 240);
            case "get_SpectrumGradient":
                var blend = new System.Drawing.Drawing2D.ColorBlend(5);
                blend.Colors = new[] { Color.White, Color.LightBlue, Color.DodgerBlue, Color.FromArgb(0, 0, 80), Color.Black };
                blend.Positions = new[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
                return blend;
            case "get_FFTSpectrumStyle": return Enum.ToObject(method.ReturnType, 3);    // StaticGradient
        }
        var t = method.ReturnType;
        return (t != typeof(void) && t.IsValueType) ? Activator.CreateInstance(t) : null;
    }
}

static class Program
{
    const double FS = 312500.0;

    [STAThread]
    static int Main(string[] args)
    {
        string sdrDir = args[0];
        string outDir = args[1];
        Directory.CreateDirectory(outDir);

        // SDR#'s assemblies are loaded from its folder
#if NETCOREAPP
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string path = Path.Combine(sdrDir, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };
#else
        AppDomain.CurrentDomain.AssemblyResolve += (o, e) =>
        {
            string path = Path.Combine(sdrDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
#endif
        try
        {
            if (args.Length > 2 && args[2] == "--api-dump") { return DumpApi(sdrDir); }
            return Run(outDir);
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL: " + e);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int Run(string outDir)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var control = FakeControl.Create();
        var plugin = new MpxPlugin();
        plugin.Initialize(control);
        Console.WriteLine($"Plugin: {plugin.DisplayName}, hook {FakeSdr.Hook?.GetType().Name} on {FakeSdr.HookType}");
        if (!(FakeSdr.Hook is IIQProcessor hook)) { Console.WriteLine("FAIL: no IQ hook registered"); return 1; }

        // The side panel, as SDR# would host it
        var host = new Form { Text = "SDR# side panel (test host)", Width = 280, Height = 560, StartPosition = FormStartPosition.Manual, Location = new Point(10, 10) };
        var gui = plugin.Gui;
        gui.Dock = DockStyle.Top;
        host.Controls.Add(gui);
        host.Show();

#if NETCOREAPP
        // Current SDR#: the MPX Spectrum dock panel, hosted here the way SDR# docks it (second plugin in the DLL)
        var spectrumPlugin = new MpxSpectrumPlugin();
        spectrumPlugin.Initialize(control);
        Console.WriteLine($"Plugin: {spectrumPlugin.DisplayName}, dock position {spectrumPlugin.DockPosition}");
        var dockHost = new Form { Text = "MPX Spectrum (dock panel test host)", Width = 900, Height = 320, StartPosition = FormStartPosition.Manual, Location = new Point(300, 10) };
        var panelGui = spectrumPlugin.Gui;
        panelGui.Dock = DockStyle.Fill;
        dockHost.Controls.Add(panelGui);
        dockHost.Show();
#else
        // Older SDR#: floating window
        plugin.Core.ShowWindow(true, host);
#endif

        // FM signal: 9% pilot, 40% 1kHz tone, 20% 39.5kHz (L-R like), 4% 57kHz tone. Continuous phase like a real
        // transmitter; fed at about real time from a separate thread like SDR#'s DSP thread
        hook.SampleRate = FS;
        double seconds = 4.0;
        var feeder = new Thread(() =>
        {
            const int block = 8192;
            var iq = new float[2 * block];
            double phase = 0.0;
            long n = 0;
            var sw = Stopwatch.StartNew();
            double Avg(double f, double ph, long i) { double w = 2 * Math.PI * f / FS; return (Math.Sin(w * i + ph) - Math.Sin(w * (i - 1) + ph)) / w; }
            while (n < FS * seconds)
            {
                for (int i = 0; i < block; i++, n++)
                {
                    double m = 0.09 * Avg(19000, 0, n) + 0.40 * Avg(1000, 0, n) + 0.20 * Avg(39500, 0, n) + 0.04 * Avg(57000, 0, n);
                    phase += 2 * Math.PI * 75000 * m / FS;
                    iq[2 * i] = (float)Math.Cos(phase);
                    iq[2 * i + 1] = (float)Math.Sin(phase);
                }
                fixed (float* p = iq) { hook.Process((Complex*)p, block); }
                double ahead = n / FS - sw.Elapsed.TotalSeconds;
                if (ahead > 0) { Thread.Sleep((int)(ahead * 1000)); }
            }
        }) { IsBackground = true };
        feeder.Start();

        var wait = Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < seconds + 0.5)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        int failures = 0;
        var core = plugin.Core;
        if (core.Hook.Error != null) { Console.WriteLine("FAIL: DSP error " + core.Hook.Error); failures++; }
        if (!core.Chain.GetMeasurements(out var m)) { Console.WriteLine("FAIL: no measurements (spectrum display not detected as visible?)"); failures++; }
        Console.WriteLine($"IQ rate {core.Chain.IqRate}, pilot {m.PilotKHz:F3} kHz, peak dev {m.PeakDevKHz:F2} kHz, MPX power {m.PowerDBr:F2} dBr");
        if (Math.Abs(m.PilotKHz - 6.75) > 0.05) { Console.WriteLine("FAIL: pilot level"); failures++; }

        // Screenshots
        Save(host, Path.Combine(outDir, "panel.png"));
        foreach (Form f in Application.OpenForms)
        {
            if (f != host) { Save(f, Path.Combine(outDir, "mpx_window.png")); }
        }

        // Same display after switching SDR# to a light theme (picked up by the plugin within about a second)
        FakeSdr.Dark = false;
        wait.Restart();
        while (wait.Elapsed.TotalSeconds < 1.5) { Application.DoEvents(); Thread.Sleep(10); }
        foreach (Form f in Application.OpenForms)
        {
            if (f != host) { Save(f, Path.Combine(outDir, "mpx_window_light.png")); }
        }

#if NETCOREAPP
        spectrumPlugin.Close();
#endif
        plugin.Close();
        host.Close();
        Console.WriteLine(failures == 0 ? "Smoke test passed" : "Smoke test FAILED");
        return failures == 0 ? 0 : 1;
    }

    // Lists SDR#'s public API related to themes and colours (to follow SDR#'s theme)
    static int DumpApi(string sdrDir)
    {
        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var rx = new System.Text.RegularExpressions.Regex("Theme|Color|Colour|Gradient|Skin|Style|Palette|Dark|Brush", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var file in Directory.GetFiles(sdrDir, "SDRSharp*.dll"))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(Path.GetFullPath(file)); }
            catch (Exception e) { Console.WriteLine($"## {Path.GetFileName(file)}: {e.Message}"); continue; }
            Console.WriteLine($"## {asm.GetName().Name}");
            Type[] types;
            try { types = asm.GetExportedTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null && t.IsPublic); }
            catch (Exception e) { Console.WriteLine("  " + e.Message); continue; }
            foreach (var t in types)
            {
                bool all = rx.IsMatch(t.Name) || t.Name == "ISharpControl" || t.Name == "Utils";
                var lines = new System.Collections.Generic.List<string>();
                try
                {
                    foreach (var m in t.GetMembers(flags))
                    {
                        if (m is MethodInfo mi && mi.IsSpecialName) { continue; }
                        string sig = m switch
                        {
                            PropertyInfo pi => $"prop {pi.PropertyType.Name} {pi.Name}",
                            FieldInfo fi => $"field {fi.FieldType.Name} {fi.Name}" + (fi.IsLiteral ? " = " + fi.GetRawConstantValue() : ""),
                            MethodInfo mi2 => $"method {mi2.ReturnType.Name} {mi2.Name}({string.Join(", ", Array.ConvertAll(mi2.GetParameters(), p => p.ParameterType.Name + " " + p.Name))})",
                            EventInfo ei => $"event {ei.EventHandlerType?.Name} {ei.Name}",
                            _ => null
                        };
                        if (sig != null && (all || rx.IsMatch(sig))) { lines.Add("    " + (m is FieldInfo f2 && f2.IsStatic || m is MethodInfo m2 && m2.IsStatic ? "static " : "") + sig); }
                    }
                }
                catch (Exception e) { lines.Add("    (" + e.Message + ")"); }
                if (all || lines.Count > 0)
                {
                    Console.WriteLine($"  {(t.IsInterface ? "interface" : t.IsEnum ? "enum" : "type")} {t.FullName} : {t.BaseType?.Name}");
                    if (t.IsEnum) { Console.WriteLine("    " + string.Join(", ", Enum.GetNames(t))); }
                    foreach (var l in lines) { Console.WriteLine(l); }
                }
            }
        }
        return 0;
    }

    static void Save(Form f, string path)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine("Saved " + path);
    }
}
