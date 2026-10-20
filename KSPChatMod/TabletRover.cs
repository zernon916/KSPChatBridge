// RS1: rover tablet pages (AUTOPILOT when the CRAFT page ROVER switch is on): DRIVE / SCIENCE TOUR / PLACES / CRAFT, plus the
// PLANE | ROVER switch card drawn at the very top of AUTOPILOT / CRAFT. Commands go through NativeFlightController.Execute.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static int tabletRoverSub = -1; static Vector2 roverScroll; static double tabletRoverSpeed = 6; static string tabletRoverNote = "";
        static readonly string[] RoverBuildings = { "VAB", "SPH", "R&D", "Tracking Station", "Mission Control", "Astronaut Complex", "Administration", "Launch Pad", "Runway" };

        /// <summary>RS1: PLANE | ROVER switch + the classifier hint. Returns the height used.</summary>
        float TabletVehicleSwitch(Rect r, float row)
        {
            string hint = "Detected: " + VehicleClass.Name(NativeFlightController.CurrentClass) + " (" + NativeFlightController.CurrentClassReason + ")";
            float pad = 16 + tabletFont * 1.6f, hh = TabletUi.SmallWrapHeight(hint, r.width - 24) + 4, h = pad + row + 6 + hh;
            Rect c = TabletUi.CardPanel(new Rect(r.x, r.y, r.width, h), "VEHICLE - plane pages or rover pages for this craft");
            bool rover = NativeFlightController.RoverMode; float bw = Mathf.Max(160, tabletFont * 8f);
            if (TabletUi.Button(new Rect(c.x, c.y, bw, row), "PLANE", !rover, TabletLink) && rover) { NativeFlightController.SetRoverMode(false); tabletRoverSub = -1; }
            if (TabletUi.Button(new Rect(c.x + bw + 8, c.y, bw, row), "ROVER", rover, TabletLink) && !rover) { NativeFlightController.SetRoverMode(true); tabletRoverSub = NativeFlightController.RoverMode ? TabletNav.RoverCraft : -1; }
            TabletUi.SmallWrap(new Rect(c.x, c.y + row + 6, c.width, hh), hint);
            return h;
        }

        void TabletRoverAutopilot(Rect area)
        {
            float kh = Mathf.Max(48, tabletFont * 2.6f);
            if (tabletRoverSub >= 0 && tabletRoverSub < TabletNav.RoverSubPages.Length)
            {
                float bw = Mathf.Max(150, tabletFont * 8.5f);
                if (TabletUi.Button(new Rect(area.x, area.y, bw, kh), "< ROVER")) { tabletRoverSub = -1; return; }
                TabletUi.Label(new Rect(area.x + bw + 14, area.y, area.width - bw - 14, kh), "ROVER / " + TabletNav.RoverSubPages[tabletRoverSub]);
                Rect sb = new Rect(area.x, area.y + kh + 8, area.width, Mathf.Max(60, area.height - kh - 8));
                switch (tabletRoverSub)
                {
                    case TabletNav.RoverDrive: TabletRoverDrive(sb); break;
                    case TabletNav.RoverTour: TabletRoverTour(sb); break;
                    case TabletNav.RoverPlaces: TabletRoverPlaces(sb); break;
                    default: TabletCraftSub(sb); break;
                }
                return;
            }
            int n = TabletNav.RoverSubPages.Length; float gap = 8, nw = (area.width - gap * (n - 1)) / n, ny = area.yMax - kh;
            for (int i = 0; i < n; i++)
                if (TabletUi.Button(new Rect(area.x + i * (nw + gap), ny, nw, kh), TabletNav.RoverSubPages[i] + " >")) tabletRoverSub = i;
            Rect body = new Rect(area.x, area.y, area.width, Mathf.Max(60, area.height - kh - gap));
            GUILayout.BeginArea(body);
            try
            {
                roverScroll = GUILayout.BeginScrollView(roverScroll);
                try { float h = TextCard(new Rect(0, 0, body.width - 26, 0), "ROVER STATUS", NativeFlightController.RoverStatusLines()); GUILayout.Space(h); }
                finally { GUILayout.EndScrollView(); }
            }
            finally { GUILayout.EndArea(); }
        }

        static void RoverRun(string tool, string json)
        {
            string r = NativeFlightController.Execute(tool, json ?? "{}"); tabletRoverNote = r ?? "";
            ChatLog.Write("tablet", "rover " + tool + " " + (json ?? "{}") + " -> " + tabletRoverNote);
        }
        static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        void TabletRoverDrive(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.5f), gap = 8, bw = Mathf.Max(120, tabletFont * 6f), y = area.y; bool en = TabletLink;
            float x = area.x;
            if (TabletUi.ActionButton(new Rect(x, y, bw * .6f, row), "-1", en, false)) tabletRoverSpeed = Math.Max(1, tabletRoverSpeed - 1); x += bw * .6f + 4;
            TabletUi.Label(new Rect(x, y, bw * 1.4f, row), "Speed " + tabletRoverSpeed.ToString("0", CultureInfo.InvariantCulture) + " m/s"); x += bw * 1.4f + 4;
            if (TabletUi.ActionButton(new Rect(x, y, bw * .6f, row), "+1", en, false)) tabletRoverSpeed = Math.Min(25, tabletRoverSpeed + 1); x += bw * .6f + 4;
            if (TabletUi.ActionButton(new Rect(x, y, bw * 1.3f, row), "HOLD SPEED", en)) RoverRun("drive_speed", "{\"speed\":" + tabletRoverSpeed.ToString(CultureInfo.InvariantCulture) + "}"); x += bw * 1.3f + 4;
            if (TabletUi.ActionButton(new Rect(x, y, bw, row), "STOP", en)) RoverRun("stop_current", "{}");
            y += row + gap;
            TabletUi.Label(new Rect(area.x, y, area.width, row * .8f), "DRIVE TO (KSC)", true); y += row * .8f;
            x = area.x; float cw = Mathf.Max(bw * 1.4f, tabletFont * 9f);
            foreach (string b in RoverBuildings)
            {
                if (x + cw > area.xMax) { x = area.x; y += row + 4; }
                if (TabletUi.ActionButton(new Rect(x, y, cw, row), b, en)) RoverRun("drive_to_building", "{\"buildings\":" + Q(b) + ",\"science\":true,\"speed\":" + tabletRoverSpeed.ToString(CultureInfo.InvariantCulture) + "}");
                x += cw + 4;
            }
            y += row + gap;
            if (!string.IsNullOrEmpty(tabletRoverNote)) TabletUi.LabelWrap(new Rect(area.x, y, area.width, Mathf.Max(row, area.yMax - y)), tabletRoverNote, true);
        }

        void TabletRoverPlaces(Rect area) { TabletUi.LabelWrap(area, "Named places: not available yet.", true); }

        void TabletRoverTour(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.5f), gap = 8, bw = Mathf.Max(200, tabletFont * 10f); bool en = TabletLink;
            if (TabletUi.ActionButton(new Rect(area.x, area.y, bw, row), "START SCIENCE TOUR", en)) RoverRun("rover_science_tour", "{}");
            if (TabletUi.ActionButton(new Rect(area.x + bw + 8, area.y, bw * .6f, row), "STOP", en)) RoverRun("stop_current", "{}");
            Rect body = new Rect(area.x, area.y + row + gap, area.width, Mathf.Max(60, area.height - row - gap));
            GUILayout.BeginArea(body);
            try
            {
                roverScroll = GUILayout.BeginScrollView(roverScroll);
                try
                {
                    float w = body.width - 26, y = 0;
                    if (!string.IsNullOrEmpty(tabletRoverNote)) y += TextCard(new Rect(0, y, w, 0), "LAST COMMAND", new List<string> { tabletRoverNote }) + gap;
                    GUILayout.Space(y);
                }
                finally { GUILayout.EndScrollView(); }
            }
            finally { GUILayout.EndArea(); }
        }
    }
}