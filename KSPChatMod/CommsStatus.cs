using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class CommsStatus
    {
        internal static float SignalFraction(double strength)
        {
            if (double.IsNaN(strength) || double.IsInfinity(strength)) return 0;
            return (float)Math.Max(0, Math.Min(1, strength));
        }

        internal static string SignalPercent(double strength)
        {
            if (double.IsNaN(strength) || double.IsInfinity(strength)) return "--";
            return (SignalFraction(strength) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        internal static string FormatDelay(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--";
            if (seconds < 1) return (seconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " ms";
            return seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        internal static string FormatName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? "UNKNOWN" : name.Replace('_', ' ').ToUpperInvariant();
        }
    }
}
