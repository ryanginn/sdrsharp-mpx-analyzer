// Compile-only stand-in for SDR# 1716's SDRSharp.Common.dll (see ../README.md): the members the plugin uses
using System.Windows.Forms;
using SDRSharp.Radio;

namespace SDRSharp.Common
{
    public interface ISharpControl
    {
        DetectorType DetectorType { get; set; }
        long CenterFrequency { get; set; }
        int FilterBandwidth { get; set; }
        long Frequency { get; set; }
        bool IsPlaying { get; }
        void RegisterStreamHook(object streamHook, ProcessorType processorType);
        void UnregisterStreamHook(object streamHook);
    }

    // Same members as SDR# 1716's ISharpPlugin
    public interface ISharpPlugin
    {
        void Initialize(ISharpControl control);
        void Close();
        UserControl Gui { get; }
        string DisplayName { get; }
    }
}
