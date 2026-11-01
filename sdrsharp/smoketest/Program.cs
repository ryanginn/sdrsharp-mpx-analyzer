using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.MPXAnalyzer;
using SDRSharp.Radio;

// Stand-in for SDR#: answers the ISharpControl calls the plugin makes, and keeps the stream hook it registers
public class FakeControl : DispatchProxy
{
    public static object Hook;
    public static ProcessorType HookType;

    protected override object Invoke(MethodInfo method, object[] args)
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

        // Other SDR# assemblies that SDRSharp.Common/Radio refer to
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string path = Path.Combine(sdrDir, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };
        try
        {
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

        var control = DispatchProxy.Create<ISharpControl, FakeControl>();
        var plugin = new MpxPlugin();
        plugin.Initialize(control);
        Console.WriteLine($"Plugin: {plugin.DisplayName}, hook {FakeControl.Hook?.GetType().Name} on {FakeControl.HookType}");
        if (!(FakeControl.Hook is IIQProcessor hook)) { Console.WriteLine("FAIL: no IQ hook registered"); return 1; }

        // The side panel, as SDR# would host it
        var host = new Form { Text = "SDR# side panel (test host)", Width = 280, Height = 560, StartPosition = FormStartPosition.Manual, Location = new Point(10, 10) };
        var gui = plugin.Gui;
        gui.Dock = DockStyle.Top;
        host.Controls.Add(gui);
        host.Show();
        plugin.ShowWindow(true, host);

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
        if (plugin.Hook.Error != null) { Console.WriteLine("FAIL: DSP error " + plugin.Hook.Error); failures++; }
        if (!plugin.Chain.GetMeasurements(out var m)) { Console.WriteLine("FAIL: no measurements"); failures++; }
        Console.WriteLine($"IQ rate {plugin.Chain.IqRate}, pilot {m.PilotKHz:F3} kHz, peak dev {m.PeakDevKHz:F2} kHz, MPX power {m.PowerDBr:F2} dBr");
        if (Math.Abs(m.PilotKHz - 6.75) > 0.05) { Console.WriteLine("FAIL: pilot level"); failures++; }

        // Screenshots
        Save(host, Path.Combine(outDir, "panel.png"));
        foreach (Form f in Application.OpenForms)
        {
            if (f != host) { Save(f, Path.Combine(outDir, "mpx_window.png")); }
        }

        plugin.Close();
        host.Close();
        Console.WriteLine(failures == 0 ? "Smoke test passed" : "Smoke test FAILED");
        return failures == 0 ? 0 : 1;
    }

    static void Save(Form f, string path)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine("Saved " + path);
    }
}
