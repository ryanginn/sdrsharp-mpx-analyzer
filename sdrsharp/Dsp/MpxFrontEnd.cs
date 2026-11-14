using System;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    // FM demodulator output at the IQ rate -> 192kHz MPX. At high IQ rates (a wide filter in SDR#) it's first
    // decimated by an integer factor to 400-800kHz with a short FIR, so the resampler (whose length and design
    // time grow with its input rate) stays small. Built off SDR#'s DSP thread, see MpxChain
    public sealed class MpxFrontEnd
    {
        // Decimate when the IQ rate allows it to this or more (aliases then fall at >= 304kHz, far from 0-96kHz)
        const double MIN_DECIMATED_RATE = 400000.0;
        // Pre-decimation filter: flat (< 0.001dB) to 96kHz, >100dB down from 300kHz; ~38 taps per MHz of IQ rate
        const double DECIM_CUTOFF = 190000.0;
        const double DECIM_TRANS = 100000.0;

        readonly RealDecimator decimator;
        readonly MpxResampler resampler;
        float[] midBuf = new float[0];

        public MpxFrontEnd(double iqRate, double outRate, double cutoff, double transWidth)
        {
            Rate = iqRate;
            int decim = (int)Math.Floor(iqRate / MIN_DECIMATED_RATE);
            double resampRate = iqRate;
            if (decim >= 2)
            {
                decimator = new RealDecimator(DspUtil.LowPass(DECIM_CUTOFF, DECIM_TRANS, iqRate), decim);
                resampRate = iqRate / decim;
            }
            // The droop to correct is the demodulator's, at the full IQ rate
            resampler = new MpxResampler(resampRate, outRate, cutoff, transWidth, iqRate);
        }

        // IQ rate this was built for
        public double Rate { get; }

        public int Decimation => decimator?.Factor ?? 1;

        public void Reset()
        {
            decimator?.Reset();
            resampler.Reset();
        }

        public int MaxOutput(int count) => resampler.MaxOutput(decimator != null ? decimator.MaxOutput(count) : count);

        public int Process(float[] input, int count, float[] output)
        {
            if (decimator == null) { return resampler.Process(input, count, output); }
            int max = decimator.MaxOutput(count);
            if (midBuf.Length < max) { midBuf = new float[max]; }
            int n = decimator.Process(input, count, midBuf);
            return resampler.Process(midBuf, n, output);
        }
    }
}
