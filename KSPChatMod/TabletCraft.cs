// GROK TAB-CRAFT1: AUTOPILOT / CRAFT / DETAILS - the active vessel's craft_overrides (CraftOverrides.Defs, grouped SPEEDS / APPROACH /
// TOUCHDOWN / ROLLOUT / TAKEOFF) in a scroll list: -/+ (small/large step), a number field + SET, AUTO/ON/OFF for switches, CLEAR per row and
// RESET ALL TO AUTO (with confirmation). Saved at once and applied live; the wheel over the tablet scrolls (TabletInputLock), not the camera.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static int tabletCraftPage; static string tabletCraftFor; static string[] tabletCraftText, tabletCraftAutoShown;   // AUTO2: auto text currently placed in each field
        static Vector2 tabletCraftScroll; static bool tabletCraftConfirm;

        void TabletCraftSub(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            float row = Mathf.Max(44, tabletFont * 2.5f), gap = 8;
            int n = TabletNav.CraftPages.Length; float pw = Mathf.Max(150, tabletFont * 8f);
            for (int i = 0; i < n; i++) if (TabletUi.Button(new Rect(area.x + i * (pw + gap), area.y, pw, row), TabletNav.CraftPages[i] + " >", tabletCraftPage == i)) tabletCraftPage = i;   // page nav
            Rect body = new Rect(area.x, area.y + row + gap, area.width, Mathf.Max(60, area.height - row - gap));
            if (!NativeFlightController.CraftReady) { TabletUi.Label(body, "No active vessel.", true); return; }
            string craft = NativeFlightController.CraftName; var defs = CraftOverrides.Defs;
            GUILayout.BeginArea(body);
            try
            {
                tabletCraftScroll = GUILayout.BeginScrollView(tabletCraftScroll);
                try
                {
                    float w = body.width - 26, y = 0, pad = 16 + tabletFont * 1.6f; int groups = 0; string g0 = null;
                    foreach (var d in defs) if (d.Group != g0) { groups++; g0 = d.Group; }
                    Rect probe = new Rect(12, 0, w - 24, 0); float rowsH = 0; for (int k = 0; k < defs.Count; k++) rowsH += CraftOverrideRowHeight(probe, k, defs[k].Label, row) + gap;   // UI1: measured
                float eh = pad + rowsH + groups * (row * .8f) + row + gap;
                    Rect c = TabletUi.CardPanel(new Rect(0, y, w, eh), "DETAILS - " + craft + " (dim value = automatic)");
                    float ry = c.y; g0 = null;
                    for (int i = 0; i < defs.Count; i++)
                    {
                        if (defs[i].Group != g0) { g0 = defs[i].Group; TabletUi.Label(new Rect(c.x, ry, c.width, row * .8f), g0, true); ry += row * .8f; }
                        ry = CraftOverrideRow(c, ry, i, defs[i].Label, row, TabletLink) + gap;
                    }
                    float rb = Mathf.Max(260, tabletFont * 14f);
                    if (!tabletCraftConfirm) { if (TabletUi.ActionButton(new Rect(c.x, ry, rb, row), "RESET ALL TO AUTO", TabletLink)) tabletCraftConfirm = true; }
                    else
                    {
                        if (TabletUi.ActionButton(new Rect(c.x, ry, rb, row), "CONFIRM RESET ALL", TabletLink)) { NativeFlightController.CraftOverrideSet(null, double.NaN); tabletCraftFor = null; tabletCraftConfirm = false; }
                        if (TabletUi.Button(new Rect(c.x + rb + 8, ry, Mathf.Max(120, tabletFont * 6f), row), "CANCEL")) tabletCraftConfirm = false;
                    }
                    y += eh + gap;
                    y += TextCard(new Rect(0, y, w, 0), "LEARNED / IN USE (read-only)", NativeFlightController.CraftInfoLines()) + gap;
                    GUILayout.Space(y);
                }
                finally { GUILayout.EndScrollView(); }
            }
            finally { GUILayout.EndArea(); }
        }

        /// <summary>UI1: the row's label text ("<label> <value> <unit> (auto)" / "<label>: ON (auto)").</summary>
        string CraftRowHead(int i, string label, out bool dim)
        {
            var d = CraftOverrides.Defs[i]; double cur = NativeFlightController.CraftOverrideGet(d.Key), use = NativeFlightController.CraftInUse(d.Key); dim = double.IsNaN(cur);
            if (d.Bool) { bool on = dim ? use >= .5 : cur >= .5; return label + ": " + (on ? "ON" : "OFF") + (dim ? " (auto)" : ""); }
            return string.IsNullOrEmpty(d.Unit) ? label : label + " (" + d.Unit + ")";   // AUTO2: the value lives in the field now (dim + AUTO lit = automatic)
        }
        float CraftLabelW(Rect c) { return Mathf.Max(260, c.width * .36f); }
        /// <summary>UI1: measured label height when the label does not fit its column (0 = single line, controls beside it).</summary>
        float CraftRowLabelH(Rect c, int i, string label, float row)
        {
            bool dim; string head = CraftRowHead(i, label, out dim);
            return Mathf.Max(row * .8f, TabletUi.WrapHeight(head, c.width - 2 * CraftBoxPad));   // UI2: always its own full-width line(s); UI3: inside the box padding
        }
        /// <summary>UI1: real height of a row - long labels wrap on their own line(s) with the controls below.</summary>
        internal float CraftOverrideRowHeight(Rect c, int i, string label, float row) { return CraftBoxPad + CraftRowLabelH(c, i, label, row) + CraftRowDescH(c, i) + CraftRowGap + row + CraftBoxPad; }
        /// <summary>AUTO1: the setting's one-line description under its label (wrapped, measured).</summary>
        float CraftRowDescH(Rect c, int i) { return TabletUi.SmallWrapHeight(CraftOverrides.Desc(CraftOverrides.Defs[i].Key), c.width - 2 * CraftBoxPad); }   // UI3: box padding included (callers add the gap between boxes)
        const float CraftRowGap = 4, CraftRowIndent = 12, CraftBoxPad = 7;   // UI2: label-to-controls gap, controls indent (same for every row)
        static string StepText(double s) { return (s > 0 ? "+" : "") + s.ToString("0.##"); }
        /// <summary>One override row, shared by CRAFT / DETAILS and the LANDING page (same craft_overrides entry and text buffers, so both
        /// pages stay in sync). Numbers: "<label> <value> <unit>" | -L -S [field] +S +L SET X. Switches: AUTO / ON / OFF. Returns the next y.
        /// UI1: a label wider than its column is word-wrapped across the full width, controls on the line below (measured height).</summary>
        float CraftOverrideRow(Rect c, float ry, int i, string label, float row, bool en)
        {
            var defs = CraftOverrides.Defs; string craft = NativeFlightController.CraftName;
            if (tabletCraftFor != craft || tabletCraftText == null || tabletCraftText.Length != defs.Count)
            { tabletCraftFor = craft; tabletCraftText = new string[defs.Count]; tabletCraftAutoShown = new string[defs.Count]; for (int k = 0; k < defs.Count; k++) tabletCraftText[k] = CraftOverrides.Text(NativeFlightController.CraftOverrideGet(defs[k].Key)); }
            var d = defs[i]; double cur = NativeFlightController.CraftOverrideGet(d.Key), use = NativeFlightController.CraftInUse(d.Key);
            float boxH = CraftOverrideRowHeight(c, i, label, row); TabletUi.RowBox(new Rect(c.x, ry, c.width, boxH)); float rowEnd = ry + boxH; ry += CraftBoxPad;   // UI3: each setting in its own box
            float fw = Mathf.Max(90, tabletFont * 5f), x = c.x + CraftBoxPad + CraftRowIndent;   // UI2: one layout - label line(s) full width, controls below at a fixed indent
            float bw = Mathf.Max(52, TabletUi.ButtonWidth("+0.5")); foreach (double s in new[] { -d.Large, -d.Small, d.Small, d.Large }) bw = Mathf.Max(bw, TabletUi.ButtonWidth(StepText(s)));
            float sw = Mathf.Max(bw, TabletUi.ButtonWidth("SET")), ow = Mathf.Max(bw * 1.4f, TabletUi.ButtonWidth("AUTO"));
            bool dim; string head = CraftRowHead(i, label, out dim); float lh = CraftRowLabelH(c, i, label, row);
            TabletUi.LabelWrap(new Rect(c.x + CraftBoxPad, ry, c.width - 2 * CraftBoxPad, lh), head, dim); ry += lh;
            { float dh = CraftRowDescH(c, i); TabletUi.SmallWrap(new Rect(c.x + CraftBoxPad, ry, c.width - 2 * CraftBoxPad, dh), CraftOverrides.Desc(d.Key)); ry += dh + CraftRowGap; }   // AUTO1
            /* UI2: no side-by-side label */
            if (d.Bool)
            {
                bool auto = double.IsNaN(cur), on = auto ? use >= .5 : cur >= .5;
                if (TabletUi.Button(new Rect(x, ry, ow, row), "AUTO", auto, en) && !auto) CraftSet(i, double.NaN); x += ow + 4;
                if (TabletUi.Button(new Rect(x, ry, ow, row), "ON", !auto && on, en)) CraftSet(i, 1); x += ow + 4;
                if (TabletUi.Button(new Rect(x, ry, ow, row), "OFF", !auto && !on, en)) CraftSet(i, 0);
                return rowEnd;
            }
            foreach (double s in new[] { -d.Large, -d.Small }) { if (TabletUi.ActionButton(new Rect(x, ry, bw, row), StepText(s), en, false)) CraftSet(i, CraftOverrides.Bump(cur, s, use, d.Key)); x += bw + 4; }
            bool isAuto; string shown = CraftOverrides.FieldText(cur, use, tabletCraftText[i], ref tabletCraftAutoShown[i], out isAuto);   // AUTO2: blank = the autopilot's live value, dimmed
            tabletCraftText[i] = TabletUi.Field(new Rect(x, ry, fw, row), shown, en, isAuto); x += fw + 4;
            foreach (double s in new[] { d.Small, d.Large }) { if (TabletUi.ActionButton(new Rect(x, ry, bw, row), StepText(s), en, false)) CraftSet(i, CraftOverrides.Bump(cur, s, use, d.Key)); x += bw + 4; }
            double? p = CraftOverrides.Parse(d.Key, tabletCraftText[i]);
            if (TabletUi.ActionButton(new Rect(x, ry, sw, row), "SET", en && p.HasValue, false)) CraftSet(i, p.Value); x += sw + 4;
            if (TabletUi.Button(new Rect(x, ry, ow, row), "AUTO", isAuto, en) && !isAuto) CraftSet(i, double.NaN);   // AUTO2: lit = following the autopilot; tap = reset to auto
            return rowEnd;
        }
        static int CraftIndex(string key) { return Array.IndexOf(CraftOverrides.Fields, key); }

        static void CraftSet(int i, double v)
        {
            string key = CraftOverrides.Fields[i];
            NativeFlightController.CraftOverrideSet(key, v);
            if (tabletCraftText != null && i < tabletCraftText.Length) { tabletCraftText[i] = CraftOverrides.Text(NativeFlightController.CraftOverrideGet(key)); if (tabletCraftAutoShown != null && i < tabletCraftAutoShown.Length) tabletCraftAutoShown[i] = null; }
        }
    }
}