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
        Vessel atmoLandCraft;
        bool atmoLandSampled, atmoLandBurn;
        double atmoLandTime, atmoLandVs, atmoLandDrag, atmoLandThrust, atmoLandGravity;
        double atmoLandPitchError, atmoLandYawError;

        // True means guidance applied, including an unpowered/chute descent.
        // Caller owns mode engagement, engine ignition and staging.
        internal bool AtmoLandStep(Vessel v, FlightCtrlState c, out string status)
        {
            status = "Atmosphere landing needs a vessel and control state.";
            if (c == null) return false;
            c.mainThrottle = 0; c.pitch = c.yaw = c.roll = 0;
            if (v == null || v.mainBody == null || v.ReferenceTransform == null || v.parts == null) return false;
            try
            {
                if (atmoLandCraft != v || v.LandedOrSplashed)
                {
                    atmoLandCraft = v; atmoLandSampled = atmoLandBurn = false; atmoLandDrag = 0;
                }
                if (v.LandedOrSplashed) { status = "Atmosphere landing: touchdown."; return true; }
                double agl = v.radarAltitude;
                if (!AtmoLandMath.Finite(agl) || agl < 0) agl = v.heightFromTerrain;
                double mass = v.GetTotalMass(), radius = v.mainBody.Radius + v.altitude;
                double gravity = v.mainBody.gravParameter / (radius * radius);
                double now = Planetarium.GetUniversalTime(), dt = now - atmoLandTime;
                if (!AtmoLandMath.Finite(agl) || agl < 0 || !AtmoLandMath.Finite(mass) || mass <= 0 ||
                    !AtmoLandMath.Finite(gravity) || gravity <= 0 || !AtmoLandMath.Finite(now) ||
                    !AtmoLandMath.Finite(v.verticalSpeed) || !AtmoLandMath.Finite(v.srfSpeed))
                { atmoLandSampled = false; status = "Atmosphere landing: telemetry unavailable."; return false; }

                Vector3d up = v.upAxis.normalized;
                double alignment = Vector3d.Dot(v.ReferenceTransform.up, up);
                double capacity = 0, actual = 0;
                bool drogues = false, mains = false;
                var chutes = new List<ModuleParachute>();
                foreach (var part in v.parts)
                {
                    if (part == null) continue;
                    foreach (var engine in part.FindModulesImplementing<ModuleEngines>())
                    {
                        if (engine == null || !engine.EngineIgnited || engine.flameout) continue;
                        var multi = part.FindModuleImplementing<MultiModeEngine>();
                        if (multi != null && engine.engineID != (multi.runningPrimary ? multi.primaryEngineID : multi.secondaryEngineID)) continue;
                        // All thrust is measured in kN; vessel mass is tonnes.
                        // Powered landing assumes engines aligned with the control point.
                        actual += Math.Max(0, engine.finalThrust);
                        if (engine.throttleLocked) continue;
                        double available = engine.currentThrottle > .01 && engine.finalThrust > 0
                            ? engine.finalThrust / engine.currentThrottle
                            : engine.GetMaxThrust() * engine.thrustPercentage / 100;
                        if (AtmoLandMath.Finite(available)) capacity += Math.Max(0, available);
                    }
                    foreach (var chute in part.FindModulesImplementing<ModuleParachute>())
                    {
                        if (chute == null || chute.deploymentState != ModuleParachute.deploymentStates.STOWED) continue;
                        chutes.Add(chute);
                        if (AtmoLandDrogue(chute)) drogues = true; else mains = true;
                    }
                }
                double thrustUp = actual / mass * alignment;
                bool sample = atmoLandSampled && dt > 0 && dt <= 1;
                if (sample)
                {
                    // dv/dt = -g + drag + thrust + curvature. Remove engine
                    // acceleration so a burn cannot masquerade as extra drag.
                    double measured = (v.verticalSpeed - atmoLandVs) / dt + (gravity + atmoLandGravity) * .5 -
                        (thrustUp + atmoLandThrust) * .5 - v.horizontalSrfSpeed * v.horizontalSrfSpeed / radius;
                    measured = Math.Max(0, measured);
                    atmoLandDrag += (measured - atmoLandDrag) * (1 - Math.Exp(-dt / .5));
                }
                else atmoLandDrag = 0;
                atmoLandTime = now; atmoLandVs = v.verticalSpeed;
                atmoLandThrust = thrustUp; atmoLandGravity = gravity;
                atmoLandSampled = true;

                var command = AtmoLandMath.Evaluate(agl, v.verticalSpeed, atmoLandDrag,
                    capacity / mass * Math.Max(0, alignment), gravity, v.srfSpeed, drogues, mains, atmoLandBurn);
                if (command.StartBurn && !atmoLandBurn)
                { atmoLandBurn = true; ChatLog.Write("rocket", "Atmosphere landing: final burn at " + agl.ToString("F1", CultureInfo.InvariantCulture) + " m AGL."); }

                // Surface retrograde; avoid turning upside down if the craft
                // briefly rises, or chasing an ill-defined vector at near-zero speed.
                Vector3d desired = v.srfSpeed > .5 && v.verticalSpeed < 0 ? -v.srf_velocity.normalized : up;
                double pe = Vector3d.Dot(desired, -v.ReferenceTransform.forward);
                double ye = Vector3d.Dot(desired, v.ReferenceTransform.right);
                double pr = sample ? (pe - atmoLandPitchError) / dt : 0;
                double yr = sample ? (ye - atmoLandYawError) / dt : 0;
                atmoLandPitchError = pe; atmoLandYawError = ye;
                c.pitch = (float)Math.Max(-1, Math.Min(1, 2 * pe + .6 * pr));
                c.yaw = (float)Math.Max(-1, Math.Min(1, 2 * ye + .6 * yr));
                c.mainThrottle = (float)command.Throttle;

                foreach (var chute in chutes)
                {
                    bool deploy = AtmoLandDrogue(chute) ? command.DeployDrogue : command.DeployMain;
                    if (!deploy || !v.mainBody.atmosphere || v.staticPressurekPa <= 0 ||
                        chute.deploymentSafeState != ModuleParachute.deploymentSafeStates.SAFE) continue;
                    // Deploy() stages/arms the stock module; its own pressure and
                    // configured full-opening altitude still govern the canopy.
                    chute.Deploy();
                    ChatLog.Write("rocket", "Atmosphere landing: staged safe " + (AtmoLandDrogue(chute) ? "drogue." : "main chute."));
                }
                if (agl < 200 && v.ActionGroups != null && !v.ActionGroups[KSPActionGroup.Gear])
                { v.ActionGroups.SetGroup(KSPActionGroup.Gear, true); ChatLog.Write("rocket", "Atmosphere landing: gear down."); }
                status = (atmoLandBurn ? "Final burn" : "Atmospheric descent") + "; drag " +
                    atmoLandDrag.ToString("F1", CultureInfo.InvariantCulture) + " m/s²; throttle " +
                    command.Throttle.ToString("F2", CultureInfo.InvariantCulture);
                if (capacity <= 0) status += "; no throttleable thrust available.";
                return true;
            }
            catch (Exception ex)
            {
                c.mainThrottle = 0; c.pitch = c.yaw = c.roll = 0;
                atmoLandSampled = false;
                status = "Atmosphere landing stopped: " + ex.Message;
                return false;
            }
        }

        static bool AtmoLandDrogue(ModuleParachute chute)
        {
            // Stock exposes no drogue type flag. Recognize the stock part IDs
            // (including radialDrogue); unknown chutes use conservative main limits.
            return chute.part != null && chute.part.partInfo != null &&
                !string.IsNullOrEmpty(chute.part.partInfo.name) &&
                chute.part.partInfo.name.IndexOf("drogue", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
