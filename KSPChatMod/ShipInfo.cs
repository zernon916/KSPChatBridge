using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class ShipInfo
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static string Distance(double meters)
        {
            if (!Finite(meters)) return "--";
            return Math.Abs(meters) >= 1000
                ? (meters / 1000).ToString("#,0.0", Inv) + " km"
                : meters.ToString("#,0", Inv) + " m";
        }

        internal static string Speed(double metersPerSecond)
        {
            return Finite(metersPerSecond) ? metersPerSecond.ToString("#,0.0", Inv) + " m/s" : "--";
        }

        internal static string Mass(double tonnes)
        {
            if (!Finite(tonnes)) return "--";
            return Math.Abs(tonnes) >= 1
                ? tonnes.ToString("#,0.00", Inv) + " t"
                : (tonnes * 1000).ToString("#,0", Inv) + " kg";
        }

        internal static string Ratio(double ratio)
        {
            return Finite(ratio) ? ratio.ToString("0.00", Inv) + "x" : "--";
        }

        internal static string Percent(double fraction)
        {
            return Finite(fraction) ? (fraction * 100).ToString("0", Inv) + "%" : "--";
        }

        internal static string Resource(double amount, double capacity)
        {
            if (!Finite(amount) || !Finite(capacity) || capacity <= 0) return "--";
            return amount.ToString("#,0.0", Inv) + " / " + capacity.ToString("#,0.0", Inv);
        }

        internal static float ResourceFraction(double amount, double capacity)
        {
            if (!Finite(amount) || !Finite(capacity) || capacity <= 0) return 0;
            return (float)Math.Max(0, Math.Min(1, amount / capacity));
        }

        internal static string Angle(double degrees)
        {
            return Finite(degrees) ? degrees.ToString("0.0", Inv) + "\u00B0" : "--";
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
