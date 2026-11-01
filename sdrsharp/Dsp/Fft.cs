using System;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    // In place radix 2 complex FFT of a fixed power of two size
    public sealed class Fft
    {
        readonly int size;
        readonly int[] bitRev;
        readonly double[] cosTab;
        readonly double[] sinTab;

        public Fft(int size)
        {
            if (size < 2 || (size & (size - 1)) != 0) { throw new ArgumentException("FFT size must be a power of two"); }
            this.size = size;
            int bits = 0;
            while ((1 << bits) < size) { bits++; }
            bitRev = new int[size];
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) { r |= ((i >> b) & 1) << (bits - 1 - b); }
                bitRev[i] = r;
            }
            cosTab = new double[size / 2];
            sinTab = new double[size / 2];
            for (int i = 0; i < size / 2; i++)
            {
                cosTab[i] = Math.Cos(2.0 * Math.PI * i / size);
                sinTab[i] = -Math.Sin(2.0 * Math.PI * i / size);
            }
        }

        public int Size => size;

        public void Transform(double[] re, double[] im)
        {
            for (int i = 0; i < size; i++)
            {
                int j = bitRev[i];
                if (j > i)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= size; len <<= 1)
            {
                int halfLen = len >> 1;
                int step = size / len;
                for (int start = 0; start < size; start += len)
                {
                    for (int k = 0; k < halfLen; k++)
                    {
                        double wr = cosTab[k * step], wi = sinTab[k * step];
                        int a = start + k, b = a + halfLen;
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
