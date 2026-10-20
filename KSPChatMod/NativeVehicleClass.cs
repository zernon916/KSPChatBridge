// RS1: vessel scan for VehicleClass (wings, rotors, props, wheels, skis, motors, engines) + the per-craft ROVER switch.
using System;
using System.Collections.Generic;
using System.Globalization;
using Expansions.Serenity;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        VehicleKind classKind = VehicleKind.Rocket; string classReason = ""; Guid classFor; int classParts = -1;

        void ScanClass()
        {
            if (vessel == null || (vessel.id == classFor && vessel.parts.Count == classParts)) return;
            classFor = vessel.id; classParts = vessel.parts.Count;
            var c = new VehicleCounts();
            foreach (Part p in vessel.parts)
            {
                string name = p.partInfo != null ? p.partInfo.name : p.name, title = p.partInfo != null ? p.partInfo.title : "", tags = p.partInfo != null ? p.partInfo.tags : "";
                bool rotor = false, propModule = false, lift = false, ctrl = false, wheel = false, motor = false, engine = false;
                foreach (PartModule m in p.Modules)
                {
                    if (m is ModuleRoboticServoRotor) rotor = true;
                    else if (m is ModuleControlSurface) ctrl = true;
                    else if (m is ModuleLiftingSurface) lift = true;
                    var wb = m as ModuleWheelBase; if (wb != null && wb.wheelType != WheelType.LEG) wheel = true;
                    if (m is ModuleWheels.ModuleWheelMotor) motor = true;
                    if (m is ModuleEngines) engine = true;
                    if (VehicleClass.IsPropModule(m.moduleName)) propModule = true;
                }
                bool onRotor = p.parent != null && p.parent.FindModuleImplementing<ModuleRoboticServoRotor>() != null;
                bool blade = (lift || ctrl) && (onRotor || VehicleClass.IsPropName(name, title));
                if (rotor) c.Rotors++;
                if (blade || propModule) c.Props++;
                else if (lift) c.Wings++;
                if (VehicleClass.IsSki(name, title, tags)) c.Skis++;
                else if (wheel) c.Wheels++;
                if (motor) c.Motors++;
                if (engine && !propModule) c.Engines++;
            }
            classKind = VehicleClass.Classify(c, out classReason);
            ChatLog.Write("class", VehicleClass.LogLine(vessel.vesselName, classKind, classReason) + " | ROVER switch " + (RoverMode ? "ON" : "OFF"));
        }

        /// <summary>RS1: classifier result for the active vessel (hint; the ROVER switch decides the tablet pages).</summary>
        internal static VehicleKind CurrentClass { get { var i = instance; if (i == null || i.vessel == null) return VehicleKind.Rocket; i.ScanClass(); return i.classKind; } }
        internal static string CurrentClassReason { get { var i = instance; if (i == null || i.vessel == null) return ""; i.ScanClass(); return i.classReason; } }
        /// <summary>RS1: the per-craft ROVER switch (craft_overrides.vehicle_rover); unset/off = plane pages, as before.</summary>
        internal static bool RoverMode { get { return CraftReady && CraftOverrideGet(CraftOverrides.RoverKey) >= .5; } }
        /// <summary>RS1: ROVER home card on the tablet.</summary>
        internal static List<string> RoverStatusLines()
        {
            var r = new List<string>(); var i = instance; if (!CraftReady) { r.Add("No active vessel."); return r; }
            var v = i.vessel; double amt, max; i.Ec(out amt, out max);
            r.Add(v.vesselName + " - ROVER pages (detected " + VehicleClass.Name(CurrentClass) + ")");
            r.Add("Autopilot: " + i.mode + (i.scienceTouring ? " - science tour" : i.touring ? " - building tour" : ""));
            r.Add("Speed " + v.srfSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s, heading " + FlightGlobals.ship_heading.ToString("0", CultureInfo.InvariantCulture) + " deg, " + v.situation);
            r.Add("Battery " + (max > 0 ? (100 * amt / max).ToString("0", CultureInfo.InvariantCulture) + "%" : "none"));
            try { r.Add("Here: " + v.mainBody.bodyName + " / " + i.ScienceBiomeHere()); } catch (Exception) { }
            return r;
        }

        /// <summary>RS2: the biome name stock science uses here - the KSC mini-biome (vessel.landedAt) when landed on one, else the map biome.</summary>
        string ScienceBiomeHere()
        {
            if (vessel == null || vessel.mainBody == null) return "";
            if (!string.IsNullOrEmpty(vessel.landedAt)) return Vessel.GetLandedAtString(vessel.landedAt);
            return ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude);
        }

        internal static void SetRoverMode(bool on)
        {
            if (!CraftReady) return;
            CraftOverrideSet(CraftOverrides.RoverKey, on ? 1 : double.NaN);
            ChatLog.Write("class", CraftName + ": ROVER switch " + (on ? "ON - rover pages" : "OFF - plane pages") + " (detected " + VehicleClass.Name(CurrentClass) + ")");
        }
    }
}