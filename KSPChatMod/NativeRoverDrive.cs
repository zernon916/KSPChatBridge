// RS3 game side: autopilot-only rover guard called from the mode "rover" loop (never runs while the player drives by hand):
// learned per-rover speed caps per surface, slope ahead, roll / turn limits, obstacle rays, runway hold-short, stuck back-up.
// Raycasts and terrain samples are throttled to 4 Hz.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        RoverCaps roverCaps; string roverCapsFor; readonly RoverStuck roverStuck = new RoverStuck(); RoverSpeedLearner roverSpeedLearn = new RoverSpeedLearner();
        readonly RoverCircling roverCircling = new RoverCircling(); readonly RoverArrival roverArrival = new RoverArrival(); string roverEscapeWhy = ""; bool roverWasBacking;   // RS7b
        internal static string RoverAlert = "";   // RS7b: shown on the tablet SCIENCE TOUR page
        float roverGuardNext; double roverGuardCap = double.PositiveInfinity, roverDetour, roverPrevHeading = double.NaN, roverYawRate; float roverDetourUntil, roverCapsSaveAt;
        bool roverGuardBrake, roverHoldShort; RoverSurface roverSurface; string roverGuardWhy = "", roverGuardLogged = "";

        static double RoverSetting(string key) { double v = CraftOverrides.L(key); return double.IsNaN(v) ? CraftOverrides.Find(key).Default : v; }

        RoverCaps CurrentRoverCaps()
        {
            if (roverCaps != null && roverCapsFor == vessel.vesselName) return roverCaps;
            object o; var t = settingsData.TryGetValue("rover_caps", out o) ? o as Dictionary<string, object> : null; object e = null; if (t != null) t.TryGetValue(vessel.vesselName, out e);
            roverCaps = RoverCaps.FromDict(e as Dictionary<string, object>); roverCapsFor = vessel.vesselName; roverSpeedLearn = new RoverSpeedLearner(); return roverCaps;
        }
        void SaveRoverCaps()
        {
            object o; var t = settingsData.TryGetValue("rover_caps", out o) ? o as Dictionary<string, object> : null;
            if (t == null) settingsData["rover_caps"] = t = new Dictionary<string, object>();
            t[vessel.vesselName] = roverCaps.ToDict(); try { Save(); } catch (Exception) { }
        }
        static string SurfaceKey(RoverSurface s) { return s == RoverSurface.Runway ? "rover_runway_speed" : s == RoverSurface.Road ? "rover_road_speed" : "rover_max_speed"; }

        /// <summary>CRAFT page value in use for rover settings: learned caps for the speed rows, else the default.</summary>
        double RoverInUse(CraftOverrides.Def d)
        {
            if (vessel != null && (d.Key == "rover_max_speed" || d.Key == "rover_road_speed" || d.Key == "rover_runway_speed"))
            { var c = CurrentRoverCaps(); return d.Key == "rover_runway_speed" ? c.Runway : d.Key == "rover_road_speed" ? c.Road : c.OffRoad; }
            return d.Default;
        }

        /// <summary>RS3: called every physics frame in mode "rover" (autopilot). Caps targetSpeed; true = the guard drives (backing up / holding).</summary>
        bool RoverGuardTick(FlightCtrlState c, ref double targetSpeed, double pitch, double roll, double dt)
        {
            float now = Time.realtimeSinceStartup;
            if (now >= roverGuardNext)
            {
                double gdt = roverGuardNext > 0 ? Math.Max(.05, now - roverGuardNext + .25) : .25; roverGuardNext = now + .25f;
                try { RoverGuardSample(gdt); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] rover guard: " + ex.Message); }
                try { RoverCirclingCheck(now); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] rover circling: " + ex.Message); }
            }
            double want = targetSpeed;
            targetSpeed = Math.Min(targetSpeed, roverGuardCap);
            double rollSlow = RoverSetting("rover_roll_slow_deg");
            targetSpeed = RoverDrivePolicy.Cap(targetSpeed, double.PositiveInfinity, 0, 90, roll, rollSlow, roverYawRate);
            if (roverNavigating && now < roverDetourUntil) roverHeading = FlightPolicy.Wrap(roverHeading + roverDetour);
            if (roverGuardWhy != roverGuardLogged) { roverGuardLogged = roverGuardWhy; if (roverGuardWhy.Length > 0) ChatLog.Write("rover", "guard: " + roverGuardWhy); }
            var caps = CurrentRoverCaps(); bool user = !double.IsNaN(CraftOverrides.L(SurfaceKey(roverSurface)));
            if (CraftOverrides.On("rover_learn_speed", true))
            {
                string ch = roverSpeedLearn.Sample(now, roverSurface, caps, user, vessel.srfSpeed, roll, rollSlow, RoverDrivePolicy.BrakeForRoll(roll) || Math.Abs(roll) > rollSlow);
                if (ch != null) { ChatLog.Write("rover", "[" + vessel.vesselName + "] learned " + ch); roverCapsSaveAt = now + 5; }
            }
            if (roverCapsSaveAt > 0 && now >= roverCapsSaveAt) { roverCapsSaveAt = 0; SaveRoverCaps(); }
            if (roverCharging) { roverIntegral = 0; c.wheelThrottle = 0; SetGroup(vessel, KSPActionGroup.Brakes, true); roverStuck.Reset(); return true; }   // RS4: parked while charging
            if (roverGuardBrake && vessel.srfSpeed < .5 && !roverStuck.Backing) { roverStuck.ForceBack(now); roverGuardBrake = false; roverDetour = -roverDetour; roverDetourUntil = now + 8; roverGuardWhy = "blocked: backing up"; }
            if (RoverEscape.GiveUp(roverStuck.Count))
            {   // RS7b: 3 escapes (back up, nose to the next waypoint, re-plan) failed
                string what = roverEscapeWhy.Length > 0 ? roverEscapeWhy : "stuck";
                RoverAlert = "Rover stopped: " + what + " again after " + RoverEscape.MaxRetries + " escape attempts near "
                    + vessel.latitude.ToString("0.0000", CultureInfo.InvariantCulture) + "," + vessel.longitude.ToString("0.0000", CultureInfo.InvariantCulture) + ". Drive it clear and start the tour again.";
                roverStuck.Count = 0; roverStuck.Reset(); roverCircling.Reset();
                ChatLog.Write("rover", "guard: " + what + " " + (RoverEscape.MaxRetries + 1) + " times - autopilot and tour stopped"); ChatWindow.Notice(RoverAlert);
                scienceTouring = false; touring = false; Stop(); return true;
            }
            if (roverGuardBrake || roverHoldShort)
            {
                roverIntegral = 0; c.wheelThrottle = 0; SetGroup(vessel, KSPActionGroup.Brakes, true); roverStuck.Reset(); return true;
            }
            if (roverStuck.Sample(now, want, vessel.srfSpeed, dt))
            {
                if (roverStuck.Count > 0 && roverGuardWhy != "stuck: backing up" && roverGuardWhy != "circling: backing up") { roverGuardWhy = "stuck: backing up"; roverEscapeWhy = "stuck"; roverDetour = roverStuck.Count % 2 == 0 ? -40 : 40; roverDetourUntil = now + 8; }
                // RS7b: reverse with the nose turning toward the next road waypoint (reversing flips the steering)
                double err = FlightPolicy.Wrap(roverHeading - FlightGlobals.ship_heading);
                SetGroup(vessel, KSPActionGroup.Brakes, false); roverIntegral = 0; c.wheelThrottle = -.5f;
                c.wheelSteer = roverHeadingEnabled ? (float)RoverEscape.BackSteer(KscRoverPolicy.Steering(err, 0)) : 0;
                roverWasBacking = true; return true;
            }
            if (roverWasBacking)
            {   // RS7b: escape done - re-snap to the nearest road and plan again from here
                roverWasBacking = false; roverCircling.Reset(); roverArrival.Reset(); RoverReplanRoad();
            }
            return false;
        }

        /// <summary>RS7b (4 Hz): orbiting / no progress toward the target -> the RS3 stuck escape (back up 5 m with the nose turning to the
        /// next road waypoint, then re-plan from here); after RoverEscape.MaxRetries escapes the guard stops the tour.</summary>
        void RoverCirclingCheck(float now)
        {
            if (!roverNavigating || roverStuck.Backing || roverCharging || roverGuardBrake || roverHoldShort) { if (!roverNavigating) roverCircling.Reset(); return; }
            double dist = RoverRoadActive && !double.IsNaN(roverRoadLeft) ? roverRoadLeft
                : NavigationMath.Distance(vessel.latitude, vessel.longitude, roverDestinationLat, roverDestinationLon, vessel.mainBody.Radius);
            string why = roverCircling.Sample(now, FlightGlobals.ship_heading, dist, vessel.srfSpeed);
            if (why == null) return;
            ChatLog.Write("rover", "circling: " + why + " - backing up, nose to the next waypoint, re-planning (escape " + (roverStuck.Count + 1) + "/" + RoverEscape.MaxRetries + ")");
            roverCircling.Reset(); roverEscapeWhy = "circling"; roverGuardWhy = "circling: backing up"; roverDetourUntil = 0;
            roverStuck.ForceBack(now);
        }

        void RoverGuardSample(double gdt)
        {
            var body = vessel.mainBody; double r = body.Radius, h = FlightGlobals.ship_heading;
            roverYawRate = double.IsNaN(roverPrevHeading) ? 0 : FlightPolicy.Wrap(h - roverPrevHeading) * Math.PI / 180 / gdt; roverPrevHeading = h;
            roverSurface = RoverDrivePolicy.Surface(vessel.landedAt, vessel.latitude, vessel.longitude, r);
            var caps = CurrentRoverCaps(); double user = CraftOverrides.L(SurfaceKey(roverSurface)); double cap = double.IsNaN(user) ? caps.Get(roverSurface) : user;
            // slope 20 m ahead along the travel direction (heading when slow)
            double dir = vessel.srfSpeed > 1 ? h + (Math.Atan2(Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.right), Math.Max(.1, Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.up))) * 180 / Math.PI) : h;
            double alat, alon; NavigationMath.Offset(vessel.latitude, vessel.longitude, dir, 20, r, out alat, out alon);
            double h0 = MapTerrain(body, vessel.latitude, vessel.longitude), h1 = MapTerrain(body, alat, alon), slope = RoverDrivePolicy.SlopeDeg(h0, h1, 20);
            cap = RoverDrivePolicy.SlopeCap(cap, slope, RoverSetting("rover_slope_deg"));
            string why = Math.Abs(slope) > RoverSetting("rover_slope_deg") * .7 ? "slope " + slope.ToString("0", CultureInfo.InvariantCulture) + " deg ahead" : "";
            // obstacles: three rays (ahead, +-25 deg) at bumper height, own parts and ground ignored
            roverGuardBrake = false; double look = RoverDrivePolicy.LookAhead(vessel.srfSpeed);
            if (vessel.srfSpeed > .5)
            {
                Vector3d up = (vessel.CoMD - body.position).normalized, fwd = Vector3d.Exclude(up, vessel.srf_velocity).normalized; Vector3 o = (Vector3)(vessel.CoMD + up * .6);
                string what; double ce = Ray(o, fwd, look, up, out what), le = Ray(o, Quaternion.AngleAxis(-25, up) * (Vector3)fwd, look, up, out string _l), ri = Ray(o, Quaternion.AngleAxis(25, up) * (Vector3)fwd, look, up, out string _r);
                bool brake; double bias = RoverDrivePolicy.Detour(ce, le, ri, vessel.srfSpeed, out brake);
                if (!double.IsNaN(ce))
                {
                    roverGuardBrake = brake; cap = Math.Min(cap, 4);
                    if (bias != 0 && roverNavigating) { roverDetour = bias; roverDetourUntil = Time.realtimeSinceStartup + 3; }
                    why = "obstacle " + ce.ToString("0", CultureInfo.InvariantCulture) + " m ahead (" + what + ")" + (brake ? ": braking" : ": detour " + (bias > 0 ? "right" : "left"));
                }
            }
            // runway hold-short while an aircraft is airborne close by
            roverHoldShort = false;
            bool onRw = roverSurface == RoverSurface.Runway, aheadRw = RoverDrivePolicy.OnKscRunway(alat, alon, r);
            if (!onRw && aheadRw)
                foreach (Vessel ov in FlightGlobals.Vessels)
                {
                    if (ov == null || ov == vessel || ov.mainBody != body || ov.LandedOrSplashed || ov.vesselType == VesselType.Debris || ov.vesselType == VesselType.Flag || ov.vesselType == VesselType.SpaceObject) continue;
                    double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, ov.latitude, ov.longitude, r);
                    if (RoverDrivePolicy.HoldShort(onRw, aheadRw, d, ov.altitude)) { roverHoldShort = true; why = "holding short of the runway: " + ov.vesselName + " " + d.ToString("0", CultureInfo.InvariantCulture) + " m out"; break; }
                }
            roverGuardCap = cap; roverGuardWhy = why;
        }

        double Ray(Vector3 origin, Vector3 dir, double range, Vector3d up, out string what)
        {
            what = ""; double best = double.NaN;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, dir, (float)range, (1 << 0) | (1 << 15)))
            {
                if (hit.collider == null) continue;
                var hp = hit.collider.GetComponentInParent<Part>(); if (hp != null && hp.vessel == vessel) continue;
                if (Vector3.Dot(hit.normal, (Vector3)up) > .75f) continue;   // ground / road
                if (double.IsNaN(best) || hit.distance < best) { best = hit.distance; what = hp != null && hp.vessel != null ? hp.vessel.vesselName : hit.collider.name; }
            }
            return best;
        }
    }
}