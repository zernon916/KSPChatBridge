using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Optional IVA MFD pages for RasterPropMonitor (soft dependency: no RPM reference). AICS_RPM.cfg (:NEEDS[RasterPropMonitor])
    /// adds PAGE { PAGEHANDLER { name = AicsRpmPages method = IlsPage|MapPage } } to RPM MFD props; RPM calls these by reflection.</summary>
    public class AicsRpmPages : InternalModule
    {
        [KSPField] public float spanKm = 20;
        [KSPField] public int charW = 16, charH = 32;
        public string IlsPage(int screenWidth, int screenHeight)
        {
            try
            {
                var v = FlightGlobals.ActiveVessel; if (v == null) return "AICS ILS\nno vessel";
                var act = NativeFlightController.ActiveRunway; double tla, tlo, ela, elo, el; string label;
                if (act != null) { tla = act.Lat; tlo = act.Lon; ela = act.EndLat; elo = act.EndLon; el = act.Elevation; label = act.Key ?? ""; }
                else { tla = -.0485997; tlo = -74.724375; ela = -.0502119; elo = -74.4903; el = 69.1; label = "KSC 09 (no autoland)"; }
                var r = act != null && act.Coupled && act.Ils != null ? act.Ils : Ils.Compute(v.latitude, v.longitude, v.altitude, tla, tlo, ela, elo, el, v.mainBody.Radius, act != null ? act.TouchdownM : 350);
                string cpl = act == null ? "AP OFF - raw ILS" : act.Coupled ? (act.GsCoupled ? "COUPLED LOC+GS" : "COUPLED LOC") : "LOC ARMED";
                double tgt = act != null && act.DesiredSpeed > 0 ? act.DesiredSpeed : 1.3 * Math.Max(30, NativeFlightController.MapStall);
                return MfdText.IlsPage(r, label, cpl, v.ActionGroups[KSPActionGroup.Gear], v.ActionGroups[KSPActionGroup.Brakes], v.indicatedAirSpeed, tgt, v.altitude - el, v.verticalSpeed, screenWidth / Math.Max(1, charW), screenHeight / Math.Max(1, charH));
            }
            catch (Exception ex) { return "AICS ILS\n" + ex.Message; }
        }
        public string MapPage(int screenWidth, int screenHeight)
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
                return MfdText.MapPage(v.latitude, v.longitude, v.mainBody.Radius, spanKm * 1000, rw, route, join, screenWidth / Math.Max(1, charW), screenHeight / Math.Max(1, charH));
            }
            catch (Exception ex) { return "AICS MAP\n" + ex.Message; }
        }
    }
}
