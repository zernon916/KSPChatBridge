using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal sealed class FlightLogEntry
    {
        internal readonly double UniversalTime;
        internal readonly string Body;
        internal readonly string Kind;
        internal readonly string Text;
        internal readonly long Sequence;

        internal FlightLogEntry(double universalTime, string body, string kind, string text, long sequence)
        {
            UniversalTime = universalTime;
            Body = body;
            Kind = kind;
            Text = text;
            Sequence = sequence;
        }
    }

    internal static class FlightLog
    {
        internal const int Capacity = 200;
        static readonly List<FlightLogEntry> Entries = new List<FlightLogEntry>();
        static long nextSequence;

        internal static void Add(double universalTime, string body, string kind, string text)
        {
            Entries.Add(new FlightLogEntry(universalTime, string.IsNullOrEmpty(body) ? "Unknown body" : body,
                string.IsNullOrEmpty(kind) ? "Event" : kind, text ?? "", nextSequence++));
            if (Entries.Count > Capacity) Entries.RemoveAt(0);
        }

        internal static FlightLogEntry[] SnapshotNewestFirst()
        {
            var snapshot = Entries.ToArray();
            Array.Sort(snapshot, (a, b) =>
            {
                int time = b.UniversalTime.CompareTo(a.UniversalTime);
                return time != 0 ? time : b.Sequence.CompareTo(a.Sequence);
            });
            return snapshot;
        }

        internal static string FormatTime(double universalTime)
        {
            if (double.IsNaN(universalTime) || double.IsInfinity(universalTime) || universalTime < 0) return "--";
            double wholeSeconds = Math.Floor(universalTime);
            long days = (long)Math.Floor(wholeSeconds / 86400);
            int daySeconds = (int)(wholeSeconds - days * 86400);
            int hours = daySeconds / 3600, minutes = daySeconds / 60 % 60, seconds = daySeconds % 60;
            return "D+" + days.ToString(CultureInfo.InvariantCulture) + " " +
                hours.ToString("00", CultureInfo.InvariantCulture) + ":" +
                minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        internal static string Format(FlightLogEntry entry)
        {
            if (entry == null) return "";
            return FormatTime(entry.UniversalTime) + "  " + entry.Kind.ToUpperInvariant() + "  " +
                entry.Body + " - " + entry.Text;
        }

        internal static void Clear()
        {
            Entries.Clear();
            nextSequence = 0;
        }
    }
}
