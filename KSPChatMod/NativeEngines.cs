using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class NativeEngines
    {
        readonly EngineRestart<ModuleEngines> restart = new EngineRestart<ModuleEngines>();
        static bool Selected(ModuleEngines engine)
        {
            var multi = engine.part.FindModuleImplementing<MultiModeEngine>();
            return multi == null || engine.engineID == (multi.runningPrimary ? multi.primaryEngineID : multi.secondaryEngineID);
        }
        static bool Eligible(ModuleEngines engine, Vessel vessel)
        {
            return engine != null && engine.part.vessel == vessel && Selected(engine)
                && EngineRestart<ModuleEngines>.Eligible(engine.EngineIgnited, engine.allowRestart, engine.part.inverseStage, vessel.currentStage);
        }
        internal void Request(Vessel vessel, double now)
        {
            var engines = new List<ModuleEngines>();
            foreach (Part part in vessel.parts) foreach (var engine in part.FindModulesImplementing<ModuleEngines>())
                if (Eligible(engine, vessel)) engines.Add(engine);
            if (restart.Schedule(engines, now)) { ChatWindow.Notice("Local pilot: engine restart pending (4 seconds)."); NativeFlightController.CrewEmergency("flameout", engines.Count > 0 && engines[0].part.partInfo != null ? engines[0].part.partInfo.title : "engine"); }
        }
        internal void Tick(Vessel vessel, double now)
        {
            int count = restart.Tick(now, engine => Eligible(engine, vessel), engine => engine.Activate());
            if (count > 0) ChatWindow.Notice("Local pilot: restart requested for " + count + " engine(s).");
        }
        internal void Cancel() { restart.Cancel(); }
        internal static void Takeoff(Vessel vessel)
        {
            foreach (Part part in vessel.parts) foreach (var engine in part.FindModulesImplementing<ModuleEngines>())
                if (Selected(engine) && !engine.EngineIgnited) engine.Activate();
        }
    }
}
