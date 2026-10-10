using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    // POST-TESTING: hold_pattern - circle a spot at a set altitude until told otherwise.
    public partial class NativeFlightController
    {
        bool patternOn, patternRight; double patternLat, patternLon, patternRadius; string patternName; float nextPattern;

        /// <summary>Chat/menu commands that take over navigation end the pattern (internal Command() calls don't).</summary>
        static readonly HashSet<string> NavTakeover = new HashSet<string> { "plane_hold", "set_heading", "turn", "fly_to", "fly_to_place", "circle_here", "land", "land_plane",
            "land_at_spot", "land_at_ksc", "go_around", "takeoff", "stop_current", "heli_control", "taxi_to", "hold_pattern", "touch_and_go" };

        void ExtrasCancel(string name)
        {
            if (patternOn && NavTakeover.Contains(name)) { patternOn = false; }
            if (touring && (name == "taxi_to" || name == "stop_current" || name == "takeoff" || name == "drive_to_building")) touring = false;
            TerrainCancel(name);
        }
        partial void TerrainCancel(string name);

        string ExtrasCommand(string name, Dictionary<string, object> a)
        {
            string pc = PilotCommand(name, a); if (pc != null) return pc;
            switch (name)
            {
                case "follow_terrain": return FollowTerrain(a);
                case "fuel_check_return": return FuelReturnArm(a);
                case "formation": return Formation(a);
                case "drive_to_building": return DriveToBuilding(a);
                case "hold_pattern": return HoldPattern(a);
                case "taxi_route": return TaxiRouteCmd(a);
                case "craft_class": return CraftClassCmd(Str(a, "mode", "auto"));
                case "learn_plane": return LearnPlaneCmd(a);
            }
            if (name.StartsWith("mj_")) return MechJebCmd(name, a);
            return null;
        }

        string HoldPattern(Dictionary<string, object> a)
        {
            if (vessel.LandedOrSplashed) return "Take off first, then hold_pattern.";
            if (!IsPlane() || props.HasLift(vessel)) return "hold_pattern is for planes (helis: heli_control hover).";
            string where = Str(a, "name", "");
            double lat = vessel.latitude, lon = vessel.longitude; string label = "here";
            if (where.Length > 0 && !where.Equals("here", StringComparison.OrdinalIgnoreCase))
            {
                TaxiMission.Point p = null;
                try { p = spots.Point(where, vessel.mainBody.bodyName); } catch (ArgumentException ex) { return ex.Message; }
                if (p == null) return "Unknown place: " + where + ". Save it as a landing spot first, or say hold_pattern here.";
                lat = p.Lat; lon = p.Lon; label = p.Name;
            }
            double alt = Num(a, "altitude_m", -1); if (alt <= 0) alt = vessel.altitude;
            double r = Math.Max(Num(a, "radius_m", 0), FlightExtrasPolicy.MinRadius(Math.Max(vessel.srfSpeed, speed)));
            string res = Command("plane_hold", Args("altitude_m", alt, "altitude_ref", "msl", "heading", FlightGlobals.ship_heading, "speed", -1));
            if (mode != "hold") return res;
            patternOn = true; patternRight = !Str(a, "direction", "right").Trim().StartsWith("l", StringComparison.OrdinalIgnoreCase);
            patternLat = lat; patternLon = lon; patternRadius = r; patternName = label; nextPattern = 0; flying2 = false; directBank = null;
            return "Holding " + (patternRight ? "right" : "left") + "-hand pattern over " + label + ", " + FlightResidualPolicy.Distance(r) + " radius at "
                + alt.ToString("0", Inv) + " m until told otherwise.";
        }

        void ExtrasTick()
        {
            if (patternOn)
            {
                if (mode != "hold") patternOn = false;
                else if (Time.realtimeSinceStartup >= nextPattern)
                {
                    nextPattern = Time.realtimeSinceStartup + 1;
                    double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, patternLat, patternLon, vessel.mainBody.Radius);
                    double b = NavigationMath.Bearing(vessel.latitude, vessel.longitude, patternLat, patternLon);
                    heading = FlightExtrasPolicy.PatternHeading(b, d, patternRadius, patternRight); holdHeading = true; directBank = null;
                }
            }
            TerrainTick();
            FuelReturnTick();
            FormationTick();
            RoverTourTick();
        }
        partial void TerrainTick();
        partial void FuelReturnTick();
        partial void FormationTick();
        partial void RoverTourTick();
    }
}
