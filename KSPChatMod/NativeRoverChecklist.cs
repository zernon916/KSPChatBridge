// RS7: game side of the science tour checklist (TourChecklist.cs is the tested logic). Sites = KSC facility stops (near KSC) + places on
// this body; gains = live R&D values per experiment (KSC mini-biomes, stored-on-board and used one-shots count as 0), distances = driving
// distance along the KSC roads (RS3b). Selections saved per rover in settings ("rover_tour_sel"). START TOUR drives the checked sites in
// the shortest driving order and runs only the ticked experiments; the chat tool rover_science_tour still does the automatic tour.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        List<TourSite> checklist = new List<TourSite>(); float checklistAt = -100; bool checklistDirty = true;
        TourSelection checklistSel; string checklistCraft;
        Dictionary<string, HashSet<string>> scienceTourOnly;   // non-null: checklist tour (stop name -> experiment ids)
        readonly Dictionary<string, bool> checklistFlat = new Dictionary<string, bool>(); int checklistFlatVersion = -1;

        TourSelection ChecklistSelection()
        {
            string craft = vessel != null ? vessel.vesselName : "";
            if (checklistSel == null || checklistCraft != craft)
            {
                object root, v = null; var d = settingsData.TryGetValue("rover_tour_sel", out root) ? root as Dictionary<string, object> : null;
                if (d != null) d.TryGetValue(craft, out v);
                checklistSel = TourSelection.FromData(v); checklistCraft = craft;
            }
            return checklistSel;
        }
        void ChecklistSave()
        {
            if (checklistSel == null) return;
            object root; var d = settingsData.TryGetValue("rover_tour_sel", out root) ? root as Dictionary<string, object> : null;
            if (d == null) settingsData["rover_tour_sel"] = d = new Dictionary<string, object>();
            d[checklistCraft ?? ""] = checklistSel.ToData();
            try { Save(); } catch (Exception) { }
        }

        bool NearKsc(out double hubDistance)
        {
            hubDistance = double.PositiveInfinity;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.bodyName != "Kerbin") return false;
            if (KscRoverMap.Points.Count == 0) KscRoverMap.Refresh(vessel.mainBody);
            if (KscRoverMap.Points.Count == 0 || double.IsNaN(KscRoverMap.HubLat)) return false;
            hubDistance = NavigationMath.Distance(vessel.latitude, vessel.longitude, KscRoverMap.HubLat, KscRoverMap.HubLon, vessel.mainBody.Radius);
            return hubDistance <= 12000;
        }

        void ChecklistRefresh(bool force)
        {
            if (!force && !checklistDirty && Time.realtimeSinceStartup - checklistAt < 3) return;
            checklistAt = Time.realtimeSinceStartup; checklistDirty = false;
            var sites = new List<TourSite>();
            if (vessel == null || vessel.mainBody == null || !vessel.LandedOrSplashed) { checklist = sites; return; }
            var body = vessel.mainBody; double minSci = RoverMinScience();
            var groups = new Dictionary<string, List<ModuleScienceExperiment>>();
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            { string id = e.experimentID ?? ""; List<ModuleScienceExperiment> g; if (!groups.TryGetValue(id, out g)) groups[id] = g = new List<ModuleScienceExperiment>(); g.Add(e); }
            var stored = StoredSubjectIds(); bool scientist = HasScientist();
            int crew = vessel.GetCrewCount(); bool controllable = vessel.IsControllable;
            bool inAtmo = body.atmosphere && vessel.altitude < body.atmosphereDepth;
            var cands = new List<KscRoverWaypoint>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); double hubD;
            if (NearKsc(out hubD))
            {
                if (checklistFlatVersion != KscRoverMap.Version) { checklistFlat.Clear(); checklistFlatVersion = KscRoverMap.Version; }
                foreach (var w in KscRoverMap.Points)
                {
                    bool flat; if (!checklistFlat.TryGetValue(w.Name, out flat)) checklistFlat[w.Name] = flat = IsFlatApproach(w);
                    if (flat && names.Add(w.Name)) cands.Add(w);
                }
            }
            foreach (Place pl in AllPlaces())
                if (pl.Body == body.bodyName && NavigationMath.Distance(vessel.latitude, vessel.longitude, pl.Lat, pl.Lon, body.Radius) <= 20000 && names.Add(pl.Name))
                    cands.Add(new KscRoverWaypoint { Name = pl.Name, Biome = "", Lat = pl.Lat, Lon = pl.Lon, ApproachLat = pl.Lat, ApproachLon = pl.Lon, Source = "place" });
            foreach (var c in cands)
            {
                var site = new TourSite { Key = c.Name, Name = c.Name, Source = c.Source ?? "", Lat = c.ApproachLat, Lon = c.ApproachLon,
                    Distance = RoverDriveDistance(vessel.latitude, vessel.longitude, c.ApproachLat, c.ApproachLon) };
                string mini = c.Source == "place" ? null : ScienceInventory.KscMiniBiome(c.Biome);
                foreach (var g in groups)
                {
                    AvailableScience sci;
                    try { sci = ScienceAt(g.Key, ExperimentSituations.SrfLanded, body, c.ApproachLat, c.ApproachLon, mini); } catch (Exception) { continue; }
                    if (sci.Experiment == null) continue;
                    int usable = 0; bool oneShot = true;
                    foreach (var m in g.Value)
                    {
                        if (m.Inoperable && !scientist) continue;
                        if (!SciencePolicy.CanRun(m.usageReqMaskInternal, crew, m.part.protoModuleCrew.Count, scientist, controllable, sci.Experiment.requireAtmosphere, inAtmo)) continue;
                        if (!m.rerunnable && m.GetScienceCount() > 0 && !scientist) continue;
                        usable++; if (m.rerunnable) oneShot = false;
                    }
                    bool onBoard = sci.SubjectId.Length > 0 && stored.Contains(sci.SubjectId);
                    double gain = sci.Available && usable > 0 && !onBoard && sci.Value > minSci ? Math.Round(sci.Value, 1) : 0;
                    site.Exps.Add(new TourExp { Id = g.Key, Title = sci.Experiment.experimentTitle ?? g.Key, Gain = gain, OneShot = oneShot && !scientist && usable > 0, Modules = Math.Max(1, usable) });
                }
                sites.Add(site);
            }
            TourChecklist.Apply(sites, ChecklistSelection());
            checklist = sites;
        }

        void ChecklistMarkRan(string site, List<string> ids)
        {
            if (ids == null || ids.Count == 0 || vessel == null) return;
            TourChecklist.MarkRan(checklist, site, ids, ChecklistSelection()); ChecklistSave(); checklistDirty = true;
            ChatLog.Write("rover", "checklist: ran at " + site + ": " + string.Join(", ", ids.ToArray()));
        }
        void ChecklistClearRan() { if (vessel == null) return; TourChecklist.ClearRan(checklist, ChecklistSelection()); ChecklistSave(); checklistDirty = true; }

        string StartChecklistTour(double speed)
        {
            if (vessel == null || !vessel.LandedOrSplashed) return "Science tours start with a landed rover.";
            if (!HasPoweredWheels()) return "Science tours require powered wheels.";
            ChecklistClearRan(); ChecklistRefresh(true);   // RS7b: a new tour starts without check marks
            var route = TourChecklist.RouteSites(checklist);
            if (route.Count == 0) return "Tick at least one site with science to gain.";
            int n = route.Count; var st = new double[n]; var d = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                st[i] = RoverDriveDistance(vessel.latitude, vessel.longitude, route[i].Lat, route[i].Lon);
                for (int j = 0; j < n; j++) d[i, j] = i == j ? 0 : RoverDriveDistance(route[i].Lat, route[i].Lon, route[j].Lat, route[j].Lon);
            }
            var order = TourChecklist.Order(st, d);
            RoverAlert = "";
            double hubD; bool ksc = NearKsc(out hubD);
            touring = false; scienceTourDriving = false;
            scienceTourStops.Clear(); scienceTourVisited.Clear(); scienceTourRoute.Clear();
            scienceTourRouteIndex = 0; scienceTourStopN = 0; tourWaitUntil = 0;
            scienceTourSpeed = Math.Max(2, Math.Min(RoverCaps.Ceiling, speed > 0 ? speed : 20));
            scienceTourKsc = ksc && TourHub(out scienceTourHub);
            scienceTourOnly = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var labels = new List<string>(); double km = 0, sci = 0; int prev = -1;
            foreach (int i in order)
            {
                var s = route[i];
                scienceTourStops.Add(new KscRoverWaypoint { Name = s.Name, Biome = "", Lat = s.Lat, Lon = s.Lon, ApproachLat = s.Lat, ApproachLon = s.Lon, Source = s.Source });
                scienceTourOnly[s.Name] = TourChecklist.RunIds(s);
                labels.Add(s.Name); km += prev < 0 ? st[i] : d[prev, i]; prev = i; sci += s.Checked;
            }
            scienceTouring = true;
            string line = string.Join(" -> ", labels.ToArray());
            ChatLog.Write("rover", "checklist tour: " + line + " | " + (km / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km, "
                + sci.ToString("0.0", CultureInfo.InvariantCulture) + " science checked");
            return "Science tour: " + line + " (" + (km / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km, about " + sci.ToString("0.0", CultureInfo.InvariantCulture) + " science).";
        }

        // tablet access (main thread, IMGUI)
        internal static List<TourSite> RoverChecklist() { var i = instance; if (i == null || !CraftReady) return new List<TourSite>(); i.ChecklistRefresh(false); return i.checklist; }
        internal static void RoverChecklistSite(TourSite s, bool on) { var i = instance; if (i == null) return; TourChecklist.SetSite(i.checklist, s, on, i.ChecklistSelection()); i.ChecklistSave(); }
        internal static void RoverChecklistExp(TourSite s, TourExp e, bool on) { var i = instance; if (i == null) return; TourChecklist.SetExp(i.checklist, s, e, on, i.ChecklistSelection()); i.ChecklistSave(); }
        internal static void RoverChecklistAll(bool on) { var i = instance; if (i == null) return; TourChecklist.SetAll(i.checklist, on, i.ChecklistSelection()); i.ChecklistSave(); }
        internal static string RoverChecklistStart(double speed) { var i = instance; return i == null || !CraftReady ? "No active vessel." : i.StartChecklistTour(speed); }
        internal static bool RoverChecklistRunning { get { var i = instance; return i != null && i.scienceTouring && i.scienceTourOnly != null; } }
    }
}