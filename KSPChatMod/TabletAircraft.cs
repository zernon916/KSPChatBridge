// Tablet AIRCRAFT page: the control box's panel 0 (AicsMenu.Aircraft) in modern tablet style. Same data (the apXxx hold
// statics + NativeFlightController.ApSummary) and the same handlers (OnKey("aircraft", key), EngageHolds) - no new
// autopilot logic. LIVE LINK off = every control greyed and dead; the summary and the hold-error gauges stay visible.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        // GROK TAB1: top tabs FLIGHT / AUTOPILOT / ABORT / CRAFT (TabletNav); TAKEOFF / LANDING / STATUS / TAXI / TRIM are AUTOPILOT
        // sub-pages. Tabs and sub-page buttons are navigation (work with LIVE LINK off); actions live inside the sub-pages.
        static int tabletFlightPage, tabletApSub = -1; static Vector2 apScroll, apSubScroll;

        void TabletFlightTab(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            float sh = Mathf.Max(44, tabletFont * 2.4f);
            int p = TabletUi.Segments(new Rect(area.x, area.y, area.width, sh), TabletNav.TopTabs, tabletFlightPage);
            if (p != tabletFlightPage) { tabletFlightPage = p; tabletApSub = -1; Save(); }
            Rect body = new Rect(area.x, area.y + sh + 8, area.width, Mathf.Max(60, area.height - sh - 8));
            if (tabletFlightPage == TabletNav.Abort) TabletAbort(body);
            else if (tabletFlightPage == TabletNav.Craft) TabletShipInfo(body);
            else if (tabletFlightPage == TabletNav.Autopilot) TabletAutopilot(body);
            else TabletFlight(body);
        }

        /// <summary>AUTOPILOT: the holds page + a bottom row of sub-page buttons; a sub-page gets a "< AUTOPILOT" back button.</summary>
        void TabletAutopilot(Rect area)
        {
            float kh = Mathf.Max(48, tabletFont * 2.6f);
            if (tabletApSub >= 0 && tabletApSub < TabletNav.ApSubPages.Length)
            {
                float bw = Mathf.Max(150, tabletFont * 8.5f);
                if (TabletUi.Button(new Rect(area.x, area.y, bw, kh), "< AUTOPILOT")) { tabletApSub = -1; Save(); return; }
                TabletUi.Label(new Rect(area.x + bw + 14, area.y, area.width - bw - 14, kh), "AUTOPILOT / " + TabletNav.ApSubPages[tabletApSub]);
                Rect sb = new Rect(area.x, area.y + kh + 8, area.width, Mathf.Max(60, area.height - kh - 8));
                switch (tabletApSub)
                {
                    case TabletNav.SubTakeoff: TabletTakeoffSub(sb); break;
                    case TabletNav.SubLanding: TabletApproach(sb); break;
                    case TabletNav.SubStatus: TabletStatusSub(sb); break;
                    case TabletNav.SubTaxi: TabletTaxi(sb); break;
                    case TabletNav.SubCraft: TabletCraftSub(sb); break;
                    default: TabletTrim(sb); break;
                }
                return;
            }
            int n = TabletNav.ApSubPages.Length; float gap = 8, nw = (area.width - gap * (n - 1)) / n, ny = area.yMax - kh;
            for (int i = 0; i < n; i++)
                if (TabletUi.Button(new Rect(area.x + i * (nw + gap), ny, nw, kh), TabletNav.ApSubPages[i] + " >")) { tabletApSub = i; Save(); }
            TabletAircraft(new Rect(area.x, area.y, area.width, Mathf.Max(60, area.height - kh - gap)));
        }

        /// <summary>Card helper for the simple phase-1 sub-pages: caption + text lines; returns the height used.</summary>
        float TextCard(Rect r, string caption, IList<string> lines)
        {
            float lh = Mathf.Max(26, tabletFont * 1.5f), h = 16 + tabletFont * 1.6f + lines.Count * lh;
            Rect c = TabletUi.CardPanel(new Rect(r.x, r.y, r.width, h), caption);
            for (int i = 0; i < lines.Count; i++) TabletUi.Label(new Rect(c.x, c.y + i * lh, c.width, lh), lines[i]);
            return h;
        }
        /// <summary>TAKEOFF sub-page (phase 1): current autopilot summary + ENGAGE TAKEOFF (on the ground only).</summary>
        void TabletTakeoffSub(Rect area)
        {
            var v = FlightGlobals.ActiveVessel; float row = Mathf.Max(48, tabletFont * 2.6f);
            if (toSet == null) ToLoad();
            var lines = new List<string>();
            if (v != null) lines.Add("Situation: " + v.situation + (v.LandedOrSplashed ? " (on the ground)" : " (airborne)") + "   Runway heading " + (v.LandedOrSplashed ? FlightGlobals.ship_heading.ToString("000") : "-"));
            lines.Add(FuelReadout(v) + "   Flaps: " + (NativeFlightController.HasFlapsNow ? "available" : "none found"));
            float y = area.y + TextCard(area, "Takeoff", lines) + 10;
            bool can = TabletLink && v != null && v.LandedOrSplashed;
            if (NativeFlightController.CraftReady)
            {   // TAB-TAKEOFF2: rotation speed / angle (same craft_overrides as CRAFT > DETAILS), live during the roll
                float pad = 16 + tabletFont * 1.6f, rr = Mathf.Max(44, tabletFont * 2.5f);
                Rect rp = new Rect(12, 0, area.width - 24, 0); float rh = CraftOverrideRowHeight(rp, CraftIndex(CraftOverrides.Vr), "Rotation speed", rr) + 8 + CraftOverrideRowHeight(rp, CraftIndex(CraftOverrides.RotatePitch), "Rotation angle", rr);   // UI2: measured
                Rect sc = TabletUi.CardPanel(new Rect(area.x, y, area.width, pad + rh), "Rotation - " + NativeFlightController.CraftName + " (also in CRAFT > DETAILS)");
                float sy = CraftOverrideRow(sc, sc.y, CraftIndex(CraftOverrides.Vr), "Rotation speed", rr, TabletLink) + 8;
                CraftOverrideRow(sc, sy, CraftIndex(CraftOverrides.RotatePitch), "Rotation angle", rr, TabletLink);
                y += pad + rh + 10;
            }
            {   // GROK TO2: takeoff settings (persisted; sent with ENGAGE TAKEOFF)
                float lh = Mathf.Max(30, tabletFont * 1.7f), lw = Mathf.Min(260, area.width * .45f), fw = Mathf.Min(160, area.width - lw - 10);
                string[] caps = { "Altitude above runway (m)", "Climb rate (m/s, blank = auto)", "Heading after takeoff (blank = runway)", "Max climb pitch (deg)" };
                string[] vals = { toSet.Alt, toSet.Climb, toSet.Hdg, toSet.MaxPitch };
                for (int i = 0; i < 4; i++)
                {
                    TabletUi.Label(new Rect(area.x, y, lw, lh), caps[i]); vals[i] = TabletUi.Field(new Rect(area.x + lw + 10, y, fw, lh - 4), vals[i], TabletLink); y += lh;
                }
                toSet.Alt = vals[0]; toSet.Climb = vals[1]; toSet.Hdg = vals[2]; toSet.MaxPitch = vals[3];
                toSet.Flaps = TabletUi.Toggle(new Rect(area.x, y, area.width, lh), "USE FLAPS" + (NativeFlightController.HasFlapsNow ? "" : " (no flaps found)"), toSet.Flaps, TabletLink && NativeFlightController.HasFlapsNow); y += lh + 8;
                ToSave();
            }
            if (NativeFlightController.Phase == "takeoff") { ActiveJobRow(new Rect(area.x, y, area.width, row), "TAKEOFF ACTIVE"); return; }   // GROK TAB2: repeat presses impossible while active
            if (TabletUi.ActionButton(new Rect(area.x, y, area.width, row), "ENGAGE TAKEOFF", can)) { ChatLog.Write("tablet", "ENGAGE TAKEOFF"); ChatWindow.ToolFromMenu("takeoff", toSet.ToJson()); }
            if (!can) TabletUi.Label(new Rect(area.x, y + row + 6, area.width, row), !TabletLink ? "INFO ONLY - turn LIVE LINK on to use the controls" : "Takeoff needs the craft on the ground.", true);
        }
        static TakeoffSettings toSet; static string toSaved; static double fuelPrev = double.NaN, fuelRate = double.NaN; static float fuelT;
        static string ToPath { get { return System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/takeoff_settings.txt"); } }
        static void ToLoad()
        {
            toSet = new TakeoffSettings();
            try { if (System.IO.File.Exists(ToPath)) { string t = System.IO.File.ReadAllText(ToPath); if (!AtomicFile.Torn(t)) { toSet = TakeoffSettings.Parse(t); toSaved = t.Trim(); } } } catch (Exception) { }
        }
        static void ToSave() { string t = toSet.Serialize(); if (t == toSaved) return; try { AtomicFile.Write(ToPath, t); toSaved = t; } catch (Exception) { } }
        /// <summary>GROK TO2: fuel % (engine propellants) and time left at the current burn rate (sampled every 2 s).</summary>
        static string FuelReadout(Vessel v)
        {
            if (v == null) return "Fuel -";
            double amt = 0, max = 0;
            foreach (Part p in v.parts) foreach (PartResource r in p.Resources)
                    if (r.resourceName != "ElectricCharge" && r.resourceName != "IntakeAir" && r.resourceName != "Ablator" && r.resourceName != "Ore") { amt += r.amount; max += r.maxAmount; }
            if (Time.realtimeSinceStartup - fuelT > 2) { if (!double.IsNaN(fuelPrev)) fuelRate = (fuelPrev - amt) / Math.Max(.5, Time.realtimeSinceStartup - fuelT); fuelPrev = amt; fuelT = Time.realtimeSinceStartup; }
            string time = !double.IsNaN(fuelRate) && fuelRate > 1e-4 ? TimeSpan.FromSeconds(Math.Min(359999, amt / fuelRate)).ToString(@"hh\:mm\:ss") : "-";
            return "Fuel " + (max > 0 ? (100 * amt / max).ToString("0") + "%" : "-") + "   Time " + time;
        }
        /// <summary>GROK TAB2: running job = status text + a DISENGAGE action (stop_current), instead of the ENGAGE button.</summary>
        void ActiveJobRow(Rect r, string what)
        {
            float bw = Mathf.Max(160, r.width * .35f);
            TabletUi.Status(new Rect(r.x, r.y, r.width - bw - 10, r.height), what + "  (" + NativeFlightController.JobText + ")", TabletUi.Good);
            if (TabletUi.ActionButton(new Rect(r.xMax - bw, r.y, bw, r.height), "DISENGAGE", TabletLink)) { ChatLog.Write("tablet", what + ": DISENGAGE"); OnKey("aircraft", "STOP"); }
        }
        /// <summary>STATUS sub-page (phase 1): the autopilot summary / status lines + posting the full status to the chat.</summary>
        void TabletStatusSub(Rect area)
        {
            float row = Mathf.Max(48, tabletFont * 2.6f);
            var lines = new List<string>(NativeFlightController.ApSummary().Split('\n')); lines.Add(NativeFlightController.ApLine);
            if (landing.Length > 0) lines.AddRange(landing.Split('\n'));
            float y = area.y + TextCard(area, "Autopilot status", lines) + 10;
            if (TabletUi.ActionButton(new Rect(area.x, y, area.width, row), "POST STATUS TO CHAT", TabletLink)) OnKey("aircraft", "STATUS");
        }

        void TabletAircraft(Rect area)
        {
            if (!apLoaded) { apLoaded = true; ApLoad(); }   // the same saved holds file the control box panel reads/writes
            string sh = ApFieldSync.Take(); if (sh != null) { apHdgT = sh; apHdg = true; }   // heading set from chat / Learn
            var v = FlightGlobals.ActiveVessel;
            // GROK TAB1: no action key row here any more (TAKEOFF / LANDING / STATUS are sub-pages; GO AROUND / ABORT removed - ABORT is a top tab)
            float top = area.y, lh0 = Mathf.Max(26, tabletFont * 1.5f);
            if (!TabletLink) { TabletUi.Label(new Rect(area.x, top, area.width, lh0), "INFO ONLY - turn LIVE LINK on to use the controls", true); top += lh0 + 4; }
            if (area.yMax - top < 100) return;
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
            apEngaged = NativeFlightController.HoldsEngaged;   // GROK FIX1: chip follows the real autopilot, not the last button press
            var sum = NativeFlightController.ApSummary().Split('\n');
            float lh = Mathf.Max(22, tabletFont * 1.45f), ch = Mathf.Max(30, tabletFont * 1.9f);
            float h = 12 + lh * sum.Length + 6 + ch + 10;
            Rect c = TabletUi.CardPanel(new Rect(r0.x, r0.y, r0.width, h), "Autopilot");
            for (int i = 0; i < sum.Length; i++) TabletUi.Label(new Rect(c.x, c.y + i * lh, c.width, lh), sum[i].Trim(), !sum[i].StartsWith("Target"));
            float cy = c.y + sum.Length * lh + 6, cw = (c.width - gap) / 2;
            TabletUi.Status(new Rect(c.x, cy, cw, ch), apEngaged ? "HOLDS ENGAGED" : "HOLDS OFF", apEngaged ? TabletUi.Good : TabletUi.Dim);   // GROK TAB1: status text, not a button look
            string job = NativeFlightController.JobText;   // GROK TAB2: plain words, LANDING never shows as "hold"
            TabletUi.Status(new Rect(c.x + cw + gap, cy, cw, ch), job, job == "IDLE" ? TabletUi.Dim : TabletUi.Accent);
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
            double a = Math.Abs(err); bool show = TabletNav.ShowDeviation(NativeFlightController.HoldsEngaged, on);   // GROK TAB1: '-' while holds are off
            TabletUi.DevBar(r, caption, show ? (float)(err / range) : 0, TabletNav.DeviationText(NativeFlightController.HoldsEngaged, on, err, fmt, unit),
                !show ? TabletUi.Dim : a <= range * .25 ? TabletUi.Good : a <= range ? TabletUi.Warn : TabletUi.Bad, !show);
            return r.y + r.height + 4;
        }
        static double TabletNum(string t, double fallback) { double d; return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback; }
        static double TabletWrap180(double a) { a %= 360; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }
    }
}
