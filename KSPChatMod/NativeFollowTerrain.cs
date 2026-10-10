using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    // POST-TESTING: follow_terrain - hold a fixed height above ground (planes in a local hold).
    public partial class NativeFlightController
    {
        bool terrainOn; double terrainAgl, terrainTarget = double.NaN; float nextTerrainFollow;
        static readonly HashSet<string> TerrainTakeover = new HashSet<string> { "set_altitude", "level_off", "land", "land_plane", "land_at_spot", "land_at_ksc",
            "takeoff", "stop_current", "heli_control", "taxi_to", "go_around", "plane_pitch", "touch_and_go" };

        partial void TerrainCancel(string name) { if (terrainOn && TerrainTakeover.Contains(name)) TerrainOff(); }
        void TerrainOff() { terrainOn = false; terrainMargin = 300; terrainTarget = double.NaN; }

        string FollowTerrain(Dictionary<string, object> a)
        {
            double agl = Num(a, "agl_m", 0);
            if (agl <= 0) { bool was = terrainOn; TerrainOff(); return was ? "Terrain following off; holding " + vessel.altitude.ToString("0", Inv) + " m." : "Terrain following is off."; }
            if (vessel.LandedOrSplashed) return "Take off first, then follow_terrain.";
            if (!IsPlane() || props.HasLift(vessel)) return "follow_terrain is for planes for now (helis: heli_control altitude).";
            if (mode != "hold") { string r = Command("plane_hold", Args("altitude_m", vessel.altitude, "altitude_ref", "msl", "heading", FlightGlobals.ship_heading, "speed", -1)); if (mode != "hold") return r; }
            agl = FlightExtrasPolicy.ClampAgl(agl);
            terrainOn = true; terrainAgl = agl; terrainMargin = FlightExtrasPolicy.FloorMargin(agl); terrainTarget = double.NaN; nextTerrainFollow = 0;
            return "Following terrain at " + agl.ToString("0", Inv) + " m AGL (climbs early for ridges, descends gently).";
        }

        partial void TerrainTick()
        {
            if (!terrainOn) return;
            if (mode != "hold") { TerrainOff(); return; }
            if (Time.realtimeSinceStartup < nextTerrainFollow) return;
            nextTerrainFollow = Time.realtimeSinceStartup + 1;
            double h = vessel.heightFromTerrain > 0 ? vessel.heightFromTerrain : vessel.radarAltitude;
            double below = vessel.altitude - h;
            terrainTarget = FlightExtrasPolicy.TerrainTarget(below, terrainAheadM, terrainAgl, terrainTarget);
            altitude = terrainTarget; holdAltitude = true;
        }
    }
}
