using System;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer
{
    // Plugin settings, kept in MPXAnalyzer.json next to SDR# (or in %LOCALAPPDATA%\SDRSharp.MPXAnalyzer if
    // SDR#'s folder isn't writable)
    public sealed class MpxSettings
    {
        public bool ShowWindow { get; set; } = true;
        public int AvgFrames { get; set; } = 5;
        public int SettingsVersion { get; set; }        // For migrating defaults that changed
        public float MaxFreqKHz { get; set; } = 96.0f;
        public float DbMin { get; set; } = -100.0f;
        public float DbMax { get; set; } = 0.0f;
        public bool PeakHold { get; set; }
        public bool ShowMarkers { get; set; } = true;       // Pilot / 38k / RDS / SCA markers on the spectrum
        public bool LevelPercent { get; set; }
        public int PeakResponseUs { get; set; } = 125;      // Deviation peak response time, 0 = instant
        public float MpxPowerLimitDBr { get; set; }         // MPX power reading turns amber above this
        public string RecPath { get; set; } = DefaultRecPath;
        public WavSampleType SampleType { get; set; } = WavSampleType.Int16;
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; } = 760;
        public int WindowHeight { get; set; } = 380;

        const string FILE_NAME = "MPXAnalyzer.json";

        // Choices for the deviation peak response time: instant (sample peak), Europe, US
        public static readonly int[] PEAK_RESPONSES_US = { 0, 125, 1000 };

        static string DefaultRecPath => Path.Combine(AppContext.BaseDirectory, "Recordings");

        static string[] Candidates => new[]
        {
            Path.Combine(AppContext.BaseDirectory, FILE_NAME),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SDRSharp.MPXAnalyzer", FILE_NAME)
        };

        public static MpxSettings Load()
        {
            foreach (var path in Candidates)
            {
                try
                {
                    if (!File.Exists(path)) { continue; }
                    return FromJson(File.ReadAllText(path)).Validated();
                }
                catch { }
            }
            return new MpxSettings { SettingsVersion = 2 };
        }

        public void Save()
        {
            string json = ToJson();
            foreach (var path in Candidates)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, json);
                    return;
                }
                catch { }
            }
        }

        // ---- Flat JSON object of the public properties (no JSON library on .NET Framework) ----

        string ToJson()
        {
            var sb = new StringBuilder("{\n");
            var props = typeof(MpxSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < props.Length; i++)
            {
                object v = props[i].GetValue(this);
                string text;
                if (v is string str) { text = Quote(str); }
                else if (v is bool b) { text = b ? "true" : "false"; }
                else if (v is Enum) { text = Convert.ToInt32(v).ToString(CultureInfo.InvariantCulture); }
                else { text = Convert.ToString(v, CultureInfo.InvariantCulture); }
                sb.Append("  ").Append(Quote(props[i].Name)).Append(": ").Append(text).Append(i < props.Length - 1 ? ",\n" : "\n");
            }
            return sb.Append("}\n").ToString();
        }

        static MpxSettings FromJson(string json)
        {
            var s = new MpxSettings();
            var rx = new Regex(@"""(\w+)""\s*:\s*(""(?:[^""\\]|\\.)*""|[^,}\s]+)");
            foreach (Match m in rx.Matches(json))
            {
                var prop = typeof(MpxSettings).GetProperty(m.Groups[1].Value, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite) { continue; }
                string raw = m.Groups[2].Value;
                try
                {
                    var t = prop.PropertyType;
                    object v;
                    if (t == typeof(string)) { v = raw.StartsWith("\"") ? Unquote(raw) : null; }
                    else if (t == typeof(bool)) { v = raw == "true"; }
                    else if (t.IsEnum) { v = Enum.ToObject(t, int.Parse(raw, CultureInfo.InvariantCulture)); }
                    else { v = Convert.ChangeType(raw, t, CultureInfo.InvariantCulture); }
                    if (v != null) { prop.SetValue(s, v); }
                }
                catch { }
            }
            return s;
        }

        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) { sb.AppendFormat("\\u{0:x4}", (int)c); } else { sb.Append(c); }
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string Unquote(string q)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < q.Length - 1; i++)
            {
                char c = q[i];
                if (c != '\\' || i + 1 >= q.Length - 1) { sb.Append(c); continue; }
                char e = q[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(q.Substring(i + 1, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;     // \" \\ \/
                }
            }
            return sb.ToString();
        }

        MpxSettings Validated()
        {
            // Version 2: averaging became attack/decay with a faster default (5 instead of 10). Keep a user's own
            // choice, but move the old default along
            if (SettingsVersion < 2 && AvgFrames == 10) { AvgFrames = 5; }
            SettingsVersion = 2;
            AvgFrames = MathUtil.Clamp(AvgFrames, 1, 100);
            MaxFreqKHz = MathUtil.Clamp(MaxFreqKHz, 20.0f, (float)(MpxChain.SAMPLERATE / 2000.0));
            DbMax = MathUtil.Clamp(DbMax, -60.0f, 20.0f);
            DbMin = MathUtil.Clamp(DbMin, -160.0f, -20.0f);
            if (DbMin > DbMax - 10.0f) { DbMin = DbMax - 10.0f; }
            if (Array.IndexOf(PEAK_RESPONSES_US, PeakResponseUs) < 0) { PeakResponseUs = 125; }
            MpxPowerLimitDBr = MathUtil.Clamp(MpxPowerLimitDBr, -6.0f, 12.0f);
            if (string.IsNullOrWhiteSpace(RecPath)) { RecPath = DefaultRecPath; }
            if (!Enum.IsDefined(typeof(WavSampleType), SampleType)) { SampleType = WavSampleType.Int16; }
            WindowWidth = Math.Max(WindowWidth, 480);
            WindowHeight = Math.Max(WindowHeight, 300);
            return this;
        }
    }
}
