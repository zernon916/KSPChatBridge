using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class NativeFlaps
    {
        readonly List<ModuleControlSurface> surfaces = new List<ModuleControlSurface>();
        readonly FlapSchedule schedule = new FlapSchedule();
        internal NativeFlaps(Vessel vessel)
        {
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                var surface = module as ModuleControlSurface;
                if (surface != null && part.partInfo.title.IndexOf("blade", StringComparison.OrdinalIgnoreCase) < 0
                    && (part.partInfo.title.IndexOf("flap", StringComparison.OrdinalIgnoreCase) >= 0
                        || (surface.deploy && surface.ignorePitch && surface.ignoreRoll && surface.ignoreYaw))) surfaces.Add(surface);
            }
        }
        internal void Tick(Vessel vessel, NativeRecovery recovery, double now, double speed, double stall, double wanted)
        {
            double angle = schedule.Step(now, speed, stall, wanted);
            foreach (var surface in surfaces) if (surface != null && surface.part.vessel == vessel && recovery.CanTrim(surface))
            {
                bool deploy = angle > 0;
                float signed = (float)angle * (surface.deployAngle < 0 ? -1 : 1);
                if (surface.deploy == deploy && (!deploy || Math.Abs(surface.deployAngle - signed) < .01)) continue;
                if (deploy) surface.deployAngle = signed;
                surface.deploy = deploy; recovery.AcceptSurface(surface);
            }
        }
    }
}
