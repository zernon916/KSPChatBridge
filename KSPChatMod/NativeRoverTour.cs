using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    // Rover drive-to-building: KSC building positions read in-game, road-ish waypoints via the KSC hub, science at each stop.
    public partial class NativeFlightController
    {
        readonly List<KeyValuePair<string, KscRoverPolicy.P>> tour = new List<KeyValuePair<string, KscRoverPolicy.P>>();
        bool touring, tourScience, tourDriving; double tourSpeed = 8; float tourWaitUntil; KscRoverPolicy.P tourHub;

        Dictionary<string, KscRoverPolicy.P> ReadBuildings()
        {
            var sum = new Dictionary<string, double[]>();
            foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
            {
                string f = KscRoverPolicy.Facility(b.id); if (f == null) continue;
                Vector3d pos = b.transform.position; double[] s;
                if (!sum.TryGetValue(f, out s)) sum[f] = s = new double[3];
                s[0] += vessel.mainBody.GetLatitude(pos); s[1] += vessel.mainBody.GetLongitude(pos); s[2]++;
            }
            var res = new Dictionary<string, KscRoverPolicy.P>();
            foreach (var kv in sum) res[kv.Key] = new KscRoverPolicy.P(kv.Value[0] / kv.Value[2], kv.Value[1] / kv.Value[2]);
            return res;
        }

        string DriveToBuilding(Dictionary<string, object> a)
        {
            if (!vessel.LandedOrSplashed) return "Rover tours start on the ground.";
            if (vessel.mainBody.bodyName != "Kerbin") return "KSC buildings are on Kerbin.";
            var all = ReadBuildings();
            if (all.Count == 0) return "Can't see any KSC buildings from here (drive within a few km of KSC).";
            string want = Str(a, "buildings", Str(a, "name", "all"));
            tour.Clear();
            double hlat = 0, hlon = 0; foreach (var p in all.Values) { hlat += p.Lat; hlon += p.Lon; }
            tourHub = new KscRoverPolicy.P(hlat / all.Count, hlon / all.Count);
            var picked = new List<string>();
            if (want.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) picked.AddRange(all.Keys);
            else foreach (string w in want.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string f = KscRoverPolicy.Match(w);
                if (f == null || !all.ContainsKey(f)) return "Unknown or unseen building: " + w.Trim() + ". Try VAB, SPH, R&D, Tracking Station, Mission Control, Astronaut Complex, Administration, Launch Pad, Runway or all.";
                if (!picked.Contains(f)) picked.Add(f);
            }
            var stops = new List<KscRoverPolicy.P>(); foreach (string f in picked) stops.Add(all[f]);
            var here = new KscRoverPolicy.P(vessel.latitude, vessel.longitude);
            foreach (int i in (want.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? KscRoverPolicy.Order(here, stops, vessel.mainBody.Radius) : Range(stops.Count)))
                tour.Add(new KeyValuePair<string, KscRoverPolicy.P>(picked[i], stops[i]));
            tourScience = Bool(a, "science", true); tourSpeed = Math.Max(2, Math.Min(20, Num(a, "speed", 8))); touring = true; tourDriving = false; tourWaitUntil = 0;
            var names = new List<string>(); foreach (var t in tour) names.Add(KscRoverPolicy.Display(t.Key));
            return "Rover tour: " + string.Join(" -> ", names.ToArray()) + (tourScience ? ", science at each stop." : ".") + " Straight lines via the KSC hub; watch for obstacles.";
        }
        static IEnumerable<int> Range(int n) { for (int i = 0; i < n; i++) yield return i; }

        string NextLeg()
        {
            var to = tour[0].Value;
            var pts = KscRoverPolicy.Route(new KscRoverPolicy.P(vessel.latitude, vessel.longitude), to, tourHub, vessel.mainBody.Radius);
            var parts = new List<string>(); foreach (var p in pts) parts.Add(p.Lat.ToString("0.000000", CultureInfo.InvariantCulture) + "," + p.Lon.ToString("0.000000", CultureInfo.InvariantCulture));
            tourDriving = true;
            return Command("taxi_to", Args("name", string.Join(";", parts.ToArray()), "speed", tourSpeed));
        }

        partial void RoverTourTick()
        {
            if (!touring || Time.realtimeSinceStartup < tourWaitUntil) return;
            if (tour.Count == 0) { touring = false; ChatWindow.Notice(Voice().Key + ": Tour done, Captain."); return; }
            if (!tourDriving) { string r = NextLeg(); if (mode != "taxi") { touring = false; ChatWindow.Notice("Rover tour stopped: " + r); } return; }
            if (mode == "taxi") return;
            var stop = tour[0]; tourDriving = false;
            double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, stop.Value.Lat, stop.Value.Lon, vessel.mainBody.Radius);
            if (d > KscRoverPolicy.Approach + KscRoverPolicy.Arrived) { touring = false; ChatWindow.Notice("Rover tour stopped short of " + KscRoverPolicy.Display(stop.Key) + " (" + d.ToString("0") + " m)."); return; }
            tour.RemoveAt(0); SetGroup(vessel, KSPActionGroup.Brakes, true);
            string sci = "";
            if (tourScience)
            {
                string blocker; var ran = RunExperiments(false, out blocker);
                sci = blocker != null ? " No science: " + blocker + "." : ran.Count > 0 ? " Science: " + string.Join(", ", ran.ToArray()) + "." : " Nothing new to run here.";
                if (ran.Count > 0) sciTransmitAt = Time.realtimeSinceStartup + 2;
            }
            ChatWindow.Notice(Voice().Key + ": At the " + KscRoverPolicy.Display(stop.Key) + "." + sci);
            tourWaitUntil = Time.realtimeSinceStartup + 6;
        }
    }
}
