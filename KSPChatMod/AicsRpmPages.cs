using System;
using System.IO;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Optional IVA MFD pages for RasterPropMonitor (soft dependency: no RPM reference). AICS_RPM.cfg (:NEEDS[RasterPropMonitor])
    /// adds PAGE { PAGEHANDLER { name = AicsRpmPages method = IlsPage|MapPage } } to RPM MFD props; RPM calls these by reflection.</summary>
    public class AicsRpmPages : InternalModule
    {
        [KSPField] public float spanKm = 20;
        [KSPField] public int charW = 16, charH = 32;
        internal const string Footer = "R8: ILS > MAP > AP STATUS";
        static bool logged;
        /// <summary>Setting "RPM AICS pages": auto (hide ours where the cockpit has the native AICS MFD), on, off. PluginData/rpm_pages.txt.</summary>
        internal static string Mode
        {
            get { if (mode == null) { try { string p = ModePath; mode = File.Exists(p) ? File.ReadAllText(p).Trim().ToLowerInvariant() : "auto"; } catch (Exception) { mode = "auto"; } if (mode != "on" && mode != "off") mode = "auto"; } return mode; }
            set { mode = value; try { File.WriteAllText(ModePath, value); } catch (Exception) { } }
        }
        static string mode;
        static string ModePath { get { return Path.Combine(AicsCore.PluginDataDirectory, "rpm_pages.txt"); } }
        bool NativeHere()
        {
            try { var m = internalProp != null ? internalProp.internalModel : null; if (m == null) return false; foreach (var p in m.props) foreach (var x in p.internalModules) if (x is AicsIvaMfd && ((AicsIvaMfd)x).Active) return true; } catch (Exception) { }
            return false;
        }
        string Seen(string page, string text)
        {
            if (!logged) { logged = true; UnityEngine.Debug.Log("[KSPChatBridge] RPM page handler active (" + page + ")"); }
            if (MfdNav.RpmHidden(Mode, NativeHere())) return "AICS\n\n" + (Mode == "off" ? "AICS RPM pages are off." : "This cockpit has the native AICS MFD:\nuse that screen.") + "\n(AICS Settings: RPM AICS pages auto/on/off)";
            return text + "\n" + Footer;
        }
        int Cols(int w) { return w / Math.Max(1, charW); }
        int Rows(int h) { return h / Math.Max(1, charH) - 1; }
        public string IlsPage(int screenWidth, int screenHeight) { return Seen("ILS", IlsText(Cols(screenWidth), Rows(screenHeight))); }
        public string MapPage(int screenWidth, int screenHeight) { return Seen("MAP", MapText(Cols(screenWidth), Rows(screenHeight), spanKm * 1000)); }
        public string StatusPage(int screenWidth, int screenHeight) { return Seen("STATUS", StatusText()); }
        // page text shared with the native IVA MFD (IvaMfd.cs)
        internal static string IlsText(int cols, int rows)
        {
            try
            {
                var v = FlightGlobals.ActiveVessel; if (v == null) return "AICS ILS\nno vessel";
                var act = NativeFlightController.ActiveRunway; double tla, tlo, ela, elo, el; string label;
                if (act != null) { tla = act.Lat; tlo = act.Lon; ela = act.EndLat; elo = act.EndLon; el = act.Elevation; label = act.Key ?? ""; }
                else { tla = KscRunway.Lat; tlo = KscRunway.Lon09; ela = KscRunway.Lat; elo = KscRunway.Lon27; el = 69.1; label = "KSC 09 (no autoland)"; }
                var r = act != null && act.Coupled && act.Ils != null ? act.Ils : Ils.Compute(v.latitude, v.longitude, v.altitude, tla, tlo, ela, elo, el, v.mainBody.Radius, act != null ? act.TouchdownM : 350);
                string cpl = act == null ? "AP OFF - raw ILS" : act.Coupled ? (act.GsCoupled ? "COUPLED LOC+GS" : "COUPLED LOC") : "LOC ARMED";
                double tgt = act != null && act.DesiredSpeed > 0 ? act.DesiredSpeed : 1.3 * Math.Max(30, NativeFlightController.MapStall);
                return MfdText.IlsPage(r, label, cpl, v.ActionGroups[KSPActionGroup.Gear], v.ActionGroups[KSPActionGroup.Brakes], v.indicatedAirSpeed, tgt, v.altitude - el, v.verticalSpeed, cols, rows);
            }
            catch (Exception ex) { return "AICS ILS\n" + ex.Message; }
        }
        internal static string MapText(int cols, int rows, double spanM)
        {
            try
            {
                var v = FlightGlobals.ActiveVessel; if (v == null) return "AICS MAP\nno vessel";
                var act = NativeFlightController.ActiveRunway; List<double[]> rw = null, route = null; double[] join = null;
                if (act != null)
                {
                    rw = new List<double[]> { new[] { act.Lat, act.Lon }, new[] { act.EndLat, act.EndLon } };
                    if (act.Route != null) { route = new List<double[]>(); for (int i = Math.Max(0, act.RouteIndex); i < act.Route.Count; i++) route.Add(new[] { act.Route[i].Lat, act.Route[i].Lon }); }
                    if (act.Chart != null && act.Chart.JoinPoint != null && act.Phase == "entry") join = new[] { act.Chart.JoinPoint.Lat, act.Chart.JoinPoint.Lon };
                }
                return MfdText.MapPage(v.latitude, v.longitude, v.mainBody.Radius, spanM, rw, route, join, cols, rows);
            }
            catch (Exception ex) { return "AICS MAP\n" + ex.Message; }
        }
        /// <summary>Autopilot status page: mode, active approach phase, plan.</summary>
        internal static string StatusText()
        {
            try
            {
                var act = NativeFlightController.ActiveRunway;
                string t = "AICS AUTOPILOT\n\nMode:  " + NativeFlightController.Phase.ToUpperInvariant();
                if (act != null) t += "\nRwy:   " + (act.Key ?? "") + "\nPhase: " + act.Phase + "\nDist:  " + (act.Distance / 1000).ToString("0.0") + " km" + (act.DesiredSpeed > 0 ? "\nTgt:   " + act.DesiredSpeed.ToString("0") + " m/s" : "");
                t += "\nPlan:  " + NativeFlightController.PlanStatus;
                return t;
            }
            catch (Exception ex) { return "AICS AUTOPILOT\n" + ex.Message; }
        }
    }
}
