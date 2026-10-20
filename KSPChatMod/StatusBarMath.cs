// RS6: tablet status bar mapping (pure, tested) - phone-style signal bars (dot + 0-4 bars) from CommNet signal strength and a battery
// gauge (green > 50%, amber 20-50%, red < 20%).
using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class StatusBarMath
    {
        internal const int MaxBars = 4;
        /// <summary>0 bars without a link; any link shows at least one bar; 25 / 50 / 75 % steps.</summary>
        internal static int Bars(bool connected, double signal)
        {
            if (!connected) return 0; float s = CommsStatus.SignalFraction(signal);
            if (s <= 0) return 0; if (s >= .75f) return 4; if (s >= .5f) return 3; if (s >= .25f) return 2; return 1;
        }
        /// <summary>Bar i (0-based) height as a fraction of the icon height (rising).</summary>
        internal static float BarHeight(int i) { return .35f + .65f * (i + 1) / MaxBars; }
        /// <summary>Battery fraction 0..1, NaN without batteries.</summary>
        internal static double Battery(double amount, double max)
        {
            if (!(max > 0) || double.IsNaN(amount)) return double.NaN; return Math.Max(0, Math.Min(1, amount / max));
        }
        /// <summary>2 = green (> 50%), 1 = amber (20-50%), 0 = red (< 20%), -1 = no battery.</summary>
        internal static int BatteryBand(double frac) { if (double.IsNaN(frac)) return -1; return frac > .5 ? 2 : frac >= .2 ? 1 : 0; }
        internal static string Label(bool connected, double signal, double delayS, bool relay, double batt)
        {
            string s = connected ? CommsStatus.SignalPercent(signal) + (delayS > 0 ? " " + CommsStatus.FormatDelay(delayS) : "") + (relay ? " R" : "") : "NO LINK";
            return s + "  " + (double.IsNaN(batt) ? "--" : (100 * batt).ToString("0", CultureInfo.InvariantCulture) + "%");
        }
    }
}