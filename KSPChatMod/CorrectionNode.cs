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
        public bool PlanCorrectionNode(CelestialBody target, double targetPeA, out string msg)
        {
            msg = "No active vessel or patched-conic solver.";
            var craft = FlightGlobals.ActiveVessel;
            if (craft == null || craft.orbit == null || craft.patchedConicSolver == null) return false;
            var solver = craft.patchedConicSolver;
            if (target == null || target.orbit == null || target.referenceBody == null ||
                craft.orbit.referenceBody == null || !CorrectionMath.Finite(targetPeA) || targetPeA < 0 ||
                !CorrectionMath.Finite(target.Radius) || target.Radius <= 0 ||
                targetPeA+target.Radius >= target.sphereOfInfluence)
            { msg = "Choose a target and periapsis altitude inside its SOI."; return false; }
            if (craft.orbit.referenceBody == target || craft.LandedOrSplashed)
            { msg = "Correction requires a transfer trajectory outside the target SOI."; return false; }
            if (solver.maneuverNodes == null || solver.maneuverNodes.Count != 0)
            { msg = "Clear existing maneuver nodes before planning a correction."; return false; }
            double now = Planetarium.GetUniversalTime(), entry = double.NaN;
            Orbit current = craft.orbit;
            for (int i = 0; current != null && i < 5; i++, current = current.nextPatch)
                if (current.referenceBody == target && current.StartUT > now)
                { entry = current.StartUT; break; }
            double burn = CorrectionMath.Finite(entry) ? now+(entry-now)/3 : now+600;
            // For a missing encounter, search roughly one target orbit ahead. EjectionMiss
            // samples only patches sharing the target's parent and respects patch end times.
            double horizon = target.orbit.period;
            if (!CorrectionMath.Finite(horizon) || horizon <= 0) horizon = 21600*426;
            double arrival = CorrectionMath.Finite(entry) ? entry : burn+horizon;
            ManeuverNode node = null;
            try
            {
                node = solver.AddManeuverNode(burn);
                if (node == null) { msg = "Could not create a correction node."; return false; }
                CorrectionMath.Prediction predict = delegate(double r, double n, double p,
                    out double pea, out double miss)
                {
                    node.OnGizmoUpdated(new Vector3d(r,n,p), burn);
                    solver.UpdateFlightPlan();
                    pea = double.NaN; miss = double.PositiveInfinity;
                    Orbit patch = node.nextPatch;
                    for (int i = 0; patch != null && i < 5; i++, patch = patch.nextPatch)
                        if (patch.referenceBody == target)
                        { pea = patch.PeA; return; }
                    bool encounter;
                    miss = EjectionMiss(node.nextPatch, target, burn, arrival, out encounter);
                };
                double radial, normal, prograde, periapsis;
                CorrectionMath.Search(predict, targetPeA, out radial, out normal, out prograde, out periapsis);
                // The final trial need not be the best; restore and verify the selected node.
                double ignored;
                predict(radial, normal, prograde, out periapsis, out ignored);
                bool success = CorrectionMath.Finite(periapsis) &&
                    Math.Abs(periapsis-targetPeA) <= CorrectionMath.Tolerance(targetPeA);
                msg = string.Format(CultureInfo.InvariantCulture,
                    "{0} correction: {1:F1} m/s at UT {2:F1}; {3}. Target PeA {4:F1} km{5}.",
                    target.bodyName, node.DeltaV.magnitude, burn,
                    CorrectionMath.Finite(periapsis) ? "predicted PeA "+(periapsis/1000).ToString("F1", CultureInfo.InvariantCulture)+" km" : "no target SOI encounter",
                    targetPeA/1000, success ? " (within tolerance)" : " (best effort; tolerance not reached)");
                ChatLog.Write("rocket", msg);
                return success;
            }
            catch (Exception ex)
            {
                msg = "Correction planning failed: "+ex.Message;
                if (node != null)
                {
                    try { node.RemoveSelf(); solver.UpdateFlightPlan(); }
                    catch (Exception cleanup) { msg += "; node cleanup failed: "+cleanup.Message; }
                }
                return false;
            }
        }
    }
}
