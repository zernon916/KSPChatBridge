using System;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        float nextAuthorityLog, nextAuthoritySample;
        double pitchAuthority = double.NaN, rollAuthority = double.NaN;
        Vessel authorityVessel;

        // Approximate gain scheduling estimate. KSP uses tonnes, kN and tonne*m^2, so torque / MOI is rad/s^2.
        // Surface area is a stock lift-area proxy, not measured geometry; unit lift coefficient
        // and the part origin/forward normal approximate force and its moment arm.
        void LateUpdate()
        {
            if (vessel == null || vessel.packed || vessel != FlightGlobals.ActiveVessel) return;
            float now = Time.realtimeSinceStartup;
            if (authorityVessel == vessel && now < nextAuthoritySample) return;
            if (authorityVessel != vessel) nextAuthorityLog = 0;
            authorityVessel = vessel; nextAuthoritySample = now + .5f;
            try
            {
                double pitchAero = 0, rollAero = 0, pitchWheel = 0, rollWheel = 0;
                double q = vessel.dynamicPressurekPa;
                var frame = vessel.ReferenceTransform;
                foreach (var s in vessel.FindPartModulesImplementing<ModuleControlSurface>())
                {
                    if (!s.moduleIsEnabled || s.part.ShieldedFromAirstream) continue;
                    double area = Math.Max(0, s.deflectionLiftCoeff * s.ctrlSurfaceArea);
                    double force = area * q * Math.Abs(s.authorityLimiter) * .01;
                    if (s.ctrlSurfaceRange == 0) continue;
                    Vector3 moment = frame.InverseTransformDirection(Vector3.Cross(
                        s.part.transform.position - vessel.CurrentCoM, s.part.transform.forward));
                    if (!s.ignorePitch) pitchAero += force * Math.Abs(moment.x);
                    if (!s.ignoreRoll) rollAero += force * Math.Abs(moment.y);
                }
                foreach (var w in vessel.FindPartModulesImplementing<ModuleReactionWheel>())
                {
                    if (!w.moduleIsEnabled || !w.operational || w.State != ModuleReactionWheel.WheelState.Active
                        || (VesselActuatorMode)w.actuatorModeCycle == VesselActuatorMode.SAS) continue;
                    double limiter = Math.Max(0, w.authorityLimiter) * .01;
                    pitchWheel += Math.Abs(w.PitchTorque) * limiter;
                    rollWheel += Math.Abs(w.RollTorque) * limiter;
                }
                var moi = vessel.MOI; // ReferenceTransform axes: X pitch, Y roll, Z yaw.
                double pitch = moi.x > 0 ? (pitchAero + pitchWheel) / moi.x : double.NaN;
                double roll = moi.y > 0 ? (rollAero + rollWheel) / moi.y : double.NaN;
                bool changed = (PilotPolicy.AuthorityGain(pitchAuthority) == 1) != (PilotPolicy.AuthorityGain(pitch) == 1)
                    || (PilotPolicy.AuthorityGain(rollAuthority) == 1) != (PilotPolicy.AuthorityGain(roll) == 1);
                pitchAuthority = pitch; rollAuthority = roll;
                if (!changed && now < nextAuthorityLog) return;
                nextAuthorityLog = now + 10;
                ChatLog.Write("att", "authority PID gains: pitch x" + AuthorityText(PilotPolicy.AuthorityGain(pitch))
                    + ", roll x" + AuthorityText(PilotPolicy.AuthorityGain(roll))
                    + "; inverse authority, nominal 1 rad/s2, bounded 0.5..2; zero/invalid uses old gains");
                ChatLog.Write("att", string.Format(CultureInfo.InvariantCulture,
                    "authority estimate: pitch {0}, roll {1} rad/s2; q {2:0.###} kPa; " +
                    "aero torque {3:0.###}/{4:0.###}, wheel torque {5:0.###}/{6:0.###} kNm; " +
                    "MOI {7:0.###}/{8:0.###} t*m2; unit-lift area proxy, wheel power assumed available",
                    AuthorityText(pitch), AuthorityText(roll), q, pitchAero, rollAero,
                    pitchWheel, rollWheel, moi.x, moi.y));
            }
            catch (Exception ex) { pitchAuthority = rollAuthority = double.NaN; ChatLog.Write("att", "authority estimate unavailable: " + ex.Message); }
        }

        double AuthorityScale(bool pitch)
        {
            return vessel == authorityVessel && vessel != null && !vessel.packed
                && Time.realtimeSinceStartup <= nextAuthoritySample + 1
                ? PilotPolicy.AuthorityGain(pitch ? pitchAuthority : rollAuthority) : 1;
        }

        static string AuthorityText(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value < 0
                ? "unknown" : value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
