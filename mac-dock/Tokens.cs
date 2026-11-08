using System;

namespace MacDock
{
    // Design tokens shared by every window: corner radii and spacing (96-dpi units, scale with Dpi), durations (seconds)
    // and easing curves. Springs stay in Anim.cs; this is for fixed-time transitions. One font stack: Typography.cs (Inter).
    static class Tokens
    {
        // Radii
        public const int RadiusSm = 8, RadiusMd = 14, RadiusLg = 22;
        // Spacing
        public const int Space1 = 4, Space2 = 8, Space3 = 12, Space4 = 14, Space5 = 20;
        // Durations (seconds); Motion.Scale shortens them all to zero when Reduce Motion is on.
        public const double Fast = 0.12, Normal = 0.22, Slow = 0.38;

        public static double Duration(double seconds) { return seconds * Motion.Scale; }

        // Easing: input and output are 0..1.
        public static double Linear(double t) { return Clamp(t); }
        public static double EaseOut(double t) { t = 1 - Clamp(t); return 1 - t * t * t; }
        public static double EaseIn(double t) { t = Clamp(t); return t * t * t; }
        public static double EaseInOut(double t) { t = Clamp(t); return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
        static double Clamp(double t) { return t < 0 ? 0 : t > 1 ? 1 : t; }
    }

    // Reduce Motion: Windows "Animation effects" off (SPI_GETCLIENTAREAANIMATION). Scale is 1 normally, 0 when reduced.
    static class Motion
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);
        public static bool Reduced;
        public static double Scale { get { return Reduced ? 0 : 1; } }
        public static void Refresh()
        {
            bool on = true;
            try { if (SystemParametersInfo(0x1042, 0, ref on, 0)) Reduced = !on; } catch { }
        }
    }
}
