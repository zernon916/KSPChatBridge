// RS4 game side: transmit policy (CRAFT rover_transmit; Science-Full-Transmit / ScienceRelay detected and logged), kept data moved into a
// science container, scientist / lab reset of used one-shot experiments, the charging ladder (stop, solar, EC converters that have their
// inputs, wait, optional physics warp up to x4) with a fuel reserve for the drive home, and AutomatedScienceSampler (KerboKatz) coexistence.
// All mod integrations by reflection (soft dependencies).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        bool roverCharging; float roverPowerNext, roverChargeNoticeAt; readonly List<BaseConverter> roverStartedConv = new List<BaseConverter>(); readonly List<ModuleGenerator> roverStartedGen = new List<ModuleGenerator>();
        double roverEcPerM = double.NaN, roverLastEc = double.NaN, roverHomeLat = double.NaN, roverHomeLon = double.NaN; Vector3d roverLastPos; string roverPolicyLogged;
        readonly HashSet<string> roverKeptLogged = new HashSet<string>();
        static bool modsLooked, sftInstalled, relayInstalled; static Type assType;
        object assPausedSettings; FieldInfo assRunField; Guid assPausedFor; string assLogged;

        static void LookForMods()
        {
            if (modsLooked) return; modsLooked = true;
            try { sftInstalled = System.IO.Directory.Exists(System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/Science-Full-Transmit")); } catch (Exception) { }
            foreach (var a in AssemblyLoader.loadedAssemblies)
            {
                if (a.name == "ScienceRelay") relayInstalled = true;
                if (a.name == "AutomatedScienceSampler") try { assType = a.assembly.GetType("KerboKatz.ASS.AutomatedScienceSampler"); } catch (Exception) { }
            }
            ChatLog.Write("science", "[rover] mods: Science-Full-Transmit " + (sftInstalled ? "yes" : "no") + ", ScienceRelay " + (relayInstalled ? "yes" : "no") + ", AutomatedScienceSampler " + (assType != null ? "yes" : "no"));
        }

        static int RoverChoice(string key, int max) { double v = CraftOverrides.L(key); if (double.IsNaN(v)) v = CraftOverrides.Find(key).Default; return Math.Max(0, Math.Min(max, (int)Math.Round(v))); }

        string RoverSciencePolicyText()
        {
            LookForMods();
            return RoverPowerPolicy.PolicyText(RoverChoice(CraftOverrides.RoverTransmit, 3), sftInstalled, relayInstalled) + " | science by " + (RoverPowerPolicy.WeRunScience(RoverAssMode(false)) ? "this autopilot" : "other mods");
        }

        bool CommLinked() { try { return vessel.connection == null || vessel.connection.IsConnected; } catch (Exception) { return true; } }

        /// <summary>RS4: TransmitAll asks per experiment in rover mode; false = keep on board (moved into a container when there is one).</summary>
        bool RoverTransmitOk(ModuleScienceExperiment e, ScienceData[] data)
        {
            int choice = RoverChoice(CraftOverrides.RoverTransmit, 3); bool ok = true; double frac = 1;
            foreach (var d in data) { frac = d.baseTransmitValue > 0 ? d.baseTransmitValue : e.xmitDataScalar; ok &= RoverPowerPolicy.ShouldTransmit(choice, frac, CommLinked()); }
            if (ok) return true;
            string key = e.experimentID + "|" + (data.Length > 0 ? data[0].subjectID : "");
            if (roverKeptLogged.Add(key)) ChatLog.Write("science", "[science] " + e.experimentID + ": KEPT on board (" + RoverPowerPolicy.TransmitNames[choice] + ", transmit " + (100 * frac).ToString("0", CultureInfo.InvariantCulture) + "%" + (CommLinked() ? "" : ", no CommNet link") + ")");
            RoverStoreInContainer(e);
            return false;
        }

        void RoverStoreInContainer(ModuleScienceExperiment e)
        {
            var data = SciData(e); if (data == null || data.Length == 0) return;
            foreach (var ct in vessel.FindPartModulesImplementing<ModuleScienceContainer>())
            {
                foreach (var d in data)
                    try { if (!ct.HasData(d) && ct.AddData(d)) { SciDump(e, d); ChatLog.Write("science", "[science] " + e.experimentID + ": stored in " + ct.part.partInfo.title); } } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] container store: " + ex.Message); }
                return;
            }
        }

        /// <summary>RS4: after a stop - used one-shot experiments reset by a scientist aboard (EVA emulated) or a crewed lab, once their data is safe.</summary>
        void RoverResetExperiments()
        {
            bool sci = HasScientist(); ModuleScienceLab lab = null; foreach (var l in vessel.FindPartModulesImplementing<ModuleScienceLab>()) { lab = l; break; }
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                if (SciCount(e) > 0) RoverStoreInContainer(e);
                string act = RoverPowerPolicy.ResetAction(e.rerunnable, e.Inoperable, SciCount(e) == 0, sci, lab != null && sci);
                if (act == null) continue;
                try
                {
                    if (act == "lab clean") { var m = lab.GetType().GetMethod("CleanModulesEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (m != null) m.Invoke(lab, null); else act = "scientist reset"; }
                    if (act == "scientist reset" && e.Inoperable) { e.Inoperable = false; e.Deployed = false; try { e.ResetExperiment(); } catch (Exception) { } }
                }
                catch (Exception ex) { act += " failed: " + ex.Message; }
                ChatLog.Write("science", "[science] " + e.experimentID + " (" + e.part.partInfo.title + "): " + act);
            }
        }

        /// <summary>RS4: AutomatedScienceSampler state for this craft. apply = pause / restore it according to the CRAFT choice.</summary>
        AssMode RoverAssMode(bool apply)
        {
            LookForMods(); int by = RoverChoice(CraftOverrides.RoverScienceBy, 1);
            if (assType == null) return RoverPowerPolicy.Ass(false, by, false);
            object cs = null; FieldInfo rf = null;
            try
            {
                var inst = UnityEngine.Object.FindObjectOfType(assType); var cf = assType.GetField("craftSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                cs = inst != null && cf != null ? cf.GetValue(inst) : null; rf = cs != null ? cs.GetType().GetField("runAutoScience", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] ASS reflection: " + ex.Message); }
            var mode = RoverPowerPolicy.Ass(true, by, rf != null && rf.FieldType == typeof(bool));
            if (apply)
            {
                try
                {
                    if (mode == AssMode.OursAssPaused && (bool)rf.GetValue(cs)) { rf.SetValue(cs, false); assPausedSettings = cs; assRunField = rf; assPausedFor = vessel.id; ChatLog.Write("science", "[rover] AutomatedScienceSampler paused for " + vessel.vesselName + " (our autopilot runs the science)"); }
                    if (mode == AssMode.LeftToOther) RestoreAss();
                }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] ASS pause: " + ex.Message); }
                string txt = mode == AssMode.OursRunFirst ? "ASS active, cannot be paused in this version - we run each experiment first" : mode == AssMode.LeftToOther ? "ASS active - science left to it (we drive and transmit)" : mode == AssMode.OursAssPaused ? "ASS paused for this craft" : "";
                if (txt.Length > 0 && txt != assLogged) { assLogged = txt; ChatLog.Write("science", "[rover] " + txt); }
            }
            return mode;
        }
        void RestoreAss()
        {
            if (assPausedSettings == null) return;
            try { assRunField.SetValue(assPausedSettings, true); ChatLog.Write("science", "[rover] AutomatedScienceSampler auto science restored"); } catch (Exception) { }
            assPausedSettings = null;
        }

        /// <summary>RS4: 1 Hz from RoverTourTick - ASS coexistence, EC-per-metre learning and the charging ladder (rover pages only).</summary>
        void RoverScienceTick()
        {
            float now = Time.realtimeSinceStartup; if (now < roverPowerNext || vessel == null) return; roverPowerNext = now + 1;
            if (assPausedSettings != null && (!RoverMode || vessel.id != assPausedFor)) RestoreAss();
            if (!RoverMode) { if (roverCharging) RoverChargeStop("rover pages off"); return; }
            try
            {
                RoverAssMode(true);
                string pol = RoverSciencePolicyText(); if (pol != roverPolicyLogged) { roverPolicyLogged = pol; ChatLog.Write("science", "[rover] effective policy: " + pol); }
                bool driving = mode == "rover" || scienceTouring;
                double amt, max; Ec(out amt, out max); double pct = max > 0 ? 100 * amt / max : double.NaN;
                if (driving && !roverCharging && vessel.srfSpeed > 1 && !double.IsNaN(roverLastEc))
                {
                    double dm = (vessel.CoMD - roverLastPos).magnitude, de = roverLastEc - amt;
                    if (dm > 1 && de > 0) { double k = de / dm; roverEcPerM = double.IsNaN(roverEcPerM) ? k : .9 * roverEcPerM + .1 * k; }
                }
                roverLastEc = amt; roverLastPos = vessel.CoMD;
                if (double.IsNaN(roverHomeLat) || !driving) { roverHomeLat = vessel.latitude; roverHomeLon = vessel.longitude; }
                if (!driving && !roverCharging) return;
                bool was = roverCharging;
                roverCharging = RoverPowerPolicy.Charging(was, pct, RoverSetting("rover_low_ec_pct"), RoverSetting("rover_resume_ec_pct"));
                if (roverCharging && !was) RoverChargeStart(pct);
                else if (!roverCharging && was) RoverChargeStop("battery " + pct.ToString("0", CultureInfo.InvariantCulture) + "%");
                else if (roverCharging) RoverReserveCheck();
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] rover power: " + ex.Message); }
        }

        double HomeMetres()
        {
            if (vessel.mainBody.bodyName == "Kerbin") { double mid = (KscRunway.Lon09 + KscRunway.Lon27) * .5; return NavigationMath.Distance(vessel.latitude, vessel.longitude, KscRunway.LatAt(mid), mid, vessel.mainBody.Radius); }
            return double.IsNaN(roverHomeLat) ? 0 : NavigationMath.Distance(vessel.latitude, vessel.longitude, roverHomeLat, roverHomeLon, vessel.mainBody.Radius);
        }

        bool ConverterUsable(BaseConverter cv, out double fuel, out double fuelPerEc)
        {
            fuel = double.PositiveInfinity; fuelPerEc = 0; var outs = new List<string>(); var ins = new List<double>(); double ecOut = 0, inRate = 0;
            foreach (var r in cv.outputList) { outs.Add(r.ResourceName); if (r.ResourceName == "ElectricCharge") ecOut += r.Ratio; }
            foreach (var r in cv.inputList)
            {
                var def = PartResourceLibrary.Instance.GetDefinition(r.ResourceName); double a = 0, m = 0; if (def != null) vessel.GetConnectedResourceTotals(def.id, out a, out m);
                ins.Add(a); if (r.ResourceName != "ElectricCharge") { fuel = Math.Min(fuel, a); inRate += r.Ratio; }
            }
            if (ecOut > 0) fuelPerEc = inRate / ecOut; if (double.IsInfinity(fuel)) fuel = 0;
            return RoverPowerPolicy.ConverterOk(outs, ins);
        }

        void RoverChargeStart(double pct)
        {
            SetGroup(vessel, KSPActionGroup.Brakes, true);
            var panels = vessel.FindPartModulesImplementing<ModuleDeployableSolarPanel>(); int solar = 0, conv = 0;
            foreach (var p in panels)
                if (p.deployState == ModuleDeployablePart.DeployState.RETRACTED && DeployableUnsafe(p.part, p.isBreakable, vessel.dynamicPressurekPa, false) == null) { p.Extend(); solar++; }
            double reserve = RoverSetting("rover_reserve_pct");
            foreach (var cv in vessel.FindPartModulesImplementing<BaseConverter>())
            {
                double fuel, fpe; if (cv.IsActivated || !ConverterUsable(cv, out fuel, out fpe)) continue;
                double need = RoverPowerPolicy.ReserveNeed(HomeMetres(), roverEcPerM, fpe, reserve);
                if (!RoverPowerPolicy.MayBurnFuel(fuel, need)) { ChatLog.Write("power", "[rover] " + cv.part.partInfo.title + " not started: fuel " + fuel.ToString("0.0", CultureInfo.InvariantCulture) + " kept for the drive home (need " + need.ToString("0.0", CultureInfo.InvariantCulture) + ")"); continue; }
                cv.StartResourceConverter(); roverStartedConv.Add(cv); conv++;
            }
            foreach (var g in vessel.FindPartModulesImplementing<ModuleGenerator>())
            {
                if (g.generatorIsActive || g.isAlwaysActive || !g.resHandler.outputResources.Exists(r => r.name == "ElectricCharge")) continue;
                bool inputs = true; foreach (var r in g.resHandler.inputResources) { var def = PartResourceLibrary.Instance.GetDefinition(r.name); double a = 0, m = 0; if (def != null) vessel.GetConnectedResourceTotals(def.id, out a, out m); inputs &= a > 1e-6; }
                if (!inputs) continue; g.Activate(); roverStartedGen.Add(g); conv++;
            }
            int warp = RoverChoice(CraftOverrides.RoverChargeWarp, 3);
            var ladder = RoverPowerPolicy.Ladder(panels.Count > 0, conv > 0, warp);
            ChatLog.Write("power", "[rover] battery " + pct.ToString("0", CultureInfo.InvariantCulture) + "%: charging - " + string.Join(" > ", ladder.ToArray()) + " (solar out " + solar + ", sources started " + conv + ")");
            if (warp > 0 && vessel.Landed && vessel.srfSpeed < .5)
                try { TimeWarp.fetch.Mode = TimeWarp.Modes.LOW; TimeWarp.SetRate(RoverPowerPolicy.WarpIndex(warp), true); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] rover warp: " + ex.Message); }
            if (Time.realtimeSinceStartup >= roverChargeNoticeAt) { roverChargeNoticeAt = Time.realtimeSinceStartup + 120; ChatWindow.Notice(Voice().Key + ": Battery low - parked to charge" + (panels.Count == 0 && conv == 0 ? " (no solar panels or generators: waiting)" : "") + "."); }
        }

        void RoverReserveCheck()
        {
            double reserve = RoverSetting("rover_reserve_pct");
            for (int i = roverStartedConv.Count - 1; i >= 0; i--)
            {
                var cv = roverStartedConv[i]; double fuel, fpe; if (cv == null) { roverStartedConv.RemoveAt(i); continue; }
                ConverterUsable(cv, out fuel, out fpe); double need = RoverPowerPolicy.ReserveNeed(HomeMetres(), roverEcPerM, fpe, reserve);
                if (!RoverPowerPolicy.MayBurnFuel(fuel, need)) { cv.StopResourceConverter(); roverStartedConv.RemoveAt(i); ChatLog.Write("power", "[rover] " + cv.part.partInfo.title + " stopped: fuel reserve for the drive home"); }
            }
        }

        void RoverChargeStop(string why)
        {
            roverCharging = false;
            foreach (var cv in roverStartedConv) if (cv != null && cv.IsActivated) cv.StopResourceConverter();
            foreach (var g in roverStartedGen) if (g != null && g.generatorIsActive) g.Shutdown();
            roverStartedConv.Clear(); roverStartedGen.Clear();
            try { if (TimeWarp.CurrentRateIndex > 0 && TimeWarp.fetch.Mode == TimeWarp.Modes.LOW) TimeWarp.SetRate(0, true); } catch (Exception) { }
            ChatLog.Write("power", "[rover] charging done (" + why + ") - driving on");
        }
    }
}