using System;
using System.IO;
using System.Text;

namespace SDRSharp.MPXAnalyzer.Dsp
{
    public enum WavSampleType
    {
        Int16,
        Int32,
        Float32
    }

    // Mono WAV writer. Integer formats clip at +/-1.0 (75kHz deviation), Float32 keeps over-deviation
    public sealed class WavWriter : IDisposable
    {
        const int HEADER_UPDATE_SAMPLES = 192000 * 10;   // Keep the header valid every ~10s in case SDR# crashes

        readonly FileStream file;
        readonly BinaryWriter writer;
        readonly WavSampleType type;
        readonly int sampleRate;
        readonly long dataSizePos;
        readonly long riffSizePos;
        readonly long factPos = -1;
        readonly long dataStart;
        long samples;
        long samplesAtHeaderUpdate;
        byte[] buffer = new byte[0];

        public WavWriter(string path, int sampleRate, WavSampleType type)
        {
            this.type = type;
            this.sampleRate = sampleRate;
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
            writer = new BinaryWriter(file, Encoding.ASCII, true);

            bool isFloat = (type == WavSampleType.Float32);
            int bytes = BytesPerSample(type);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            riffSizePos = file.Position;
            writer.Write(0u);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(isFloat ? 18u : 16u);
            writer.Write((ushort)(isFloat ? 3 : 1));    // WAVE_FORMAT_IEEE_FLOAT / WAVE_FORMAT_PCM
            writer.Write((ushort)1);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * bytes));
            writer.Write((ushort)bytes);
            writer.Write((ushort)(bytes * 8));
            if (isFloat)
            {
                writer.Write((ushort)0);
                // Non-PCM formats should have a fact chunk with the sample count
                writer.Write(Encoding.ASCII.GetBytes("fact"));
                writer.Write(4u);
                factPos = file.Position;
                writer.Write(0u);
            }

            writer.Write(Encoding.ASCII.GetBytes("data"));
            dataSizePos = file.Position;
            writer.Write(0u);
            dataStart = file.Position;
        }

        public static int BytesPerSample(WavSampleType type) => (type == WavSampleType.Int16) ? 2 : 4;

        public long SamplesWritten => samples;

        public long DataBytes => samples * BytesPerSample(type);

        public void Write(float[] data, int count)
        {
            int bytes = BytesPerSample(type);
            if (buffer.Length < count * bytes) { buffer = new byte[count * bytes]; }
            switch (type)
            {
                case WavSampleType.Int16:
                    for (int i = 0; i < count; i++)
                    {
                        float x = Math.Clamp(data[i], -1.0f, 1.0f);
                        short s = (short)Math.Round(x * 32767.0f);
                        buffer[2 * i] = (byte)s;
                        buffer[2 * i + 1] = (byte)(s >> 8);
                    }
                    break;
                case WavSampleType.Int32:
                    for (int i = 0; i < count; i++)
                    {
                        double x = Math.Clamp(data[i], -1.0f, 1.0f);
                        int s = (int)Math.Round(x * 2147483647.0);
                        buffer[4 * i] = (byte)s;
                        buffer[4 * i + 1] = (byte)(s >> 8);
                        buffer[4 * i + 2] = (byte)(s >> 16);
                        buffer[4 * i + 3] = (byte)(s >> 24);
                    }
                    break;
                default:
                    Buffer.BlockCopy(data, 0, buffer, 0, count * 4);
                    break;
            }
            file.Write(buffer, 0, count * bytes);
            samples += count;

            if (samples - samplesAtHeaderUpdate >= HEADER_UPDATE_SAMPLES) { UpdateHeader(); }
        }

        void UpdateHeader()
        {
            long end = file.Position;
            uint dataBytes = (uint)DataBytes;
            file.Position = riffSizePos;
            writer.Write((uint)(dataStart - 8 + dataBytes));
            if (factPos >= 0)
            {
                file.Position = factPos;
                writer.Write((uint)samples);
            }
            file.Position = dataSizePos;
            writer.Write(dataBytes);
            file.Position = end;
            file.Flush();
            samplesAtHeaderUpdate = samples;
        }

        public void Dispose()
        {
            UpdateHeader();
            writer.Dispose();
            file.Dispose();
        }
    }
}
