using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SDRSharp.MPXAnalyzer.Dsp;

// Feeds synthetic FM broadcast signals into MpxChain as IQ, at the IQ rates SDR# typically delivers in WFM mode,
// and checks the measurements against the levels the signals were generated with.
static class Program
{
    static int failures;

    static void Check(string what, double value, double expected, double tolerance)
    {
        bool ok = Math.Abs(value - expected) <= tolerance;
        if (!ok) { failures++; }
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what,-28} {value,10:F3}  (expected {expected:F3} +/- {tolerance:F3})");
    }

    static void CheckBool(string what, bool value, bool expected)
    {
        bool ok = value == expected;
        if (!ok) { failures++; }
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what,-28} {value,10}  (expected {expected})");
    }

    sealed class Signal
    {
        public double PilotPct;             // Pilot deviation, % of 75kHz
        public List<(double freq, double pct)> Tones = new List<(double, double)>();
        public double RdsPct;               // Peak of the theoretical EN 50067 waveform, % of 75kHz
        public double RdsPhaseDeg;          // RDS carrier vs 3rd pilot harmonic
        public double RdsOffsetHz;          // RDS carrier frequency error (unlocked)
        public double PilotOffsetHz;        // Pilot frequency error (RDS stays locked to it)
        public double NoiseRms;             // IQ noise relative to a carrier amplitude of 1
    }

    // RDS biphase baseband, unit peak, sampled at fs: s(t) = sum_k a_k [g(t - k Td) - g(t - k Td - Td/2)]
    // with g the EN 50067 cos shaping filter's impulse response
    static double[] RdsBaseband(double fs, int length, int seed)
    {
        double td = 1.0 / 1187.5;
        int span = (int)(4 * td * fs);
        var g = new double[2 * span + 1];
        int steps = 2000;
        double fmax = 2.0 / td;
        for (int i = -span; i <= span; i++)
        {
            double t = i / fs, acc = 0.0;
            for (int k = 0; k < steps; k++)
            {
                double f = (k + 0.5) * fmax / steps;
                acc += Math.Cos(Math.PI * f * td / 4.0) * Math.Cos(2.0 * Math.PI * f * t);
            }
            g[i + span] = 2.0 * acc * fmax / steps;
        }

        var rnd = new Random(seed);
        var s = new double[length];
        int bits = (int)(length / fs / td) + 10;
        int prev = 1;
        for (int k = -5; k < bits; k++)
        {
            int a = (rnd.Next(2) == 0) ? prev : -prev;  // Differentially coded
            prev = a;
            foreach (double t0 in new[] { k * td, k * td + td / 2 })
            {
                double sign = (t0 == k * td) ? a : -a;
                int c = (int)Math.Round(t0 * fs);
                for (int i = Math.Max(0, c - span); i < Math.Min(length, c + span + 1); i++)
                {
                    // Interpolate g at the exact offset
                    double x = (i / fs - t0) * fs + span;
                    int xi = (int)Math.Floor(x);
                    if (xi < 0 || xi >= g.Length - 1) { continue; }
                    double fr = x - xi;
                    s[i] += sign * (g[xi] + fr * (g[xi + 1] - g[xi]));
                }
            }
        }
        double peak = 0.0;
        for (int i = (int)(fs * 0.01); i < length; i++) { peak = Math.Max(peak, Math.Abs(s[i])); }
        for (int i = 0; i < length; i++) { s[i] /= peak; }
        return s;
    }

    // Generates `seconds` of FM IQ and runs it through a chain. Returns the chain (measurements valid)
    static unsafe MpxChain Run(Signal sig, double fs, double seconds, out double mpxPowerDBr, out double peakPct)
    {
        int length = (int)(fs * seconds);
        double[] rds = sig.RdsPct > 0 ? RdsBaseband(fs, length, 1) : null;

        // Like a real transmitter the phase is continuous: each sample's phase step is the exact integral of the
        // MPX over the sample period (the RDS envelope is slow, so it's taken at the middle of the period)
        var step = new double[length];
        double sumSq = 0.0, peak = 0.0;
        double CosAvg(double w, double ph, int i) => (Math.Sin(w * i + ph) - Math.Sin(w * (i - 1) + ph)) / w;
        double pw = 2.0 * Math.PI * (19000.0 + sig.PilotOffsetHz) / fs;
        double rdsPh = sig.RdsPhaseDeg * Math.PI / 180.0;
        double rw = 3.0 * pw + 2.0 * Math.PI * sig.RdsOffsetHz / fs;
        for (int i = 0; i < length; i++)
        {
            double avg = sig.PilotPct / 100.0 * CosAvg(pw, 0.0, i);
            double m = sig.PilotPct / 100.0 * Math.Cos(pw * i);
            foreach (var (f, pct) in sig.Tones)
            {
                double w = 2.0 * Math.PI * f / fs;
                avg += pct / 100.0 * CosAvg(w, -Math.PI / 2, i);
                m += pct / 100.0 * Math.Sin(w * i);
            }
            if (rds != null)
            {
                double env = 0.5 * (rds[i] + rds[Math.Max(i - 1, 0)]);
                avg += sig.RdsPct / 100.0 * env * CosAvg(rw, rdsPh, i);
                m += sig.RdsPct / 100.0 * rds[i] * Math.Cos(rw * i + rdsPh);
            }
            step[i] = avg;
            sumSq += m * m;
            peak = Math.Max(peak, Math.Abs(m));
        }
        mpxPowerDBr = 10.0 * Math.Log10((sumSq / length) / ((19000.0 / 75000.0) * (19000.0 / 75000.0) / 2.0));
        peakPct = peak * 100.0;

        var chain = new MpxChain();
        var rnd = new Random(7);
        double phase = 0.3;
        const int block = 16384;
        float[] iq = new float[2 * block];
        for (int pos = 0; pos < length; pos += block)
        {
            int n = Math.Min(block, length - pos);
            for (int i = 0; i < n; i++)
            {
                phase += 2.0 * Math.PI * 75000.0 * step[pos + i] / fs;
                double nr = 0.0, ni = 0.0;
                if (sig.NoiseRms > 0)
                {
                    nr = Gauss(rnd) * sig.NoiseRms / Math.Sqrt(2);
                    ni = Gauss(rnd) * sig.NoiseRms / Math.Sqrt(2);
                }
                iq[2 * i] = (float)(0.5 * Math.Cos(phase) + 0.5 * nr);
                iq[2 * i + 1] = (float)(0.5 * Math.Sin(phase) + 0.5 * ni);
            }
            fixed (float* p = iq) { chain.ProcessIQ(p, n, fs); }
        }
        return chain;
    }

    static double Gauss(Random r) => Math.Sqrt(-2.0 * Math.Log(1.0 - r.NextDouble())) * Math.Cos(2.0 * Math.PI * r.NextDouble());

    static void Main()
    {
        var sw = Stopwatch.StartNew();
        foreach (double fs in new[] { 312500.0, 250000.0, 500000.0, 384000.0, 200000.0 })
        {
            Console.WriteLine($"IQ rate {fs / 1000:F1} kS/s");

            // Stereo-like station: tones, pilot, RDS at +30 degrees
            var sig = new Signal { PilotPct = 9.0, RdsPct = 4.0, RdsPhaseDeg = 30.0 };
            sig.Tones.Add((1000.0, 40.0));
            sig.Tones.Add((38000.0 + 1500.0, 20.0));
            var chain = Run(sig, fs, 4.0, out double dbr, out double peakPct);
            chain.GetMeasurements(out var m);
            Check("pilot kHz", m.PilotKHz, 6.75, 0.03);
            Check("pilot Hz", m.PilotFreqHz, 19000.0, 0.01);
            // Sample peaks at 192k and at the IQ rate differ slightly for a complex signal
            Check("deviation peak %", m.PeakDevKHz / 0.75, peakPct, 1.5);
            Check("MPX power dBr", m.PowerDBr, dbr, 0.05);
            // The plugin reads RDS with a 1.51 crest factor (real encoders), the generator uses the
            // theoretical waveform (1.444), so it reads 1.51/1.444 high
            Check("RDS kHz", m.RdsKHz, 3.0 * 1.51 / 1.444, 0.12);
            CheckBool("RDS locked", m.Locked, true);
            Check("RDS lock phase deg", Math.Abs(m.LockPhaseDeg), 30.0, 3.0);

            // Single tone at 57kHz: flatness at the top of the band (demodulator droop correction)
            sig = new Signal();
            sig.Tones.Add((57000.0, 10.0));
            sig.Tones.Add((19000.0, 10.0));
            chain = Run(sig, fs, 1.5, out dbr, out peakPct);
            chain.GetMeasurements(out m);
            Check("19k tone as pilot kHz", m.PilotKHz, 7.5, 0.03);
            Check("57k+19k MPX power dBr", m.PowerDBr, dbr, 0.05);
            var avg = new float[MpxChain.FFT_BINS];
            var pk = new float[MpxChain.FFT_BINS];
            chain.GetSpectrum(avg, pk);
            double binHz = MpxChain.SAMPLERATE / MpxChain.FFT_SIZE;
            double Level(double f)
            {
                int b = (int)Math.Round(f / binHz);
                double p = 0;
                for (int i = b - 5; i <= b + 5; i++) { p += avg[i]; }
                return 10.0 * Math.Log10(p);
            }
            // Power summed over the window's main lobe, so it doesn't depend on where the tone falls between bins
            Check("57k vs 19k spectrum dB", Level(57000) - Level(19000), 0.0, 0.05);

            // Off-frequency pilots
            foreach (double off in new[] { 5.0, -120.0 })
            {
                sig = new Signal { PilotPct = 9.0, RdsPct = 4.0, PilotOffsetHz = off };
                sig.Tones.Add((1000.0, 40.0));
                chain = Run(sig, fs, 1.5, out _, out _);
                chain.GetMeasurements(out m);
                Check($"pilot Hz, {off:+0;-0} Hz off", m.PilotFreqHz, 19000.0 + off, 0.01);
            }

            // Unlocked RDS, mono (no pilot): RDS lock n/a
            sig = new Signal { PilotPct = 9.0, RdsPct = 4.0, RdsOffsetHz = 0.5 };
            chain = Run(sig, fs, 6.0, out _, out _);
            chain.GetMeasurements(out m);
            CheckBool("unlocked RDS locked", m.Locked, false);

            sig = new Signal();
            sig.Tones.Add((1000.0, 50.0));
            chain = Run(sig, fs, 1.5, out _, out _);
            chain.GetMeasurements(out m);
            CheckBool("mono pilot present", m.PilotPresent, false);
            CheckBool("mono RDS present", m.RdsPresent, false);
            Check("1k 50% deviation kHz", m.PeakDevKHz, 37.5, 0.1);
        }

        // Noisy signal: RDS level corrected for noise
        {
            Console.WriteLine("Noisy signal, 312.5 kS/s");
            var sig = new Signal { PilotPct = 9.0, RdsPct = 4.0, NoiseRms = 0.03 };
            var chain = Run(sig, 312500.0, 4.0, out _, out _);
            chain.GetMeasurements(out var m);
            Check("pilot kHz", m.PilotKHz, 6.75, 0.08);
            Check("pilot Hz", m.PilotFreqHz, 19000.0, 0.05);
            Check("RDS kHz", m.RdsKHz, 3.0 * 1.51 / 1.444, 0.2);
        }

        // Deviation peak response time: steady tones read their full peak, short peaks read lower
        {
            Console.WriteLine("Peak response time");
            foreach (var (resp, tol) in new[] { (0.0, 0.002), (125e-6, 0.01), (1e-3, 0.015) })
            {
                var chain = new MpxChain();
                chain.SetPeakResponse(resp);
                var sig = new float[19200];
                for (int k = 0; k < 20; k++)
                {
                    for (int i = 0; i < sig.Length; i++) { sig[i] = 0.5f * (float)Math.Sin(2.0 * Math.PI * 1000.0 * (k * sig.Length + i) / 192000.0); }
                    chain.ProcessMpx(sig, sig.Length);
                }
                chain.GetMeasurements(out var m);
                Check($"1k 50% tone, {resp * 1e6:0} us", m.PeakDevKHz, 37.5, 37.5 * tol);
                Check($"  response reported, {resp * 1e6:0} us", m.PeakResponseSec * 1e6, resp * 1e6, 0.01);
            }

            // 30% tone with a single 50us spike to 100% (repeated short peaks build up, like on any quasi-peak meter)
            var readings = new List<double>();
            foreach (double resp in new[] { 0.0, 125e-6, 1e-3 })
            {
                var chain = new MpxChain();
                chain.SetPeakResponse(resp);
                var sig = new float[19200];
                for (int k = 0; k < 20; k++)
                {
                    for (int i = 0; i < sig.Length; i++) { sig[i] = 0.3f * (float)Math.Sin(2.0 * Math.PI * 1000.0 * (k * sig.Length + i) / 192000.0); }
                    if (k == 10) { for (int i = 5000; i < 5000 + 10; i++) { sig[i] = 1.0f; } }
                    chain.ProcessMpx(sig, sig.Length);
                }
                chain.GetMeasurements(out var m);
                readings.Add(m.MaxDevKHz);
            }
            Check("50us spike, instant", readings[0], 75.0, 0.5);
            // Detector at 0.3 (tone peak) charges toward 1.0 for 50us: 0.3 + 0.7 * (1 - exp(-50/T))
            Check("50us spike, 125 us", readings[1], 75.0 * (0.3 + 0.7 * (1 - Math.Exp(-52.0 / 125.0))), 1.5);
            Check("50us spike, 1 ms", readings[2], 75.0 * (0.3 + 0.7 * (1 - Math.Exp(-52.0 / 1000.0))), 1.5);
        }

        // Recording
        {
            Console.WriteLine("Recording");
            string dir = Path.Combine(Path.GetTempPath(), "mpx_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var chain = new MpxChain();
            int file = 0;
            chain.StartRecording(() => Path.Combine(dir, $"rec{file++}.wav"), WavSampleType.Int16);
            var data = new float[19200];
            for (int i = 0; i < data.Length; i++) { data[i] = (float)Math.Sin(i * 0.1); }
            for (int k = 0; k < 10; k++) { chain.ProcessMpx(data, data.Length); }
            chain.StopRecording();
            var bytes = File.ReadAllBytes(Path.Combine(dir, "rec0.wav"));
            Check("WAV size", bytes.Length, 44 + 192000 * 2, 0);
            Check("WAV data size field", BitConverter.ToUInt32(bytes, 40), 192000 * 2, 0);
            Check("WAV rate field", BitConverter.ToUInt32(bytes, 24), 192000, 0);
            Directory.Delete(dir, true);
        }

        // Throughput
        {
            var sig = new Signal { PilotPct = 9.0, RdsPct = 4.0 };
            sig.Tones.Add((1000.0, 40.0));
            var t = Stopwatch.StartNew();
            Run(sig, 312500.0, 10.0, out _, out _);
            Console.WriteLine($"10 s of signal (incl. generation) in {t.Elapsed.TotalSeconds:F2} s");
        }

        Console.WriteLine(failures == 0 ? "All checks passed" : $"{failures} check(s) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
