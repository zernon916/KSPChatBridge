// RS6: phone-style status bar in the tablet bezel header (tablet only): a dot plus 4 rising rounded bars (dark = active, light grey =
// inactive) on a light plate, "R" when the link goes through a relay, and a battery gauge with %. Data refreshed at most 2 Hz.
using System;
using CommNet;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static float sbAt = -99; static int sbBars; static double sbSignal, sbDelay, sbBatt = double.NaN; static bool sbLinked, sbRelay; static string sbText = "";
        static readonly Color SbPlate = new Color(.93f, .94f, .96f), SbOn = new Color(.16f, .17f, .19f), SbOff = new Color(.76f, .77f, .80f);

        static void StatusBarRefresh()
        {
            if (Time.realtimeSinceStartup - sbAt < .5f) return; sbAt = Time.realtimeSinceStartup;
            var v = FlightGlobals.ActiveVessel; sbLinked = false; sbRelay = false; sbSignal = 0; sbDelay = 0; sbBatt = double.NaN;
            if (v == null) { sbBars = 0; sbText = ""; return; }
            try
            {
                var cn = v.connection;
                if (cn == null) { sbLinked = true; sbSignal = 1; }   // CommNet off: always linked
                else
                {
                    sbLinked = cn.IsConnected; sbSignal = cn.SignalStrength; sbDelay = cn.SignalDelay;
                    var path = cn.ControlPath; sbRelay = sbLinked && path != null && path.Count > 1;
                }
            }
            catch (Exception) { }
            try { var ec = PartResourceLibrary.Instance.GetDefinition("ElectricCharge"); double a = 0, m = 0; if (ec != null) v.GetConnectedResourceTotals(ec.id, out a, out m); sbBatt = StatusBarMath.Battery(a, m); } catch (Exception) { }
            sbBars = StatusBarMath.Bars(sbLinked, sbSignal); sbText = StatusBarMath.Label(sbLinked, sbSignal, sbDelay, sbRelay, sbBatt);
        }

        float TabletStatusBarWidth(float h) { return h * 1.3f + h * 1.6f + Mathf.Max(90, tabletFont * 7.5f) + 16; }

        void TabletStatusBar(Rect r)
        {
            StatusBarRefresh(); if (FlightGlobals.ActiveVessel == null) return; TabletUi.Ensure(tabletFont);
            float h = r.height, iw = h * 1.3f, x = r.x;
            // signal: light plate, dot + 4 rising rounded bars
            TabletUi.Fill(new Rect(x, r.y, iw, h), SbPlate);
            float pad = h * .14f, ih = h - 2 * pad, unit = (iw - 2 * pad) / 5.4f, bw = unit * .9f;
            float dot = bw; TabletUi.Fill(new Rect(x + pad, r.y + pad + ih - dot, dot, dot), sbLinked ? SbOn : SbOff);
            for (int i = 0; i < StatusBarMath.MaxBars; i++)
            {
                float bh = ih * StatusBarMath.BarHeight(i), bx = x + pad + unit * 1.1f * (i + 1);
                TabletUi.Fill(new Rect(bx, r.y + pad + ih - bh, bw, bh), i < sbBars ? SbOn : SbOff);
            }
            x += iw + 4;
            if (sbRelay) TabletUi.Label(new Rect(x, r.y, h * .5f, h), "R"); x += h * .5f;
            // battery: outline + fill + nub
            float bwid = h * 1.1f, bht = h * .55f, by = r.y + (h - bht) / 2; int band = StatusBarMath.BatteryBand(sbBatt);
            Color fill = band == 2 ? TabletUi.Good : band == 1 ? TabletUi.Warn : TabletUi.Bad;
            TabletUi.Rect(new Rect(x, by, bwid, bht), SbPlate); TabletUi.Rect(new Rect(x + 2, by + 2, bwid - 4, bht - 4), SbOn);
            if (band >= 0) TabletUi.Rect(new Rect(x + 3, by + 3, (bwid - 6) * (float)sbBatt, bht - 6), fill);
            TabletUi.Rect(new Rect(x + bwid, by + bht * .3f, 3, bht * .4f), SbPlate);
            x += bwid + 8;
            TabletUi.Label(new Rect(x, r.y, r.xMax - x, h), sbText, true);
        }
    }
}