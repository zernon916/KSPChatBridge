// RS5: named places (airports, sites, buildings) - PluginData/places.json. Manual places (pinned / edited on the tablet) are the file;
// stock landing_spots (NativeSpots, read-only) and Kerbal Konstructs launch sites / statics (reflection, NativePlaces.cs) are merged in
// at runtime, manual wins on a name clash. Places marked as stops join the rover science tour. Pure, tested.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal sealed class Place
    {
        internal string Name = "", Kind = "site", Body = "Kerbin", Notes = "", Source = "manual";
        internal double Lat, Lon, Alt = double.NaN, Heading = double.NaN;
        internal bool Stop;
        internal Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object> { { "name", Name }, { "kind", Kind }, { "body", Body }, { "lat", Lat }, { "lon", Lon }, { "stop", Stop }, { "source", Source } };
            if (!double.IsNaN(Alt)) d["alt"] = Alt; if (!double.IsNaN(Heading)) d["heading"] = Heading; if (!string.IsNullOrEmpty(Notes)) d["notes"] = Notes;
            return d;
        }
        internal static Place FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null; var p = new Place();
            p.Name = S(d, "name", ""); if (p.Name.Trim().Length == 0) return null;
            p.Kind = PlacesStore.NormalizeKind(S(d, "kind", "site")); p.Body = S(d, "body", "Kerbin"); p.Notes = S(d, "notes", ""); p.Source = S(d, "source", "manual");
            p.Lat = N(d, "lat"); p.Lon = N(d, "lon"); p.Alt = N(d, "alt"); p.Heading = N(d, "heading");
            object o; p.Stop = d.TryGetValue("stop", out o) && o is bool && (bool)o;
            return double.IsNaN(p.Lat) || double.IsNaN(p.Lon) ? null : p;
        }
        static string S(Dictionary<string, object> d, string k, string def) { object o; return d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : def; }
        static double N(Dictionary<string, object> d, string k)
        {
            object o; if (!d.TryGetValue(k, out o) || o == null) return double.NaN;
            try { double v = Convert.ToDouble(o, CultureInfo.InvariantCulture); return double.IsInfinity(v) ? double.NaN : v; } catch (Exception) { return double.NaN; }
        }
        internal string Line()
        {
            var ci = CultureInfo.InvariantCulture;
            return Name + " [" + Kind + ", " + Source + "] " + Body + " " + Lat.ToString("0.0000", ci) + ", " + Lon.ToString("0.0000", ci) + (Stop ? " - TOUR STOP" : "");
        }
    }

    internal sealed class PlacesStore
    {
        internal static readonly string[] Kinds = { "airport", "site", "building" };
        internal readonly List<Place> Manual = new List<Place>();

        internal static string NormalizeKind(string k) { k = (k ?? "").Trim().ToLowerInvariant(); return Array.IndexOf(Kinds, k) >= 0 ? k : "site"; }
        static string Key(string name) { return (name ?? "").Trim().Replace('_', ' ').ToLowerInvariant(); }

        internal string ToJson()
        {
            var list = new List<object>(); foreach (var p in Manual) list.Add(p.ToDict());
            return MiniJson.Serialize(new Dictionary<string, object> { { "version", 1 }, { "places", list } });
        }
        internal static PlacesStore FromJson(string json)
        {
            var s = new PlacesStore(); if (string.IsNullOrEmpty(json)) return s;
            Dictionary<string, object> root; try { root = MiniJson.Deserialize(json); } catch (Exception) { return s; }
            object o; var list = root != null && root.TryGetValue("places", out o) ? o as System.Collections.IEnumerable : null;
            if (list != null) foreach (var it in list) { var p = Place.FromDict(it as Dictionary<string, object>); if (p != null && s.Find(p.Name) == null) s.Manual.Add(p); }
            return s;
        }

        internal Place Find(string name) { string k = Key(name); return Manual.Find(p => Key(p.Name) == k); }

        /// <summary>Pin a place; a blank or taken name gets "Pin N".</summary>
        internal Place Add(string name, string kind, string body, double lat, double lon, double alt, double heading)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0 || Find(n) != null) { int i = 1; string b = n.Length == 0 ? "Pin" : n; do n = b + " " + i++; while (Find(n) != null); }
            var p = new Place { Name = n, Kind = NormalizeKind(kind), Body = body ?? "Kerbin", Lat = lat, Lon = lon, Alt = alt, Heading = heading };
            Manual.Add(p); return p;
        }
        internal bool Rename(string from, string to)
        {
            var p = Find(from); string t = (to ?? "").Trim(); if (p == null || t.Length == 0) return false;
            var clash = Find(t); if (clash != null && clash != p) return false; p.Name = t; return true;
        }
        internal bool Delete(string name) { var p = Find(name); return p != null && Manual.Remove(p); }

        /// <summary>Manual places + imported ones (KK, landing_spots); manual wins on a name clash, the first import wins between imports.</summary>
        internal List<Place> Merged(IEnumerable<Place> imported)
        {
            var r = new List<Place>(Manual); var seen = new HashSet<string>(); foreach (var p in Manual) seen.Add(Key(p.Name));
            if (imported != null) foreach (var p in imported) if (p != null && seen.Add(Key(p.Name))) r.Add(p);
            return r;
        }

        /// <summary>Tour stops on this body (marked places only).</summary>
        internal static List<Place> Stops(IEnumerable<Place> places, string body)
        {
            var r = new List<Place>(); if (places == null) return r;
            foreach (var p in places) if (p.Stop && string.Equals(p.Body, body, StringComparison.OrdinalIgnoreCase)) r.Add(p);
            return r;
        }

        /// <summary>Stock landing_spots rows (name -> {body, lat, lon, alt, heading}) as read-only places.</summary>
        internal static List<Place> FromLandingSpots(Dictionary<string, object> spots)
        {
            var r = new List<Place>(); if (spots == null) return r;
            foreach (var kv in spots)
            {
                var row = kv.Value as Dictionary<string, object>; if (row == null) continue;
                var d = new Dictionary<string, object>(row); d["name"] = kv.Key; d["kind"] = "airport"; d["source"] = "landing_spots"; if (!d.ContainsKey("body")) d["body"] = "Kerbin";
                var p = Place.FromDict(d); if (p != null) r.Add(p);
            }
            return r;
        }

        /// <summary>KK launch-site category -> place kind.</summary>
        internal static string KindForKk(string category)
        {
            string c = (category ?? "").ToLowerInvariant();
            return c.Contains("runway") || c.Contains("airport") ? "airport" : "site";
        }
    }
}