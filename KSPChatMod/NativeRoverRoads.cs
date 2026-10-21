// RS3b: keep the rover on KSC roads. RoadNet (pure) gives the polyline; the rover loop steers to a pure-pursuit point on it
// (TaxiRoute.Target: cross-track correction back to the centreline) and slows before corners. Only the last leg to an off-road site
// leaves the road. PluginData/roads/ROADS_KSC.json overrides the generated network (ROADS_KSC_generated.json is written for editing;
// RS7b: not in charts/ any more - the approach chart loader picked it up as a chart "ROADS KSC").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        static RoadNet roadNet; static int roadNetVersion = -1; static bool roadSceneLogged;
        TaxiRoute roverRoad; int roverRoadLeg, roverRoadOffLeg = -1; double roverRoadEndLat = double.NaN, roverRoadEndLon, roverRoadCap = double.MaxValue;
        const double RoadMaxFromHubM = 4000;

        static string RoadsDir { get { return Path.Combine(AicsCore.PluginDataDirectory, "roads"); } }
        double roverRoadLeft = double.NaN;
        bool RoverRoadActive { get { return roverRoad != null && roverNavigating && Math.Abs(roverDestinationLat - roverRoadEndLat) <= 1e-7 && Math.Abs(roverDestinationLon - roverRoadEndLon) <= 1e-7; } }

        /// <summary>RS7b: after a stuck / circling escape - re-snap to the nearest road from here and plan again.</summary>
        void RoverReplanRoad()
        {
            if (!RoverRoadActive) return;
            double lat = roverRoadEndLat, lon = roverRoadEndLon; string note = RoverRoadAttach(lat, lon);
            ChatLog.Write("rover", "re-planned the route from here" + (note.Length > 0 ? note : " (straight)"));
        }

        /// <summary>KSC road network for this body (null away from Kerbin / before the KSC map is known).</summary>
        static RoadNet KscRoads(CelestialBody body)
        {
            if (body == null || body.bodyName != "Kerbin") return null;
            if (KscRoverMap.Points.Count == 0) KscRoverMap.Refresh(body);
            if (double.IsNaN(KscRoverMap.HubLat)) return null;
            if (roadNet != null && roadNetVersion == KscRoverMap.Version) return roadNet;
            roadNetVersion = KscRoverMap.Version; roadNet = null;
            try
            {
                string custom = Path.Combine(RoadsDir, "ROADS_KSC.json");
                if (File.Exists(custom)) { roadNet = RoadNet.FromJson(File.ReadAllText(custom), body.Radius); if (roadNet != null) ChatLog.Write("roads", "using " + custom + " (" + roadNet.Nodes.Count + " nodes, " + roadNet.Edges.Count + " edges)"); }
            }
            catch (Exception ex) { ChatLog.Write("roads", "ROADS_KSC.json unreadable: " + ex.Message); }
            if (roadNet == null)
            {
                var fronts = new List<KeyValuePair<string, KscRoverPolicy.P>>();
                var buildings = new List<KeyValuePair<KscRoverPolicy.P, double>>();   // RS7d: footprints the hub spokes must bend around
                foreach (var w in KscRoverMap.Points)
                {
                    fronts.Add(new KeyValuePair<string, KscRoverPolicy.P>(w.Name, new KscRoverPolicy.P(w.ApproachLat, w.ApproachLon)));
                    if (w.Name == "Runway" || w.Name == "KSC Grass" || w.Name == "Shores" || w.Name == "Crawlerway" || w.Name == "Launch Pad" || w.Source == "place") continue;
                    buildings.Add(new KeyValuePair<KscRoverPolicy.P, double>(new KscRoverPolicy.P(w.Lat, w.Lon), RoadNet.FootprintRadius(NavigationMath.Distance(w.Lat, w.Lon, w.ApproachLat, w.ApproachLon, body.Radius))));
                }
                roadNet = RoadNet.Ksc(new KscRoverPolicy.P(KscRoverMap.HubLat, KscRoverMap.HubLon), fronts, body.Radius, buildings);
                try { Directory.CreateDirectory(RoadsDir); File.WriteAllText(Path.Combine(RoadsDir, "ROADS_KSC_generated.json"), roadNet.ToJson()); } catch (Exception) { }
                ChatLog.Write("roads", "generated KSC road network: " + roadNet.Nodes.Count + " nodes, " + roadNet.Edges.Count + " edges (copy ROADS_KSC_generated.json to ROADS_KSC.json to edit)");
            }
            if (!roadSceneLogged) { roadSceneLogged = true; LogRoadObjects(body); }
            return roadNet;
        }

        /// <summary>One-time log of scene objects that look like roads, for tuning ROADS_KSC.json later.</summary>
        static void LogRoadObjects(CelestialBody body)
        {
            try
            {
                int n = 0;
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                {
                    string nm = t.name.ToLowerInvariant();
                    if (!(nm.Contains("road") || nm.Contains("crawler") || nm.Contains("taxiway") || nm.Contains("path"))) continue;
                    if (++n > 40) break;
                    double lat = body.GetLatitude(t.position), lon = body.GetLongitude(t.position);
                    ChatLog.Write("roads", "scene object '" + t.name + "' at " + lat.ToString("0.000000", CultureInfo.InvariantCulture) + "," + lon.ToString("0.000000", CultureInfo.InvariantCulture));
                }
                if (n == 0) ChatLog.Write("roads", "no road-named scene objects found");
            }
            catch (Exception ex) { ChatLog.Write("roads", "scene scan failed: " + ex.Message); }
        }

        /// <summary>Road polyline from here to (lat, lon) when both ends are around KSC, else null (drive straight).</summary>
        List<KscRoverPolicy.P> RoverRoadRoute(double lat, double lon)
        {
            if (vessel == null) return null;
            var net = KscRoads(vessel.mainBody); if (net == null) return null;
            var hub = new KscRoverPolicy.P(KscRoverMap.HubLat, KscRoverMap.HubLon); double r = vessel.mainBody.Radius;
            if (NavigationMath.Distance(vessel.latitude, vessel.longitude, hub.Lat, hub.Lon, r) > RoadMaxFromHubM || NavigationMath.Distance(lat, lon, hub.Lat, hub.Lon, r) > RoadMaxFromHubM) return null;
            return net.Route(new KscRoverPolicy.P(vessel.latitude, vessel.longitude), new KscRoverPolicy.P(lat, lon));
        }

        /// <summary>Driving distance here -> (lat, lon): along the roads at KSC, straight line elsewhere (RS7 site distances).</summary>
        internal double RoverDriveDistance(double fromLat, double fromLon, double lat, double lon)
        {
            double r = vessel != null ? vessel.mainBody.Radius : 600000;
            var net = vessel != null ? KscRoads(vessel.mainBody) : null;
            if (net == null || double.IsNaN(KscRoverMap.HubLat) || NavigationMath.Distance(lat, lon, KscRoverMap.HubLat, KscRoverMap.HubLon, r) > RoadMaxFromHubM || NavigationMath.Distance(fromLat, fromLon, KscRoverMap.HubLat, KscRoverMap.HubLon, r) > RoadMaxFromHubM)
                return NavigationMath.Distance(fromLat, fromLon, lat, lon, r);
            return net.DriveDistance(new KscRoverPolicy.P(fromLat, fromLon), new KscRoverPolicy.P(lat, lon));
        }

        /// <summary>drive_to hook: attach the road polyline for this destination (or clear it). Returns a note for the reply.</summary>
        string RoverRoadAttach(double lat, double lon)
        {
            roverRoad = null; roverRoadCap = double.MaxValue; roverRoadOffLeg = -1;
            var pts = RoverRoadRoute(lat, lon); if (pts == null || pts.Count < 2) return "";
            int trimmed = RoverTurn.TrimBehind(pts, vessel.latitude, vessel.longitude, FlightGlobals.ship_heading, vessel.mainBody.Radius);   // RS7l
            if (trimmed > 0) ChatLog.Write("rover", "road: skipped " + trimmed + " route point(s) just behind the rover");
            if (pts.Count < 2) { roverRoad = null; return ""; }
            {   // RS7o: short hop the roads make long -> straight, if clear of every building keep-out
                var here = new KscRoverPolicy.P(vessel.latitude, vessel.longitude); double roadM = roadNet.Length(here, pts), r = vessel.mainBody.Radius;
                if (RoverShortcut.Take(here, new KscRoverPolicy.P(lat, lon), roadM, KscRoverMap.KeepOuts, r))
                {
                    double s = NavigationMath.Distance(here.Lat, here.Lon, lat, lon, r);
                    ChatLog.Write("rover", "road: straight " + s.ToString("0", CultureInfo.InvariantCulture) + " m instead of " + roadM.ToString("0", CultureInfo.InvariantCulture) + " m by road (clear of buildings)");
                    roverRoad = null; return " Driving straight (" + s.ToString("0", CultureInfo.InvariantCulture) + " m).";
                }
            }
            var route = new TaxiRoute { Name = "road" };
            route.Points.Add(new TaxiRoute.Wp { Name = "start", Lat = vessel.latitude, Lon = vessel.longitude });
            for (int i = 0; i < pts.Count; i++) route.Points.Add(new TaxiRoute.Wp { Name = "road " + i, Lat = pts[i].Lat, Lon = pts[i].Lon });
            roverRoad = route; roverRoadLeg = 0; roverRoadEndLat = lat; roverRoadEndLon = lon;
            double len = roadNet.Length(new KscRoverPolicy.P(vessel.latitude, vessel.longitude), pts);
            ChatLog.Write("rover", "road route " + pts.Count + " points, " + len.ToString("0", CultureInfo.InvariantCulture) + " m");
            return " Following KSC roads (" + (len / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km).";
        }

        /// <summary>Rover loop: steer along the road polyline. remaining = metres left along it.</summary>
        bool RoverRoadSteer(out double remaining)
        {
            remaining = double.NaN; roverRoadCap = double.MaxValue; roverRoadLeft = double.NaN;
            if (!RoverRoadActive) return false;
            double r = vessel.mainBody.Radius, tLat, tLon, cross, toEnd;
            roverRoad.Target(ref roverRoadLeg, vessel.latitude, vessel.longitude, r, out tLat, out tLon, out cross, out toEnd);
            if (double.IsNaN(tLat) || double.IsNaN(tLon)) return false;
            roverHeading = NavigationMath.Bearing(vessel.latitude, vessel.longitude, tLat, tLon);
            remaining = toEnd; roverRoadLeft = toEnd;
            if (Math.Abs(cross) > 8 && roverRoadOffLeg != roverRoadLeg) { roverRoadOffLeg = roverRoadLeg; ChatLog.Write("rover", "road: " + Math.Abs(cross).ToString("0", CultureInfo.InvariantCulture) + " m off the centreline on leg " + roverRoadLeg + ", steering back"); }
            // corner ahead: the turn at the end of this leg
            var p = roverRoad.Points; int i = roverRoadLeg;
            if (i + 2 < p.Count)
            {
                double h1 = NavigationMath.Bearing(p[i].Lat, p[i].Lon, p[i + 1].Lat, p[i + 1].Lon), h2 = NavigationMath.Bearing(p[i + 1].Lat, p[i + 1].Lon, p[i + 2].Lat, p[i + 2].Lon);
                double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, p[i + 1].Lat, p[i + 1].Lon, r);
                roverRoadCap = RoadNet.CornerSpeed(double.MaxValue, FlightPolicy.Wrap(h2 - h1), d);
            }
            return true;
        }
    }
}