// Compile-only stand-in for SDR# 1716's SDRSharp.Radio.dll (see ../README.md): the members the plugin uses.
// Enum values are in SDR# 1716's order
using System.Runtime.InteropServices;

namespace SDRSharp.Radio
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Complex
    {
        public float Real;
        public float Imag;
    }

    public enum DetectorType { NFM, WFM, AM, DSB, LSB, USB, CW, RAW }

    public enum ProcessorType { RawIQ, DecimatedAndFilteredIQ, DemodulatorOutput, FilteredAudioOutput, FMMPX, RDSBitStream }

    public interface IBaseProcessor
    {
        bool Enabled { get; set; }
    }

    public interface IStreamProcessor : IBaseProcessor
    {
        double SampleRate { set; }
    }

    public unsafe interface IIQProcessor : IStreamProcessor
    {
        void Process(Complex* buffer, int length);
    }
}
