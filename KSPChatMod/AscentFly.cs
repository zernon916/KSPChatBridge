using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        Vessel ascentCraft;
        double ascentTarget;
        bool ascentCoasting, ascentNodePlaced;
        ManeuverNode ascentNode;
        AscentPhase ascentLastPhase;
        double ascentPitchError, ascentYawError;
        bool ascentSteeringSampled;

        // True = valid guidance applied (including coasting / node handoff).
        // This is deliberately not wired into Fly or Command yet.
        internal bool AscentStep(Vessel v, FlightCtrlState c, double targetAlt, out string status)
        {
            status = "Ascent needs a vessel, control state, body and orbit.";
            if (c == null) return false;
            c.mainThrottle = 0;
            c.pitch = c.yaw = c.roll = 0;
            if (v == null || v.mainBody == null || v.mainBody.transform == null || v.orbit == null || v.ReferenceTransform == null || v.parts == null) return false;
            try
            {
                if (ascentCraft != v || ascentTarget != targetAlt || (v.LandedOrSplashed && ascentCoasting))
                {
                    ascentCraft = v; ascentTarget = targetAlt;
                    ascentCoasting = ascentNodePlaced = false; ascentNode = null;
                    ascentLastPhase = AscentPhase.Climb;
                    ascentSteeringSampled = false;
                }
                var body = v.mainBody;
                var orbit = v.orbit;
                double atmosphere = body.atmosphere ? body.atmosphereDepth : 0;
                double radius = body.Radius + v.altitude, mass = v.GetTotalMass();
                double gravity = body.gravParameter / (radius * radius), thrust = 0;
                if (!AscentGuidance.Finite(mass) || mass <= 0 || !AscentGuidance.Finite(gravity) || gravity <= 0)
                { status = "Ascent mass or gravity unavailable."; return false; }
                foreach (var part in v.parts)
                {
                    if (part == null) continue;
                    foreach (var engine in part.FindModulesImplementing<ModuleEngines>())
                    {
                        if (engine == null || !engine.EngineIgnited || engine.flameout) continue;
                        var multi = part.FindModuleImplementing<MultiModeEngine>();
                        if (multi != null && engine.engineID != (multi.runningPrimary ? multi.primaryEngineID : multi.secondaryEngineID)) continue;
                        // kN / tonnes gives m/s^2. Measure current capacity when
                        // firing; at idle use current-pressure maximum and limiter.
                        double capacity = engine.currentThrottle > .01 && engine.finalThrust > 0
                            ? engine.finalThrust / engine.currentThrottle
                            : engine.GetMaxThrust() * engine.thrustPercentage / 100;
                        if (AscentGuidance.Finite(capacity) && capacity > 0) thrust += capacity;
                    }
                }
                // A stationary / radial launch can have eccentricity exactly one
                // while still having negative orbital energy. Do not reject it.
                if (!v.LandedOrSplashed && (!AscentGuidance.Finite(orbit.semiMajorAxis) || orbit.semiMajorAxis <= 0))
                { status = "Ascent stopped: no bound apoapsis."; return false; }
                var command = AscentGuidance.Evaluate(v.altitude, v.verticalSpeed, v.horizontalSrfSpeed,
                    thrust / (mass * gravity), v.dynamicPressurekPa, targetAlt, atmosphere,
                    ascentCoasting ? Math.Max(targetAlt, orbit.ApA) : v.LandedOrSplashed ? v.altitude : orbit.ApA,
                    v.LandedOrSplashed ? 0 : orbit.timeToAp);
                if (command.Phase != AscentPhase.Climb) ascentCoasting = true;

                // Follow horizontal surface velocity once established; initially
                // launch east. At a pole use the control point's tangent instead.
                Vector3d up = v.upAxis.normalized;
                Vector3d horizontal = Vector3d.Exclude(up, v.srf_velocity);
                if (horizontal.magnitude < 10) horizontal = Vector3d.Cross(body.transform.up, up);
                if (horizontal.sqrMagnitude < 1e-10) horizontal = Vector3d.Exclude(up, v.ReferenceTransform.up);
                if (horizontal.sqrMagnitude < 1e-10) horizontal = Vector3d.Exclude(up, v.ReferenceTransform.right);
                double angle = command.TargetPitch * Math.PI / 180;
                Vector3d desired = up * Math.Sin(angle) + horizontal.normalized * Math.Cos(angle);
                // Same control axes and error-rate damping as NativeVerticalLanding.
                double pitchError = Vector3d.Dot(desired, -v.ReferenceTransform.forward);
                double yawError = Vector3d.Dot(desired, v.ReferenceTransform.right);
                double dt = Math.Max(.001, TimeWarp.fixedDeltaTime);
                double pitchRate = ascentSteeringSampled ? (pitchError - ascentPitchError) / dt : 0;
                double yawRate = ascentSteeringSampled ? (yawError - ascentYawError) / dt : 0;
                ascentPitchError = pitchError; ascentYawError = yawError; ascentSteeringSampled = true;
                c.pitch = (float)Math.Max(-1, Math.Min(1, 2 * pitchError + .6 * pitchRate));
                c.yaw = (float)Math.Max(-1, Math.Min(1, 2 * yawError + .6 * yawRate));
                c.mainThrottle = (float)command.Throttle;
                status = command.Phase + ": pitch " + command.TargetPitch.ToString("F1", CultureInfo.InvariantCulture) +
                    ", throttle " + command.Throttle.ToString("F2", CultureInfo.InvariantCulture);
                if (command.Phase != ascentLastPhase)
                { ChatLog.Write("rocket", status); ascentLastPhase = command.Phase; }
                if (!ascentCoasting) return true;
                if (v.altitude < atmosphere) { status += "; waiting to leave atmosphere before node planning."; return true; }
                var solver = v.patchedConicSolver;
                if (solver == null || solver.maneuverNodes == null)
                { status = "Coasting: maneuver solver unavailable."; return false; }
                if (ascentNodePlaced && (ascentNode == null || !solver.maneuverNodes.Contains(ascentNode)))
                { status = "Coasting: ascent node removed; automatic recreation stopped."; return true; }
                if (!ascentNodePlaced && solver.maneuverNodes.Count != 0)
                { status = "Coasting: clear existing maneuver nodes before planning circularization."; return false; }
                if (orbit.timeToAp <= 0 || v.verticalSpeed < 0)
                { status = "Coasting: apoapsis passed; circularization requires replanning."; return false; }
                double ut = Planetarium.GetUniversalTime() + orbit.timeToAp;
                double dv = RocketMath.CircularizeAtAp(body.gravParameter, orbit.ApR, orbit.PeR);
                if (!AscentGuidance.Finite(ut) || !AscentGuidance.Finite(dv) || dv < 0)
                { status = "Coasting: circularization geometry unavailable."; return false; }
                if (!ascentNodePlaced)
                {
                    ascentNode = solver.AddManeuverNode(ut);
                    if (ascentNode == null) { status = "Coasting: node creation failed."; return false; }
                    ascentNodePlaced = true;
                    ascentNode.DeltaV = new Vector3d(0, 0, dv);
                    solver.UpdateFlightPlan();
                    ChatLog.Write("rocket", "Ascent circularization node at UT " + ut.ToString("F1", CultureInfo.InvariantCulture));
                }
                else if (command.Phase == AscentPhase.CoastToAp)
                {
                    ascentNode.OnGizmoUpdated(new Vector3d(0, 0, dv), ut);
                    solver.UpdateFlightPlan();
                }
                status += "; circularization node ready (burn execution is separate).";
                return true;
            }
            catch (Exception ex)
            {
                c.mainThrottle = 0; c.pitch = c.yaw = c.roll = 0;
                status = "Ascent stopped: " + ex.Message;
                return false;
            }
        }
    }
}
