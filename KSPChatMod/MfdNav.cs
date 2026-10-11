using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>AICS MFD navigation (Luke): HOME has the group keys; each group is paged (8 item keys per page on the left, PREV /
    /// NEXT on the bottom row when there are more); the right column and bottom keys 1-4 are the selected item's context keys;
    /// BACK is always bottom key 0 on every page except HOME. Every former menu item has a key. Pure; tested.</summary>
    internal static class MfdNav
    {
        internal const int Side = 8, BottomCount = 8, TopCount = 8, BackKey = 0, PrevKey = 6, NextKey = 7, Ctx = 5;
        internal sealed class Item
        {
            internal string Id, Label; internal int Panel = -1;   // AicsMenu panel index, -1 = custom page
            internal Item(string id, string label, int panel = -1) { Id = id; Label = label; Panel = panel; }
        }
        internal sealed class Group { internal string Id, Label; internal bool NeedsMj; internal List<Item> Items = new List<Item>(); }

        internal static List<Group> Groups(bool mj)
        {
            var g = new List<Group>();
            Func<string, string, bool, Item[], Group> G = (id, l, m, it) => { var x = new Group { Id = id, Label = l, NeedsMj = m }; x.Items.AddRange(it); return x; };
            g.Add(G("ap", "AUTOPILOT", false, new[] { new Item("aircraft", "AIRCRAFT", 0), new Item("approach", "APPROACH", 1), new Item("guidance", "LAND GUID", 2), new Item("taxi", "TAXI", 12),
                new Item("about", "ABOUT PLANE"), new Item("trim", "TRIM"), new Item("abort", "ABORT/STAT", 13), new Item("orbitap", "ORBITAL AP", 7), new Item("capture", "CAPTURE", 5), new Item("docking", "DOCKING", 6), new Item("sunlock", "SUN LOCK", 8) }));
            g.Add(G("map", "MAP", false, new[] { new Item("map", "MAP"), new Item("chart", "CHART"), new Item("ils", "ILS") }));
            g.Add(G("plan", "PLAN", false, new[] { new Item("flightplan", "FLIGHT PLAN", 3), new Item("orbitplan", "ORBIT PLAN", 4) }));
            g.Add(G("comms", "COMMS", false, new[] { new Item("intercom", "INTERCOM"), new Item("system", "SYSTEM"), new Item("pilot", "PILOT"), new Item("all", "ALL") }));
            g.Add(G("crew", "CREW", false, new[] { new Item("crew", "CREW / EVA", 11), new Item("science", "SCIENCE", 10) }));
            if (mj) g.Add(G("mj", "MECHJEB", true, new[] { new Item("mjatt", "SMARTASS"), new Item("mjguide", "GUIDANCE") }));
            g.Add(G("sys", "SYS", false, new[] { new Item("alarm", "MSTR ALARM"), new Item("systems", "SYSTEMS"), new Item("status", "STATUS"), new Item("power", "POWER", 9) }));
            g.Add(G("settings", "SETTINGS", false, new[] { new Item("settings", "SETTINGS", 14) }));
            return g;
        }

        internal static int Pages(Group g) { return Math.Max(1, (g.Items.Count + Side - 1) / Side); }
        internal static List<Item> PageItems(Group g, int page)
        {
            page = Math.Max(0, Math.Min(page, Pages(g) - 1)); var r = new List<Item>();
            for (int i = page * Side; i < Math.Min(g.Items.Count, (page + 1) * Side); i++) r.Add(g.Items[i]);
            return r;
        }

        /// <summary>HOME: group keys down the left, then the right column.</summary>
        internal static string[] HomeKeys(bool mj)
        {
            var gs = Groups(mj); var k = new string[Side * 2];
            for (int i = 0; i < gs.Count && i < k.Length; i++) k[i] = gs[i].Label;
            return k;
        }

        static string[] Pad(string[] a, int n) { var r = new string[n]; Array.Copy(a, r, Math.Min(n, a.Length)); return r; }
        /// <summary>Right-column context keys for an item (null slots blank; 8 keys, spare ones blank for later).</summary>
        /// <summary>Top row (8 keys): pan / center / zoom on MAP and CHART; blank elsewhere.</summary>
        internal static string[] Top(string item)
        {
            if (item == "map" || item == "chart") return Pad(new[] { "\u2190", "\u2193", "\u2191", "\u2192", "CENTER", "ZOOM+", "ZOOM-" }, TopCount);
            return new string[TopCount];
        }
        /// <summary>AP annunciator: "AP ENGAGED APPROACH LOC GS" (green) or "AP OFF" (amber).</summary>
        internal static string ApStatus(string mode, bool loc, bool gs)
        {
            if (string.IsNullOrEmpty(mode) || mode == "idle") return "AP OFF";
            string m = mode == "landing" ? "APPROACH" : mode.ToUpperInvariant();
            return "AP ENGAGED " + m + (loc ? " LOC" : "") + (gs ? " GS" : "");
        }
        /// <summary>RPM-style fuel field: "LF 85%", "!Empty!" below 1%, "" when the craft carries none.</summary>
        internal static string FuelStatus(double amount, double cap) { if (cap <= 0) return ""; double f = amount / cap; return f < .01 ? "!Empty!" : "LF " + (100 * f).ToString("0") + "%"; }
        internal static string[] Right(string item) { return Pad(RightKeys(item), Side); }
        static string[] RightKeys(string item)
        {
            switch (item)
            {
                case "map": return new[] { "ZOOM+", "ZOOM-", "CENTER", "RWY <", "RWY >", "CTR RWY" };
                case "chart": return new[] { "RWY <", "RWY >", "VARIANT", "WP <", "WP >", "ALT +50", "ALT -50", "AGL/MSL" };
                case "ils": return new[] { "RWY <", "RWY >", "GUIDE", null, null, null };
                case "about": return new[] { "LEARN", "<", ">", "SCROLL UP", "SCROLL DN", "TOGGLE", "SKIP" };
                case "aircraft": return new[] { "TAKEOFF", "LAND", "GO AROUND", "ABORT", "STATUS", "LEARN" };
                case "approach": return new[] { "LAND", "GO AROUND", "ABORT", null, null, null };
                case "taxi": return new[] { "HANGAR", "RWY 09", "RWY 27", "STOP", null, null };
                case "trim": return new[] { "TRIM WIN", "AUTO TRIM", null, null, null, null };
                case "abort": return new[] { "ABORT", "STOP", "STATUS", null, null, null };
                case "mjatt": return new[] { "PROGRADE", "RETRO", "NORMAL+", "NORMAL-", "RAD+", "RAD-" };
                case "mjguide": return new[] { "ASCENT", "LAND", "EXEC NODE", "RENDEZV", "DOCK", "AIRCRAFT" };
                case "flightplan": return new[] { "CHECK", "FLY", "STOP", null, null, null };
                case "alarm": return new[] { "ACK", "MAYDAY LT" };
                case "systems": return new[] { "OVERVIEW", "ROTORS", "MAYDAY LT" };
                case "intercom": case "system": case "pilot": case "all": return new[] { "SEND", "CLEAR", "CHAT WIN" };
                default: return new string[Side];
            }
        }

        /// <summary>Bottom keys 1-5 (context); 0 = BACK, 6/7 = PREV/NEXT are added by Bottom().</summary>
        internal static string[] BottomContext(string item) { return Pad(BottomKeys(item), Ctx); }
        static string[] BottomKeys(string item)
        {
            switch (item)
            {
                case "map": return new[] { "\u2190", "\u2193", "\u2191", "\u2192" };
                case "chart": return new[] { "ADD WP", "REMOVE", "SAVE", "RESET", "ZOOM" };
                case "mjatt": return new[] { "KILLROT", "NODE", "TARGET+", "OFF" };
                case "mjguide": return new[] { "SPACEPLN", "STATUS", "ALL OFF", null };
                default: return new string[0];
            }
        }

        /// <summary>The whole bottom row for the current page: BACK fixed at key 0 (blank on HOME), PREV/NEXT at 5/6 when paged.</summary>
        internal static string[] Bottom(bool home, string item, int page, int pages)
        {
            var b = new string[BottomCount]; if (home) return b;
            b[BackKey] = "BACK"; var c = BottomContext(item); for (int i = 0; i < Ctx; i++) b[1 + i] = c[i];
            if (pages > 1) { b[PrevKey] = page > 0 ? "PREV" : null; b[NextKey] = page < pages - 1 ? "NEXT" : null; }
            return b;
        }

        /// <summary>SmartASS mode for a MECHJEB attitude key.</summary>
        internal static string SmartMode(string key)
        {
            switch (key)
            {
                case "PROGRADE": return "PROGRADE"; case "RETRO": return "RETROGRADE"; case "NORMAL+": return "NORMAL_PLUS"; case "NORMAL-": return "NORMAL_MINUS";
                case "RAD+": return "RADIAL_PLUS"; case "RAD-": return "RADIAL_MINUS"; case "KILLROT": return "KILLROT"; case "NODE": return "NODE"; case "TARGET+": return "TARGET_PLUS"; case "OFF": return "OFF";
            }
            return null;
        }

        /// <summary>COMMS filter pages: PILOT = what you typed, SYSTEM = AICS / tool / alarm lines, INTERCOM = the crew, ALL.</summary>
        internal static string ChatKind(string line)
        {
            string l = line ?? "";
            if (l.StartsWith("You:") || l.StartsWith("You (")) return "pilot";
            if (l.StartsWith("AICS") || l.StartsWith("[SYSTEM]") || l.StartsWith("[") || l.StartsWith("SYSTEM") || l.StartsWith("CAUTION") || l.StartsWith("WARNING") || l.StartsWith("MAYDAY")) return "system";
            return "intercom";
        }
        internal static bool ChatShows(string page, string line) { return page == "all" || ChatKind(line) == page; }

        static readonly string[] MpAssemblies = { "LmpClient", "LunaMultiplayer", "LmpCommon", "DarkMultiPlayer", "DMPClient", "KMP", "KerbalMultiPlayer" };
        /// <summary>A multiplayer mod (Luna Multiplayer, DarkMultiPlayer, ...) is loaded: AICS is single-player only.</summary>
        internal static bool IsMultiplayer(IEnumerable<string> assemblies) { foreach (var a in assemblies) foreach (var n in MpAssemblies) if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        /// <summary>Setting "RPM AICS pages" auto/on/off: auto hides ours in cockpits that have the native AICS MFD (no duplicates).</summary>
        internal static bool RpmHidden(string mode, bool cockpitHasNativeMfd) { return mode == "off" || (mode == "auto" && cockpitHasNativeMfd); }

        /// <summary>Every panel index 0..14 reachable from some item (pinned by a test).</summary>
        internal static bool CoversPanels(int count, bool mj)
        {
            var seen = new HashSet<int>(); foreach (var g in Groups(mj)) foreach (var it in g.Items) if (it.Panel >= 0) seen.Add(it.Panel);
            for (int i = 0; i < count; i++) if (!seen.Contains(i)) return false; return true;
        }
        /// <summary>ABOUT/LEARN page scroll (Luke 8:25 PM), shared by the outside panel and the IVA screens: line offset; follows
    /// the [>] current step when it changes, manual SCROLL keys / wheel otherwise.</summary>
    /// <summary>Overlay text must never match the map / fog grey (Luke 11:47 PM): reject low-saturation mid greys.</summary>
    internal static bool TextColorOk(float r, float g, float b) { float y = .299f * r + .587f * g + .114f * b, sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)); return !(sat < .12f && y > .3f && y < .62f); }
    internal static readonly float[][] OverlayColors = { new[] { .35f, .9f, 1f }, new[] { .85f, 1f, .85f }, new[] { 1f, 1f, 1f }, new[] { .75f, .5f, .5f }, new[] { 1f, .69f, .13f }, new[] { .49f, 1f, .49f } };
    internal static int AboutPage, LearnCursor;
    internal static void MoveCursor(int d, int n) { LearnCursor = Math.Max(0, Math.Min(n - 1, LearnCursor + d)); }
    internal static void AboutTurn(int d, int pages) { AboutPage = ((AboutPage + d) % pages + pages) % pages; AboutOffset = 0; aboutCur = -1; }
    internal static int AboutOffset; static int aboutCur = -1;
    internal static int AboutWindow(List<string> lines, int rows)
    {
        int cur = lines.FindIndex(l => l.StartsWith("[>]"));
        if (cur >= 0 && cur != aboutCur) { aboutCur = cur; if (cur < AboutOffset || cur + 1 >= AboutOffset + rows) AboutOffset = Math.Max(0, cur - 2); }
        AboutOffset = Math.Max(0, Math.Min(AboutOffset, Math.Max(0, lines.Count - rows)));
        return AboutOffset;
    }
    internal static void AboutScroll(int d) { AboutOffset = Math.Max(0, AboutOffset + d); }
    internal static string DefaultPage(int index) { var p = new[] { "map", "ils", "aircraft", "chart", "all" }; return p[((index % p.Length) + p.Length) % p.Length]; }
    }
}