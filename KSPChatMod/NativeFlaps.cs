using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>Flaps (Luke 9:45 AM): stock ModuleControlSurface only. Dedicated flaps (pitch/yaw/roll all off) and roll-only surfaces
    /// (via the stock deploy feature, still rolling). Pitch-enabled surfaces are never deployed. Original deploy settings are restored.</summary>
    public partial class NativeFlightController
    {
        sealed class FlapSnap { internal bool Deploy, Invert; internal float Angle; }
        readonly Dictionary<ModuleControlSurface, FlapSnap> flapOrig = new Dictionary<ModuleControlSurface, FlapSnap>();
        readonly List<ModuleControlSurface> flaps = new List<ModuleControlSurface>();
        int flapSet, rollFlapCount; float flapNextStep, flapScanAt = -100, climbSince = -1; bool farFlaps, goAroundFlaps, takeoffFlaps; Vessel flapVessel;
        internal static int FlapSetting { get { return instance == null ? 0 : instance.flapSet; } }
        internal static bool HasFlapsNow { get { return instance != null && instance.flaps.Count > 0; } }
        /// <summary>"FLAPS UP" / "FLAPS 2" / "FLAPS FULL" (+ limit); "" when the craft has no usable flaps.</summary>
        internal static string FlapLine { get { var me = instance; if (me == null || me.flaps.Count == 0) return me != null && me.farFlaps ? "FLAPS: FAR (not driven)" : ""; return "FLAPS " + FlapsPolicy.Name(me.flapSet) + " (lim " + me.FlapLimit().ToString("0") + ")"; } }

        readonly List<ModuleControlSurface> brakeSurfs = new List<ModuleControlSurface>(); bool surfTagged; string surfLog = "";
        internal static bool SurfacesTagged { get { return instance != null && instance.surfTagged; } }
        /// <summary>Flap / brake surfaces. Tags (AICS role / kOS name tag) override everything: any tag on the craft = tagged parts only.
        /// Untagged craft: geometry auto-detect (SurfaceRoles.Classify) - spoilers on top of the wing, Brakes-AG surfaces and stock airbrakes
        /// are brakes, never flaps; untagged leading-edge surfaces are left alone. Each part's role is logged whenever the result changes.</summary>
        void ScanFlaps()
        {
            flaps.Clear(); brakeSurfs.Clear(); farFlaps = false; rollFlapCount = 0; flapVessel = vessel; surfTagged = false;
            var surfs = new List<ModuleControlSurface>();
            foreach (Part p in vessel.parts)
            {
                if (p.Modules.Contains("FARControllableSurface")) { farFlaps = true; continue; }
                foreach (var cs in p.FindModulesImplementing<ModuleControlSurface>()) { surfs.Add(cs); if (SurfaceRoles.FromTag(AicsSurfaceTag.TagOf(p)).HasValue) surfTagged = true; }
            }
            Transform rt = vessel.ReferenceTransform; Vector3 fwd = rt.up, right = rt.right, top = -rt.forward, com = vessel.CoM;
            var wings = new List<Vector3>(); double half = 0;
            foreach (Part p in vessel.parts)
            {
                Vector3 d = p.transform.position - com; Vector3 l = new Vector3(Vector3.Dot(d, right), Vector3.Dot(d, fwd), Vector3.Dot(d, top));
                if (p.FindModuleImplementing<ModuleLiftingSurface>() != null && p.FindModuleImplementing<ModuleControlSurface>() == null) wings.Add(l);
                half = Math.Max(half, Math.Abs(l.x));
            }
            var log = new System.Text.StringBuilder();
            foreach (var cs in surfs)
            {
                Part p = cs.part; SurfRole? tagged = SurfaceRoles.FromTag(AicsSurfaceTag.TagOf(p)); SurfRole role; string why;
                Vector3 d = p.transform.position - com; Vector3 l = new Vector3(Vector3.Dot(d, right), Vector3.Dot(d, fwd), Vector3.Dot(d, top));
                int chord = 0, vert = 0, defl = 0; double span01 = half > .5 ? Math.Abs(l.x) / half : 0;
                if (wings.Count > 0)
                {   // nearest fixed wing panel on the same side: in front of it = leading edge, behind = trailing, above = top surface
                    Vector3 w = wings[0]; float best = float.MaxValue;
                    foreach (var c in wings) { if (Math.Sign(c.x) != Math.Sign(l.x) && Math.Abs(l.x) > .5f) continue; float dd = (c - l).sqrMagnitude; if (dd < best) { best = dd; w = c; } }
                    chord = l.y > w.y + .3f ? 1 : l.y < w.y - .3f ? -1 : 0; vert = l.z > w.z + .15f ? 1 : l.z < w.z - .15f ? -1 : 0;
                }
                try
                {   // deploy deflection direction (logged; only a tie-breaker): rotate the moving edge about the hinge axis
                    Transform hinge = (string.IsNullOrEmpty(cs.transformName) ? null : p.FindModelTransform(cs.transformName)) ?? p.transform;
                    float sg = (cs.deployInvert ? -1 : 1) * (cs.usesMirrorDeploy && cs.mirrorDeploy ? -1 : 1) * (cs.deployAngle < 0 ? -1 : 1);
                    Vector3 lever = chord == 1 ? fwd : -fwd, moved = Quaternion.AngleAxis(10 * sg, hinge.right) * lever;
                    float dz = Vector3.Dot(moved - lever, top); defl = dz < -.02f ? 1 : dz > .02f ? -1 : 0;
                }
                catch (Exception) { defl = 0; }
                bool brakeAg = false;
                foreach (BaseAction a in cs.Actions) if ((a.actionGroup & KSPActionGroup.Brakes) != 0) brakeAg = true;
                bool aero = cs is ModuleAeroSurface;
                if (surfTagged) { role = tagged ?? SurfRole.Other; why = tagged.HasValue ? "tag '" + AicsSurfaceTag.TagOf(p) + "'" : "untagged (tags in use)"; }
                else { role = SurfaceRoles.Classify(!cs.ignorePitch, !cs.ignoreYaw, !cs.ignoreRoll, aero, brakeAg, span01, chord, vert, defl); why = "auto"; }
                if (SurfaceRoles.HighLift(role)) { flaps.Add(cs); if (role == SurfRole.RollFlap) rollFlapCount++; }
                else if (role == SurfRole.Brake) brakeSurfs.Add(cs);
                log.Append(p.partInfo != null ? p.partInfo.title : p.name).Append(" @").Append(l.x.ToString("0.0")).Append("/").Append(l.y.ToString("0.0")).Append("/").Append(l.z.ToString("0.0"))
                   .Append(" span ").Append(span01.ToString("0.00")).Append(chord == 1 ? " LE" : chord == -1 ? " TE" : " mid").Append(vert == 1 ? " top" : vert == -1 ? " bottom" : "")
                   .Append(defl == 1 ? " defl-down" : defl == -1 ? " defl-up" : "").Append(brakeAg ? " brakesAG" : "").Append(aero ? " airbrake" : "")
                   .Append(" -> ").Append(role == SurfRole.Unknown ? "unknown (leading edge, left alone)" : role.ToString().ToLowerInvariant()).Append(" [").Append(why).Append("]; ");
            }
            string s2 = log.ToString();
            if (SurfaceRoles.ShouldLog(ref surfLog, vessel != null ? vessel.id.ToString() : "", s2)) { ChatLog.Write("flaps", "surfaces (" + (surfTagged ? "TAGGED: tags only" : "auto-detect") + ", " + flaps.Count + " high-lift, " + brakeSurfs.Count + " brake): " + s2); }
        }
        /// <summary>Brake surfaces follow the Brakes action group (deploy), so spoilers not wired to the AG still act as airbrakes; only what we deployed is retracted.</summary>
        void BrakeSurfacesTick()
        {
            if (vessel == null) return; bool on = vessel.ActionGroups[KSPActionGroup.Brakes] && !vessel.LandedOrSplashed;
            foreach (var cs in brakeSurfs)
            {
                if (cs == null || flaps.Contains(cs) || cs.deploy == on) continue;
                if (!on && !brakeOwned.Contains(cs)) continue;
                cs.deploy = on; if (on) brakeOwned.Add(cs); else brakeOwned.Remove(cs);
                try { recovery.AcceptSurface(cs); } catch (Exception) { }
            }
        }
        readonly HashSet<ModuleControlSurface> brakeOwned = new HashSet<ModuleControlSurface>();
        double FlapLimit()
        {
            double learned = profile != null ? profile.FlapLimit : double.NaN;
            return FlapsPolicy.SpeedLimit(stall, learned);
        }
        int FlapsWanted()
        {
            if (!takeoffFlaps && learn != null && !learn.Done && vessel.LandedOrSplashed && FlapsPolicy.LearnTakeoffPhase(learn.Phase)) { takeoffFlaps = true; ChatLog.Write("flaps", "takeoff flaps armed (Learn takeoff, " + flaps.Count + " tagged surfaces)"); }
            if (takeoffFlaps && mode != "landing")
            {   // Oct 12: partial flaps for the roll, stepped retraction after 120 m AGL
                int w = FlapsPolicy.TakeoffSetting(flapSet, vessel.LandedOrSplashed, vessel.radarAltitude, Eas(), stall, ApproachProfile.FlapRatio);
                if (w == 0 && flapSet == 0 && !vessel.LandedOrSplashed) takeoffFlaps = false;
                return w;
            }
            if (learn != null && !learn.Done) return Math.Max(0, learn.Flaps);
            if (goAroundFlaps)
            {   // keep them until a positive climb for 3 s, then retract
                if (vessel.verticalSpeed > 2) { if (climbSince < 0) climbSince = Time.realtimeSinceStartup; } else climbSince = -1;
                if (climbSince >= 0 && Time.realtimeSinceStartup - climbSince > 3) { goAroundFlaps = false; return 0; }
                return flapSet;
            }
            if (mode == "landing" && runway != null) { int sch = FlapsPolicy.ApproachSetting(runway.Phase, runway.Distance, ApproachChart.ShortFix); bool air = runway.Phase == "entry" || runway.Phase == "intercept" || runway.Phase == "final"; return air ? Math.Max(sch, FlapsPolicy.EarlySetting(Eas(), FlapLimit(), flapSet)) : sch; }   // Luke Oct 12: early flaps by speed
            return 0;
        }
        void FlapsTick()
        {
            if (vessel == null) return;
            if (flapVessel != vessel || Time.realtimeSinceStartup > flapScanAt + 10) { if (flapVessel != vessel) RestoreFlaps(); ScanFlaps(); flapScanAt = Time.realtimeSinceStartup; }
            BrakeSurfacesTick();
            if (flaps.Count == 0) return;
            int want = FlapsPolicy.GroundZero(vessel.LandedOrSplashed, mode, takeoffFlaps) ? 0 : FlapsWanted();   // Oct 12: the ground gate zeroed takeoff flaps
            want = Math.Min(want, FlapsPolicy.MaxAllowed(Eas(), FlapLimit(), flapSet));   // flap limit is a stall multiple: EAS (dfbcd0b)
            if (want == 0 && flapSet > 0 && learn != null && !learn.Done)
            {   // Learn flaps test ended / aborted / skipped / climbing back: all the way up at once, not a notch per 2 s
                flapSet = 0; RestoreFlaps(); flapNextStep = Time.realtimeSinceStartup + 2; ChatLog.Write("flaps", "UP (Learn flaps test over / climb-back)"); return;
            }
            if (want != flapSet && Time.realtimeSinceStartup >= flapNextStep)
            {   // one notch at a time, 2 s apart
                flapSet += Math.Sign(want - flapSet); flapNextStep = Time.realtimeSinceStartup + 2;
                if (flapSet == 0 && !goAroundFlaps) RestoreFlaps(); else ApplyFlaps();
                ChatLog.Write("flaps", (takeoffFlaps ? (want > flapSet || vessel.LandedOrSplashed ? "takeoff deploy " : "takeoff retract step ") : "") + FlapsPolicy.Name(flapSet) + " at " + vessel.radarAltitude.ToString("0") + " m AGL, " + Eas().ToString("0") + " m/s (limit " + FlapLimit().ToString("0") + ", " + flaps.Count + " surfaces, " + rollFlapCount + " roll-only)");
            }
        }
        void ApplyFlaps()
        {
            foreach (var cs in flaps)
            {
                if (cs == null || !cs.ignorePitch) continue;   // NEVER deploy a pitch surface
                FlapSnap o; if (!flapOrig.TryGetValue(cs, out o)) flapOrig[cs] = o = new FlapSnap { Deploy = cs.deploy, Invert = cs.deployInvert, Angle = cs.deployAngle };
                float max = o.Angle > .1f ? o.Angle : cs.ctrlSurfaceRange;
                cs.deployAngle = max * (float)FlapsPolicy.Fraction(flapSet); cs.deployInvert = o.Invert; cs.deploy = flapSet > 0;
                recovery.AcceptSurface(cs);
            }
        }
        /// <summary>Restore every surface's original deploy / angle / direction.</summary>
        void RestoreFlaps()
        {
            foreach (var kv in flapOrig) if (kv.Key != null) { kv.Key.deploy = kv.Value.Deploy; kv.Key.deployAngle = kv.Value.Angle; kv.Key.deployInvert = kv.Value.Invert; try { recovery.AcceptSurface(kv.Key); } catch (Exception) { } }
            flapOrig.Clear(); flapSet = 0;
        }
    }
}
