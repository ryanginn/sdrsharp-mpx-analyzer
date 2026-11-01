using System;
using System.IO;
using System.Text.Json;
using SDRSharp.MPXAnalyzer.Dsp;

namespace SDRSharp.MPXAnalyzer
{
    // Plugin settings, kept in MPXAnalyzer.json next to SDR# (or in %LOCALAPPDATA%\SDRSharp.MPXAnalyzer if
    // SDR#'s folder isn't writable)
    public sealed class MpxSettings
    {
        public bool ShowWindow { get; set; } = true;
        public int AvgFrames { get; set; } = 10;
        public float MaxFreqKHz { get; set; } = 96.0f;
        public float DbMin { get; set; } = -100.0f;
        public float DbMax { get; set; } = 0.0f;
        public bool PeakHold { get; set; }
        public bool LevelPercent { get; set; }
        public string RecPath { get; set; } = DefaultRecPath;
        public WavSampleType SampleType { get; set; } = WavSampleType.Int16;
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; } = 760;
        public int WindowHeight { get; set; } = 380;

        const string FILE_NAME = "MPXAnalyzer.json";

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
                    var s = JsonSerializer.Deserialize<MpxSettings>(File.ReadAllText(path));
                    if (s != null) { return s.Validated(); }
                }
                catch { }
            }
            return new MpxSettings();
        }

        public void Save()
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
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

        MpxSettings Validated()
        {
            AvgFrames = Math.Clamp(AvgFrames, 1, 100);
            MaxFreqKHz = Math.Clamp(MaxFreqKHz, 20.0f, (float)(MpxChain.SAMPLERATE / 2000.0));
            DbMax = Math.Clamp(DbMax, -60.0f, 20.0f);
            DbMin = Math.Clamp(DbMin, -160.0f, -20.0f);
            if (DbMin > DbMax - 10.0f) { DbMin = DbMax - 10.0f; }
            if (string.IsNullOrWhiteSpace(RecPath)) { RecPath = DefaultRecPath; }
            if (!Enum.IsDefined(typeof(WavSampleType), SampleType)) { SampleType = WavSampleType.Int16; }
            WindowWidth = Math.Max(WindowWidth, 480);
            WindowHeight = Math.Max(WindowHeight, 300);
            return this;
        }
    }
}
