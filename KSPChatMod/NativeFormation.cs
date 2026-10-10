using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    // POST-TESTING: formation - an AI wingman flies a second loaded craft (its own OnFlyByWire/FlightCtrlState) on our wing.
    public partial class NativeFlightController
    {
        Vessel wing; int wingSide = 1; double wingSpacing = 60, wingThrottle = .5, wingHdgCmd, wingSpdCmd, wingAltCmd, wingPitchI; float wingLastStep; string wingName;

        static Vessel FindWing(string name, Vessel lead)
        {
            Vessel best = null; double bestD = double.MaxValue; string n = (name ?? "").Trim().ToLowerInvariant();
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == lead || v.vesselType == VesselType.Debris || v.vesselType == VesselType.EVA || v.vesselType == VesselType.Flag) continue;
                if (n.Length > 0 && !v.vesselName.ToLowerInvariant().Contains(n)) continue;
                double d = Vector3d.Distance(v.CoM, lead.CoM);
                if (d < bestD) { bestD = d; best = v; }
            }
            return best;
        }

        void WingRelease(string why)
        {
            if (wing != null) { wing.OnFlyByWire -= WingFly; if (why != null) ChatWindow.Notice("Wingman " + wingName + " released: " + why); }
            wing = null;
        }

        string Formation(Dictionary<string, object> a)
        {
            if (Bool(a, "off", false)) { bool was = wing != null; WingRelease(null); return was ? "Wingman released; it keeps its last controls (SAS on)." : "No wingman."; }
            if (vessel.LandedOrSplashed) return "Take off first; the wingman joins in the air.";
            var w = FindWing(Str(a, "wingman", ""), vessel);
            if (w == null) return "No other loaded craft" + (Str(a, "wingman", "").Length > 0 ? " named " + Str(a, "wingman", "") : "") + " within physics range (~2.3 km).";
            double d = Vector3d.Distance(w.CoM, vessel.CoM);
            if (d > FlightExtrasPolicy.PhysicsRange) return w.vesselName + " is " + FlightResidualPolicy.Distance(d) + " away; it must be within ~2.3 km to fly.";
            if (w.LandedOrSplashed || w.packed) return w.vesselName + " must be airborne and unpacked to fly formation.";
            WingRelease(null);
            wing = w; wingName = w.vesselName; wingSide = Str(a, "side", "right").Trim().StartsWith("l", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            wingSpacing = Math.Max(30, Math.Min(300, Num(a, "spacing_m", 60))); wingThrottle = Math.Max(.3, w.ctrlState.mainThrottle); wingLastStep = 0; wingPitchI = 0;
            wingHdgCmd = FlightGlobals.ship_heading; wingSpdCmd = vessel.srfSpeed; wingAltCmd = vessel.altitude;
            w.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            w.OnFlyByWire += WingFly;
            return wingName + " joining on our " + (wingSide > 0 ? "right" : "left") + " wing, " + wingSpacing.ToString("0", Inv) + " m echelon.";
        }

        partial void FormationTick()
        {
            if (wing == null) return;
            if (wing.state == Vessel.State.DEAD || !wing.loaded || wing.packed) { WingRelease("out of physics range or lost."); return; }
            double d = Vector3d.Distance(wing.CoM, vessel.CoM);
            if (d > FlightExtrasPolicy.PhysicsRange) { WingRelease("over 2.3 km apart."); return; }
            if (wing.LandedOrSplashed) { WingRelease("it's on the ground."); return; }
            double along, cross;
            FlightExtrasPolicy.SlotError(vessel.latitude, vessel.longitude, FlightGlobals.ship_heading, wing.latitude, wing.longitude, wingSpacing, wingSide, vessel.mainBody.Radius, out along, out cross);
            wingHdgCmd = FlightExtrasPolicy.WingHeading(FlightGlobals.ship_heading, cross, along);
            wingSpdCmd = FlightExtrasPolicy.WingSpeed(vessel.srfSpeed, along);
            wingAltCmd = vessel.altitude;
        }

        static double HeadingOf(Vessel v)
        {
            Vector3d up = (v.CoM - v.mainBody.position).normalized, north = Vector3d.Exclude(up, v.mainBody.transform.up).normalized, east = Vector3d.Cross(up, north);
            Vector3d vel = Vector3d.Exclude(up, v.srf_velocity); if (vel.magnitude < 5) vel = Vector3d.Exclude(up, (Vector3d)v.ReferenceTransform.up);
            return FlightExtrasPolicy.Norm(Math.Atan2(Vector3d.Dot(vel, east), Vector3d.Dot(vel, north)) * 180 / Math.PI);
        }

        void WingFly(FlightCtrlState c)
        {
            var w = wing; if (w == null) return;
            Vector3d up = (w.CoM - w.mainBody.position).normalized; Transform t = w.ReferenceTransform;
            double bank = -Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(t.right, up)))) * 180 / Math.PI;   // + right wing down
            double pitch = Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(t.up, up)))) * 180 / Math.PI;
            double bankCmd = FlightExtrasPolicy.WingBank(FlightExtrasPolicy.HeadingError(wingHdgCmd, HeadingOf(w)), w.srfSpeed);
            double vsCmd = FlightExtrasPolicy.WingVs(wingAltCmd - w.altitude);
            double vsErr = vsCmd - w.verticalSpeed; wingPitchI = Math.Max(-5, Math.Min(5, wingPitchI + vsErr * .002));
            c.roll = (float)Math.Max(-1, Math.Min(1, (bankCmd - bank) * .02));
            c.pitch = (float)Math.Max(-1, Math.Min(1, vsErr * .03 + wingPitchI * .1 + (pitch < -20 ? .3 : 0) + (pitch > 25 ? -.3 : 0)));
            c.yaw = 0;
            float now = Time.realtimeSinceStartup;
            double nt = FlightExtrasPolicy.ThrottleStep(wingThrottle, wingSpdCmd - w.srfSpeed, now - wingLastStep);
            if (nt != wingThrottle) { wingThrottle = nt; wingLastStep = now; }
            c.mainThrottle = (float)wingThrottle;
        }
    }
}
