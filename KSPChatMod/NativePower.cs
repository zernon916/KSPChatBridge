using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    // Main-thread stock power recovery. Discovery is refreshed on vessel/part changes.
    internal sealed class NativePower
    {
        readonly List<ModuleDeployableSolarPanel> panels = new List<ModuleDeployableSolarPanel>();
        readonly List<BaseConverter> cells = new List<BaseConverter>();
        readonly List<ModuleGenerator> generators = new List<ModuleGenerator>();
        readonly List<ModuleReactionWheel> wheels = new List<ModuleReactionWheel>();
        double nextTick, nextNotice;
        internal NativePower(Vessel vessel)
        {
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                var panel = module as ModuleDeployableSolarPanel; if (panel != null) panels.Add(panel);
                var wheel = module as ModuleReactionWheel; if (wheel != null) wheels.Add(wheel);
                var cell = module as BaseConverter;
                if (cell != null && cell.outputList.Exists(r => r.ResourceName == "ElectricCharge" && r.Ratio > 0)) cells.Add(cell);
                var generator = module as ModuleGenerator;
                if (generator != null && generator.resHandler.outputResources.Exists(r => r.name == "ElectricCharge")) generators.Add(generator);
            }
        }
        internal void Tick(Vessel vessel, double now)
        {
            if (now < nextTick || vessel.packed) return;
            nextTick = now + 2;
            double charge = NativePropulsion.Charge(vessel);
            if (double.IsNaN(charge) || charge >= .25) return;
            int changed = 0;
            if (vessel.ActionGroups[KSPActionGroup.Light]) { vessel.ActionGroups.SetGroup(KSPActionGroup.Light, false); changed++; }
            bool retainedWheel = false;
            foreach (var wheel in wheels) if (wheel != null && wheel.wheelState == ModuleReactionWheel.WheelState.Active)
            {
                if (!retainedWheel) { retainedWheel = true; continue; }
                wheel.OnToggle(); changed++;
            }
            if (FlightPolicy.SafeSolar(!vessel.LandedOrSplashed && vessel.atmDensity > 0, vessel.srfSpeed, vessel.dynamicPressurekPa * 1000))
                foreach (var panel in panels) if (panel != null && panel.deployState == ModuleDeployablePart.DeployState.RETRACTED)
                { panel.Extend(); changed++; }
            foreach (var cell in cells) if (cell != null && !cell.IsActivated) { cell.StartResourceConverter(); changed++; }
            foreach (var generator in generators) if (generator != null && !generator.generatorIsActive) { generator.Activate(); changed++; }
            if (changed > 0 && now >= nextNotice)
            { nextNotice = now + 90; ChatWindow.Notice("Low electric charge: power sources enabled; spare wheels/lights off."); }
        }
    }
}
