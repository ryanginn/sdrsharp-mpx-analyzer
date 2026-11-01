using System;
using System.Numerics;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    // Resamples the FM demodulator output from SDR#'s IQ rate (whatever it is) to the 192kHz MPX rate.
    //
    // The prototype low pass also corrects the demodulator's high frequency roll-off. The demodulator measures
    // the phase change over one input sample, i.e. the average frequency over 1/fs, which scales modulation at
    // frequency f by sinc(f / fs): at fs = 312.5kHz that's -0.6% at 19kHz and -5.4% at 57kHz. The passband is
    // shaped by the inverse of that (windowed frequency sampling design), so the MPX is flat up to the cutoff.
    //
    // Polyphase with PHASES sub-sample positions; coefficients between two phases are linearly interpolated.
    public sealed class MpxResampler
    {
        const int PHASES = 256;
        const int DESIGN_STEPS = 1000;

        readonly double inRate;
        readonly double step;       // Input samples per output sample
        readonly int taps;          // Taps per phase (even)
        readonly int half;
        readonly float[][] bank;    // [phase 0..PHASES][tap]

        float[] hist;               // Input history, hist[0] is the oldest sample kept
        int histLen;
        double pos;                 // Position of the next output sample in hist (input sample units)

        public MpxResampler(double inRate, double outRate, double cutoff, double transWidth)
        {
            this.inRate = inRate;
            step = inRate / outRate;

            // Keep the passband below the input's Nyquist frequency when the IQ rate is low
            cutoff = Math.Min(cutoff, 0.5 * inRate - 0.5 * transWidth);
            half = Math.Max(4, (int)Math.Ceiling(1.9 * inRate / transWidth));
            taps = 2 * half;

            // Frequency samples of the target response (inverse of the demodulator droop) up to the cutoff
            double df = cutoff / DESIGN_STEPS;
            var gain = new double[DESIGN_STEPS];
            for (int k = 0; k < DESIGN_STEPS; k++)
            {
                double x = Math.PI * ((k + 0.5) * df) / inRate;
                gain[k] = x / Math.Sin(x);
            }

            // Continuous impulse response h(u), u in input samples, evaluated on a PHASES times finer grid:
            // h(u) = 2/fs * sum_k gain_k * cos(2 pi f_k u / fs) * df, times a Nuttall window over [-half, half]
            int gridLen = taps * PHASES + 1;
            var grid = new double[gridLen];
            for (int g = 0; g < gridLen; g++)
            {
                double u = (double)g / PHASES - half;
                double w = DspUtil.Nuttall(u + half, taps);
                // cos(2 pi (k + 0.5) df u / fs) by rotating a phasor
                double a = 2.0 * Math.PI * df * u / inRate;
                double cr = Math.Cos(0.5 * a), ci = Math.Sin(0.5 * a);
                double sr = Math.Cos(a), si = Math.Sin(a);
                double acc = 0.0;
                for (int k = 0; k < DESIGN_STEPS; k++)
                {
                    acc += gain[k] * cr;
                    double nr = cr * sr - ci * si;
                    ci = cr * si + ci * sr;
                    cr = nr;
                }
                grid[g] = (2.0 * acc * df / inRate) * w;
            }

            // Phase p (fraction p / PHASES), tap j multiplies input n = i0 - half + 1 + j, at u = frac + half - 1 - j
            bank = new float[PHASES + 1][];
            double sumAll = 0.0;
            for (int p = 0; p <= PHASES; p++)
            {
                bank[p] = new float[taps];
                for (int j = 0; j < taps; j++)
                {
                    int g = p + (taps - 1 - j) * PHASES;   // (u + half) * PHASES
                    bank[p][j] = (float)grid[g];
                    if (p < PHASES) { sumAll += grid[g]; }
                }
            }

            // Unity gain at DC
            float norm = (float)(PHASES / sumAll);
            foreach (var b in bank) { for (int j = 0; j < taps; j++) { b[j] *= norm; } }

            hist = new float[Math.Max(taps * 4, 16384)];
            Reset();
        }

        public double InputRate => inRate;

        public void Reset()
        {
            Array.Clear(hist, 0, hist.Length);
            histLen = taps;     // Start with silence
            pos = half - 1;
        }

        // Upper bound on the number of output samples for `count` input samples
        public int MaxOutput(int count) => (int)Math.Ceiling((count + taps) / step) + 2;

        public int Process(float[] input, int count, float[] output)
        {
            // Append the input to the history
            if (histLen + count > hist.Length) { Array.Resize(ref hist, Math.Max(hist.Length * 2, histLen + count)); }
            Array.Copy(input, 0, hist, histLen, count);
            histLen += count;

            int outCount = 0;
            int vlen = Vector<float>.Count;
            while (true)
            {
                int i0 = (int)pos;
                int first = i0 - half + 1;
                if (first + taps > histLen) { break; }
                double fracPh = (pos - i0) * PHASES;
                int p = (int)fracPh;
                float a = (float)(fracPh - p);

                // Two dot products, blended
                float[] b0 = bank[p], b1 = bank[p + 1];
                var acc0 = Vector<float>.Zero;
                var acc1 = Vector<float>.Zero;
                int j = 0;
                for (; j <= taps - vlen; j += vlen)
                {
                    var x = new Vector<float>(hist, first + j);
                    acc0 += x * new Vector<float>(b0, j);
                    acc1 += x * new Vector<float>(b1, j);
                }
                float s0 = Vector.Dot(acc0, Vector<float>.One);
                float s1 = Vector.Dot(acc1, Vector<float>.One);
                for (; j < taps; j++)
                {
                    float x = hist[first + j];
                    s0 += x * b0[j];
                    s1 += x * b1[j];
                }
                output[outCount++] = s0 + a * (s1 - s0);
                pos += step;
            }

            // Drop the history that is no longer needed
            int drop = Math.Max(0, (int)pos - half + 1);
            if (drop > 0)
            {
                Array.Copy(hist, drop, hist, 0, histLen - drop);
                histLen -= drop;
                pos -= drop;
            }
            return outCount;
        }
    }
}
