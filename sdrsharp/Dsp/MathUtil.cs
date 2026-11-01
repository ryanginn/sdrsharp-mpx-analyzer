namespace SDRSharp.MPXAnalyzer.Dsp
{
    // Math.Clamp isn't available on .NET Framework (older SDR# builds)
    public static class MathUtil
    {
        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
        public static decimal Clamp(decimal v, decimal min, decimal max) => v < min ? min : (v > max ? max : v);
    }
}
