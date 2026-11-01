using System;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    // Complex input, real taps, decimating FIR (equivalent to SDR++'s DecimatingFIR<complex_t, float>)
    public sealed class DecimatingComplexFir
    {
        readonly float[] taps;      // Reversed, so the newest sample lines up with the last tap
        readonly int decim;
        float[] histRe, histIm;
        int histLen;
        int offset;

        public DecimatingComplexFir(float[] taps, int decim)
        {
            this.taps = (float[])taps.Clone();
            Array.Reverse(this.taps);
            this.decim = decim;
            histRe = new float[taps.Length * 2 + 8192];
            histIm = new float[taps.Length * 2 + 8192];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(histRe, 0, histRe.Length);
            Array.Clear(histIm, 0, histIm.Length);
            histLen = taps.Length - 1;
            offset = 0;
        }

        // Returns the number of outputs written
        public int Process(float[] inRe, float[] inIm, int count, float[] outRe, float[] outIm)
        {
            if (histLen + count > histRe.Length)
            {
                Array.Resize(ref histRe, histLen + count);
                Array.Resize(ref histIm, histLen + count);
            }
            Array.Copy(inRe, 0, histRe, histLen, count);
            Array.Copy(inIm, 0, histIm, histLen, count);
            histLen += count;

            int n = taps.Length;
            int outCount = 0;
            for (; offset + n <= histLen; offset += decim)
            {
                float re = 0.0f, im = 0.0f;
                for (int j = 0; j < n; j++)
                {
                    re += histRe[offset + j] * taps[j];
                    im += histIm[offset + j] * taps[j];
                }
                outRe[outCount] = re;
                outIm[outCount] = im;
                outCount++;
            }

            // Keep the last n - 1 samples (plus whatever the next output needs)
            int drop = offset;
            Array.Copy(histRe, drop, histRe, 0, histLen - drop);
            Array.Copy(histIm, drop, histIm, 0, histLen - drop);
            histLen -= drop;
            offset = 0;
            return outCount;
        }
    }
}
