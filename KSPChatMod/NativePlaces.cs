// RS5 game side: PluginData/places.json (PlacesStore), stock landing_spots read-only, Kerbal Konstructs v1.12.3.0 launch sites and facility
// statics by reflection (KerbalKonstructs.Core.LaunchSiteManager.AllLaunchSites: KKLaunchSite.LaunchSiteName/refLat/refLon/refAlt/body/Category;
// KerbalKonstructs.Core.StaticDatabase.GetAllStatics(): StaticInstance.Group/FacilityType/RefLatitude/RefLongitude/CelestialBody - names
// checked against KerbalKonstructs.dll). Every reflection call is guarded; without KK the list still works.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        static PlacesStore places; static List<Place> kkPlaces, mergedPlaces; static float mergedAt = -99;
        static string PlacesPath { get { return Path.Combine(AicsCore.PluginDataDirectory, "places.json"); } }
        static PlacesStore Places
        {
            get
            {
                if (places != null) return places;
                try { places = File.Exists(PlacesPath) ? PlacesStore.FromJson(File.ReadAllText(PlacesPath)) : new PlacesStore(); }
                catch (Exception ex) { places = new PlacesStore(); ChatLog.Write("places", "places.json unreadable: " + ex.Message); }
                return places;
            }
        }
        static void SavePlaces()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(PlacesPath)); File.WriteAllText(PlacesPath, Places.ToJson()); mergedAt = -99; }
            catch (Exception ex) { ChatLog.Write("places", "places.json not saved: " + ex.Message); }
        }

        static object Member(object o, Type t, string name)
        {
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var fi = t.GetField(name, f); if (fi != null) return fi.GetValue(o);
            var pi = t.GetProperty(name, f); return pi != null ? pi.GetValue(o, null) : null;
        }
        static double D(object o) { try { return o == null ? double.NaN : Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch (Exception) { return double.NaN; } }

        /// <summary>KK launch sites + facility statics (read once per scene; empty without KK).</summary>
        static List<Place> KkPlaces()
        {
            if (kkPlaces != null) return kkPlaces; kkPlaces = new List<Place>();
            Assembly kk = null; foreach (var a in AssemblyLoader.loadedAssemblies) if (a.name == "KerbalKonstructs") kk = a.assembly;
            if (kk == null) { ChatLog.Write("places", "Kerbal Konstructs not installed - manual places and landing spots only"); return kkPlaces; }
            int sites = 0, fac = 0;
            try
            {
                var lsm = kk.GetType("KerbalKonstructs.Core.LaunchSiteManager");
                var all = lsm != null ? Member(null, lsm, "AllLaunchSites") as IEnumerable : null;
                if (all != null) foreach (var s in all)
                {
                    if (s == null) continue; var t = s.GetType(); var body = Member(s, t, "body") as CelestialBody;
                    var p = new Place { Name = Convert.ToString(Member(s, t, "LaunchSiteName")), Kind = PlacesStore.KindForKk(Convert.ToString(Member(s, t, "Category"))), Body = body != null ? body.bodyName : "Kerbin",
                        Lat = D(Member(s, t, "refLat")), Lon = D(Member(s, t, "refLon")), Alt = D(Member(s, t, "refAlt")), Source = "KerbalKonstructs" };
                    if (!string.IsNullOrEmpty(p.Name) && !double.IsNaN(p.Lat) && !double.IsNaN(p.Lon)) { kkPlaces.Add(p); sites++; }
                }
                else ChatLog.Write("places", "KK: LaunchSiteManager.AllLaunchSites not found (version change?)");
            }
            catch (Exception ex) { ChatLog.Write("places", "KK launch sites unreadable: " + ex.Message); }
            try
            {
                var db = kk.GetType("KerbalKonstructs.Core.StaticDatabase"); var m = db != null ? db.GetMethod("GetAllStatics", BindingFlags.Public | BindingFlags.Static) : null;
                var all = m != null ? m.Invoke(null, null) as IEnumerable : null; var seen = new HashSet<string>();
                if (all != null) foreach (var s in all)
                {
                    if (s == null) continue; var t = s.GetType(); string ft = Convert.ToString(Member(s, t, "FacilityType"));
                    if (string.IsNullOrEmpty(ft) || ft == "None") continue;
                    string name = Convert.ToString(Member(s, t, "Group")) + " " + ft; if (!seen.Add(name)) continue;
                    var body = Member(s, t, "CelestialBody") as CelestialBody;
                    var p = new Place { Name = name.Trim(), Kind = "building", Body = body != null ? body.bodyName : "Kerbin", Lat = D(Member(s, t, "RefLatitude")), Lon = D(Member(s, t, "RefLongitude")), Source = "KerbalKonstructs" };
                    if (!double.IsNaN(p.Lat) && !double.IsNaN(p.Lon)) { kkPlaces.Add(p); fac++; }
                }
                else ChatLog.Write("places", "KK: StaticDatabase.GetAllStatics not found (version change?)");
            }
            catch (Exception ex) { ChatLog.Write("places", "KK statics unreadable: " + ex.Message); }
            ChatLog.Write("places", "Kerbal Konstructs: " + sites + " launch site(s), " + fac + " facility static(s)");
            return kkPlaces;
        }

        internal static List<Place> AllPlaces()
        {
            if (mergedPlaces != null && Time.realtimeSinceStartup - mergedAt < 2) return mergedPlaces;
            var imp = new List<Place>(KkPlaces());
            try { object o; var i = instance; if (i != null && i.settingsData != null && i.settingsData.TryGetValue("landing_spots", out o)) imp.AddRange(PlacesStore.FromLandingSpots(o as Dictionary<string, object>)); } catch (Exception) { }
            mergedPlaces = Places.Merged(imp); mergedAt = Time.realtimeSinceStartup; return mergedPlaces;
        }

        /// <summary>Nearest named building within 150 m: KSC DestructibleBuilding facility, else a KK facility static.</summary>
        string NearestBuildingName()
        {
            string best = ""; double bd = 150;
            try
            {
                foreach (DestructibleBuilding b in UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>())
                {
                    string f = KscRoverPolicy.Facility(b.id); if (f == null) continue;
                    double d = (b.transform.position - vessel.CoMD).magnitude; if (d < bd) { bd = d; best = KscRoverPolicy.Display(f); }
                }
            }
            catch (Exception) { }
            foreach (var p in KkPlaces())
                if (p.Kind == "building" && p.Body == vessel.mainBody.bodyName)
                { double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, p.Lat, p.Lon, vessel.mainBody.Radius); if (d < bd) { bd = d; best = p.Name; } }
            return best;
        }

        internal static string PlacePinHere(string name, string kind)
        {
            var i = instance; if (!CraftReady) return "No active vessel.";
            var v = i.vessel; string n = string.IsNullOrEmpty((name ?? "").Trim()) ? i.NearestBuildingName() : name;
            var p = Places.Add(n, string.IsNullOrEmpty(kind) ? (n.Length > 0 && string.IsNullOrEmpty(name) ? "building" : "site") : kind, v.mainBody.bodyName, v.latitude, v.longitude, v.altitude, FlightGlobals.ship_heading);
            SavePlaces(); ChatLog.Write("places", "pinned " + p.Line()); return "Pinned " + p.Name + ".";
        }
        /// <summary>Edits: a non-manual place is copied into places.json first (manual wins on the name).</summary>
        static Place EditablePlace(string name)
        {
            var p = Places.Find(name); if (p != null) return p;
            foreach (var m in AllPlaces()) if (m.Name == name) { var c = Places.Add(m.Name, m.Kind, m.Body, m.Lat, m.Lon, m.Alt, m.Heading); c.Source = m.Source; return c; }
            return null;
        }
        internal static void PlaceToggleStop(string name) { var p = EditablePlace(name); if (p == null) return; p.Stop = !p.Stop; SavePlaces(); ChatLog.Write("places", p.Name + (p.Stop ? " marked as a tour stop" : " no longer a tour stop")); }
        internal static void PlaceCycleKind(string name) { var p = EditablePlace(name); if (p == null) return; p.Kind = PlacesStore.Kinds[(Array.IndexOf(PlacesStore.Kinds, p.Kind) + 1) % PlacesStore.Kinds.Length]; SavePlaces(); }
        internal static string PlaceRename(string name, string to) { var p = EditablePlace(name); if (p == null || !Places.Rename(p.Name, to)) return "Rename failed (blank or taken name)."; SavePlaces(); ChatLog.Write("places", name + " renamed " + to); return "Renamed to " + to + "."; }
        internal static void PlaceDelete(string name) { if (Places.Delete(name)) { SavePlaces(); ChatLog.Write("places", "deleted " + name); } }
        internal static bool PlaceIsManual(string name) { return Places.Find(name) != null; }

        List<Place> PlaceStopsHere() { return vessel == null || vessel.mainBody == null ? new List<Place>() : PlacesStore.Stops(AllPlaces(), vessel.mainBody.bodyName); }
    }
}