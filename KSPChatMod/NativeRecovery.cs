using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    // Tracks configuration only. Destroyed modules are removed; no physics repair is attempted.
    internal sealed class NativeRecovery
    {
        sealed class Entry
        {
            internal PartModule Module;
            internal Func<bool> Changed;
            internal Action Restore;
            internal readonly RecoveryGate Gate = new RecoveryGate();
        }
        readonly Dictionary<PartModule, Entry> entries = new Dictionary<PartModule, Entry>();
        internal bool CanTrim(ModuleControlSurface surface)
        { Entry entry; return !entries.TryGetValue(surface, out entry) || !entry.Changed(); }
        internal NativeRecovery(Vessel vessel)
        {
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                var surface = module as ModuleControlSurface;
                if (surface != null && part.partInfo.title.IndexOf("blade", StringComparison.OrdinalIgnoreCase) < 0) AcceptSurface(surface);
                var intake = module as ModuleResourceIntake;
                if (intake != null)
                {
                    bool opened = intake.intakeEnabled;
                    entries[module] = new Entry { Module = module, Changed = () => intake.intakeEnabled != opened,
                        Restore = () => { if (opened) intake.Activate(); else intake.Deactivate(); } };
                }
                var engine = module as ModuleEngines;
                if (engine != null)
                {
                    float limit = engine.thrustPercentage;
                    entries[module] = new Entry { Module = module, Changed = () => Math.Abs(engine.thrustPercentage - limit) > .01,
                        Restore = () => engine.thrustPercentage = limit };
                }
                var multi = module as MultiModeEngine;
                if (multi != null)
                {
                    bool? forwardPrimary = ReversePolicy.ForwardPrimary(multi.primaryEngineID + " " + multi.primaryEngineModeDisplayName,
                        multi.secondaryEngineID + " " + multi.secondaryEngineModeDisplayName);
                    if (forwardPrimary.HasValue) entries[module] = new Entry { Module = module,
                        Changed = () => multi.runningPrimary != forwardPrimary.Value,
                        Restore = () => { if (multi.runningPrimary != forwardPrimary.Value) multi.ToggleMode(); } };
                }
            }
        }
        internal void AcceptSurface(ModuleControlSurface surface)
        {
            float angle = surface.deployAngle, authority = surface.authorityLimiter;
            bool deploy = surface.deploy, invert = surface.deployInvert, partInvert = surface.partDeployInvert,
                pitch = surface.ignorePitch, roll = surface.ignoreRoll, yaw = surface.ignoreYaw;
            entries[surface] = new Entry { Module = surface,
                Changed = () => Math.Abs(surface.deployAngle - angle) > .01 || Math.Abs(surface.authorityLimiter - authority) > .01
                    || surface.deploy != deploy || surface.deployInvert != invert || surface.partDeployInvert != partInvert
                    || surface.ignorePitch != pitch || surface.ignoreRoll != roll || surface.ignoreYaw != yaw,
                Restore = () => { surface.deployAngle = angle; surface.authorityLimiter = authority; surface.deploy = deploy;
                    surface.deployInvert = invert; surface.partDeployInvert = partInvert;
                    surface.ignorePitch = pitch; surface.ignoreRoll = roll; surface.ignoreYaw = yaw; } };
        }
        internal int Tick(Vessel vessel, double now, bool enabled)
        {
            int restored = 0;
            foreach (var pair in new List<KeyValuePair<PartModule, Entry>>(entries))
            {
                var entry = pair.Value;
                if (entry.Module == null || entry.Module.part.vessel != vessel) { entries.Remove(pair.Key); continue; }
                if (!enabled) { entry.Gate.Reset(); continue; }
                if (entry.Gate.Tick(entry.Changed(), now)) { entry.Restore(); entry.Gate.Reset(); restored++; }
            }
            return restored;
        }
    }
}
