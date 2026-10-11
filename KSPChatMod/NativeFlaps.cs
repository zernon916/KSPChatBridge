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
        int flapSet, rollFlapCount; float flapNextStep, flapScanAt = -100, climbSince = -1; bool farFlaps, goAroundFlaps; Vessel flapVessel;
        internal static int FlapSetting { get { return instance == null ? 0 : instance.flapSet; } }
        internal static bool HasFlapsNow { get { return instance != null && instance.flaps.Count > 0; } }
        /// <summary>"FLAPS UP" / "FLAPS 2" / "FLAPS FULL" (+ limit); "" when the craft has no usable flaps.</summary>
        internal static string FlapLine { get { var me = instance; if (me == null || me.flaps.Count == 0) return me != null && me.farFlaps ? "FLAPS: FAR (not driven)" : ""; return "FLAPS " + FlapsPolicy.Name(me.flapSet) + " (lim " + me.FlapLimit().ToString("0") + ")"; } }

        void ScanFlaps()
        {
            flaps.Clear(); farFlaps = false; rollFlapCount = 0; flapVessel = vessel;
            foreach (Part p in vessel.parts)
            {
                if (p.Modules.Contains("FARControllableSurface")) { farFlaps = true; continue; }
                foreach (var cs in p.FindModulesImplementing<ModuleControlSurface>())
                {
                    var k = FlapsPolicy.Classify(!cs.ignorePitch, !cs.ignoreYaw, !cs.ignoreRoll);
                    if (k == FlapKind.Flap || k == FlapKind.RollFlap) { flaps.Add(cs); if (k == FlapKind.RollFlap) rollFlapCount++; }
                }
            }
        }
        double FlapLimit()
        {
            double learned = profile != null ? profile.FlapLimit : double.NaN;
            return FlapsPolicy.SpeedLimit(stall, learned);
        }
        int FlapsWanted()
        {
            if (learn != null && !learn.Done) return Math.Max(0, learn.Flaps);
            if (goAroundFlaps)
            {   // keep them until a positive climb for 3 s, then retract
                if (vessel.verticalSpeed > 2) { if (climbSince < 0) climbSince = Time.realtimeSinceStartup; } else climbSince = -1;
                if (climbSince >= 0 && Time.realtimeSinceStartup - climbSince > 3) { goAroundFlaps = false; return 0; }
                return flapSet;
            }
            if (mode == "landing" && runway != null) return FlapsPolicy.ApproachSetting(runway.Phase, runway.Distance, ApproachChart.ShortFix);
            return 0;
        }
        void FlapsTick()
        {
            if (vessel == null) return;
            if (flapVessel != vessel || Time.realtimeSinceStartup > flapScanAt + 10) { if (flapVessel != vessel) RestoreFlaps(); ScanFlaps(); flapScanAt = Time.realtimeSinceStartup; }
            if (flaps.Count == 0) return;
            int want = vessel.LandedOrSplashed && mode != "landing" ? 0 : FlapsWanted();
            want = Math.Min(want, FlapsPolicy.MaxAllowed(vessel.indicatedAirSpeed, FlapLimit(), flapSet));
            if (want != flapSet && Time.realtimeSinceStartup >= flapNextStep)
            {   // one notch at a time, 2 s apart
                flapSet += Math.Sign(want - flapSet); flapNextStep = Time.realtimeSinceStartup + 2;
                if (flapSet == 0 && !goAroundFlaps) RestoreFlaps(); else ApplyFlaps();
                ChatLog.Write("flaps", FlapsPolicy.Name(flapSet) + " at " + vessel.indicatedAirSpeed.ToString("0") + " m/s (limit " + FlapLimit().ToString("0") + ", " + flaps.Count + " surfaces, " + rollFlapCount + " roll-only)");
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
