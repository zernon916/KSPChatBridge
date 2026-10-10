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
            internal Func<bool> Alive;
            internal bool? Desired;
            internal readonly RecoveryGate Gate = new RecoveryGate();
        }
        readonly Dictionary<object, Entry> entries = new Dictionary<object, Entry>();
        internal bool CanTrim(ModuleControlSurface surface)
        { Entry entry; return !entries.TryGetValue(surface, out entry) || !entry.Changed(); }
        internal NativeRecovery(Vessel vessel)
        {
            foreach (KSPActionGroup group in Enum.GetValues(typeof(KSPActionGroup)))
                if (group == KSPActionGroup.Light || group == KSPActionGroup.Brakes
                    || group == KSPActionGroup.SAS || group == KSPActionGroup.RCS || group.ToString().StartsWith("Custom"))
                    AcceptGroup(vessel, group, vessel.ActionGroups[group]);
            foreach (Part part in vessel.parts) foreach (PartResource resource in part.Resources)
            {
                PartResource target = resource; bool enabled = resource.flowState;
                entries[target] = new Entry { Alive = () => target.part != null && target.part.vessel == vessel,
                    Changed = () => target.flowState != enabled, Restore = () => target.flowState = enabled };
            }
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
            ownPending = false;   // the initial baseline is not an actuation
        }
        // Luke 5:56 PM: our own trim / flap / airbrake / action-group actuations must whitelist themselves. Stock surfaces can
        // follow an action group (deploy bound to Brakes) or settle a frame later, so for 3 s after any own actuation a changed
        // control surface is re-snapshotted as ours instead of being flagged as tampering.
        bool ownPending; double ownAt = double.NegativeInfinity; internal const double OwnGraceS = 3;
        internal static bool InOwnGrace(double now, double ownAt) { return now - ownAt >= 0 && now - ownAt < OwnGraceS; }
        internal void AcceptGroup(Vessel vessel, KSPActionGroup group, bool value)
        {
            ownPending = true;
            Entry existing; if (entries.TryGetValue(group, out existing) && existing.Desired == value) return;
            entries[group] = new Entry { Desired = value, Alive = () => true, Changed = () => vessel.ActionGroups[group] != value,
                Restore = () => vessel.ActionGroups.SetGroup(group, value) };
        }
        internal void AcceptSurface(ModuleControlSurface surface)
        {
            ownPending = true;
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
        internal readonly List<string> Noticed = new List<string>();
        internal int Tick(Vessel vessel, double now, bool enabled)
        {
            Noticed.Clear();
            int restored = 0;
            if (ownPending) { ownAt = now; ownPending = false; }
            bool grace = InOwnGrace(now, ownAt);
            foreach (var pair in new List<KeyValuePair<object, Entry>>(entries))
            {
                var entry = pair.Value;
                if (entry.Alive != null ? !entry.Alive() : entry.Module == null || entry.Module.part.vessel != vessel) { entries.Remove(pair.Key); continue; }
                if (!enabled) { entry.Gate.Reset(); continue; }
                var cs = entry.Module as ModuleControlSurface;
                if (grace && cs != null && entry.Changed()) { AcceptSurface(cs); ownPending = false; continue; }   // our own actuation settling
                bool due = entry.Gate.Tick(entry.Changed(), now);
                if (entry.Gate.JustNoticed) Noticed.Add(entry.Module != null && entry.Module.part != null && entry.Module.part.partInfo != null ? entry.Module.part.partInfo.title : pair.Key.ToString());
                if (due) { entry.Restore(); entry.Gate.Reset(); restored++; }
            }
            return restored;
        }
    }
}
