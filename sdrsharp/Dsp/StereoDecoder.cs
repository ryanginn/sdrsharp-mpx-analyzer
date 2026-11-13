using System;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    // FM stereo decoder for the scope and the demodulated levels. Input is the 192kHz MPX (+/-1.0 = 75kHz), output
    // at 48kHz in the same units:
    //   M = (L+R)/2 and S = (L-R)/2 as transmitted, so L = M + S and R = M - S
    // A transmitter at 100% with 9% pilot and L = R = full scale gives M = 0.91, i.e. 68.25kHz. No de-emphasis:
    // the levels are as transmitted, so composite clipping shows up as overshoot on L and R.
    //
    // ITU-R BS.450: MPX = M + S sin(2wt) + pilot sin(wt). A PLL locks a 19kHz oscillator to the pilot, and its
    // doubled phase demodulates the 38kHz subcarrier. Without a pilot it decodes mono (S = 0).
    public sealed class StereoDecoder
    {
        public const int DECIM = 4;
        public const double OUT_RATE = MpxChain.SAMPLERATE / DECIM;

        const double PILOT_FREQ = 19000.0;
        // Audio filter: -0.06dB at 15kHz, pilot -78dB (291 taps; the transition band is about twice AUDIO_TRANS)
        const double AUDIO_CUTOFF = 16700.0;
        const double AUDIO_TRANS = 2500.0;
        // PLL: 2nd order loop, ~10Hz natural frequency. Locks to any real pilot (19kHz +/-2Hz) in well under a second
        const double LOOP_HZ = 10.0;
        const double LOOP_DAMPING = 0.707;
        // Pilot amplitude estimate (for the loop gain and the stereo decision), ~20ms time constant
        const double AMP_ALPHA = 1.0 / (0.02 * MpxChain.SAMPLERATE);

        readonly DecimatingComplexFir fir;
        readonly double kp, ki;
        double phase;                       // Oscillator phase (pilot = A sin(phase) when locked)
        double freq;                        // Radians per sample
        double ampI, ampQ;                  // Lowpassed x sin(phase), x cos(phase): (A/2, 0) when locked
        float[] mixRe = new float[0], mixIm = new float[0];
        float[] outRe = new float[0], outIm = new float[0];

        public StereoDecoder()
        {
            fir = new DecimatingComplexFir(DspUtil.LowPass(AUDIO_CUTOFF, AUDIO_TRANS, MpxChain.SAMPLERATE), DECIM);
            double wn = 2.0 * Math.PI * LOOP_HZ / MpxChain.SAMPLERATE;
            kp = 2.0 * LOOP_DAMPING * wn;
            ki = wn * wn;
            Reset();
        }

        public void Reset()
        {
            fir.Reset();
            Stereo = false;
            phase = 0.0;
            freq = 2.0 * Math.PI * PILOT_FREQ / MpxChain.SAMPLERATE;
            ampI = ampQ = 0.0;
        }

        // Pilot amplitude seen by the PLL (1.0 = 75kHz)
        public double PilotAmplitude => 2.0 * Math.Sqrt(ampI * ampI + ampQ * ampQ);

        // Decode stereo. Set by the chain from its pilot measurement, which is much more selective than the PLL's
        // amplitude estimate (a loud tone near 19kHz isn't taken for a pilot). The outputs are mono (S = 0) otherwise
        public bool Stereo { get; set; }

        // Pilot frequency the PLL is tracking, Hz
        public double PilotFreqHz => freq * MpxChain.SAMPLERATE / (2.0 * Math.PI);

        // Decodes `count` MPX samples. Returns the number of 48kHz samples in Mid / Side
        public int Process(float[] data, int count)
        {
            if (mixRe.Length < count)
            {
                mixRe = new float[count];
                mixIm = new float[count];
                outRe = new float[count / DECIM + 2];
                outIm = new float[count / DECIM + 2];
            }

            bool stereo = Stereo;
            double ph = phase, f = freq, ai = ampI, aq = ampQ;
            for (int i = 0; i < count; i++)
            {
                double x = data[i];
                double s = Math.Sin(ph), c = Math.Cos(ph);

                // Phase detector: x cos(phase) averages (A/2) sin(error). Normalised by the amplitude so the loop
                // bandwidth doesn't depend on the pilot level
                ai += AMP_ALPHA * (x * s - ai);
                aq += AMP_ALPHA * (x * c - aq);
                double amp = Math.Max(Math.Sqrt(ai * ai + aq * aq), 1e-4);
                // Not clipped: the audio and subcarrier terms are much larger than the pilot's, and any nonlinearity
                // here would turn them into a phase offset. The low loop gain averages them out
                double err = x * c / amp;
                f += ki * err;
                ph += f + kp * err;
                if (ph > Math.PI) { ph -= 2.0 * Math.PI; }

                // sin(2 phase) = 2 s c. S sin^2 averages S/2, hence the 2
                mixRe[i] = (float)x;
                mixIm[i] = stereo ? (float)(4.0 * x * s * c) : 0.0f;
            }
            // Keep the oscillator close to 19kHz if it wanders off without a pilot (real pilots are within +/-2Hz)
            double fNom = 2.0 * Math.PI * PILOT_FREQ / MpxChain.SAMPLERATE;
            double fMax = 2.0 * Math.PI * 50.0 / MpxChain.SAMPLERATE;
            f = fNom + Math.Max(-fMax, Math.Min(fMax, f - fNom));
            phase = ph;
            freq = f;
            ampI = ai;
            ampQ = aq;

            return fir.Process(mixRe, mixIm, count, outRe, outIm);
        }

        // Outputs of the last Process() call
        public float[] Mid => outRe;
        public float[] Side => outIm;
    }
}
