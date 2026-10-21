using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Rover drive-to-building at KSC: facility names, road-ish routes and tour order (pure).
    /// Building positions are read in-game (DestructibleBuilding); roads are approximated through the KSC hub (building centroid).</summary>
    internal static class KscRoverPolicy
    {
        internal const double Approach = 45, DirectRange = 250, Arrived = 60;
        internal static double Steering(double headingError, double speed)
        {
            double limit = FlightPolicy.Clamp(8 / (8 + Math.Max(0, speed)), .2, 1);
            return FlightPolicy.WheelSteering(headingError, limit);
        }
        internal static bool ReduceSpeed(double pitch, double roll, double sideSpeed, double speed)
        {
            return Math.Abs(pitch) > 20 || Math.Abs(roll) > 20
                || Math.Abs(sideSpeed) > Math.Max(2, Math.Max(0, speed) * .35);
        }
        internal static bool BrakeForRollover(double roll) { return Math.Abs(roll) > 30; }
        internal static double ApproachSpeed(double target, double distance)
        {
            return distance < 100 ? Math.Min(target, Math.Max(1, Math.Min(4, distance / 25))) : target;
        }
        internal static double LearnedTopSpeed(double target, double topSpeed)
        {
            return topSpeed > 0 && !double.IsNaN(topSpeed) && !double.IsInfinity(topSpeed) ? Math.Min(target, topSpeed) : target;
        }
        internal static double LearnedTurnSpeed(double target, double safeTurnSpeed, double headingError)
        {
            return Math.Abs(headingError) > 5 && safeTurnSpeed > 0 && !double.IsNaN(safeTurnSpeed) && !double.IsInfinity(safeTurnSpeed)
                ? Math.Min(target, safeTurnSpeed) : target;
        }
        internal static double LearnedBrakingSpeed(double target, double distance, double brakingDistance, double stopDistance)
        {
            if (brakingDistance <= 0 || double.IsNaN(brakingDistance) || double.IsInfinity(brakingDistance)
                || distance <= stopDistance) return target;
            double allowed = 10 * Math.Sqrt((distance - stopDistance) / brakingDistance);
            return Math.Min(target, allowed);
        }
        internal static bool ReachedDestination(double distance) { return distance <= 10; }
        internal static bool FlatSurface(double pitch, double roll, double maxAngle)
        {
            return !double.IsNaN(pitch) && !double.IsInfinity(pitch) && !double.IsNaN(roll) && !double.IsInfinity(roll)
                && Math.Abs(pitch) <= maxAngle && Math.Abs(roll) <= maxAngle;
        }

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

        /// <summary>RS7g (Luke Oct 21: "KSC Grass" parked on the runway - it was 300 m west of the runway midpoint ON the centreline, approach
        /// 40 m north on the shoulder): open grass 180 m south of the runway centreline and 500 m west of its midpoint, i.e. between the
        /// runway and the Astronaut Complex / Admin, 200+ m from every building and road.</summary>
        internal const double GrassSouthM = 180, GrassWestM = 500, CreepStepM = 35;
        internal const int CreepTries = 2;
        internal static P GrassPoint(double radius)
        {
            double mLon = (KscRunway.Lon09 + KscRunway.Lon27) * .5, mLat = KscRunway.LatAt(mLon), la, lo, la2, lo2;
            NavigationMath.Offset(mLat, mLon, 180, GrassSouthM, radius, out la, out lo);
            NavigationMath.Offset(la, lo, 270, GrassWestM, radius, out la2, out lo2);
            return new P(la2, lo2);
        }
        static readonly string[] Paved = { "Runway", "LaunchPad", "Crawlerway", "VAB", "SPH", "R&D", "TrackingStation", "AstronautComplex", "MissionControl", "Administration", "FlagPole", "Helipad" };
        /// <summary>RS7g: did the rover park in the stop's biome? Stock KSC sub-biomes share the facility prefix (VABMainBuilding, R&DWindTunnel ...);
        /// "KSC" (grass) is any landedAt / biome that is not pavement or a building. No wanted biome (places) = always fine.</summary>
        internal static bool StopBiomeOk(string wanted, string got)
        {
            if (string.IsNullOrEmpty(wanted)) return true;
            got = (got ?? "").Replace(" ", "");
            if (wanted == "KSC") { foreach (var p in Paved) if (got.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false; return true; }
            return got.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>RS7g: one creep toward the stop's anchor (building centre / grass point), at most CreepStepM, never inside keepOutM of it
        /// (building footprint). False when there is nothing useful left to creep (under 5 m).</summary>
        internal static bool CreepTarget(P here, P anchor, double keepOutM, double radius, out P target)
        {
            target = here; double d = D(here, anchor, radius), step = Math.Min(CreepStepM, d - Math.Max(0, keepOutM));
            if (step < 5) return false;
            double la, lo; NavigationMath.Offset(here.Lat, here.Lon, NavigationMath.Bearing(here.Lat, here.Lon, anchor.Lat, anchor.Lon), step, radius, out la, out lo);
            target = new P(la, lo); return true;
        }
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

        internal static List<P> GroundRoute(P from, P destination, P hub, double radius)
        {
            var points = new List<P>();
            if (D(from, destination, radius) > DirectRange
                && D(from, hub, radius) > 30 && D(destination, hub, radius) > 30)
                points.Add(hub);
            points.Add(destination);
            return points;
        }

        internal static double GroundRouteDistance(P from, P destination, P hub, double radius)
        {
            var points = GroundRoute(from, destination, hub, radius);
            double distance = 0;
            P previous = from;
            foreach (P point in points) { distance += D(previous, point, radius); previous = point; }
            return distance;
        }

        /// <summary>Shortest KSC stop order; Held-Karp for up to 16 biomes, with 2-opt for larger custom maps.</summary>
        internal static List<int> ShortestOrder(P start, IList<P> stops, P hub, double radius)
        {
            int n = stops.Count;
            if (n == 0) return new List<int>();
            if (n > 16) return ImproveOrder(start, stops, hub, radius, Order(start, stops, radius));

            int states = 1 << n;
            var costs = new double[states, n];
            var previous = new short[states, n];
            for (int mask = 0; mask < states; mask++)
                for (int i = 0; i < n; i++) { costs[mask, i] = double.PositiveInfinity; previous[mask, i] = -1; }
            for (int i = 0; i < n; i++) costs[1 << i, i] = GroundRouteDistance(start, stops[i], hub, radius);

            for (int mask = 1; mask < states; mask++)
                for (int last = 0; last < n; last++)
                {
                    double cost = costs[mask, last];
                    if (double.IsInfinity(cost) || (mask & (1 << last)) == 0) continue;
                    for (int next = 0; next < n; next++)
                    {
                        int bit = 1 << next;
                        if ((mask & bit) != 0) continue;
                        int nextMask = mask | bit;
                        double candidate = cost + GroundRouteDistance(stops[last], stops[next], hub, radius);
                        if (candidate < costs[nextMask, next])
                        {
                            costs[nextMask, next] = candidate;
                            previous[nextMask, next] = (short)last;
                        }
                    }
                }

            int full = states - 1, end = 0;
            for (int i = 1; i < n; i++) if (costs[full, i] < costs[full, end]) end = i;
            var order = new List<int>();
            for (int mask = full, at = end; at >= 0;)
            {
                order.Add(at);
                int prior = previous[mask, at];
                mask ^= 1 << at;
                at = prior;
            }
            order.Reverse();
            return order;
        }

        static List<int> ImproveOrder(P start, IList<P> stops, P hub, double radius, List<int> order)
        {
            bool changed;
            do
            {
                changed = false;
                for (int first = 0; first < order.Count - 1 && !changed; first++)
                    for (int last = first + 1; last < order.Count; last++)
                    {
                        P before = first == 0 ? start : stops[order[first - 1]];
                        P after = stops[order[last]];
                        double oldCost = GroundRouteDistance(before, stops[order[first]], hub, radius);
                        if (last + 1 < order.Count) oldCost += GroundRouteDistance(after, stops[order[last + 1]], hub, radius);
                        double newCost = GroundRouteDistance(before, after, hub, radius);
                        if (last + 1 < order.Count) newCost += GroundRouteDistance(stops[order[first]], stops[order[last + 1]], hub, radius);
                        if (newCost + .01 >= oldCost) continue;
                        order.Reverse(first, last - first + 1);
                        changed = true;
                        break;
                    }
            } while (changed);
            return order;
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
