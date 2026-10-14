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
        readonly List<KscRoverWaypoint> scienceTourStops = new List<KscRoverWaypoint>();
        readonly HashSet<string> scienceTourVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<KscRoverPolicy.P> scienceTourRoute = new List<KscRoverPolicy.P>();
        bool scienceTouring, scienceTourDriving;
        int scienceTourRouteIndex;
        double scienceTourSpeed = 10;
        KscRoverPolicy.P scienceTourHub;

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

        string RoverScienceTour(Dictionary<string, object> a)
        {
            if (vessel == null || !vessel.LandedOrSplashed) return "Rover science tours start with a landed rover.";
            if (vessel.mainBody == null || vessel.mainBody.bodyName != "Kerbin") return "The KSC rover science tour is only available on Kerbin.";
            if (!HasPoweredWheels()) return "Rover science tours require powered wheels.";
            if (KscRoverMap.Points.Count == 0) return "KSC map data is not available yet; load the rover at the Space Center and try again.";
            double kscDistance = NavigationMath.Distance(vessel.latitude, vessel.longitude, KscRunway.Lat,
                (KscRunway.Lon09 + KscRunway.Lon27) * .5, vessel.mainBody.Radius);
            if (kscDistance > 12000) return "Drive the rover within 12 km of KSC before starting the science tour.";
            if (!TourHub(out scienceTourHub)) return "No flat Crawlerway or runway waypoint is available for a safe rover route.";

            touring = false;
            scienceTouring = true;
            scienceTourDriving = false;
            scienceTourStops.Clear();
            scienceTourVisited.Clear();
            scienceTourRoute.Clear();
            scienceTourRouteIndex = 0;
            tourWaitUntil = 0;
            scienceTourSpeed = Math.Max(2, Math.Min(10, Num(a, "speed", 10)));
            BuildScienceTourStops();
            if (scienceTourStops.Count == 0)
            {
                scienceTouring = false;
                return "No KSC rover science biomes have eligible experiments with science remaining.";
            }

            var labels = new List<string>();
            foreach (KscRoverWaypoint stop in scienceTourStops) labels.Add(stop.Name);
            ChatLog.Write("rover-tour", "science tour start: " + string.Join(" -> ", labels.ToArray())
                + " | speed cap " + scienceTourSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s");
            return "Rover science tour: " + string.Join(" -> ", labels.ToArray())
                + ". Driving via the Crawlerway at up to " + scienceTourSpeed.ToString("0.0", CultureInfo.InvariantCulture)
                + " m/s; science is rechecked at every stop.";
        }

        bool TourHub(out KscRoverPolicy.P hub)
        {
            foreach (KscRoverWaypoint point in KscRoverMap.Points)
                if (point.Name == "Crawlerway" && IsFlatApproach(point))
                {
                    hub = new KscRoverPolicy.P(point.ApproachLat, point.ApproachLon);
                    return true;
                }
            foreach (KscRoverWaypoint point in KscRoverMap.Points)
                if (point.Name == "Runway" && IsFlatApproach(point))
                {
                    hub = new KscRoverPolicy.P(point.ApproachLat, point.ApproachLon);
                    return true;
                }
            hub = new KscRoverPolicy.P();
            return false;
        }

        bool IsFlatApproach(KscRoverWaypoint point)
        {
            double center = NativeFlightController.MapTerrain(vessel.mainBody, point.ApproachLat, point.ApproachLon);
            if (double.IsNaN(center) || double.IsInfinity(center)) return false;
            foreach (double bearing in new[] { 0d, 90d, 180d, 270d })
            {
                double lat, lon;
                NavigationMath.Offset(point.ApproachLat, point.ApproachLon, bearing, 20, vessel.mainBody.Radius, out lat, out lon);
                double height = NativeFlightController.MapTerrain(vessel.mainBody, lat, lon);
                if (double.IsNaN(height) || double.IsInfinity(height)
                    || Math.Atan2(Math.Abs(height - center), 20) * 180 / Math.PI > 5) return false;
            }
            return true;
        }

        void BuildScienceTourStops()
        {
            scienceTourStops.Clear();
            var bestByBiome = new Dictionary<string, KscRoverWaypoint>(StringComparer.OrdinalIgnoreCase);
            var valueByBiome = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KscRoverWaypoint waypoint in KscRoverMap.Points)
            {
                if (!IsFlatApproach(waypoint)) continue;
                string biome = ScienceUtil.GetExperimentBiome(vessel.mainBody, waypoint.ApproachLat, waypoint.ApproachLon);
                if (string.IsNullOrEmpty(biome) || scienceTourVisited.Contains(biome)) continue;
                int count; double value = ScienceValueAt(waypoint.ApproachLat, waypoint.ApproachLon, out count);
                if (count == 0 || value <= SciencePolicy.MinSubjectValue) continue;
                double oldValue;
                if (!valueByBiome.TryGetValue(biome, out oldValue) || value > oldValue)
                {
                    bestByBiome[biome] = waypoint;
                    valueByBiome[biome] = value;
                }
            }

            var candidates = new List<KscRoverWaypoint>(bestByBiome.Values);
            var coordinates = new List<KscRoverPolicy.P>();
            foreach (KscRoverWaypoint stop in candidates)
                coordinates.Add(new KscRoverPolicy.P(stop.ApproachLat, stop.ApproachLon));
            List<int> order = KscRoverPolicy.ShortestOrder(new KscRoverPolicy.P(vessel.latitude, vessel.longitude),
                coordinates, scienceTourHub, vessel.mainBody.Radius);
            foreach (int index in order) scienceTourStops.Add(candidates[index]);
        }

        string StartScienceTourLeg()
        {
            if (scienceTourStops.Count == 0) return "No eligible science stops remain.";
            KscRoverWaypoint stop = scienceTourStops[0];
            if (scienceTourRoute.Count == 0)
            {
                scienceTourRoute.AddRange(KscRoverPolicy.GroundRoute(
                    new KscRoverPolicy.P(vessel.latitude, vessel.longitude),
                    new KscRoverPolicy.P(stop.ApproachLat, stop.ApproachLon),
                    scienceTourHub, vessel.mainBody.Radius));
                scienceTourRouteIndex = 0;
            }
            if (scienceTourRouteIndex >= scienceTourRoute.Count) return "Rover tour route is empty.";
            KscRoverPolicy.P target = scienceTourRoute[scienceTourRouteIndex];
            string speedResult = Command("drive_speed", Args("speed", scienceTourSpeed));
            if (mode != "rover") return speedResult;
            string result = Command("drive_to", Args("name",
                target.Lat.ToString("0.000000", CultureInfo.InvariantCulture) + ","
                + target.Lon.ToString("0.000000", CultureInfo.InvariantCulture)));
            scienceTourDriving = mode == "rover" && roverNavigating;
            return result;
        }

        void ScienceTourTick()
        {
            if (!scienceTouring || Time.realtimeSinceStartup < tourWaitUntil) return;
            if (!scienceTourDriving)
            {
                BuildScienceTourStops();
                if (scienceTourStops.Count == 0)
                {
                    scienceTouring = false;
                    ChatLog.Write("rover-tour", "science tour complete");
                    ChatWindow.Notice(Voice().Key + ": Rover science tour complete.");
                    return;
                }
                string result = StartScienceTourLeg();
                if (!scienceTourDriving)
                {
                    scienceTouring = false;
                    ChatLog.Write("rover-tour", "science tour stopped: " + result);
                    ChatWindow.Notice("Rover science tour stopped: " + result);
                }
                return;
            }
            if (mode == "rover" && roverNavigating) return;

            KscRoverPolicy.P expected = scienceTourRoute[scienceTourRouteIndex];
            double distance = NavigationMath.Distance(vessel.latitude, vessel.longitude, expected.Lat, expected.Lon, vessel.mainBody.Radius);
            if (distance > KscRoverPolicy.Arrived + 20)
            {
                scienceTouring = false;
                ChatLog.Write("rover-tour", "science tour stopped short of route waypoint (" + distance.ToString("0", CultureInfo.InvariantCulture) + " m)");
                ChatWindow.Notice("Rover science tour stopped short of its route waypoint (" + distance.ToString("0") + " m).");
                return;
            }

            scienceTourDriving = false;
            scienceTourRouteIndex++;
            if (scienceTourRouteIndex < scienceTourRoute.Count)
            {
                string result = StartScienceTourLeg();
                if (!scienceTourDriving)
                {
                    scienceTouring = false;
                    ChatLog.Write("rover-tour", "science tour stopped: " + result);
                    ChatWindow.Notice("Rover science tour stopped: " + result);
                }
                return;
            }

            KscRoverWaypoint stop = scienceTourStops[0];
            scienceTourStops.RemoveAt(0);
            scienceTourVisited.Add(ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude));
            scienceTourRoute.Clear();
            scienceTourRouteIndex = 0;
            SetGroup(vessel, KSPActionGroup.Brakes, true);
            int experimentCount; double scienceValue = ScienceValueAt(vessel.latitude, vessel.longitude, out experimentCount);
            string blocker; List<string> ran = RunExperiments(false, out blocker);
            if (ran.Count > 0) sciTransmitAt = Time.realtimeSinceStartup + 2;
            string log = "stop " + stop.Name + " / " + ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude)
                + " | " + (blocker == null ? ran.Count.ToString(CultureInfo.InvariantCulture) + " experiment(s), "
                    + (ran.Count == 0 ? 0 : scienceValue).ToString("0.0", CultureInfo.InvariantCulture) + " estimated science value" : "science blocked: " + blocker)
                + " | " + (ran.Count == 0 ? "none run" : string.Join(", ", ran.ToArray()));
            ChatLog.Write("rover-tour", log);
            ChatWindow.Notice(Voice().Key + ": At " + stop.Name + ". "
                + (blocker != null ? "Science blocked: " + blocker + "."
                    : ran.Count > 0 ? "Ran " + string.Join(", ", ran.ToArray()) + "; about "
                        + scienceValue.ToString("0.0", CultureInfo.InvariantCulture) + " science value."
                    : "Nothing new to run here."));
            tourWaitUntil = Time.realtimeSinceStartup + 6;
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
            if (scienceTouring) { ScienceTourTick(); return; }
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
