using System;
using SDRSharp.MPXAnalyzer.Dsp;
using SDRSharp.Radio;

namespace SDRSharp.MPXAnalyzer
{
    // Receives SDR#'s decimated and filtered IQ (the Radio's channel, after its IF filter) on the DSP thread
    public sealed unsafe class MpxIQHook : IIQProcessor
    {
        readonly MpxChain chain;
        volatile bool failed;

        public MpxIQHook(MpxChain chain)
        {
            this.chain = chain;
        }

        public double SampleRate { get; set; }

        public bool Enabled { get; set; } = true;

        // Last exception from the DSP thread (processing stops after one rather than taking SDR# down)
        public string Error { get; private set; }

        public void Process(Complex* buffer, int length)
        {
            if (failed) { return; }
            try
            {
                // SDR#'s Complex is two floats (Real, Imag)
                chain.ProcessIQ((float*)buffer, length, SampleRate);
            }
            catch (Exception e)
            {
                Error = e.ToString();
                failed = true;
            }
        }
    }
}
