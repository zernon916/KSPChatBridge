using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    internal sealed class KscRoverWaypoint
    {
        internal string Name;
        internal string Biome;
        internal double Lat, Lon;
        internal double ApproachLat, ApproachLon;
        internal string Source;
    }

    internal static class KscRoverMap
    {
        sealed class Site
        {
            internal readonly string Facility, Name, Biome;
            internal readonly double Lat, Lon;
            internal Site(string facility, string name, string biome, double lat, double lon)
            { Facility = facility; Name = name; Biome = biome; Lat = lat; Lon = lon; }
        }

        sealed class Building
        {
            internal string Name;
            internal double Lat, Lon;
        }

        static readonly Site[] Facilities =
        {
            new Site("LaunchPad", "Launch Pad", "LaunchPad", -.0972, -74.5577),
            new Site("Runway", "Runway", "Runway", KscRunway.LatAt((KscRunway.Lon09 + KscRunway.Lon27) * .5), (KscRunway.Lon09 + KscRunway.Lon27) * .5),
            new Site("VehicleAssemblyBuilding", "VAB", "VehicleAssemblyBuilding", -.0958, -74.6170),
            new Site("SpaceplaneHangar", "SPH", "SpaceplaneHangar", -.0520, -74.6750),
            new Site("ResearchAndDevelopment", "R&D", "ResearchAndDevelopment", -.0870, -74.6420),
            new Site("TrackingStation", "Tracking Station", "TrackingStation", -.0380, -74.6330),
            new Site("AstronautComplex", "Astronaut Complex", "AstronautComplex", -.0670, -74.6140),
            new Site("MissionControl", "Mission Control", "MissionControl", -.0830, -74.6120),
            new Site("Administration", "Admin", "Administration", -.0560, -74.6200)
        };

        static readonly List<KscRoverWaypoint> points = new List<KscRoverWaypoint>();
        internal static IList<KscRoverWaypoint> Points { get { return points.AsReadOnly(); } }
        /// <summary>RS3b: KSC hub (facility centroid) of the last Refresh; Version bumps on every Refresh.</summary>
        internal static double HubLat = double.NaN, HubLon = double.NaN; internal static int Version;

        internal static bool Refresh(CelestialBody body)
        {
            if (body == null || body.bodyName != "Kerbin") return false;
            var buildings = new Dictionary<string, List<Building>>(StringComparer.OrdinalIgnoreCase);
            foreach (DestructibleBuilding part in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
            {
                string facility = KscRoverPolicy.Facility(part.id);
                if (facility == null) continue;
                List<Building> list;
                if (!buildings.TryGetValue(facility, out list)) buildings[facility] = list = new List<Building>();
                Vector3d position = part.transform.position;
                list.Add(new Building
                {
                    Name = string.IsNullOrWhiteSpace(part.name) ? facility : part.name.Trim(),
                    Lat = body.GetLatitude(position),
                    Lon = WrapLongitude(body.GetLongitude(position))
                });
            }
            var obstacles = new List<Building>();
            foreach (var group in buildings.Values) obstacles.AddRange(group);

            points.Clear();
            double hubLat = 0, hubLon = 0; int hubCount = 0;
            foreach (Site site in Facilities)
            {
                List<Building> list;
                if (buildings.TryGetValue(site.Facility, out list))
                {
                    foreach (Building building in list) { hubLat += building.Lat; hubLon += building.Lon; hubCount++; }
                }
            }
            if (hubCount == 0) { hubLon = (KscRunway.Lon09 + KscRunway.Lon27) * .5; hubLat = KscRunway.LatAt(hubLon); }
            else { hubLat /= hubCount; hubLon /= hubCount; }
            HubLat = hubLat; HubLon = hubLon; Version++;   // RS3b: road network anchor

            var facilityPoints = new Dictionary<string, KscRoverWaypoint>(StringComparer.OrdinalIgnoreCase);
            foreach (Site site in Facilities)
            {
                List<Building> list;
                double lat, lon; string source;
                if (buildings.TryGetValue(site.Facility, out list) && list.Count > 0)
                {
                    lat = 0; lon = 0;
                    foreach (Building building in list) { lat += building.Lat; lon += building.Lon; }
                    lat /= list.Count; lon /= list.Count; source = "SpaceCenter transform";
                }
                else { lat = site.Lat; lon = site.Lon; source = "fallback coordinate"; }

                KscRoverWaypoint waypoint = Add(site.Name, site.Biome, lat, lon,
                    ApproachBearing(site.Name, lat, lon, hubLat, hubLon), ApproachDistance(site.Name), body.Radius, source, obstacles);
                facilityPoints[site.Facility] = waypoint;
            }

            List<Building> researchBuildings;
            if (buildings.TryGetValue("ResearchAndDevelopment", out researchBuildings))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int n = 0;
                foreach (Building building in researchBuildings)
                {
                    string label = "R&D - " + building.Name;
                    if (!names.Add(label)) label += " " + (++n).ToString(CultureInfo.InvariantCulture);
                    Add(label, "ResearchAndDevelopment", building.Lat, building.Lon,
                        NavigationMath.Bearing(building.Lat, building.Lon, hubLat, hubLon), 50, body.Radius, "SpaceCenter sub-building transform", obstacles);
                }
            }

            KscRoverWaypoint pad = facilityPoints["LaunchPad"], vab = facilityPoints["VehicleAssemblyBuilding"];
            double crawlerLat, crawlerLon;
            double between = NavigationMath.Bearing(vab.Lat, vab.Lon, pad.Lat, pad.Lon);
            NavigationMath.Offset(vab.Lat, vab.Lon, between, NavigationMath.Distance(vab.Lat, vab.Lon, pad.Lat, pad.Lon, body.Radius) * .5,
                body.Radius, out crawlerLat, out crawlerLon);
            Add("Crawlerway", "Crawlerway", crawlerLat, crawlerLon, between + 90, 20, body.Radius, "derived from facility transforms", obstacles);

            double grassLat, grassLon, shoresLat, shoresLon;
            double kscLon = (KscRunway.Lon09 + KscRunway.Lon27) * .5, kscLat = KscRunway.LatAt(kscLon);
            NavigationMath.Offset(kscLat, kscLon, 270, 300, body.Radius, out grassLat, out grassLon);
            NavigationMath.Offset(kscLat, kscLon, 180, 1000, body.Radius, out shoresLat, out shoresLon);
            Add("KSC Grass", "KSC", grassLat, grassLon, 0, 40, body.Radius, "fallback offset from KSC center", obstacles);
            Add("Shores", "Shores", shoresLat, shoresLon, 0, 75, body.Radius, "fallback offset beyond KSC", obstacles);
            return true;
        }

        internal static void Log()
        {
            ChatLog.Write("rover-map", "KSC safe rover waypoints: " + points.Count.ToString(CultureInfo.InvariantCulture));
            foreach (KscRoverWaypoint point in points)
            {
                ChatLog.Write("rover-map", point.Name + " | biome " + point.Biome + " | "
                    + point.Lat.ToString("0.000000", CultureInfo.InvariantCulture) + ","
                    + point.Lon.ToString("0.000000", CultureInfo.InvariantCulture) + " | approach "
                    + point.ApproachLat.ToString("0.000000", CultureInfo.InvariantCulture) + ","
                    + point.ApproachLon.ToString("0.000000", CultureInfo.InvariantCulture) + " | " + point.Source);
            }
        }

        static KscRoverWaypoint Add(string name, string biome, double lat, double lon, double bearing, double distance, double radius, string source, IList<Building> obstacles)
        {
            double approachLat = lat, approachLon = lon;
            for (double offset = distance; offset <= distance + 300; offset += 25)
            {
                NavigationMath.Offset(lat, lon, bearing, offset, radius, out approachLat, out approachLon);
                if (Clear(approachLat, approachLon, radius, obstacles)) break;
            }
            var point = new KscRoverWaypoint
            {
                Name = name, Biome = biome, Lat = lat, Lon = lon,
                ApproachLat = approachLat, ApproachLon = approachLon, Source = source
            };
            points.Add(point);
            return point;
        }

        static bool Clear(double lat, double lon, double radius, IList<Building> obstacles)
        {
            foreach (Building obstacle in obstacles)
                if (NavigationMath.Distance(lat, lon, obstacle.Lat, obstacle.Lon, radius) < 35) return false;
            return true;
        }

        static double ApproachBearing(string name, double lat, double lon, double hubLat, double hubLon)
        {
            if (name == "Runway")
                return NavigationMath.Bearing(KscRunway.Lat09, KscRunway.Lon09, KscRunway.Lat27, KscRunway.Lon27) + 90;
            return NavigationMath.Distance(lat, lon, hubLat, hubLon, 600000) < 1 ? 0 : NavigationMath.Bearing(lat, lon, hubLat, hubLon);
        }

        static double ApproachDistance(string name)
        {
            if (name == "Launch Pad") return 120;
            if (name == "Runway") return 80;
            return 50;
        }

        static double WrapLongitude(double longitude) { return (longitude % 360 + 540) % 360 - 180; }
    }

    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class KscRoverMapLoader : MonoBehaviour
    {
        void Start() { Invoke("LoadMap", 1); }

        void LoadMap()
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null && Planetarium.fetch != null) body = Planetarium.fetch.Home;
            if (body == null) { Invoke("LoadMap", 2); return; }
            if (body.bodyName != "Kerbin") return;
            if (KscRoverMap.Refresh(body)) KscRoverMap.Log();
        }
    }
}
