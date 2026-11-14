using System;
using System.Numerics;
using System.Threading;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    public struct MpxMeasurements
    {
        public float PowerDBr;          // MPX power over the last 60s (or since reset, if shorter)
        public float PowerShortDBr;     // MPX power over the last 1s
        public float PowerSeconds;      // Seconds integrated in PowerDBr (up to 60)
        public float PeakDevKHz;        // Peak deviation over the last 1s
        public float MaxDevKHz;         // Peak deviation since reset
        public float PeakResponseSec;   // Peak detector response time used for the deviation (0 = instant)
        public float PilotKHz;          // 19kHz pilot level (deviation) over the last 1s
        public bool PilotPresent;
        public float PilotFreqHz;       // Pilot frequency over the last 1s (valid when PilotPresent)
        public float RdsKHz;            // RDS level (peak deviation, from noise corrected RMS) over the last 1s
        public bool RdsPresent;
        public bool LockValid;          // Pilot and RDS both present and enough data for the lock measurement
        public bool Locked;             // RDS 57kHz carrier locked to the 3rd harmonic of the pilot
        public float LockCoherence;     // 0..1, stability of the RDS/pilot phase difference
        public float LockPhaseDeg;      // RDS carrier phase relative to the 3rd pilot harmonic, -90..90
        public float LockSeconds;       // Seconds of data in the lock measurement (up to 5)
        public float PosPeakKHz;        // Positive and negative peak deviation over the last 1s (same detector as
        public float NegPeakKHz;        //   PeakDevKHz), to see asymmetric or clipped composite
        public float DemodLKHz;         // Peak of the decoded L and R (no de-emphasis) over the last 1s, in
        public float DemodRKHz;         //   deviation units: L = R = full scale with 9% pilot reads 68.25kHz
        public bool Stereo;             // The stereo decoder has a pilot (L and R are mono otherwise)
    }

    // Signals the scope can show
    public enum ScopeSignal { Mpx, Left, Right, Mid, Side }

    // IQ from SDR# -> FM demodulator -> 192kHz MPX -> (WAV recorder, spectrum analyzer, measurements)
    public sealed class MpxChain : IDisposable
    {
        // MPX rate. Content up to ~90kHz is kept
        public const double SAMPLERATE = 192000.0;

        // Deviation that maps to +/-1.0 at the demodulator output (broadcast FM full deviation)
        public const double DEVIATION = 75000.0;

        // Anti-alias filter for the resampling to 192kHz. Flat passband for level measurements
        public const double RESAMP_CUTOFF = 91000.0;
        public const double RESAMP_TRANS = 10000.0;

        public const int FFT_SIZE = 4096;
        public const int FFT_BINS = FFT_SIZE / 2 + 1;
        const int FFT_HOP = FFT_SIZE / 2;

        // Split recordings before the 4GiB limit of the 32bit WAV size fields
        const long MAX_WAV_BYTES = 4000000000L;

        // Level metering is done in 100ms blocks
        const int METER_BLOCK = 19200;
        const int POWER_LONG_BLOCKS = 600;      // 60s integration for MPX power (ITU-R BS.412)
        const int POWER_SHORT_BLOCKS = 10;      // 1s short term MPX power
        const int PEAK_BLOCKS = 10;             // Peak deviation over the last 1s
        const int SETTLE_BLOCKS = 5;            // Blocks ignored after a reset while the DC filter settles

        // 0dBr = power of a sine giving +/-19kHz deviation (ITU-R BS.412), in demodulator units (1.0 = 75kHz)
        const double POWER_REF = (19000.0 / DEVIATION) * (19000.0 / DEVIATION) / 2.0;

        // One pole DC removal (~3Hz) so a slightly off-centre carrier doesn't affect the measurements
        const float DC_ALPHA = 1e-4f;

        // Subcarrier levels. 19k and 57k both have a whole number of cycles in this many samples at 192kHz
        const int TONE_TABLE = 384;
        const double PILOT_FREQ = 19000.0;
        const double RDS_FREQ = 57000.0;

        // Pilot amplitude from 10ms Hann windowed correlations (~ +/-100Hz detector)
        const int PILOT_BLOCK = 1920;
        public const float PILOT_MIN_KHZ = 0.5f;    // Below this the station is considered to have no pilot

        // RDS is shifted to 0Hz, filtered to its bandwidth and decimated to 12kHz
        const int RDS_DECIM = 16;
        const double RDS_CUTOFF = 3000.0;       // Flat to 2.4kHz (RDS bandwidth), rejects L-R audio from 4kHz below 57kHz
        const double RDS_TRANS = 1200.0;
        const int RDS_CHUNK = 4096;
        const int RDS_SUB_BLOCK = 120;          // 10ms at 12kHz
        public const float RDS_MIN_KHZ = 0.3f;
        // Biphase coded RDS has almost no energy at exactly 57kHz while noise does. Noise power in the band is
        // estimated from the power of 10ms means (~ +/-50Hz around 57kHz), see ComputeDcFraction()
        // RDS is considered present when its power is at least this many times the noise power in its band
        const double RDS_MIN_SNR = 2.0;
        // Peak/RMS ratio used to report the RDS level as peak deviation. The theoretical EN 50067 waveform gives
        // 1.444, but real encoders and reference instruments read ~4.5% higher: a calibrated transmitter checked
        // with MPX Tool implies 1.51-1.52, and a Pira P175-calibrated receiver (VibeSDR, 21 UK stations) 1.507
        const double RDS_CREST_FACTOR = 1.51;

        // RDS to pilot phase lock: the 57kHz carrier phase is compared to 3x the pilot phase every 10ms and the
        // result averaged over this many 100ms blocks. Locked when the phase difference is stable enough
        const int LOCK_BLOCKS = 50;             // 5s
        const int LOCK_MIN_BLOCKS = 10;         // Don't report before 1s of data
        public const double LOCK_MIN_COHERENCE = 0.9;

        // Pilot frequency: the pilot shifted to 0Hz (the lock check's filtered 12kHz samples) rotates at the
        // frequency error. Each sample is compared with the one 1ms earlier, giving an unambiguous range of
        // +/-500Hz, and the products are averaged over 1s
        const int FREQ_LAG = 12;

        // Deviation peak detector release, when a response time is set. Long compared to audio periods, so a
        // steady tone reads its full peak; short peaks read lower the shorter they are than the response time
        const double PEAK_RELEASE_SEC = 1.0;

        // Scope history: the last 300ms of MPX (192kHz) and decoded M / S (48kHz). The scope shows up to 100ms, and
        // plays the signal out smoothly a little behind the newest samples (which arrive in bursts), see ScopeView
        public const double SCOPE_SECONDS = 0.3;
        const int SCOPE_MPX_LEN = (int)(SAMPLERATE * SCOPE_SECONDS);
        const int SCOPE_AUDIO_LEN = (int)(StereoDecoder.OUT_RATE * SCOPE_SECONDS);

        // ---- Demodulator and resampler (DSP thread) ----
        MpxFrontEnd frontEnd;       // null while the one for the current IQ rate is being built

        // Front ends are designed on a background thread: the design takes a noticeable time at high IQ rates, and
        // doing it on SDR#'s DSP thread stalled SDR# (IQ, waterfall and audio) every time the rate changed, e.g.
        // several times while dragging the filter bandwidth. The last few are kept for switching back and forth
        const int FRONT_END_CACHE = 6;
        readonly object buildLock = new object();
        readonly System.Collections.Generic.List<MpxFrontEnd> frontEndCache = new System.Collections.Generic.List<MpxFrontEnd>();
        double buildPending;        // Rate wanted by the DSP thread (0 = nothing to build)
        bool building;
        volatile MpxFrontEnd frontEndReady;

        // Build front ends on the calling thread (tests, which feed signals faster than real time)
        public static bool BuildInline;
        double iqRate;
        float prevRe = 1.0f, prevIm = 0.0f;
        float[] demodBuf = new float[0];
        float[] mpxBuf = new float[0];

        // ---- Recording ----
        readonly object recLock = new object();
        WavWriter writer;
        Func<string> pathGen;
        WavSampleType recType;
        string currentPath = "";
        volatile bool recording;
        long totalSamples;
        string recError;

        // ---- Spectrum (DSP thread) ----
        readonly Fft fft = new Fft(FFT_SIZE);
        readonly float[] window = new float[FFT_SIZE];
        readonly float[] frame = new float[FFT_SIZE];
        readonly double[] fftRe = new double[FFT_SIZE];
        readonly double[] fftIm = new double[FFT_SIZE];
        readonly float[] avgSpec = new float[FFT_BINS];
        readonly float[] peakSpec = new float[FFT_BINS];
        int framePos;
        bool firstFrame = true;
        volatile float avgAlpha = 0.1f;
        int peakReset;
        int spectrumReset;
        volatile bool analysisOn = true;

        // ---- Measurements (DSP thread) ----
        float dcEst;
        double blkSumSq;
        float blkPeak;
        int blkCount;
        int settleBlocks = SETTLE_BLOCKS;
        readonly double[] powerRing = new double[POWER_LONG_BLOCKS];
        int powerPos, powerFill;
        readonly float[] peakRing = new float[PEAK_BLOCKS];
        int peakPos, peakFill;
        float maxPeak;
        int measReset = 1;      // Also initialises the peak detector on the first block

        // Deviation peak detector (quasi-peak, DSP thread). attack = 0 means instant (sample peak)
        double peakResponse;        // Requested by the GUI, seconds
        double peakResponseUsed = -1.0;
        float peakAttack;
        float peakRelease;
        float peakDet;

        // Pilot and RDS level (DSP thread)
        readonly float[] pilotCos = new float[TONE_TABLE];
        readonly float[] pilotSin = new float[TONE_TABLE];
        readonly float[] rdsCos = new float[TONE_TABLE];
        readonly float[] rdsSin = new float[TONE_TABLE];
        readonly float[] pilotWin = new float[PILOT_BLOCK];
        readonly double pilotWinSum;
        int pilotPos;
        double pilotI, pilotQ;
        int pilotCount;
        double pilotAmpSum;
        int pilotAmpCount;
        readonly float[] pilotRing = new float[PEAK_BLOCKS];

        readonly float[] rdsTaps;
        readonly DecimatingComplexFir rdsFir;
        readonly float[] rdsMixRe = new float[RDS_CHUNK], rdsMixIm = new float[RDS_CHUNK];
        readonly float[] rdsBaseRe = new float[RDS_CHUNK], rdsBaseIm = new float[RDS_CHUNK];
        int rdsPos;
        float rdsSubSumRe, rdsSubSumIm;
        double rdsSubPow;
        int rdsSubCount;
        double rdsDcPow, rdsTotPow;
        int rdsSubBlocks;
        readonly double rdsNoiseDcFraction;
        readonly double rdsSignalDcFraction;

        // RDS to pilot phase lock (DSP thread)
        readonly DecimatingComplexFir lockPilotFir;
        readonly float[] lockPilotMixRe = new float[RDS_CHUNK], lockPilotMixIm = new float[RDS_CHUNK];
        readonly float[] lockPilotBaseRe = new float[RDS_CHUNK], lockPilotBaseIm = new float[RDS_CHUNK];
        Complex lockPilotSum, lockRdsSqSum, lockVec;
        double lockWeight;
        readonly Complex[] lockVecRing = new Complex[LOCK_BLOCKS];
        readonly double[] lockWeightRing = new double[LOCK_BLOCKS];
        int lockPos, lockFill;
        readonly double[] rdsDcRing = new double[PEAK_BLOCKS];
        readonly double[] rdsTotRing = new double[PEAK_BLOCKS];

        // Pilot frequency (DSP thread)
        readonly Complex[] freqDelay = new Complex[FREQ_LAG];
        int freqDelayPos, freqDelayFill;
        Complex freqVec;
        readonly Complex[] freqRing = new Complex[PEAK_BLOCKS];

        // Stereo decoder, demodulated and +/- peaks (DSP thread)
        readonly StereoDecoder stereo = new StereoDecoder();
        float posDet, negDet, demodDetL, demodDetR;
        float demodAttack, demodRelease;
        float blkPos, blkNeg, blkDemodL, blkDemodR;
        readonly float[] posRing = new float[PEAK_BLOCKS], negRing = new float[PEAK_BLOCKS];
        readonly float[] demodLRing = new float[PEAK_BLOCKS], demodRRing = new float[PEAK_BLOCKS];

        // Scope history (written on the DSP thread, read by the GUI under scopeLock)
        readonly object scopeLock = new object();
        readonly float[] scopeMpx = new float[SCOPE_MPX_LEN];
        readonly float[] scopeMid = new float[SCOPE_AUDIO_LEN];
        readonly float[] scopeSide = new float[SCOPE_AUDIO_LEN];
        int scopeMpxPos, scopeAudioPos;
        long scopeMpxTotal, scopeAudioTotal;    // Samples written so far
        bool scopeStereo;

        // ---- Shared with the GUI ----
        readonly object measLock = new object();
        MpxMeasurements measOut;
        bool measValid;

        readonly object specLock = new object();
        readonly float[] outAvg = new float[FFT_BINS];
        readonly float[] outPeak = new float[FFT_BINS];
        bool specValid;

        public MpxChain()
        {
            // Window, normalized so that a full scale (+/-1.0 = 75kHz deviation) sine reads 0dB
            double sum = 0.0;
            for (int i = 0; i < FFT_SIZE; i++)
            {
                window[i] = (float)DspUtil.BlackmanHarris(i, FFT_SIZE);
                sum += window[i];
            }
            float norm = (float)(2.0 / sum);
            for (int i = 0; i < FFT_SIZE; i++) { window[i] *= norm; }

            // Subcarrier reference tables
            for (int i = 0; i < TONE_TABLE; i++)
            {
                double t = i / SAMPLERATE;
                pilotCos[i] = (float)Math.Cos(2.0 * Math.PI * PILOT_FREQ * t);
                pilotSin[i] = (float)Math.Sin(2.0 * Math.PI * PILOT_FREQ * t);
                rdsCos[i] = (float)Math.Cos(2.0 * Math.PI * RDS_FREQ * t);
                rdsSin[i] = (float)Math.Sin(2.0 * Math.PI * RDS_FREQ * t);
            }
            for (int i = 0; i < PILOT_BLOCK; i++)
            {
                pilotWin[i] = (float)DspUtil.Hann(i, PILOT_BLOCK);
                pilotWinSum += pilotWin[i];
            }

            // RDS channel filter, and the pilot at 0Hz through an identical filter so its phase lines up
            // exactly with the RDS samples
            rdsTaps = DspUtil.LowPass(RDS_CUTOFF, RDS_TRANS, SAMPLERATE);
            rdsFir = new DecimatingComplexFir(rdsTaps, RDS_DECIM);
            lockPilotFir = new DecimatingComplexFir(rdsTaps, RDS_DECIM);
            rdsNoiseDcFraction = ComputeDcFraction(false);
            rdsSignalDcFraction = ComputeDcFraction(true);
        }

        public void Dispose()
        {
            StopRecording();
        }

        // ---- Input ----

        // IQ sample rate seen by the last ProcessIQ() call (0 before the first)
        public double IqRate => iqRate;

        // Interleaved IQ (re, im, re, im...) at `sampleRate`, any amplitude. Called from SDR#'s DSP thread
        public unsafe void ProcessIQ(float* iq, int length, double sampleRate)
        {
            if (length <= 0 || sampleRate <= 0.0) { return; }

            if (sampleRate != iqRate)
            {
                iqRate = sampleRate;
                frontEnd = null;
                RequestFrontEnd(sampleRate);
                Interlocked.Exchange(ref measReset, 1);
            }
            if (frontEnd == null)
            {
                var ready = frontEndReady;
                if (ready != null && ready.Rate == sampleRate)
                {
                    ready.Reset();
                    frontEnd = ready;
                    Interlocked.Exchange(ref measReset, 1);
                }
            }

            // Polar discriminator, scaled so that +/-1.0 = +/-75kHz
            if (demodBuf.Length < length) { demodBuf = new float[length]; }
            float scale = (float)(sampleRate / (2.0 * Math.PI * DEVIATION));
            float pr = prevRe, pi = prevIm;
            for (int i = 0; i < length; i++)
            {
                float re = iq[2 * i], im = iq[2 * i + 1];
                float dr = re * pr + im * pi;
                float di = im * pr - re * pi;
                demodBuf[i] = (float)Math.Atan2(di, dr) * scale;
                pr = re;
                pi = im;
            }
            prevRe = pr;
            prevIm = pi;

            // Nothing to output until the front end for this rate is ready (normally well under a second)
            if (frontEnd == null) { return; }
            int maxOut = frontEnd.MaxOutput(length);
            if (mpxBuf.Length < maxOut) { mpxBuf = new float[maxOut]; }
            int count = frontEnd.Process(demodBuf, length, mpxBuf);
            if (count > 0) { ProcessMpx(mpxBuf, count); }
        }

        // IQ rate -> MPX decimation used before the resampler (1 = none; 0 while being built)
        public int FrontEndDecimation => frontEnd?.Decimation ?? 0;

        // Makes a front end for `rate` available through frontEndReady: from the cache, or built on a background
        // thread. Only the latest request matters, so rates passed while dragging the bandwidth are skipped
        void RequestFrontEnd(double rate)
        {
            lock (buildLock)
            {
                var cached = frontEndCache.Find(f => f.Rate == rate);
                if (cached != null)
                {
                    frontEndReady = cached;
                    buildPending = 0.0;
                    return;
                }
                buildPending = rate;
                if (BuildInline)
                {
                    BuildPending();
                    return;
                }
                if (building) { return; }
                building = true;
            }
            var t = new Thread(() =>
            {
                try { BuildPending(); }
                finally { lock (buildLock) { building = false; } }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "MPX front end design" };
            t.Start();
        }

        void BuildPending()
        {
            while (true)
            {
                double rate;
                lock (buildLock)
                {
                    rate = buildPending;
                    if (rate <= 0.0) { return; }
                }
                var fe = new MpxFrontEnd(rate, SAMPLERATE, RESAMP_CUTOFF, RESAMP_TRANS);
                lock (buildLock)
                {
                    frontEndCache.Add(fe);
                    if (frontEndCache.Count > FRONT_END_CACHE) { frontEndCache.RemoveAt(0); }
                    if (buildPending == rate)
                    {
                        frontEndReady = fe;
                        buildPending = 0.0;
                    }
                }
            }
        }

        // 192kHz MPX, +/-1.0 = 75kHz deviation
        public void ProcessMpx(float[] data, int count)
        {
            Record(data, count);
            if (analysisOn)
            {
                Analyze(data, count);
                Measure(data, count);
            }
        }

        // ---- Spectrum ----

        // Exponential averaging over roughly `frames` FFT frames (1 = no averaging)
        public void SetAveraging(int frames) => avgAlpha = 1.0f / Math.Max(frames, 1);

        public void ResetPeak() => Interlocked.Exchange(ref peakReset, 1);

        // Copies the latest linear power spectra (FFT_BINS each). Returns false if nothing computed yet
        public bool GetSpectrum(float[] avg, float[] peak)
        {
            lock (specLock)
            {
                if (!specValid) { return false; }
                Array.Copy(outAvg, avg, FFT_BINS);
                Array.Copy(outPeak, peak, FFT_BINS);
                return true;
            }
        }

        // ---- Analysis on/off ----

        // Spectrum and measurements are only needed while they're displayed. Recording is not affected
        public void SetAnalysis(bool enable)
        {
            if (enable && !analysisOn)
            {
                // Start fresh rather than mixing in data from before the pause
                Interlocked.Exchange(ref measReset, 1);
                Interlocked.Exchange(ref spectrumReset, 1);
            }
            analysisOn = enable;
        }

        // ---- Measurements ----

        // Response time of the deviation peak detector: 0 = instant (true sample peak), otherwise a peak shorter
        // than this reads lower (e.g. 125us in Europe, 1ms in the US). Restarts the measurements
        public void SetPeakResponse(double seconds)
        {
            Interlocked.Exchange(ref peakResponse, Math.Max(seconds, 0.0));
            Interlocked.Exchange(ref measReset, 1);
        }

        public double PeakResponse => Interlocked.CompareExchange(ref peakResponse, 0.0, 0.0);

        // Restart the MPX power integration and peak deviation hold (e.g. after retuning)
        public void ResetMeasurements() => Interlocked.Exchange(ref measReset, 1);

        // Returns false while there isn't any measurement yet (just started or reset)
        public bool GetMeasurements(out MpxMeasurements meas)
        {
            lock (measLock)
            {
                meas = measOut;
                return measValid;
            }
        }

        // ---- Recording ----

        // `nextPath` is called to name the first file and each split file when the 4GiB limit is reached
        public bool StartRecording(Func<string> nextPath, WavSampleType type)
        {
            lock (recLock)
            {
                if (recording) { return true; }
                pathGen = nextPath;
                recType = type;
                recError = null;
                if (!OpenNext()) { return false; }
                totalSamples = 0;
                recording = true;
                return true;
            }
        }

        public void StopRecording()
        {
            lock (recLock)
            {
                if (!recording) { return; }
                CloseWriter();
                recording = false;
            }
        }

        public bool IsRecording => recording;

        public long RecordedSamples => Interlocked.Read(ref totalSamples);

        public string RecordingPath { get { lock (recLock) { return currentPath; } } }

        // Last recording error (null if none)
        public string RecordingError { get { lock (recLock) { return recError; } } }

        bool OpenNext()
        {
            try
            {
                currentPath = pathGen();
                writer = new WavWriter(currentPath, (int)SAMPLERATE, recType);
                return true;
            }
            catch (Exception e)
            {
                recError = "Failed to open " + currentPath + ": " + e.Message;
                writer = null;
                return false;
            }
        }

        void CloseWriter()
        {
            try { writer?.Dispose(); }
            catch (Exception e) { recError = "Failed to close " + currentPath + ": " + e.Message; }
            writer = null;
        }

        void Record(float[] data, int count)
        {
            if (!recording) { return; }
            lock (recLock)
            {
                if (!recording || writer == null) { return; }
                try
                {
                    // Roll over to a new file before exceeding the WAV size limit
                    if ((writer.SamplesWritten + count) * WavWriter.BytesPerSample(recType) > MAX_WAV_BYTES)
                    {
                        CloseWriter();
                        if (!OpenNext())
                        {
                            recording = false;
                            return;
                        }
                    }
                    writer.Write(data, count);
                    Interlocked.Add(ref totalSamples, count);
                }
                catch (Exception e)
                {
                    // E.g. disk full: stop rather than throwing into SDR#'s DSP thread
                    recError = "Recording stopped: " + e.Message;
                    CloseWriter();
                    recording = false;
                }
            }
        }

        // ---- Measurements (DSP thread) ----

        void Measure(float[] data, int count)
        {
            if (Interlocked.Exchange(ref measReset, 0) != 0)
            {
                powerFill = powerPos = 0;
                peakFill = peakPos = 0;
                maxPeak = 0.0f;
                blkSumSq = 0.0;
                blkPeak = 0.0f;
                blkCount = 0;
                settleBlocks = SETTLE_BLOCKS;
                pilotI = pilotQ = 0.0;
                pilotCount = 0;
                pilotAmpSum = 0.0;
                pilotAmpCount = 0;
                rdsSubSumRe = rdsSubSumIm = 0.0f;
                rdsSubPow = 0.0;
                rdsSubCount = 0;
                rdsDcPow = rdsTotPow = 0.0;
                rdsSubBlocks = 0;
                lockPilotSum = lockRdsSqSum = Complex.Zero;
                lockVec = Complex.Zero;
                lockWeight = 0.0;
                lockPos = lockFill = 0;
                freqDelayPos = freqDelayFill = 0;
                freqVec = Complex.Zero;
                peakDet = 0.0f;
                peakResponseUsed = PeakResponse;
                peakAttack = (peakResponseUsed > 0.0) ? (float)(1.0 - Math.Exp(-1.0 / (peakResponseUsed * SAMPLERATE))) : 0.0f;
                peakRelease = (float)Math.Exp(-1.0 / (PEAK_RELEASE_SEC * SAMPLERATE));
                posDet = negDet = demodDetL = demodDetR = 0.0f;
                blkPos = blkNeg = blkDemodL = blkDemodR = 0.0f;
                demodAttack = (peakResponseUsed > 0.0) ? (float)(1.0 - Math.Exp(-1.0 / (peakResponseUsed * StereoDecoder.OUT_RATE))) : 0.0f;
                demodRelease = (float)Math.Exp(-1.0 / (PEAK_RELEASE_SEC * StereoDecoder.OUT_RATE));
                lock (measLock) { measValid = false; }
            }

            MeasureRds(data, count);
            MeasureStereo(data, count);

            for (int i = 0; i < count; i++)
            {
                dcEst += DC_ALPHA * (data[i] - dcEst);
                float x = data[i] - dcEst;
                blkSumSq += x * x;

                // Deviation peak: the sample peak, or a quasi-peak detector with the chosen response time
                // (first order attack with that time constant, slow release)
                float ax = Math.Abs(x);
                if (peakAttack > 0.0f)
                {
                    peakDet = (ax > peakDet) ? peakDet + peakAttack * (ax - peakDet) : peakDet * peakRelease;
                    ax = peakDet;
                }
                blkPeak = Math.Max(blkPeak, ax);

                // Positive and negative peaks, same detector
                float px = Math.Max(x, 0.0f), nx = Math.Max(-x, 0.0f);
                if (peakAttack > 0.0f)
                {
                    posDet = (px > posDet) ? posDet + peakAttack * (px - posDet) : posDet * peakRelease;
                    negDet = (nx > negDet) ? negDet + peakAttack * (nx - negDet) : negDet * peakRelease;
                    px = posDet;
                    nx = negDet;
                }
                blkPos = Math.Max(blkPos, px);
                blkNeg = Math.Max(blkNeg, nx);

                // Pilot: correlate with 19kHz over a windowed block
                float wx = pilotWin[pilotCount] * x;
                pilotI += wx * pilotCos[pilotPos];
                pilotQ -= wx * pilotSin[pilotPos];
                pilotPos = (pilotPos + 1) % TONE_TABLE;
                if (++pilotCount == PILOT_BLOCK)
                {
                    pilotAmpSum += 2.0 * Math.Sqrt(pilotI * pilotI + pilotQ * pilotQ) / pilotWinSum;
                    pilotAmpCount++;
                    pilotI = pilotQ = 0.0;
                    pilotCount = 0;
                }

                if (++blkCount == METER_BLOCK) { FinishBlock(); }
            }
        }

        // Fraction of the power (after the RDS filter) that ends up in the 10ms means, for white noise (rds = false)
        // or for a standard RDS signal (rds = true)
        double ComputeDcFraction(bool rds)
        {
            const double outRate = SAMPLERATE / RDS_DECIM;
            const double td = 1.0 / 1187.5;
            const int points = 4096;
            double total = 0.0, dc = 0.0;
            for (int p = 0; p < points; p++)
            {
                // Frequencies across the decimated band (-outRate/2 .. outRate/2)
                double f = ((p + 0.5) / points - 0.5) * outRate;

                // Filter response
                double re = 0.0, im = 0.0;
                for (int i = 0; i < rdsTaps.Length; i++)
                {
                    double ph = -2.0 * Math.PI * f * i / SAMPLERATE;
                    re += rdsTaps[i] * Math.Cos(ph);
                    im += rdsTaps[i] * Math.Sin(ph);
                }
                double h2 = re * re + im * im;

                // EN 50067 RDS spectrum: biphase symbol pair times the cos shaping, zero above 2/td
                if (rds)
                {
                    double af = Math.Abs(f);
                    double shaping = (af <= 2.0 / td) ? Math.Cos(Math.PI * af * td / 4.0) : 0.0;
                    double biphase = Math.Sin(Math.PI * af * td / 2.0);
                    h2 *= shaping * shaping * biphase * biphase;
                }

                // Response of the mean over RDS_SUB_BLOCK samples
                double x = Math.PI * f / outRate;
                double d = (Math.Abs(Math.Sin(x)) < 1e-12) ? 1.0 : Math.Sin(RDS_SUB_BLOCK * x) / (RDS_SUB_BLOCK * Math.Sin(x));

                total += h2;
                dc += h2 * d * d;
            }
            return dc / total;
        }

        void MeasureRds(float[] data, int count)
        {
            for (int off = 0; off < count; off += RDS_CHUNK)
            {
                int n = Math.Min(RDS_CHUNK, count - off);

                // Shift 57kHz (and the pilot, for the lock check) to 0Hz, then filter and decimate
                for (int i = 0; i < n; i++)
                {
                    float x = data[off + i];
                    rdsMixRe[i] = x * rdsCos[rdsPos];
                    rdsMixIm[i] = -x * rdsSin[rdsPos];
                    lockPilotMixRe[i] = x * pilotCos[rdsPos];
                    lockPilotMixIm[i] = -x * pilotSin[rdsPos];
                    rdsPos = (rdsPos + 1) % TONE_TABLE;
                }
                int outCount = rdsFir.Process(rdsMixRe, rdsMixIm, n, rdsBaseRe, rdsBaseIm);
                lockPilotFir.Process(lockPilotMixRe, lockPilotMixIm, n, lockPilotBaseRe, lockPilotBaseIm);

                for (int i = 0; i < outCount; i++)
                {
                    float zr = rdsBaseRe[i], zi = rdsBaseIm[i];
                    float pow = zr * zr + zi * zi;

                    // Squaring removes the BPSK data, leaving twice the 57kHz carrier phase
                    var zc = new Complex(zr, zi);
                    lockRdsSqSum += zc * zc;
                    var zp = new Complex(lockPilotBaseRe[i], lockPilotBaseIm[i]);
                    lockPilotSum += zp;

                    // Pilot rotation over FREQ_LAG samples
                    if (freqDelayFill == FREQ_LAG) { freqVec += zp * Complex.Conjugate(freqDelay[freqDelayPos]); }
                    else { freqDelayFill++; }
                    freqDelay[freqDelayPos] = zp;
                    freqDelayPos = (freqDelayPos + 1) % FREQ_LAG;

                    // Power close to 57kHz (10ms means) vs total power, to tell RDS from noise
                    rdsSubSumRe += zr;
                    rdsSubSumIm += zi;
                    rdsSubPow += pow;
                    if (++rdsSubCount == RDS_SUB_BLOCK)
                    {
                        float mre = rdsSubSumRe / RDS_SUB_BLOCK;
                        float mim = rdsSubSumIm / RDS_SUB_BLOCK;
                        rdsDcPow += mre * mre + mim * mim;
                        rdsTotPow += rdsSubPow / RDS_SUB_BLOCK;
                        rdsSubBlocks++;

                        // Phase of (RDS carrier)^2 relative to (3rd pilot harmonic)^2 = 6x pilot phase,
                        // weighted by the RDS strength in this 10ms
                        double pilotMag = lockPilotSum.Magnitude;
                        if (pilotMag > 0.0)
                        {
                            Complex u = lockPilotSum / pilotMag;
                            Complex u3 = u * u * u;
                            lockVec += lockRdsSqSum * Complex.Conjugate(u3 * u3);
                            lockWeight += lockRdsSqSum.Magnitude;
                        }
                        lockPilotSum = lockRdsSqSum = Complex.Zero;
                        rdsSubSumRe = rdsSubSumIm = 0.0f;
                        rdsSubPow = 0.0;
                        rdsSubCount = 0;
                    }
                }
            }
        }

        // Stereo decoding for the scope and the demodulated L / R peaks
        void MeasureStereo(float[] data, int count)
        {
            int n = stereo.Process(data, count);
            float[] mid = stereo.Mid, side = stereo.Side;
            for (int i = 0; i < n; i++)
            {
                float l = Math.Abs(mid[i] + side[i]), r = Math.Abs(mid[i] - side[i]);
                if (demodAttack > 0.0f)
                {
                    demodDetL = (l > demodDetL) ? demodDetL + demodAttack * (l - demodDetL) : demodDetL * demodRelease;
                    demodDetR = (r > demodDetR) ? demodDetR + demodAttack * (r - demodDetR) : demodDetR * demodRelease;
                    l = demodDetL;
                    r = demodDetR;
                }
                blkDemodL = Math.Max(blkDemodL, l);
                blkDemodR = Math.Max(blkDemodR, r);
            }

            lock (scopeLock)
            {
                for (int i = 0; i < count; i++)
                {
                    scopeMpx[scopeMpxPos] = data[i];
                    if (++scopeMpxPos == SCOPE_MPX_LEN) { scopeMpxPos = 0; }
                }
                scopeMpxTotal += count;
                scopeAudioTotal += n;
                for (int i = 0; i < n; i++)
                {
                    scopeMid[scopeAudioPos] = mid[i];
                    scopeSide[scopeAudioPos] = side[i];
                    if (++scopeAudioPos == SCOPE_AUDIO_LEN) { scopeAudioPos = 0; }
                }
                scopeStereo = stereo.Stereo;
            }
        }

        // Sample rate of a scope signal
        public static double ScopeRate(ScopeSignal sig) => sig == ScopeSignal.Mpx ? SAMPLERATE : StereoDecoder.OUT_RATE;

        // Copies `count` samples of `sig` (oldest first) ending at sample index `end` (samples since the start; the
        // newest when < 0), clamped to the history kept. Returns the number copied; `newest` is the index just past
        // the newest sample written, `end` the end actually used. `stereoOut` tells whether the decoder has a pilot
        public int GetScope(ScopeSignal sig, float[] dest, int count, ref long end, out long newest, out bool stereoOut)
        {
            lock (scopeLock)
            {
                stereoOut = scopeStereo;
                bool mpx = sig == ScopeSignal.Mpx;
                int len = mpx ? SCOPE_MPX_LEN : SCOPE_AUDIO_LEN;
                int pos = mpx ? scopeMpxPos : scopeAudioPos;
                newest = mpx ? scopeMpxTotal : scopeAudioTotal;
                count = (int)Math.Min(Math.Min(count, len), Math.Min(dest.Length, newest));
                if (end < 0 || end > newest) { end = newest; }
                if (end - count < newest - len) { end = newest - len + count; }
                int back = (int)(newest - end);         // Samples between the end and the newest
                for (int i = 0; i < count; i++)
                {
                    int k = ((pos - back - count + i) % len + len) % len;
                    if (mpx) { dest[i] = scopeMpx[k]; continue; }
                    float m = scopeMid[k], sd = scopeSide[k];
                    dest[i] = sig == ScopeSignal.Left ? m + sd : sig == ScopeSignal.Right ? m - sd : sig == ScopeSignal.Mid ? m : sd;
                }
                return count;
            }
        }

        // The newest `count` samples
        public int GetScope(ScopeSignal sig, float[] dest, int count, out bool stereoOut)
        {
            long end = -1;
            return GetScope(sig, dest, count, ref end, out _, out stereoOut);
        }

        void FinishBlock()
        {
            double meanSq = blkSumSq / METER_BLOCK;
            float peak = blkPeak;
            blkSumSq = 0.0;
            blkPeak = 0.0f;
            blkCount = 0;

            float pos = blkPos, neg = blkNeg, demodL = blkDemodL, demodR = blkDemodR;
            blkPos = blkNeg = blkDemodL = blkDemodR = 0.0f;
            float pilotAmp = pilotAmpCount > 0 ? (float)(pilotAmpSum / pilotAmpCount) : 0.0f;
            pilotAmpSum = 0.0;
            pilotAmpCount = 0;
            Complex blkLockVec = lockVec;
            double blkLockWeight = lockWeight;
            lockVec = Complex.Zero;
            lockWeight = 0.0;
            Complex blkFreqVec = freqVec;
            freqVec = Complex.Zero;
            double rdsDc = rdsSubBlocks > 0 ? rdsDcPow / rdsSubBlocks : 0.0;
            double rdsTot = rdsSubBlocks > 0 ? rdsTotPow / rdsSubBlocks : 0.0;
            rdsDcPow = rdsTotPow = 0.0;
            rdsSubBlocks = 0;

            // Let the DC filter settle after a reset (e.g. a retune) before measuring
            if (settleBlocks > 0)
            {
                settleBlocks--;
                return;
            }

            powerRing[powerPos] = meanSq;
            powerPos = (powerPos + 1) % POWER_LONG_BLOCKS;
            powerFill = Math.Min(powerFill + 1, POWER_LONG_BLOCKS);

            peakRing[peakPos] = peak;
            pilotRing[peakPos] = pilotAmp;
            rdsDcRing[peakPos] = rdsDc;
            rdsTotRing[peakPos] = rdsTot;
            freqRing[peakPos] = blkFreqVec;
            posRing[peakPos] = pos;
            negRing[peakPos] = neg;
            demodLRing[peakPos] = demodL;
            demodRRing[peakPos] = demodR;
            peakPos = (peakPos + 1) % PEAK_BLOCKS;

            lockVecRing[lockPos] = blkLockVec;
            lockWeightRing[lockPos] = blkLockWeight;
            lockPos = (lockPos + 1) % LOCK_BLOCKS;
            lockFill = Math.Min(lockFill + 1, LOCK_BLOCKS);
            peakFill = Math.Min(peakFill + 1, PEAK_BLOCKS);
            maxPeak = Math.Max(maxPeak, peak);

            // Average over the most recent blocks
            double longSum = 0.0, shortSum = 0.0;
            int shortCount = Math.Min(powerFill, POWER_SHORT_BLOCKS);
            for (int k = 1; k <= powerFill; k++)
            {
                double p = powerRing[(powerPos - k + POWER_LONG_BLOCKS) % POWER_LONG_BLOCKS];
                longSum += p;
                if (k <= shortCount) { shortSum += p; }
            }
            float recentPeak = 0.0f;
            double pilotSum = 0.0, rdsDcSum = 0.0, rdsTotSum = 0.0;
            Complex freqSum = Complex.Zero;
            float recentPos = 0.0f, recentNeg = 0.0f, recentL = 0.0f, recentR = 0.0f;
            for (int k = 0; k < peakFill; k++)
            {
                recentPeak = Math.Max(recentPeak, peakRing[k]);
                pilotSum += pilotRing[k];
                rdsDcSum += rdsDcRing[k];
                rdsTotSum += rdsTotRing[k];
                freqSum += freqRing[k];
                recentPos = Math.Max(recentPos, posRing[k]);
                recentNeg = Math.Max(recentNeg, negRing[k]);
                recentL = Math.Max(recentL, demodLRing[k]);
                recentR = Math.Max(recentR, demodRRing[k]);
            }

            const double khz = DEVIATION / 1000.0;
            var meas = new MpxMeasurements();
            meas.PowerDBr = (float)(10.0 * Math.Log10(Math.Max(longSum / powerFill, 1e-20) / POWER_REF));
            meas.PowerShortDBr = (float)(10.0 * Math.Log10(Math.Max(shortSum / shortCount, 1e-20) / POWER_REF));
            meas.PowerSeconds = (float)(powerFill * METER_BLOCK / SAMPLERATE);
            meas.PeakDevKHz = (float)(recentPeak * khz);
            meas.MaxDevKHz = (float)(maxPeak * khz);
            meas.PeakResponseSec = (float)Math.Max(peakResponseUsed, 0.0);
            meas.PosPeakKHz = (float)(recentPos * khz);
            meas.NegPeakKHz = (float)(recentNeg * khz);
            meas.DemodLKHz = (float)(recentL * khz);
            meas.DemodRKHz = (float)(recentR * khz);
            meas.PilotKHz = (float)(pilotSum / peakFill * khz);
            meas.PilotPresent = meas.PilotKHz >= PILOT_MIN_KHZ;
            stereo.Stereo = meas.PilotPresent;
            meas.Stereo = stereo.Stereo;
            meas.PilotFreqHz = (float)(PILOT_FREQ + freqSum.Phase * (SAMPLERATE / RDS_DECIM) / (2.0 * Math.PI * FREQ_LAG));

            // RDS: split the band power into signal and noise using how much of each falls close to 57kHz:
            //   total = signal + noise,  dc = rdsSignalDcFraction * signal + rdsNoiseDcFraction * noise
            // then RMS -> peak. A DSB signal a(t)cos(wt) becomes a(t)/2 at 0Hz
            double rdsTotal = rdsTotSum / peakFill;
            double rdsDcPower = rdsDcSum / peakFill;
            double rdsSignal = MathUtil.Clamp((rdsNoiseDcFraction * rdsTotal - rdsDcPower) / (rdsNoiseDcFraction - rdsSignalDcFraction), 0.0, rdsTotal);
            double rdsNoise = rdsTotal - rdsSignal;
            meas.RdsKHz = (float)(2.0 * Math.Sqrt(rdsSignal) * RDS_CREST_FACTOR * khz);
            meas.RdsPresent = meas.RdsKHz >= RDS_MIN_KHZ && rdsSignal >= RDS_MIN_SNR * rdsNoise;

            // Lock: a stable phase difference gives a long average vector, a drifting one averages out
            Complex lockSum = Complex.Zero;
            double lockWeightSum = 0.0;
            for (int k = 0; k < lockFill; k++)
            {
                lockSum += lockVecRing[k];
                lockWeightSum += lockWeightRing[k];
            }
            meas.LockSeconds = (float)(lockFill * METER_BLOCK / SAMPLERATE);
            meas.LockCoherence = lockWeightSum > 0.0 ? (float)(lockSum.Magnitude / lockWeightSum) : 0.0f;
            meas.LockValid = meas.PilotPresent && meas.RdsPresent && lockFill >= LOCK_MIN_BLOCKS;
            meas.Locked = meas.LockValid && meas.LockCoherence >= LOCK_MIN_COHERENCE;

            // Halve the doubled phase. BPSK leaves a 180 degree ambiguity, so fold into -90..90
            double phase = lockSum.Phase / 2.0 * (180.0 / Math.PI);
            if (phase >= 90.0) { phase -= 180.0; }
            if (phase < -90.0) { phase += 180.0; }
            meas.LockPhaseDeg = (float)phase;

            lock (measLock)
            {
                measOut = meas;
                measValid = true;
            }
        }

        // ---- Spectrum (DSP thread) ----

        void Analyze(float[] data, int count)
        {
            if (Interlocked.Exchange(ref spectrumReset, 0) != 0)
            {
                framePos = 0;
                firstFrame = true;
            }

            int off = 0;
            while (count > 0)
            {
                int n = Math.Min(count, FFT_SIZE - framePos);
                Array.Copy(data, off, frame, framePos, n);
                framePos += n;
                off += n;
                count -= n;

                if (framePos < FFT_SIZE) { break; }
                ComputeFrame();

                // 50% overlap
                Array.Copy(frame, FFT_HOP, frame, 0, FFT_SIZE - FFT_HOP);
                framePos = FFT_SIZE - FFT_HOP;
            }
        }

        void ComputeFrame()
        {
            for (int i = 0; i < FFT_SIZE; i++)
            {
                fftRe[i] = frame[i] * window[i];
                fftIm[i] = 0.0;
            }
            fft.Transform(fftRe, fftIm);

            float alpha = avgAlpha;
            bool resetPk = Interlocked.Exchange(ref peakReset, 0) != 0;
            for (int i = 0; i < FFT_BINS; i++)
            {
                float p = (float)(fftRe[i] * fftRe[i] + fftIm[i] * fftIm[i]);
                avgSpec[i] = firstFrame ? p : avgSpec[i] + alpha * (p - avgSpec[i]);
                peakSpec[i] = (firstFrame || resetPk) ? p : Math.Max(peakSpec[i], p);
            }
            firstFrame = false;

            lock (specLock)
            {
                Array.Copy(avgSpec, outAvg, FFT_BINS);
                Array.Copy(peakSpec, outPeak, FFT_BINS);
                specValid = true;
            }
        }
    }
}
