// Tablet AIRCRAFT page: the control box's panel 0 (AicsMenu.Aircraft) in modern tablet style. Same data (the apXxx hold
// statics + NativeFlightController.ApSummary) and the same handlers (OnKey("aircraft", key), EngageHolds) - no new
// autopilot logic. LIVE LINK off = every control greyed and dead; the summary and the hold-error gauges stay visible.
using System;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        /// <summary>Flight tab pages (the suggested layout puts aircraft / approach / taxi / trim / abort / about here).</summary>
        internal static readonly string[] TabletFlightPages = { "FLIGHT", "AIRCRAFT" };
        static int tabletFlightPage; static Vector2 apScroll;

        /// <summary>Flight tab = a Segments sub-tab row + the page. Page switching is navigation, not a craft control
        /// (same as the Map tab's MAP/CHART/ILS row), so it works with LIVE LINK off.</summary>
        void TabletFlightTab(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            float sh = Mathf.Max(44, tabletFont * 2.4f);
            int p = TabletUi.Segments(new Rect(area.x, area.y, area.width, sh), TabletFlightPages, tabletFlightPage);
            if (p != tabletFlightPage) { tabletFlightPage = p; Save(); }
            Rect body = new Rect(area.x, area.y + sh + 8, area.width, Mathf.Max(60, area.height - sh - 8));
            if (tabletFlightPage == 1) TabletAircraft(body); else TabletFlight(body);
        }

        void TabletAircraft(Rect area)
        {
            if (!apLoaded) { apLoaded = true; ApLoad(); }   // the same saved holds file the control box panel reads/writes
            string sh = ApFieldSync.Take(); if (sh != null) { apHdgT = sh; apHdg = true; }   // heading set from chat / Learn
            var v = FlightGlobals.ActiveVessel;
            // key row: the MFD AIRCRAFT right column (MfdNav.Right), touch size, dead while LIVE LINK is off
            var keys = MfdNav.Right("aircraft"); float kh = Mathf.Max(48, tabletFont * 2.6f), x = area.x, y = area.y;
            GUI.enabled = TabletLink;
            foreach (var k in keys)
            {
                if (string.IsNullOrEmpty(k)) continue; float kw = Mathf.Max(110, tabletFont * (k.Length * .7f + 2f));
                if (x + kw > area.xMax) { x = area.x; y += kh + 6; }
                if (TabletUi.Button(new Rect(x, y, kw, kh), k, false, TabletLink)) OnKey("aircraft", k);
                x += kw + 8;
            }
            GUI.enabled = true;
            if (!TabletLink) TabletUi.Label(new Rect(x + 4, y, Mathf.Max(80, area.xMax - x - 4), kh), "INFO ONLY - turn LIVE LINK on to use the controls", true);
            float top = y + kh + 8; if (area.yMax - top < 100) return;
            // body: one column inside a scroll view, so the whole page is reachable in a small tablet window too
            GUILayout.BeginArea(new Rect(area.x, top, area.width, area.yMax - top));
            apScroll = GUILayout.BeginScrollView(apScroll);
            float gap = 10, w = area.width - 26, gy = 0;   // 26 px = room for the vertical scrollbar
            gy += AircraftSummaryCard(new Rect(0, gy, w, 0), gap);
            gy += AircraftErrorsCard(new Rect(0, gy, w, 0), gap, v);
            gy += AircraftHoldsCard(new Rect(0, gy, w, 0), gap);
            GUILayout.Space(gy + 4);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            ApSave();   // persists every change, exactly like the control box panel does
        }
        /// <summary>Card 1: the panel's own summary lines (job / plan / target) + holds state and the live job.</summary>
        float AircraftSummaryCard(Rect r0, float gap)
        {
            var sum = NativeFlightController.ApSummary().Split('\n');
            float lh = Mathf.Max(22, tabletFont * 1.45f), ch = Mathf.Max(30, tabletFont * 1.9f);
            float h = 12 + lh * sum.Length + 6 + ch + 10;
            Rect c = TabletUi.CardPanel(new Rect(r0.x, r0.y, r0.width, h), "Autopilot");
            for (int i = 0; i < sum.Length; i++) TabletUi.Label(new Rect(c.x, c.y + i * lh, c.width, lh), sum[i].Trim(), !sum[i].StartsWith("Target"));
            float cy = c.y + sum.Length * lh + 6, cw = (c.width - gap) / 2;
            TabletUi.Chip(new Rect(c.x, cy, cw, ch), apEngaged ? "HOLDS ENGAGED" : "HOLDS OFF", apEngaged ? TabletUi.Good : TabletUi.Dim);
            string job = NativeFlightController.Phase;
            TabletUi.Chip(new Rect(c.x + cw + gap, cy, cw, ch), "JOB " + job.ToUpperInvariant(), job == "idle" ? TabletUi.Dim : TabletUi.Accent);
            return h + gap;
        }

        /// <summary>Card 2: how far the live values are from the hold targets (centre-zero gauges; information only).</summary>
        float AircraftErrorsCard(Rect r0, float gap, Vessel v)
        {
            float bh = Mathf.Max(34, tabletFont * 2.1f), h = 12 + bh * 5 + 5 * 4 + 8;
            Rect c = TabletUi.CardPanel(new Rect(r0.x, r0.y, r0.width, h), "Live vs target");
            if (v == null) { TabletUi.Label(new Rect(c.x, c.y, c.width, c.height), "No active vessel.", true); return h + gap; }
            double hdg, pitch, roll, aoa; Attitude(v, out hdg, out pitch, out roll, out aoa);
            float y = c.y;
            y = DevRow(new Rect(c.x, y, c.width, bh), "Altitude " + (apAgl ? "AGL" : "MSL"), apAlt, (apAgl ? v.radarAltitude : v.altitude) - TabletNum(apAltT, 0), 500, "m", "#,0");
            y = DevRow(new Rect(c.x, y, c.width, bh), "Vertical speed", apVs, v.verticalSpeed - TabletNum(apVsT, 0), 20, "m/s", "0.0");
            y = DevRow(new Rect(c.x, y, c.width, bh), "Heading", apHdg, TabletWrap180(hdg - TabletNum(apHdgT, 0)), 45, "\u00B0", "0");
            y = DevRow(new Rect(c.x, y, c.width, bh), "Roll", apRoll, TabletWrap180(roll - TabletNum(apRollT, 0)), 30, "\u00B0", "0");
            y = DevRow(new Rect(c.x, y, c.width, bh), "Speed", apSpd, v.srfSpeed - TabletNum(apSpdT, 0), 50, "m/s", "0.0");
            return h + gap;
        }
        /// <summary>Card 3: the five holds + the altitude reference + engage (the same statics and EngageHolds() the panel uses).</summary>
        float AircraftHoldsCard(Rect r0, float gap)
        {
            float rh = Mathf.Max(44, tabletFont * 2.5f), sh = Mathf.Max(44, tabletFont * 2.4f), bh = Mathf.Max(48, tabletFont * 2.6f), lh = Mathf.Max(22, tabletFont * 1.45f);
            float h = 12 + rh * 5 + 4 * 4 + sh + 6 + lh * 2 + 8 + bh + 10;
            Rect c = TabletUi.CardPanel(new Rect(r0.x, r0.y, r0.width, h), "Holds");
            float fw = Mathf.Max(110, tabletFont * 6f), tw = Mathf.Min(c.width * .45f, Mathf.Max(180, tabletFont * 11f)), uw = Mathf.Max(40, c.width - tw - fw - 24);
            float y = c.y;
            y = HoldRow(new Rect(c.x, y, c.width, rh), "Altitude", ref apAlt, ref apAltT, "m", tw, fw, uw);
            GUI.enabled = TabletLink;   // Segments has no enabled arg: GUI.enabled keeps the reference switch dead while LIVE LINK is off
            int agl = TabletUi.Segments(new Rect(c.x + tw + 8, y, Mathf.Max(120, c.width - tw - 8), sh), new[] { "AGL (ground)", "MSL (sea)" }, apAgl ? 0 : 1);
            GUI.enabled = true; if (TabletLink) apAgl = agl == 0;
            y += sh + 6;
            y = HoldRow(new Rect(c.x, y, c.width, rh), "Vertical speed", ref apVs, ref apVsT, "m/s", tw, fw, uw);
            y = HoldRow(new Rect(c.x, y, c.width, rh), "Heading", ref apHdg, ref apHdgT, "\u00B0", tw, fw, uw);
            y = HoldRow(new Rect(c.x, y, c.width, rh), "Roll", ref apRoll, ref apRollT, "\u00B0", tw, fw, uw);
            y = HoldRow(new Rect(c.x, y, c.width, rh), "Speed (actual)", ref apSpd, ref apSpdT, "m/s", tw, fw, uw);
            TabletUi.Label(new Rect(c.x, y, c.width, lh), "Throttle drives V/S and speed; pitch never trades altitude for speed.", true);
            TabletUi.Label(new Rect(c.x, y + lh, c.width, lh), "Bank cap 20\u00B0 / 10\u00B0 fast.", true);
            y += lh * 2 + 8;
            float bw = (c.width - gap) / 2;
            if (TabletUi.Button(new Rect(c.x, y, bw, bh), apEngaged ? "UPDATE HOLDS" : "ENGAGE", apEngaged, TabletLink)) { EngageHolds(); apEngaged = true; }
            if (TabletUi.Button(new Rect(c.x + bw + gap, y, bw, bh), "DISENGAGE", false, TabletLink)) { ChatWindow.ToolFromMenu("plane_hold", "{\"engage\":false}"); apEngaged = false; }
            return h + gap;
        }

        /// <summary>One hold row: touch switch + touch number field + unit. Dead while LIVE LINK is off; returns the next y.</summary>
        float HoldRow(Rect r, string label, ref bool on, ref string val, string unit, float tw, float fw, float uw)
        {
            bool no = TabletUi.Toggle(new Rect(r.x, r.y, tw, r.height), label, on, TabletLink); if (TabletLink) on = no;
            float fh = Mathf.Max(36, r.height * .72f), fy = r.y + (r.height - fh) / 2;
            string nv = TabletUi.Field(new Rect(r.x + tw + 8, fy, fw, fh), val, TabletLink); if (TabletLink) val = nv;
            TabletUi.LabelRight(new Rect(r.x + tw + fw + 16, r.y, uw, r.height), unit, !on);
            return r.y + r.height + 4;
        }
        /// <summary>One centre-zero deviation gauge: |err| within a quarter of the range = green, within the range = amber, else red.</summary>
        static float DevRow(Rect r, string caption, bool on, double err, double range, string unit, string fmt)
        {
            double a = Math.Abs(err);
            TabletUi.DevBar(r, caption, (float)(err / range), (err < 0 ? "-" : "+") + a.ToString(fmt, CultureInfo.InvariantCulture) + " " + unit,
                a <= range * .25 ? TabletUi.Good : a <= range ? TabletUi.Warn : TabletUi.Bad, !on);
            return r.y + r.height + 4;
        }
        static double TabletNum(string t, double fallback) { double d; return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback; }
        static double TabletWrap180(double a) { a %= 360; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }
    }
}