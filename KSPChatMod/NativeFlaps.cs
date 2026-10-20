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
        readonly HashSet<ModuleControlSurface> rollFlaps = new HashSet<ModuleControlSurface>(); double rfDroop = 0, rfApplied = -1; float rfLast; int rfSet, rfSetApplied = -1; string rfPhase;   // GROK RF1
        readonly Dictionary<ModuleControlSurface, bool> rfInvert = new Dictionary<ModuleControlSurface, bool>();   // AICS1: deployInvert that makes this surface droop DOWN
        float configChangedAt = -100; int flapSet, rollFlapCount; float flapNextStep, flapScanAt = -100, climbSince = -1; bool farFlaps, goAroundFlaps, takeoffFlaps; Vessel flapVessel;
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
            brakeWantSet = false; flaps.Clear(); rollFlaps.Clear(); brakeSurfs.Clear(); farFlaps = false; rollFlapCount = 0; flapVessel = vessel; surfTagged = false;
            var surfs = new List<ModuleControlSurface>();
            foreach (Part p in vessel.parts)
            {
                if (p.Modules.Contains("FARControllableSurface")) { farFlaps = true; continue; }
                foreach (var cs in p.FindModulesImplementing<ModuleControlSurface>()) { surfs.Add(cs); if (SurfaceRoles.FromTag(AicsSurfaceTag.TagOf(p)).HasValue) surfTagged = true; }
            }
            Transform rt = vessel.ReferenceTransform; Vector3 fwd = rt.up, right = rt.right, top = -rt.forward, com = vessel.CoM;
            var wings = new List<Vector3>(); double half = 0, maxWing = 0;
            foreach (Part p in vessel.parts)
            {
                Vector3 d = p.transform.position - com; Vector3 l = new Vector3(Vector3.Dot(d, right), Vector3.Dot(d, fwd), Vector3.Dot(d, top));
                var ls = p.FindModuleImplementing<ModuleLiftingSurface>(); if (ls != null && p.FindModuleImplementing<ModuleControlSurface>() == null) { wings.Add(l); maxWing = Math.Max(maxWing, ls.deflectionLiftCoeff); }
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
                bool mainTe = false;
                try
                {   // GROK RF1: ROLL / FLAPS auto = attached to a main wing panel (>= half the biggest panel), horizontal hinge, not on the leading edge, not ahead of the CoM (canard)
                    Part par = p.parent; var pls = par != null && par.FindModuleImplementing<ModuleControlSurface>() == null ? par.FindModuleImplementing<ModuleLiftingSurface>() : null;
                    Transform hinge = (string.IsNullOrEmpty(cs.transformName) ? null : p.FindModelTransform(cs.transformName)) ?? p.transform;
                    mainTe = pls != null && maxWing > 0 && pls.deflectionLiftCoeff >= .5 * maxWing && Math.Abs(Vector3.Dot(hinge.right, right)) > .7f && chord != 1 && l.y < .5f;
                }
                catch (Exception) { mainTe = false; }

                bool brakeAg = false;
                foreach (BaseAction a in cs.Actions) if ((a.actionGroup & KSPActionGroup.Brakes) != 0) brakeAg = true;
                bool aero = cs is ModuleAeroSurface;
                if (surfTagged) { role = tagged ?? SurfRole.Other; why = tagged.HasValue ? "tag '" + AicsSurfaceTag.TagOf(p) + "'" : "untagged (tags in use)"; }
                else { role = SurfaceRoles.Classify(!cs.ignorePitch, !cs.ignoreYaw, !cs.ignoreRoll, aero, brakeAg, span01, chord, vert, defl, mainTe); why = "auto"; }
                if (SurfaceRoles.HighLift(role)) { flaps.Add(cs); if (role == SurfRole.RollFlap) { rollFlapCount++; rollFlaps.Add(cs); rfInvert[cs] = defl == -1 ? !cs.deployInvert : cs.deployInvert; } }
                else if (role == SurfRole.Brake) brakeSurfs.Add(cs);
                log.Append(p.partInfo != null ? p.partInfo.title : p.name).Append(" @").Append(l.x.ToString("0.0")).Append("/").Append(l.y.ToString("0.0")).Append("/").Append(l.z.ToString("0.0"))
                   .Append(" span ").Append(span01.ToString("0.00")).Append(chord == 1 ? " LE" : chord == -1 ? " TE" : " mid").Append(vert == 1 ? " top" : vert == -1 ? " bottom" : "")
                   .Append(defl == 1 ? " defl-down" : defl == -1 ? " defl-up" : "").Append(brakeAg ? " brakesAG" : "").Append(aero ? " airbrake" : "")
                   .Append(mainTe ? " mainwing" : "").Append(" -> ").Append(role == SurfRole.Unknown ? "unknown (leading edge, left alone)" : role == SurfRole.RollFlap ? "ROLL / FLAPS" + (cs.ignorePitch ? "" : " (has pitch: never drooped)") : role.ToString().ToLowerInvariant()).Append(" [").Append(why).Append("]; ");
            }
            string s2 = log.ToString();
            if (SurfaceRoles.ShouldLog(ref surfLog, vessel != null ? vessel.id.ToString() : "", s2)) { ChatLog.Write("flaps", "surfaces (" + (surfTagged ? "TAGGED: tags only" : "auto-detect") + ", " + flaps.Count + " high-lift, " + brakeSurfs.Count + " brake): " + s2); }
        }
        /// <summary>Brake surfaces follow the Brakes action group (deploy), so spoilers not wired to the AG still act as airbrakes; only what we deployed is retracted.</summary>
        void BrakeSurfacesTick()
        {
            if (vessel == null) return;
            bool dump = FlapsPolicy.LiftDump(mode, runway != null ? runway.Phase : null, vessel.LandedOrSplashed, vessel.srfSpeed);   // AICS2: AP landing rollout only, ends when disengaged / stopped
            if (dump) dumpOn = true;
            else if (dumpOn)
            {   // AICS2: lift dump over (stopped < 2 m/s, AP off, phase change) - retract what it put out
                dumpOn = false; int n = 0;
                foreach (var ab in dumpedAero) if (ab != null && ab.deploy) { ab.deploy = false; n++; }
                dumpedAero.Clear();
                foreach (var cs in brakeSurfs) if (cs != null && brakeOwned.Contains(cs) && cs.deploy && !vessel.ActionGroups[KSPActionGroup.Brakes]) { cs.deploy = false; n++; }
                brakeOwned.Clear(); brakeWantSet = false;
                ChatLog.Write("brakes", "lift dump over (" + (mode != "landing" ? "autopilot off" : vessel.srfSpeed <= 2 ? "stopped" : "rollout ended") + ") - " + n + " spoilers / airbrakes retracted");
            }
            bool on = mode != "idle" && (vessel.ActionGroups[KSPActionGroup.Brakes] && !vessel.LandedOrSplashed || dump);   // AICS2: only while the AP is engaged; disengage retracts what we put out
            if (on == brakeWant && brakeWantSet) return;   // AICS2: write surfaces only when OUR wanted state changes (no per-frame fight with the pilot / PAW)
            brakeWant = on; brakeWantSet = true;
            foreach (var cs in brakeSurfs)
            {
                if (cs == null || flaps.Contains(cs) || cs.deploy == on) continue;
                if (!on && !brakeOwned.Contains(cs)) continue;
                cs.deploy = on; if (on) brakeOwned.Add(cs); else brakeOwned.Remove(cs);
                try { recovery.AcceptSurface(cs); } catch (Exception) { }
            }
        }
        readonly HashSet<ModuleControlSurface> brakeOwned = new HashSet<ModuleControlSurface>();
        internal readonly List<ModuleAeroSurface> dumpedAero = new List<ModuleAeroSurface>(); bool brakeWant, brakeWantSet;
        /// <summary>AICS2: a role change in the PAW rescans the surfaces now (not in up to 10 s).</summary>
        /// <summary>AICS2: landing-test spawn / respawn - airbrakes + brake-role surfaces all retracted, lift dump state cleared.</summary>
        internal static int ResetBrakes(Vessel v, string why)
        {
            if (v == null) return 0; int n = 0;
            if (instance != null) { if (instance.vessel == v) instance.ScanFlaps(); foreach (var cs in instance.brakeSurfs) if (cs != null && cs.deploy) { cs.deploy = false; n++; } instance.brakeOwned.Clear(); instance.dumpedAero.Clear(); instance.dumpOn = false; instance.brakeWantSet = false; }
            foreach (var ab in v.FindPartModulesImplementing<ModuleAeroSurface>()) if (ab != null && ab.deploy) { ab.deploy = false; n++; }
            ChatLog.Write("brakes", why + ": airbrakes / brake surfaces retracted (" + n + " were out)"); return n;
        }
        bool dumpOn;
        /// <summary>AICS2: test spawn - thrust reversers forced forward.</summary>
        internal static void ForwardReversers(Vessel v, string why)
        { try { if (instance != null && instance.vessel == v && instance.reversers != null) { bool ch = instance.reversers.Set(v, false); ChatLog.Write("engine", "reverser forward (" + why + (ch ? ", was reversed" : "") + ")"); } } catch (Exception ex) { ChatLog.Write("engine", "reverser forward failed: " + ex.Message); } }
        internal static void RescanSurfaces() { if (instance != null) { instance.flapScanAt = -100; instance.brakeWantSet = false; } }
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
                if (rotateStalled) return 0;   // GROK TO3: nose won't come up - flaps behind the CoM push it down
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
            if (mode == "landing" && runway != null) { double faf = runway.Chart != null ? runway.Chart.FafD : RunwayMission.FafDistance; int sch = FlapsPolicy.ApproachSchedule(runway.Phase, runway.Distance, faf, flapSet, vessel.radarAltitude); if (FlapsPolicy.Frozen(runway.Distance, vessel.radarAltitude) && (runway.Phase == "final" || runway.Phase == "flare")) return flapSet; bool air = runway.Phase == "entry" || runway.Phase == "intercept" || runway.Phase == "final"; return air && runway.Distance > Math.Max(faf, FlapsPolicy.FullByM + 1000) ? Math.Max(sch, FlapsPolicy.EarlySetting(Eas(), FlapLimit(), flapSet)) : sch; }   // GROK LAND5b: early, stepped, frozen inside 4 km / 300 m
            return 0;
        }
        void FlapsTick()
        {
            if (vessel == null) return;
            if (flapVessel != vessel || Time.realtimeSinceStartup > flapScanAt + 10) { if (flapVessel != vessel) RestoreFlaps(); ScanFlaps(); flapScanAt = Time.realtimeSinceStartup; }
            ApproachProfile.FlapLimitSpeed = !double.IsNaN(CraftOverrides.L("flap_limit_speed")) ? CraftOverrides.L("flap_limit_speed") : flaps.Count > 0 ? FlapLimit() : double.NaN;   // TAB-CRAFT1   // GROK LAND5e: the early decel targets flap limit - 10 by D14
            BrakeSurfacesTick();
            if (flaps.Count == 0) return;
            RollFlapTick();
            int want = FlapsPolicy.GroundZero(vessel.LandedOrSplashed, mode, takeoffFlaps) ? 0 : FlapsWanted();   // Oct 12: the ground gate zeroed takeoff flaps
            if (mode == "landing" && runway != null && runway.FlareFast && runway.Phase == "flare") want = 3;   // GROK LAND5c: fast flare = full flaps, no overspeed retract
            bool latch = mode == "landing" && runway != null && (runway.FlareFast && runway.Phase == "flare" || FlapsPolicy.FinalLatch(runway.Phase == "final" || runway.Phase == "flare", runway.Distance, runway.Chart != null ? runway.Chart.FafD : RunwayMission.FafDistance, flapSet, Eas(), FlapLimit()));
            if (!latch) want = Math.Min(want, FlapsPolicy.MaxAllowed(Eas(), FlapLimit(), flapSet)); else want = Math.Max(want, flapSet);   // GROK LAND5b: latched on final   // flap limit is a stall multiple: EAS (dfbcd0b)
            if (want == 0 && flapSet > 0 && learn != null && !learn.Done)
            {   // Learn flaps test ended / aborted / skipped / climbing back: all the way up at once, not a notch per 2 s
                flapSet = 0; RestoreFlaps(); flapNextStep = Time.realtimeSinceStartup + 2; ChatLog.Write("flaps", "UP (Learn flaps test over / climb-back)"); return;
            }
            if (want == 0 && flapSet > 0 && FlapsPolicy.MaxAllowed(Eas(), FlapLimit(), flapSet) == 0)
            {   // GROK TO3: over the flap limit = all the way up now, not a notch per 2 s (live: still at 1 at 139/134)
                flapSet = 0; RestoreFlaps(); flapNextStep = Time.realtimeSinceStartup + 2; configChangedAt = Time.realtimeSinceStartup; ChatLog.Write("flaps", "UP (over limit " + FlapLimit().ToString("0") + " at " + Eas().ToString("0") + " m/s)"); return;
            }
            if (want != flapSet && Time.realtimeSinceStartup >= flapNextStep)
            {   // one notch at a time, 2 s apart
                flapSet += Math.Sign(want - flapSet); flapNextStep = Time.realtimeSinceStartup + 2; configChangedAt = Time.realtimeSinceStartup;
                if (flapSet == 0 && !goAroundFlaps) RestoreFlaps(); else ApplyFlaps();
                ChatLog.Write("flaps", (takeoffFlaps ? (want >= flapSet ? "takeoff deploy " : "takeoff retract step ") : "") + FlapsPolicy.Name(flapSet) + " at " + vessel.radarAltitude.ToString("0") + " m AGL, " + Eas().ToString("0") + " m/s (limit " + FlapLimit().ToString("0") + ", " + flaps.Count + " surfaces, " + rollFlapCount + " roll-only)");
            }
        }
        /// <summary>GROK RF1: ROLL / FLAPS droop follows the roll demand every tick (roll first, droop back when wings are level again).</summary>
        void RollFlapTick()
        {
            if (rollFlaps.Count == 0) return;
            if (mode == "idle")
            {   // FIX: AP off = hands off the surfaces; put back what the droop changed, once
                if (rfApplied > 0 || rfSetApplied > 0) { foreach (var cs in rollFlaps) { FlapSnap o; if (cs != null && flapOrig.TryGetValue(cs, out o)) { cs.deploy = o.Deploy; cs.deployAngle = o.Angle; cs.deployInvert = o.Invert; flapOrig.Remove(cs); } } ChatLog.Write("aics", "roll/flaps droop off (autopilot off) - surfaces restored"); }
                rfApplied = 0; rfSetApplied = 0; rfDroop = 0; rfSet = 0; rfPhase = "idle"; rfLast = 0; return;
            }
            float now = Time.realtimeSinceStartup; double dt = rfLast > 0 ? now - rfLast : 0; rfLast = now;
            bool act = mode == "landing" && runway != null && !vessel.LandedOrSplashed && FlapsPolicy.RollFlapPhase(runway.Phase);   // AICS1: own droop schedule on approach / final / flare
            double lim = !double.IsNaN(CraftOverrides.L("flap_limit_speed")) ? CraftOverrides.L("flap_limit_speed") : FlapLimit();
            rfSet = FlapsPolicy.RollFlapSetting(act, flapSet, Eas(), lim, rfSet);
            double bank = Roll(), want = rfSet > 0 ? FlapsPolicy.RollFlapDroop(vessel.ctrlState.roll, bankCmd - bank, bank, act ? runway.Cross : 0) : 0;
            rfDroop = FlapsPolicy.DroopStep(rfDroop, want, dt);
            if (Math.Abs(rfDroop - rfApplied) > .02 || rfSet != rfSetApplied) { rfApplied = rfDroop; rfSetApplied = rfSet; ApplyRollFlaps(); }
            string ph = act ? runway.Phase : mode ?? "";
            if (ph != rfPhase)
            {
                rfPhase = ph; double deg = 0;
                foreach (var cs in rollFlaps) if (cs != null) { FlapSnap o; float dmax = flapOrig.TryGetValue(cs, out o) && o.Angle > .1f ? o.Angle : cs.ctrlSurfaceRange; deg = Math.Max(deg, FlapsPolicy.RollFlapDeg(cs.ctrlSurfaceRange, dmax, rfSet, 1, 0)); }
                ChatLog.Write("aics", "roll/flaps droop " + deg.ToString("0.0") + " deg max (phase " + (ph == "" ? "-" : ph) + ", " + rollFlaps.Count + " surfaces, limit " + lim.ToString("0") + " m/s, now " + Eas().ToString("0") + ")");
            }
        }
        /// <summary>AICS1: ROLL / FLAPS surfaces droop DOWN by RollFlapDeg (roll keeps the rest of the throw).</summary>
        void ApplyRollFlaps()
        {
            foreach (var cs in rollFlaps)
            {
                if (cs == null || !cs.ignorePitch) continue;   // NEVER deploy a pitch surface
                FlapSnap o; if (!flapOrig.TryGetValue(cs, out o)) flapOrig[cs] = o = new FlapSnap { Deploy = cs.deploy, Invert = cs.deployInvert, Angle = cs.deployAngle };
                float deg = (float)FlapsPolicy.RollFlapDeg(cs.ctrlSurfaceRange, o.Angle > .1f ? o.Angle : cs.ctrlSurfaceRange, rfSet, rfDroop, vessel.ctrlState.roll);
                bool inv; cs.deployInvert = rfInvert.TryGetValue(cs, out inv) ? inv : o.Invert; cs.deployAngle = Math.Max(.01f, deg); cs.deploy = deg > .05f;
                try { recovery.AcceptSurface(cs); } catch (Exception) { }
            }
        }
        void ApplyFlaps()
        {
            foreach (var cs in flaps)
            {
                if (cs == null || !cs.ignorePitch || rollFlaps.Contains(cs)) continue;   // NEVER deploy a pitch surface; AICS1: ROLL / FLAPS = ApplyRollFlaps
                FlapSnap o; if (!flapOrig.TryGetValue(cs, out o)) flapOrig[cs] = o = new FlapSnap { Deploy = cs.deploy, Invert = cs.deployInvert, Angle = cs.deployAngle };
                float max = o.Angle > .1f ? o.Angle : cs.ctrlSurfaceRange; bool rf = rollFlaps.Contains(cs);
                if (rf) max = Math.Min(max, (float)FlapsPolicy.RollFlapThrow * cs.ctrlSurfaceRange);   // GROK RF1: half the throw stays for roll
                cs.deployAngle = max * (float)(FlapsPolicy.Fraction(flapSet) * (rf ? rfDroop : 1)); cs.deployInvert = o.Invert; cs.deploy = flapSet > 0;
                try { recovery.AcceptSurface(cs); } catch (Exception) { }
            }
        }
        /// <summary>Restore every surface's original deploy / angle / direction.</summary>
        void RestoreFlaps()
        {
            foreach (var kv in flapOrig) if (kv.Key != null) { kv.Key.deploy = kv.Value.Deploy; kv.Key.deployAngle = kv.Value.Angle; kv.Key.deployInvert = kv.Value.Invert; try { recovery.AcceptSurface(kv.Key); } catch (Exception) { } }
            flapOrig.Clear(); flapSet = 0; rfApplied = -1; rfSetApplied = -1;
        }
    }
}
