using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    // Main-thread stock power recovery. Discovery is refreshed on vessel/part changes.
    // QWEN T2: deployable solar panels follow the same safety rule as the science antenna
    // (NativeFlightController.DeployableUnsafe): a panel never goes out shielded in a closed bay, hot, or in an
    // airstream over 1 kPa, and only the panels AICS opened are stowed again.
    internal sealed class NativePower
    {
        readonly List<ModuleDeployableSolarPanel> panels = new List<ModuleDeployableSolarPanel>();
        readonly List<ModuleDeployableSolarPanel> extended = new List<ModuleDeployableSolarPanel>();   // QWEN T2: panels AICS deployed - the only ones it may retract
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
        static bool InputsOnBoard(Vessel vessel, BaseConverter cell)
        {
            foreach (var r in cell.inputList) { var def = PartResourceLibrary.Instance.GetDefinition(r.ResourceName); double a = 0, m = 0; if (def != null) vessel.GetConnectedResourceTotals(def.id, out a, out m); if (!(a > 1e-6)) return false; }
            return true;
        }
        internal void Tick(Vessel vessel, double now)
        {
            if (now < nextTick || vessel.packed) return;
            nextTick = now + 2;
            PanelSafetyTick(vessel);   // QWEN T2: before the charge gate - panels already out must be stowed at a healthy charge too
            double charge = NativePropulsion.Charge(vessel);
            if (double.IsNaN(charge) || charge >= .25) return;
            int changed = 0;
            if (vessel.ActionGroups[KSPActionGroup.Light]) { NativeFlightController.SetGroup(vessel, KSPActionGroup.Light, false); changed++; }
            bool retainedWheel = false;
            foreach (var wheel in wheels) if (wheel != null && wheel.wheelState == ModuleReactionWheel.WheelState.Active)
            {
                if (!retainedWheel) { retainedWheel = true; continue; }
                wheel.OnToggle(); changed++;
            }
            string blocked = null;
            if (FlightPolicy.SafeSolar(!vessel.LandedOrSplashed && vessel.atmDensity > 0, vessel.srfSpeed, vessel.dynamicPressurekPa * 1000))
                foreach (var panel in panels) if (panel != null && panel.deployState == ModuleDeployablePart.DeployState.RETRACTED)
                {
                    // QWEN T2: the science-antenna rule - shielded in a closed bay, hot, or an airstream over 1 kPa keeps the panel stowed.
                    string why = NativeFlightController.DeployableUnsafe(panel.part, panel.isBreakable, vessel.dynamicPressurekPa, !vessel.LandedOrSplashed && vessel.atmDensity > 0);
                    if (why != null) { if (blocked == null) blocked = panel.part.partInfo.title + ": " + why; continue; }
                    panel.Extend(); extended.Add(panel); changed++;
                    ChatLog.Write("power", "EXTEND " + panel.part.partInfo.title + " (low charge, q " + Q(vessel) + " kPa is safe; stowed again if the airstream turns)");
                }
            if (blocked != null) ChatLog.Write("power", "solar panel not extended - " + blocked + " (q " + Q(vessel) + " kPa)");
            foreach (var cell in cells) if (cell != null && !cell.IsActivated && InputsOnBoard(vessel, cell)) { cell.StartResourceConverter(); changed++; }   // RS4: only converters that have their inputs
            foreach (var generator in generators) if (generator != null && !generator.generatorIsActive) { generator.Activate(); changed++; }
            if (changed > 0 && now >= nextNotice)
            { nextNotice = now + 90; ChatWindow.Notice("Low electric charge: power sources enabled; spare wheels/lights off."); }
        }
        static string Q(Vessel vessel) { return vessel.dynamicPressurekPa.ToString("0.0", CultureInfo.InvariantCulture); }
        /// <summary>QWEN T2: stow the panels AICS deployed as soon as the shared deployable rule turns unsafe (an
        /// airstream over 1 kPa snaps them off, reentry heat cooks them). Panels the pilot opened are never touched,
        /// a panel inside a closed bay is shielded and stays out, and broken / destroyed / handed-back panels just
        /// drop off the list.</summary>
        void PanelSafetyTick(Vessel vessel)
        {
            if (extended.Count == 0) return;
            bool inAtmo = !vessel.LandedOrSplashed && vessel.atmDensity > 0;
            string why = null, stowed = null;
            for (int i = extended.Count - 1; i >= 0; i--)
            {
                ModuleDeployableSolarPanel panel = extended[i];
                if (panel == null || panel.part == null || panel.vessel != vessel || panel.deployState == ModuleDeployablePart.DeployState.RETRACTED
                    || panel.deployState == ModuleDeployablePart.DeployState.RETRACTING || panel.deployState == ModuleDeployablePart.DeployState.BROKEN) { extended.RemoveAt(i); continue; }
                string w = NativeFlightController.DeployableUnsafe(panel.part, panel.isBreakable, vessel.dynamicPressurekPa, inAtmo);
                if (w == null || panel.part.ShieldedFromAirstream) continue;   // safe, or in a closed bay the airstream cannot reach
                extended.RemoveAt(i);
                if (!panel.retractable) { ChatLog.Write("power", panel.part.partInfo.title + " unsafe (" + w + ") but not retractable - left deployed"); continue; }
                panel.Retract(); why = w;
                stowed = stowed == null ? panel.part.partInfo.title : stowed + ", " + panel.part.partInfo.title;
            }
            if (stowed == null) return;
            ChatLog.Write("power", "RETRACT " + stowed + " (" + why + ", q " + Q(vessel) + " kPa)");
            ChatWindow.Notice(why == "airstream"
                ? "Solar panel retracted: airstream q " + Q(vessel) + " kPa is over the " + NativeFlightController.AntennaMaxQ.ToString("0.0", CultureInfo.InvariantCulture) + " kPa it survives - deployable lost."
                : "Solar panel retracted: " + why + " - stowed the panel(s) the helper opened.");
        }
    }
}
