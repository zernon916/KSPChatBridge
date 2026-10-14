using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class ContractFormat
    {
        internal static string RemainingTime(double deadline, double now, double dayLengthSeconds)
        {
            if (double.IsNaN(deadline) || double.IsInfinity(deadline) || deadline <= 0) return "no time limit";
            double remaining = deadline - now;
            if (double.IsNaN(remaining) || double.IsInfinity(remaining)) return "unavailable";
            if (remaining <= 0) return "expired";
            if (double.IsNaN(dayLengthSeconds) || double.IsInfinity(dayLengthSeconds) || dayLengthSeconds < 60)
                return "unavailable";

            double minutesRemaining = Math.Floor(remaining / 60d);
            double minutesPerDay = Math.Floor(dayLengthSeconds / 60d);
            double days = Math.Floor(minutesRemaining / minutesPerDay);
            double minutesIntoDay = minutesRemaining % minutesPerDay;
            double hours = Math.Floor(minutesIntoDay / 60d);
            double minutes = minutesIntoDay % 60d;
            return days.ToString("0", CultureInfo.InvariantCulture) + "d " +
                hours.ToString("0", CultureInfo.InvariantCulture) + "h " +
                minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        }

        internal static string Funds(double amount)
        {
            if (double.IsNaN(amount) || double.IsInfinity(amount)) return "--";
            return "\u00A4" + amount.ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
