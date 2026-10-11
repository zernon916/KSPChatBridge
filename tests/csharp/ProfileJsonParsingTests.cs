using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSPChatBridge;

// Task 4 (tests only): offline checks that MiniJson parses a learned plane_profiles
// block - the A300 entry - into the shapes the autopilot reads: `stall` (a number),
// `turns[0]` (a numeric row) and `samples` ([key, value, mass] triples).
// The fixture mirrors PlaneProfile.ToDict() (KSPChatMod/LearnFlight.cs) and the schema
// documented in docs/AUTOPILOT_OVERHAUL.md; no production code is exercised or changed.
internal static class ProfileJsonParsingTests
{
    // A copy of the A300 plane_profiles block (native_settings.json -> plane_profiles["Stearwing A300"]).
    const string A300PlaneProfiles =
        "{" +
        "  \"plane_profiles\": {" +
        "    \"Stearwing A300\": {" +
        "      \"m0\": 42.0, \"liftoff\": 82.5, \"stall\": 63.5, \"stall_alt_loss\": 145.0," +
        "      \"climb_accel\": 2.4, \"climb_vs\": 21.0, \"decel\": 4.6, \"accel\": 6.2, \"lag\": 1.7," +
        "      \"econ_speed\": null, \"econ_throttle\": null, \"fuel_used\": 3.1," +
        "      \"complete\": true, \"abort\": \"\", \"date\": \"2026-02-14\"," +
        "      \"turns\": [[30, 1.15, 4.2, 35, 0.32, 41.5, 0.7], [45, 1.41, 6.1, 88, 0.54, 41.0, 0.83]]," +
        "      \"dives\": [[-15, 46.0, 58.0, 0.4]]," +
        "      \"sink_table\": [[5.0, 0.42, 1.3, 12.0, 0.0]]," +
        "      \"samples\": [[\"cruise_speed\", 246.5, 40.2], [\"max_level_speed\", 318.0, 40.0]," +
        "                   [\"roll_rate\", 21.5, 40.2], [\"pitch_rate\", 8.5, 40.2]," +
        "                   [\"liftoff\", 82.5, 42.0], [\"stall\", 63.5, 41.0]]," +
        "      \"perf\": {\"ground_roll\": 610.0, \"max_level\": 318.0, \"cruise_speed\": 246.5, \"cruise_throttle\": 0.68}," +
        "      \"rerun\": [], \"flap_stall\": 52.0, \"flap_limit\": 40.0, \"flap_sub\": true" +
        "    }" +
        "  }" +
        "}";

    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static double Num(object o) { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }

    static IList FindSample(IList samples, string key)
    {
        foreach (var s in samples)
        {
            var row = s as IList;
            if (row != null && row.Count >= 1 && (row[0] as string) == key) return row;
        }
        return null;
    }

    internal static void Run()
    {
        var root = MiniJson.Deserialize(A300PlaneProfiles);
        Check(root != null && root.ContainsKey("plane_profiles"), "root has plane_profiles");

        var profiles = root["plane_profiles"] as Dictionary<string, object>;
        Check(profiles != null && profiles.ContainsKey("Stearwing A300"), "plane_profiles has the A300 entry");
        var p = profiles["Stearwing A300"] as Dictionary<string, object>;
        Check(p != null, "A300 entry is an object");

        // stall: a single number (fractional -> double)
        Check(p.ContainsKey("stall") && p["stall"] is double, "stall parsed as a number");
        Check(Math.Abs(Num(p["stall"]) - 63.5) < 1e-9, "stall value = 63.5");

        // turns[0]: first learned turn row [bank, g, turn rate, alt loss, stress, mass, throttle]
        var turns = p["turns"] as IList;
        Check(turns != null && turns.Count == 2, "turns has 2 rows");
        var t0 = turns[0] as IList;
        Check(t0 != null && t0.Count == 7, "turns[0] has 7 columns");
        Check(t0[0] is int && Math.Abs(Num(t0[0]) - 30) < 1e-9, "turns[0] bank = 30 (whole number -> int)");
        Check(Math.Abs(Num(t0[1]) - 1.15) < 1e-9, "turns[0] g = 1.15");
        Check(Math.Abs(Num(t0[6]) - 0.7) < 1e-9, "turns[0] throttle = 0.7");

        // samples: [key, value, mass] triples
        var samples = p["samples"] as IList;
        Check(samples != null && samples.Count == 6, "samples has 6 triples");
        var cruise = FindSample(samples, "cruise_speed");
        Check(cruise != null && cruise[0] is string, "cruise_speed sample key is a string");
        Check(Math.Abs(Num(cruise[1]) - 246.5) < 1e-9 && Math.Abs(Num(cruise[2]) - 40.2) < 1e-9, "cruise_speed sample = 246.5 @ 40.2 t");
        Check(FindSample(samples, "max_level_speed") != null && FindSample(samples, "roll_rate") != null && FindSample(samples, "pitch_rate") != null, "max_level_speed / roll_rate / pitch_rate samples present");

        Console.WriteLine("Profile JSON parsing: 14 behavior checks passed.");
    }
}
