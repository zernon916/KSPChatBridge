using System;
using KSPChatBridge;

internal static class FlightLogTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FlightLogTests: " + name);
        passes++;
    }

    internal static void Run()
    {
        passes = 0;
        FlightLog.Clear();
        Check(FlightLog.FormatTime(0) == "D+0 00:00:00", "formats zero universal time");
        Check(FlightLog.FormatTime(90061) == "D+1 01:01:01", "formats day and clock rollover");
        Check(FlightLog.FormatTime(double.NaN) == "--" && FlightLog.FormatTime(double.PositiveInfinity) == "--",
            "invalid timestamps have a safe display");

        FlightLog.Add(100, "Kerbin", "Launch", "Liftoff");
        FlightLog.Add(300, "Mun", "SOI", "Entered Mun SOI");
        FlightLog.Add(300, "Mun", "Landing", "Touchdown");
        FlightLogEntry[] entries = FlightLog.SnapshotNewestFirst();
        Check(entries.Length == 3 && entries[0].Text == "Touchdown" && entries[2].Text == "Liftoff",
            "orders entries by descending universal time");
        Check(entries[0].Text == "Touchdown" && entries[1].Text == "Entered Mun SOI",
            "newer insertion wins equal-time ordering");
        Check(FlightLog.Format(entries[0]) == "D+0 00:05:00  LANDING  Mun - Touchdown",
            "formats time, kind, body and text");

        FlightLog.Clear();
        for (int i = 0; i < FlightLog.Capacity + 2; i++) FlightLog.Add(i, "Kerbin", "Stage", i.ToString());
        entries = FlightLog.SnapshotNewestFirst();
        Check(entries.Length == FlightLog.Capacity, "caps retained entries");
        Check(entries[0].Text == "201" && entries[entries.Length - 1].Text == "2",
            "cap drops oldest captured entries");
        FlightLog.Clear();
        Console.WriteLine("FlightLogTests: " + passes + " behavior checks passed.");
    }
}
