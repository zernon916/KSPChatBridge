using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Rover drive-to-building at KSC: facility names, road-ish routes and tour order (pure).
    /// Building positions are read in-game (DestructibleBuilding); roads are approximated through the KSC hub (building centroid).</summary>
    internal static class KscRoverPolicy
    {
        internal const double Approach = 45, DirectRange = 250, Arrived = 60;

        static readonly string[][] Names =
        {
            new[] { "VehicleAssemblyBuilding", "VAB", "vab vehicle assembly" }, new[] { "SpaceplaneHangar", "SPH", "sph hangar spaceplane plane" },
            new[] { "ResearchAndDevelopment", "R&D", "r&d rnd research development lab" }, new[] { "TrackingStation", "Tracking Station", "tracking station dish" },
            new[] { "MissionControl", "Mission Control", "mission control" }, new[] { "AstronautComplex", "Astronaut Complex", "astronaut complex crew" },
            new[] { "Administration", "Administration", "admin administration" }, new[] { "LaunchPad", "Launch Pad", "pad launchpad launch" },
            new[] { "Runway", "Runway", "runway strip" },
        };

        /// <summary>"SpaceCenter/VehicleAssemblyBuilding/Facility/..." -> "VehicleAssemblyBuilding" (null if not a known facility).</summary>
        internal static string Facility(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (string seg in id.Split('/')) foreach (var n in Names) if (seg.Equals(n[0], StringComparison.OrdinalIgnoreCase)) return n[0];
            return null;
        }
        internal static string Display(string facility) { foreach (var n in Names) if (n[0] == facility) return n[1]; return facility; }

        /// <summary>Player words -> facility key ("the VAB", "r&d", "tracking"); null if none.</summary>
        internal static string Match(string words)
        {
            string w = " " + (words ?? "").ToLowerInvariant().Replace("the ", " ").Trim() + " ";
            string best = null; int bestLen = 0;
            foreach (var n in Names)
                foreach (string alias in n[2].Split(' '))
                    if (alias.Length > 1 && w.Contains(" " + alias + " ") && alias.Length > bestLen) { best = n[0]; bestLen = alias.Length; }
            foreach (var n in Names) if (w.Trim() == n[1].ToLowerInvariant()) return n[0];
            return best;
        }

        internal struct P { internal double Lat, Lon; internal P(double a, double b) { Lat = a; Lon = b; } }
        static double D(P a, P b, double r) { return NavigationMath.Distance(a.Lat, a.Lon, b.Lat, b.Lon, r); }

        /// <summary>Waypoints from here to a building: via the KSC hub unless close, ending in front of the building (hub side).</summary>
        internal static List<P> Route(P from, P building, P hub, double radius)
        {
            var pts = new List<P>();
            double toHub = D(building, hub, radius);
            P front = toHub < 1 ? building : new P(building.Lat + (hub.Lat - building.Lat) * Math.Min(1, Approach / toHub), building.Lon + (hub.Lon - building.Lon) * Math.Min(1, Approach / toHub));
            if (D(from, front, radius) > DirectRange && D(from, hub, radius) > 30) pts.Add(hub);
            pts.Add(front);
            return pts;
        }

        /// <summary>Nearest-neighbour visiting order from the start.</summary>
        internal static List<int> Order(P start, IList<P> stops, double radius)
        {
            var left = new List<int>(); for (int i = 0; i < stops.Count; i++) left.Add(i);
            var order = new List<int>(); P at = start;
            while (left.Count > 0)
            {
                int bi = 0; for (int k = 1; k < left.Count; k++) if (D(at, stops[left[k]], radius) < D(at, stops[left[bi]], radius)) bi = k;
                order.Add(left[bi]); at = stops[left[bi]]; left.RemoveAt(bi);
            }
            return order;
        }
    }
}
