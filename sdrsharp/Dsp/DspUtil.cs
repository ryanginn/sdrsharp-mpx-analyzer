using System;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    // Window functions and filter design helpers. They follow SDR++'s definitions, so the C# port gives the
    // same results as the SDR++ plugin
    public static class DspUtil
    {
        static double Cosine(double n, double N, double a0, double a1, double a2, double a3)
        {
            double x = 2.0 * Math.PI * n / N;
            return a0 - a1 * Math.Cos(x) + a2 * Math.Cos(2.0 * x) - a3 * Math.Cos(3.0 * x);
        }

        public static double Hann(double n, double N) => 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / N);
        public static double BlackmanHarris(double n, double N) => Cosine(n, N, 0.35875, 0.48829, 0.14128, 0.01168);
        public static double Nuttall(double n, double N) => Cosine(n, N, 0.355768, 0.487396, 0.144232, 0.012604);

        static double Sinc(double x) => (x == 0.0) ? 1.0 : Math.Sin(x) / x;

        // Same as SDR++'s dsp::taps::lowPass(cutoff, transWidth, sampleRate): Nuttall windowed sinc,
        // 3.8 * fs / transWidth taps, unity gain at DC
        public static float[] LowPass(double cutoff, double transWidth, double sampleRate)
        {
            int count = (int)(3.8 * sampleRate / transWidth);
            double omega = 2.0 * Math.PI * cutoff / sampleRate;
            double half = count / 2.0;
            double corr = omega / Math.PI;
            var taps = new float[count];
            for (int i = 0; i < count; i++)
            {
                double t = i - half + 0.5;
                taps[i] = (float)(Sinc(t * omega) * Nuttall(t - half, count) * corr);
            }
            return taps;
        }
    }
}
