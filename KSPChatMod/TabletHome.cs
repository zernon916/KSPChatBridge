using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        sealed class HomeApp
        {
            internal readonly string Id, Name;
            internal readonly int Tab;
            internal readonly Color Accent;
            internal HomeApp(string id, string name, int tab, float r, float g, float b)
            { Id = id; Name = name; Tab = tab; Accent = new Color(r, g, b); }
        }
        // Explicit indexes are the existing TabletTabs contract.
        static readonly HomeApp[] HomeApps = {
            new HomeApp("flight", "Flight", 6, .25f, .7f, 1f),
            new HomeApp("orbit", "Orbit", 8, .6f, .45f, 1f),
            new HomeApp("burns", "Burns", 1, 1f, .45f, .2f),
            new HomeApp("map", "Map", 2, .2f, .8f, .65f),
            new HomeApp("mission", "Mission", 0, .35f, .65f, 1f),
            new HomeApp("systems", "Systems", 17, .4f, .75f, .8f),
            new HomeApp("crew", "Crew", 16, .85f, .55f, .8f),
            new HomeApp("settings", "Settings", 18, .6f, .65f, .75f),
            new HomeApp("graphs", "Graphs", 3, .3f, .8f, .5f),
            new HomeApp("tests", "Tests", 4, 1f, .7f, .2f),
            new HomeApp("encyclopedia", "Encyclopedia", 5, .65f, .6f, 1f),
            new HomeApp("plan", "Plan", 7, .4f, .65f, .95f),
            new HomeApp("log", "Log", 9, .7f, .7f, .5f),
            new HomeApp("comms", "Comms", 10, .2f, .8f, .8f),
            new HomeApp("contracts", "Contracts", 11, .9f, .65f, .35f),
            new HomeApp("science", "Science", 12, .6f, .85f, .3f),
            new HomeApp("dv-map", "Dv Map", 13, .7f, .5f, .9f),
            new HomeApp("transfer", "Xfer", 14, .3f, .65f, .9f),
            new HomeApp("campaign", "Campaign", 15, .9f, .4f, .55f)
        };
        static readonly List<string> homeIds = LoadHomeIds();
        static bool homeVisible = true, homeLibrary, homeEditing;
        static Vector2 homeScroll, libraryScroll;
        static string homeError;
        static GUIStyle homeTile, homeName, homeIcon, homeSmall;
        static Texture2D homeTexture;
        static int homeFont = -1;
        static string HomePath { get { return System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/home.cfg"); } }
        static HomeApp FindHomeApp(string id) { return Array.Find(HomeApps, a => a.Id == id); }
        static List<string> LoadHomeIds()
        {
            var ids = new List<string>();
            try
            {
                if (System.IO.File.Exists(HomePath))
                {
                    var node = ConfigNode.Load(HomePath);
                    if (node != null && node.GetValue("version") == "1")
                    {
                        foreach (string id in node.GetValues("app"))
                            if (FindHomeApp(id) != null && !ids.Contains(id)) ids.Add(id);
                        return ids; // An intentionally empty home is valid.
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Home load: " + ex.Message); }
            ids.AddRange(new[] { "flight", "orbit", "burns", "map", "mission", "systems", "crew", "settings" });
            return ids;
        }
        static void SaveHomeIds()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HomePath));
                var node = new ConfigNode("TABLET_HOME");
                node.AddValue("version", "1");
                foreach (string id in homeIds) node.AddValue("app", id);
                node.Save(HomePath);
                homeError = null;
            }
            catch (Exception ex) { homeError = "Home save failed"; Debug.LogWarning("[KSPChatBridge] " + ex); }
        }
        static void EnsureHomeStyles()
        {
            if (homeTexture == null) homeTexture = RoundTex(48, 16, new Color(.16f, .19f, .25f, .96f), new Color(.35f, .4f, .5f, .6f));
            if (homeFont == tabletFont && homeTile != null) return;
            homeFont = tabletFont;
            homeTile = new GUIStyle { border = new RectOffset(18, 18, 18, 18) };
            homeTile.normal.background = homeTile.hover.background = homeTile.active.background = homeTexture;
            homeName = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Min(18, tabletFont), alignment = TextAnchor.MiddleCenter, wordWrap = true, richText = false };
            homeName.normal.textColor = new Color(.9f, .93f, 1f);
            homeIcon = new GUIStyle(homeName) { fontSize = 32, fontStyle = FontStyle.Bold };
            homeSmall = new GUIStyle(homeName) { fontSize = 12, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
        }
        static bool HomeButton(Rect r, string text, bool enabled = true)
        {
            bool old = GUI.enabled;
            try { GUI.enabled = old && enabled; return GUI.Button(r, text, homeName); }
            finally { GUI.enabled = old; }
        }
        static void GoTabletHome() { homeVisible = true; homeLibrary = false; homeEditing = false; GUI.FocusControl(null); }
        static Rect TabletHomeStatus(Rect area)
        {
            EnsureHomeStyles();
            var v = FlightGlobals.fetch == null ? null : FlightGlobals.ActiveVessel;
            double met = v == null ? 0 : v.missionTime;
            if (double.IsNaN(met) || double.IsInfinity(met) || met < 0) met = 0;
            string time = Math.Floor(met / 3600).ToString("0", CultureInfo.InvariantCulture) + ":" + ((int)(met % 3600) / 60).ToString("00") + ":" + ((int)(met % 60)).ToString("00");
            GUI.Box(new Rect(area.x, area.y, area.width, 30), GUIContent.none, homeTile);
            GUI.Label(new Rect(area.x + 8, area.y, area.width * .43f - 8, 30), v == null ? "No active vessel" : v.vesselName, homeSmall);
            GUI.Label(new Rect(area.x + area.width * .43f, area.y, area.width * .3f, 30), "MET " + time, homeSmall);
            GUI.Label(new Rect(area.x + area.width * .73f, area.y, area.width * .27f, 30), TabletLink ? "LIVE LINK: ON" : "LIVE LINK: OFF", homeSmall);
            return new Rect(area.x, area.y + 38, area.width, Mathf.Max(0, area.height - 38));
        }
        static void DrawTabletHome(Rect area)
        {
            EnsureHomeStyles();
            GUI.Label(new Rect(area.x, area.y, area.width - 200, 36), homeLibrary ? "All Apps" : "Home", homeName);
            if (HomeButton(new Rect(area.xMax - 200, area.y, 110, 36), homeLibrary ? "Home" : "All Apps")) homeLibrary = !homeLibrary;
            if (HomeButton(new Rect(area.xMax - 86, area.y, 86, 36), homeEditing ? "DONE" : "EDIT")) { homeEditing = !homeEditing; if (!homeEditing) SaveHomeIds(); }
            GUI.Label(new Rect(area.x + 8, area.y + 36, area.width - 16, 24), homeError ?? (homeEditing ? "All Apps: + / -    Home: < / > reorder, - remove" : TabletLink ? "Choose an app" : "Information only - flight controls disabled"), homeSmall);
            Rect viewport = new Rect(area.x, area.y + 64, area.width, Mathf.Max(1, area.height - 64));
            var apps = new List<HomeApp>();
            if (homeLibrary) apps.AddRange(HomeApps);
            else foreach (string id in homeIds) { var app = FindHomeApp(id); if (app != null) apps.Add(app); }
            int columns = Mathf.Max(1, Mathf.FloorToInt((viewport.width - 20) / 142));
            float cell = (viewport.width - 20) / columns, height = homeEditing ? 162 : 126;
            Vector2 scroll = homeLibrary ? libraryScroll : homeScroll;
            scroll = GUI.BeginScrollView(viewport, scroll, new Rect(0, 0, viewport.width - 20, Mathf.Max(viewport.height, ((apps.Count + columns - 1) / columns) * height)));
            try
            {
                for (int i = 0; i < apps.Count; i++)
                {
                    var app = apps[i]; float x = (i % columns) * cell, y = (i / columns) * height;
                    Rect tile = new Rect(x + (cell - 76) / 2, y + 6, 76, 76);
                    Color old = GUI.color;
                    bool open;
                    try
                    {
                        GUI.color = TabletLink ? app.Accent : Color.Lerp(app.Accent, Color.gray, .75f);
                        open = GUI.Button(tile, GUIContent.none, homeTile);
                        GUI.Label(tile, app.Name.Substring(0, 1), homeIcon);
                    }
                    finally { GUI.color = old; }
                    GUI.Label(new Rect(x + 4, y + 84, cell - 8, 36), app.Name, homeName);
                    if (open && !homeEditing) { tabletTab = app.Tab; homeVisible = false; Save(); }
                    if (!homeEditing) continue;
                    int index = homeIds.IndexOf(app.Id);
                    if (homeLibrary)
                    {
                        if (HomeButton(new Rect(x + 12, y + 120, cell - 24, 34), index < 0 ? "+" : "-"))
                        { if (index < 0) homeIds.Add(app.Id); else homeIds.RemoveAt(index); SaveHomeIds(); }
                    }
                    else
                    {
                        float w = (cell - 16) / 3;
                        if (HomeButton(new Rect(x + 8, y + 120, w, 34), "<", index > 0))
                        { homeIds.RemoveAt(index); homeIds.Insert(index - 1, app.Id); SaveHomeIds(); break; }
                        if (HomeButton(new Rect(x + 8 + w, y + 120, w, 34), "-"))
                        { homeIds.RemoveAt(index); SaveHomeIds(); break; }
                        if (HomeButton(new Rect(x + 8 + 2 * w, y + 120, w, 34), ">", index < homeIds.Count - 1))
                        { homeIds.RemoveAt(index); homeIds.Insert(index + 1, app.Id); SaveHomeIds(); break; }
                    }
                }
                if (apps.Count == 0) GUI.Label(new Rect(8, 8, viewport.width - 36, 60), "Home is empty. Use All Apps > EDIT to add apps.", homeName);
            }
            finally { GUI.EndScrollView(); }
            if (homeLibrary) libraryScroll = scroll; else homeScroll = scroll;
        }
        static void DrawTabletHomeBar(Rect bar)
        {
            EnsureHomeStyles();
            bool old = GUI.enabled;
            try
            {
                GUI.enabled = true;
                GUI.Box(bar, GUIContent.none, homeTile);
                if (HomeButton(new Rect(bar.x + 8, bar.y, 84, bar.height), "BACK")) GoTabletHome();
                if (HomeButton(new Rect(bar.center.x - 70, bar.y, 140, bar.height), "HOME")) GoTabletHome();
                if (HomeButton(new Rect(bar.xMax - 100, bar.y, 92, bar.height), "All Apps")) { homeVisible = true; homeLibrary = true; }
                var e = Event.current;
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { GoTabletHome(); e.Use(); }
            }
            finally { GUI.enabled = old; }
        }
    }
}
