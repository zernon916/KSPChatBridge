using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class NativeReversers
    {
        sealed class Reverser
        {
            internal PartModule Module;
            internal Func<bool?> Read;
            internal Func<bool, bool> Set;
            internal readonly RecoveryGate Gate = new RecoveryGate();
        }
        readonly List<Reverser> items = new List<Reverser>();
        internal NativeReversers(Vessel vessel)
        {
            foreach (Part part in vessel.parts)
            {
                bool enginePart = false;
                foreach (PartModule module in part.Modules) if (module is ModuleEngines) enginePart = true;
                if (!enginePart) continue;
                foreach (PartModule module in part.Modules)
                {
                    var multi = module as MultiModeEngine;
                    if (multi != null)
                    {
                        bool? forward = ReversePolicy.ForwardPrimary(multi.primaryEngineID + " " + multi.primaryEngineModeDisplayName,
                            multi.secondaryEngineID + " " + multi.secondaryEngineModeDisplayName);
                        if (forward.HasValue) items.Add(new Reverser { Module = module,
                            Read = () => multi.runningPrimary != forward.Value,
                            Set = reverse => { if ((multi.runningPrimary != forward.Value) != reverse) multi.ToggleMode(); return (multi.runningPrimary != forward.Value) == reverse; } });
                        continue;
                    }
                    bool named = false;
                    foreach (BaseEvent ev in module.Events) if (Direction(ev).HasValue) named = true;
                    if (!named) continue;
                    PartModule target = module;
                    items.Add(new Reverser { Module = target, Read = () => State(target), Set = reverse =>
                    {
                        if (State(target) == reverse) return true;
                        foreach (BaseEvent ev in target.Events)
                            if (ev.active && Direction(ev) == reverse) { ev.Invoke(); return State(target) == reverse; }
                        return false;
                    } });
                }
            }
        }
        static bool? Direction(BaseEvent ev)
        { return ReversePolicy.EventDirection(ev.guiName) ?? ReversePolicy.EventDirection(ev.name); }
        static bool? State(PartModule module)
        {
            bool reverse = false, forward = false;
            foreach (BaseEvent ev in module.Events) if (ev.active && ev.guiActive)
            { bool? direction = Direction(ev); reverse |= direction == true; forward |= direction == false; }
            return reverse == forward ? (bool?)null : forward;
        }
        internal bool Set(Vessel vessel, bool reverse)
        {
            if (reverse) foreach (Part part in vessel.parts)
            {
                bool activeEngine = false;
                foreach (PartModule module in part.Modules) { var engine = module as ModuleEngines; if (engine != null && engine.EngineIgnited) activeEngine = true; }
                if (activeEngine && !items.Exists(item => item.Module != null && item.Module.part == part)) return false;
            }
            bool any = false, all = true;
            foreach (var item in items) if (item.Module != null && item.Module.part.vessel == vessel)
            { bool changed = item.Set(reverse); any |= changed; all &= changed; item.Gate.Reset(); }
            if (reverse && !all) Set(vessel, false);
            return any && (!reverse || all);
        }
        internal readonly List<string> Noticed = new List<string>();
        internal int Recover(Vessel vessel, double now, bool enabled)
        {
            int count = 0; Noticed.Clear();
            foreach (var item in items)
            {
                if (item.Module == null || item.Module.part.vessel != vessel) continue;
                if (!enabled) { item.Gate.Reset(); continue; }
                bool due = item.Gate.Tick(item.Read() == true, now);
                if (item.Gate.JustNoticed) Noticed.Add(item.Module.part.partInfo != null ? item.Module.part.partInfo.title + " thrust reverser" : "thrust reverser");
                if (due && item.Set(false)) { count++; item.Gate.Reset(); }
            }
            return count;
        }
    }
}
