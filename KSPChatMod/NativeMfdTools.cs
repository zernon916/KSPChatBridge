using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KSPChatBridge
{
    // MFD-era tools: ground-chart taxi (taxi_route) and MechJeb module control for the MFD MECHJEB page (mj_*).
    public partial class NativeFlightController
    {
        internal static string ChartDir { get { EnsureCharts(); return ChartStore.Dir; } }

        /// <summary>Taxi routes on disk (ground charts); KSC defaults are generated once from the in-game SPH / hub positions.</summary>
        internal static List<TaxiRoute> TaxiRoutes() { try { return TaxiRoute.LoadAll(ChartDir); } catch (Exception) { return new List<TaxiRoute>(); } }

        void EnsureTaxiCharts()
        {
            string dir = ChartDir; Directory.CreateDirectory(dir);
            if (Directory.GetFiles(dir, TaxiRoute.Prefix + "*.json").Length > 0 || vessel == null || vessel.mainBody.bodyName != "Kerbin") return;
            var all = ReadBuildings(); KscRoverPolicy.P sph;
            if (!all.TryGetValue("SpaceplaneHangar", out sph) || all.Count == 0) return;
            double hlat = 0, hlon = 0; foreach (var p in all.Values) { hlat += p.Lat; hlon += p.Lon; }
            foreach (var r in TaxiRoute.KscDefaults(sph.Lat, sph.Lon, hlat / all.Count, hlon / all.Count))
                AtomicFile.Write(Path.Combine(dir, TaxiRoute.FileName(r.Name)), r.ToJson());
            ChatLog.Write("taxi", "default KSC taxi charts written to " + dir);
        }

        string TaxiRouteCmd(Dictionary<string, object> a)
        {
            if (!vessel.LandedOrSplashed) return "Taxi requires a grounded vessel.";
            if (vessel.mainBody.bodyName != "Kerbin") return "Taxi charts are for the KSC.";
            EnsureTaxiCharts();
            string dest = Str(a, "dest", Str(a, "to", "hangar")), name = Str(a, "route", TaxiRoute.Pick(dest, vessel.latitude, vessel.longitude));
            TaxiRoute route = null; string why = "no such taxi chart";
            string file = Path.Combine(ChartDir, TaxiRoute.FileName(name));
            if (File.Exists(file)) route = TaxiRoute.Parse(File.ReadAllText(file), out why);
            if (route == null) return "Taxi chart " + name + ": " + why + " (PluginData/charts/" + TaxiRoute.FileName(name) + "). Drive within a few km of the KSC so the SPH can be seen, then try again.";
            int leg = route.Join(vessel.latitude, vessel.longitude, vessel.mainBody.Radius);
            if (leg < 0) return "Too far from the " + name + " taxi route (more than " + TaxiRoute.MaxJoinM + " m).";
            bool wheels = false; poweredTaxi = false;
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            { wheels |= module is ModuleWheelBase; var motor = module as ModuleWheels.ModuleWheelMotor; poweredTaxi |= motor != null && motor.motorEnabled; }
            if (!wheels) return "No wheels for taxi.";
            BeginHold(); taxi = new TaxiMission(route, leg, Num(a, "speed", TaxiMission.TaxiSpeed)); taxiFile = file; taxiStamp = File.GetLastWriteTimeUtc(file); mode = "taxi"; parkingReleased = true;
            SetGroup(vessel, KSPActionGroup.Brakes, false);
            if (poweredTaxi) engines.Cancel(); else NativeEngines.Takeoff(vessel);
            return "Taxi " + route.From + " -> " + route.To + " (" + name + "): joining at " + route.Points[leg + 1].Name + ", following the centerline at " + TaxiMission.TaxiSpeed + " m/s; full stop at " + route.Points[route.Points.Count - 1].Name + ".";
        }
        string taxiFile; DateTime taxiStamp;
        /// <summary>Hot swap: an edited chart file re-joins the running taxi on the new line.</summary>
        void TaxiHotSwap()
        {
            if (taxi == null || taxi.Route == null || taxiFile == null || !File.Exists(taxiFile)) return;
            var stamp = File.GetLastWriteTimeUtc(taxiFile); if (stamp == taxiStamp) return; taxiStamp = stamp;
            string why; var r = TaxiRoute.Parse(File.ReadAllText(taxiFile), out why);
            if (r == null) { ChatLog.Write("taxi", "chart edit ignored: " + why); return; }
            int leg = r.Join(vessel.latitude, vessel.longitude, vessel.mainBody.Radius); if (leg < 0) return;
            taxi = new TaxiMission(r, leg, TaxiMission.TaxiSpeed); ChatLog.Write("taxi", "chart reloaded, re-joined leg " + leg);
        }

        // ---------------- MechJeb (MFD MECHJEB page) ----------------
        static readonly string[] SmartModes = { "OFF", "KILLROT", "NODE", "PROGRADE", "RETROGRADE", "NORMAL_PLUS", "NORMAL_MINUS", "RADIAL_PLUS", "RADIAL_MINUS", "TARGET_PLUS", "TARGET_MINUS", "RELATIVE_PLUS", "RELATIVE_MINUS", "PARALLEL_PLUS", "PARALLEL_MINUS", "SURFACE_PROGRADE", "SURFACE_RETROGRADE" };
        internal static bool MechJebInstalled { get { return MechJebLink.Installed; } }

        static bool SetEnum(object o, string field, string value)
        {
            foreach (string n in new[] { field, char.ToUpper(field[0]) + field.Substring(1) })
            {
                var f = o.GetType().GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null && f.FieldType.IsEnum) { f.SetValue(o, Enum.Parse(f.FieldType, value, true)); return true; }
                var p = o.GetType().GetProperty(n); if (p != null && p.PropertyType.IsEnum && p.CanWrite) { p.SetValue(o, Enum.Parse(p.PropertyType, value, true), null); return true; }
            }
            return false;
        }

        string MechJebCmd(string name, Dictionary<string, object> a)
        {
            object core = MechJebLink.Core(vessel); if (core == null) return "MechJeb: " + MechJebLink.Missing;
            try
            {
                switch (name)
                {
                    case "mj_smartass":
                    {
                        string m = Str(a, "mode", "OFF").Trim().ToUpperInvariant().Replace(' ', '_').Replace("+", "_PLUS").Replace("-", "_MINUS");
                        if (Array.IndexOf(SmartModes, m) < 0) return "SmartASS modes: " + string.Join(", ", SmartModes);
                        object sa = MechJebLink.Module(core, "MechJebModuleSmartASS");
                        if (m.StartsWith("SURFACE_")) { if (!SetEnum(sa, "target", "SURFACE")) return "MechJeb API mismatch: SmartASS.target"; }
                        else if (!SetEnum(sa, "target", m)) return "MechJeb API mismatch: SmartASS.target";
                        if (m != "OFF") { Stop(); SetGroup(vessel, KSPActionGroup.SAS, false); }
                        MechJebLink.Call(sa, "Engage", true);
                        return "MechJeb SmartASS: " + m.Replace('_', ' ') + ".";
                    }
                    case "mj_node":
                    {
                        if (vessel.patchedConicSolver == null || vessel.patchedConicSolver.maneuverNodes.Count == 0) return "No maneuver node to execute.";
                        try { MechJebLink.Use(MechJebLink.Get(core, "Staging"), MjOwner); } catch (Exception) { }
                        MechJebLink.Call(MechJebLink.Get(core, "Node") ?? MechJebLink.Module(core, "MechJebModuleNodeExecutor"), "ExecuteOneNode", MjOwner);
                        return "MechJeb is executing the next maneuver node (autostage on).";
                    }
                    case "mj_land":
                    {
                        if (vessel.LandedOrSplashed) return "Already landed.";
                        object lp = MechJebLink.Module(core, "MechJebModuleLandingAutopilot"); Stop();
                        bool target = Bool(a, "target", FlightGlobals.fetch.VesselTarget != null);
                        MechJebLink.Call(lp, target ? "LandAtPositionTarget" : "LandUntargeted", MjOwner);
                        return "MechJeb landing guidance: " + (target ? "landing at the target." : "landing untargeted (wherever it comes down).");
                    }
                    case "mj_rendezvous":
                    {
                        if (FlightGlobals.fetch.VesselTarget == null) return "Pick a target first (map view: set as target).";
                        object rv = MechJebLink.Module(core, "MechJebModuleRendezvousAutopilot"); Stop(); MechJebLink.Use(rv, MjOwner);
                        return "MechJeb rendezvous autopilot engaged with " + FlightGlobals.fetch.VesselTarget.GetName() + ". Then use dock_with.";
                    }
                    case "mj_aircraft":
                    {
                        object ap = MechJebLink.Module(core, "MechJebModuleAirplaneAutopilot");
                        if (!Bool(a, "on", true)) { MechJebLink.Release(ap, MjOwner); return "MechJeb aircraft autopilot off."; }
                        if (vessel.LandedOrSplashed) return "Take off first (takeoff), then engage the MechJeb aircraft autopilot.";
                        double hdg = Num(a, "heading", FlightGlobals.ship_heading), alt = Num(a, "altitude", vessel.altitude), spd = Num(a, "speed", vessel.srfSpeed);
                        Stop();
                        MechJebLink.SetValue(ap, hdg, "HeadingTarget"); MechJebLink.SetValue(ap, alt, "AltitudeTarget"); MechJebLink.SetValue(ap, spd, "SpeedTarget");
                        MechJebLink.SetValue(ap, 1, "HeadingHoldEnabled"); MechJebLink.SetValue(ap, 1, "AltitudeHoldEnabled"); MechJebLink.SetValue(ap, 1, "SpeedHoldEnabled");
                        MechJebLink.Use(ap, MjOwner);
                        return "MechJeb aircraft autopilot: heading " + hdg.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ", " + alt.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m, " + spd.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m/s.";
                    }
                    case "mj_spaceplane":
                    {
                        object sp = MechJebLink.Module(core, "MechJebModuleSpaceplaneAutopilot"); Stop();
                        MechJebLink.Call(sp, "Autoland", MjOwner);
                        return "MechJeb spaceplane autoland engaged (MechJeb's runway choice). AICS autoland (land) is usually smoother.";
                    }
                    case "mj_off":
                    {
                        foreach (string m in new[] { "MechJebModuleAirplaneAutopilot", "MechJebModuleRendezvousAutopilot", "MechJebModuleLandingAutopilot", "MechJebModuleNodeExecutor", "MechJebModuleSpaceplaneAutopilot" })
                        { try { var mod = MechJebLink.Module(core, m); MechJebLink.Release(mod, MjOwner); if (m == "MechJebModuleLandingAutopilot") MechJebLink.Call(mod, "StopLanding"); } catch (Exception) { } }
                        try { var sa = MechJebLink.Module(core, "MechJebModuleSmartASS"); SetEnum(sa, "target", "OFF"); MechJebLink.Call(sa, "Engage", true); } catch (Exception) { }
                        try { var asc = MechJebLink.Get(core, "Ascent"); if (asc != null) MechJebLink.Release(asc, MjOwner); } catch (Exception) { }
                        return "MechJeb autopilots released.";
                    }
                    case "mj_status": return MechJebStatus();
                }
            }
            catch (Exception ex) { return "MechJeb " + name.Substring(3) + ": " + (ex.InnerException ?? ex).Message; }
            return null;
        }

        /// <summary>Active MechJeb modules + target (MFD MECHJEB page status line).</summary>
        internal string MechJebStatus()
        {
            object core = MechJebLink.Core(vessel); if (core == null) return "MechJeb not on this craft.";
            var on = new List<string>();
            foreach (var kv in new[] { new[] { "MechJebModuleAirplaneAutopilot", "AIRCRAFT AP" }, new[] { "MechJebModuleRendezvousAutopilot", "RENDEZVOUS" }, new[] { "MechJebModuleLandingAutopilot", "LANDING" }, new[] { "MechJebModuleNodeExecutor", "NODE EXEC" }, new[] { "MechJebModuleSpaceplaneAutopilot", "SPACEPLANE" }, new[] { "MechJebModuleAscentAutopilot", "ASCENT" } })
            {
                try { var m = MechJebLink.Module(core, kv[0]); var en = MechJebLink.Get(m, "Enabled") ?? MechJebLink.Get(m, "enabled"); if (en is bool && (bool)en) on.Add(kv[1]); } catch (Exception) { }
            }
            string sa = "";
            try { var t = MechJebLink.Get(MechJebLink.Module(core, "MechJebModuleSmartASS"), "target"); if (t != null && t.ToString() != "OFF") sa = "SMARTASS " + t; } catch (Exception) { }
            if (sa.Length > 0) on.Add(sa);
            var tg = FlightGlobals.fetch != null ? FlightGlobals.fetch.VesselTarget : null;
            return "Active: " + (on.Count == 0 ? "none" : string.Join(", ", on.ToArray())) + "\nTarget: " + (tg == null ? "none" : tg.GetName());
        }
        internal static string MjStatusNow { get { return instance == null || instance.vessel == null ? "no vessel" : instance.MechJebStatus(); } }

        /// <summary>Nose gear = the grounded-capable wheel farthest forward of the CoM; true when it touches the ground.</summary>
        bool NoseGearDown()
        {
            ModuleWheelBase nose = null; double best = double.MinValue; Vector3 fwd = vessel.ReferenceTransform.up;
            foreach (Part p in vessel.parts) foreach (PartModule m in p.Modules)
            { var w = m as ModuleWheelBase; if (w == null) continue; double f = Vector3.Dot(p.transform.position - vessel.CoM, fwd); if (f > best) { best = f; nose = w; } }
            return nose == null || nose.isGrounded;
        }
    }
}