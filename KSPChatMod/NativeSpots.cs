using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal sealed class NativeSpots
    {
        readonly Dictionary<string, object> saved;
        internal NativeSpots(Dictionary<string, object> settings)
        {
            object data;
            saved = settings.TryGetValue("landing_spots", out data) ? data as Dictionary<string, object> : null;
            if (saved == null) settings["landing_spots"] = saved = new Dictionary<string, object>();
        }
        static double Number(Dictionary<string, object> row, string key, double fallback)
        {
            object value; if (!row.TryGetValue(key, out value) || value == null) return fallback;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("Invalid spot " + key);
            return number;
        }
        static string Text(Dictionary<string, object> row, string key, string fallback)
        { object value; return row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback; }
        static string Normalize(string name) { return (name ?? "").Trim().Replace('_', ' ').ToLowerInvariant(); }
        internal Dictionary<string, object> Find(string name, string body)
        {
            foreach (var pair in saved) if (Normalize(pair.Key) == Normalize(name))
            {
                var row = pair.Value as Dictionary<string, object>;
                if (row == null) throw new ArgumentException("Invalid saved spot: " + pair.Key);
                if (Text(row, "body", "Kerbin") != body) throw new ArgumentException("Saved spot is on another body.");
                return row;
            }
            return null;
        }
        static double[] Pair(object value)
        {
            var values = new List<double>(); var sequence = value as IEnumerable;
            if (sequence != null) foreach (object item in sequence) values.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture));
            if (values.Count != 2 || double.IsNaN(values[0]) || double.IsNaN(values[1]) || Math.Abs(values[0]) > 90 || Math.Abs(values[1]) > 180)
                throw new ArgumentException("Invalid spot coordinates.");
            return values.ToArray();
        }
        internal TaxiMission.Point Point(string name, string body)
        {
            var row = Find(name, body); if (row == null) return null;
            object first;
            double[] coordinates = row.TryGetValue("a", out first) ? Pair(first)
                : Pair(new[] { Number(row, "lat", double.NaN), Number(row, "lon", double.NaN) });
            return new TaxiMission.Point { Name = name, Lat = coordinates[0], Lon = coordinates[1] };
        }
        internal RunwayMission Runway(string name, string body, double radius, Func<double, double, double> terrain)
        {
            var row = Find(name, body); if (row == null) return null;
            if (Text(row, "mode", "V") != "H") throw new ArgumentException("Saved point is not a runway.");
            object value; double[] start, end;
            if (row.TryGetValue("a", out value))
            { start = Pair(value); if (!row.TryGetValue("b", out value)) throw new ArgumentException("Runway end missing."); end = Pair(value); }
            else
            {
                start = Pair(new[] { Number(row, "lat", double.NaN), Number(row, "lon", double.NaN) });
                double lat, lon;
                NavigationMath.Offset(start[0], start[1], Number(row, "heading", 0), FlightPolicy.Clamp(Number(row, "length", 1000), 50, 20000), radius, out lat, out lon);
                end = new[] { lat, lon };
            }
            double altitude = Number(row, "alt", double.NaN);
            if (double.IsNaN(altitude)) altitude = terrain(start[0], start[1]);
            if (double.IsNaN(altitude) || double.IsInfinity(altitude)) throw new ArgumentException("Runway terrain height unavailable.");
            return new RunwayMission { Lat = start[0], Lon = start[1], EndLat = end[0], EndLon = end[1], Elevation = altitude };
        }
        internal void Save(string name, string body, string mode, double lat, double lon, double heading, double altitude)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 40 || name.Contains("\n") || name.Contains("\r") || name.Contains("\t")) throw new ArgumentException("Spot name must be 1-40 characters.");
            if (Normalize(name) == "ksc pad" || Normalize(name) == "runway 09" || Normalize(name) == "runway 27" || Normalize(name) == "island airfield" || TaxiMission.Builtin.ContainsKey(name))
                throw new ArgumentException("Choose a name other than a built-in spot.");
            Pair(new[] { lat, lon });
            if (double.IsNaN(altitude) || double.IsInfinity(altitude) || double.IsNaN(heading) || double.IsInfinity(heading)) throw new ArgumentException("Spot elevation/heading unavailable.");
            if (mode != "V" && mode != "H") throw new ArgumentException("Spot mode must be V or H.");
            string key = name;
            foreach (var old in saved.Keys) if (Normalize(old) == Normalize(name)) { key = old; break; }
            if (saved.Count >= 50 && !saved.ContainsKey(key)) throw new ArgumentException("Maximum 50 saved spots.");
            var row = new Dictionary<string, object> { { "mode", mode }, { "body", body }, { "lat", lat }, { "lon", lon }, { "name", name }, { "alt", altitude } };
            if (mode == "H") { row["heading"] = (heading % 360 + 360) % 360; row["length"] = 1000; }
            saved[key] = row;
        }
        internal IEnumerable<TaxiMission.Point> Points(string body)
        {
            foreach (var pair in saved)
            {
                TaxiMission.Point point = null;
                try { point = Point(pair.Key, body); } catch (ArgumentException) { }
                if (point != null) yield return point;
            }
        }

        /// <summary>list_landing_spots: name\tbody\tmode rows for every saved spot (read-only).</summary>
        internal string List()
        {
            var rows = new System.Text.StringBuilder();
            foreach (var pair in saved)
            {
                var row = pair.Value as Dictionary<string, object>;
                if (row == null) continue;
                rows.Append(pair.Key).Append('\t').Append(Text(row, "body", "Kerbin")).Append('\t')
                    .Append(Text(row, "mode", "V")).Append('\n');
            }
            return rows.ToString();
        }
    }
}
