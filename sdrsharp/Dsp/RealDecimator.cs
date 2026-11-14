using System;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    // Real input, decimate by an integer factor with a low pass FIR. Only the kept outputs are computed
    public sealed class RealDecimator
    {
        readonly float[] taps;      // Reversed, so the newest sample lines up with the last tap
        readonly int decim;
        float[] hist;
        int histLen;
        int offset;                 // Start of the next output's window in hist

        public RealDecimator(float[] taps, int decim)
        {
            this.taps = (float[])taps.Clone();
            Array.Reverse(this.taps);
            this.decim = decim;
            hist = new float[taps.Length * 2 + 16384];
            Reset();
        }

        public int Factor => decim;

        public void Reset()
        {
            Array.Clear(hist, 0, hist.Length);
            histLen = taps.Length - 1;
            offset = 0;
        }

        // Upper bound on the number of outputs for `count` inputs
        public int MaxOutput(int count) => count / decim + 2;

        public int Process(float[] input, int count, float[] output)
        {
            if (histLen + count > hist.Length) { Array.Resize(ref hist, Math.Max(hist.Length * 2, histLen + count)); }
            Array.Copy(input, 0, hist, histLen, count);
            histLen += count;

            int n = taps.Length;
            int outCount = 0;
#if NETCOREAPP
            int vlen = System.Numerics.Vector<float>.Count;
#endif
            for (; offset + n <= histLen; offset += decim)
            {
                float acc = 0.0f;
                int j = 0;
#if NETCOREAPP
                var v = System.Numerics.Vector<float>.Zero;
                for (; j <= n - vlen; j += vlen)
                {
                    v += new System.Numerics.Vector<float>(hist, offset + j) * new System.Numerics.Vector<float>(taps, j);
                }
                acc = System.Numerics.Vector.Dot(v, System.Numerics.Vector<float>.One);
#endif
                for (; j < n; j++) { acc += hist[offset + j] * taps[j]; }
                output[outCount++] = acc;
            }

            Array.Copy(hist, offset, hist, 0, histLen - offset);
            histLen -= offset;
            offset = 0;
            return outCount;
        }
    }
}
