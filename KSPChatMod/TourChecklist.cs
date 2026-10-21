// RS7: rover science tour checklist (pure, tested). Sites (KSC buildings, Crawlerway, places) with one row per experiment and its live
// gain; check / uncheck sites and single experiments, selections saved per rover. One-shot experiments (no scientist on board) checked at
// several sites run only where they are worth most (as many sites as there are unused modules). The tour visits the checked sites in the
// shortest driving order (driving distances along the roads come from the game side).
using System;
using System.Collections.Generic;
using System.Linq;

namespace KSPChatBridge
{
    internal sealed class TourExp
    {
        internal string Id = "", Title = "";
        internal double Gain;
        internal bool On, OneShot, Blocked;   // Blocked: one-shot that runs at a better checked site instead
        internal bool Ran;                    // RS7b: ran on this tour - check mark on the right, gain shown as 0
        internal bool UserOff;                // RS7c: unticked by the player (zero-gain rows are off without being unticked)
        internal bool Done;                   // RS7e: live game state - subject already earned in R&D or its data held on board (never saved)
        internal double LiveGain;             // R&D value before the ran zeroing
        internal int Modules = 1;             // unused one-shot modules on board
        internal string RunsAt = "";
        internal bool Runnable { get { return On && !Blocked && Gain > 0; } }   // RS7f: a checked (partly earned) row with gain left still runs when ticked
        internal bool Dim { get { return Gain <= 0 || Blocked || !On; } }
    }

    internal sealed class TourSite
    {
        internal string Key = "", Name = "", Source = "";
        internal double Lat, Lon, Distance;
        internal bool On;
        internal readonly List<TourExp> Exps = new List<TourExp>();
        internal double Total { get { double s = 0; foreach (var e in Exps) if (e.Gain > 0) s += e.Gain; return s; } }
        internal double Checked { get { double s = 0; if (On) foreach (var e in Exps) if (e.Runnable) s += e.Gain; return s; } }
        internal bool Visit { get { return On && Exps.Exists(e => e.Runnable); } }
    }

    /// <summary>Saved choices of one rover: explicit site on/off and experiments the user unticked ("site|exp").</summary>
    internal sealed class TourSelection
    {
        internal readonly Dictionary<string, bool> Sites = new Dictionary<string, bool>();
        internal readonly HashSet<string> Off = new HashSet<string>();
        // RS7e: no "ran" set any more - check marks are derived from the game every refresh, so a revert / quickload cannot leave stale marks
        internal static string K(TourSite s, TourExp e) { return s.Key + "|" + e.Id; }

        internal Dictionary<string, object> ToData()
        {
            var s = new Dictionary<string, object>(); foreach (var kv in Sites) s[kv.Key] = kv.Value;
            var o = new Dictionary<string, object>(); foreach (var k in Off) o[k] = true;
            return new Dictionary<string, object> { { "sites", s }, { "off", o } };
        }
        internal static TourSelection FromData(object data)
        {
            var r = new TourSelection(); var d = data as Dictionary<string, object>; if (d == null) return r; object o;
            if (d.TryGetValue("sites", out o) && o is Dictionary<string, object>) foreach (var kv in (Dictionary<string, object>)o) if (kv.Value is bool) r.Sites[kv.Key] = (bool)kv.Value;
            if (d.TryGetValue("off", out o) && o is Dictionary<string, object>) foreach (var kv in (Dictionary<string, object>)o) r.Off.Add(kv.Key);
            return r;   // RS7e: an old saved "ran" key is ignored (dropped on the next save)
        }
    }

    internal static class TourChecklist
    {
        /// <summary>Apply saved choices (new sites default checked when worth anything), the one-shot rule, then sort.</summary>
        internal static void Apply(List<TourSite> sites, TourSelection sel)
        {
            foreach (var s in sites)
            {
                foreach (var e in s.Exps) { e.LiveGain = e.Gain; e.Ran = e.Done; }   // RS7e: derived, not saved; RS7f: Gain stays the live REMAINING value
                bool on; s.On = sel.Sites.TryGetValue(s.Key, out on) ? on : s.Total > 0;
                foreach (var e in s.Exps) { e.UserOff = sel.Off.Contains(TourSelection.K(s, e)); e.On = s.On && e.Gain > 0 && !e.UserOff; }   // RS7f: done rows keep their remaining gain (maxed = 0, dimmed, bottom)   // RS7b: ran rows count 0 (totals, sorting)
            }
            OneShots(sites); Sort(sites);
        }

        /// <summary>Experiments by gain (zero rows at the bottom), sites by total science (zero-total sites at the bottom).</summary>
        internal static void Sort(List<TourSite> sites)
        {
            foreach (var s in sites) { var x = s.Exps.OrderByDescending(e => e.Gain).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList(); s.Exps.Clear(); s.Exps.AddRange(x); }
            var y = sites.OrderByDescending(s => s.Total).ThenBy(s => s.Distance).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(); sites.Clear(); sites.AddRange(y);
        }

        /// <summary>A one-shot experiment checked at several sites runs only at the best ones (one site per unused module).</summary>
        internal static void OneShots(List<TourSite> sites)
        {
            foreach (var s in sites) foreach (var e in s.Exps) { e.Blocked = false; e.RunsAt = ""; }
            var ids = new HashSet<string>(); foreach (var s in sites) foreach (var e in s.Exps) if (e.OneShot) ids.Add(e.Id);
            foreach (var id in ids)
            {
                var c = new List<KeyValuePair<TourSite, TourExp>>();
                foreach (var s in sites) if (s.On) foreach (var e in s.Exps) if (e.Id == id && e.OneShot && e.On && !e.Ran && e.Gain > 0) c.Add(new KeyValuePair<TourSite, TourExp>(s, e));
                c = c.OrderByDescending(p => p.Value.Gain).ToList();
                int n = 0; foreach (var p in c) n = Math.Max(n, p.Value.Modules); n = Math.Max(1, n);
                var best = new List<string>(); for (int i = 0; i < c.Count && i < n; i++) best.Add(c[i].Key.Name);
                for (int i = n; i < c.Count; i++) { c[i].Value.Blocked = true; c[i].Value.RunsAt = string.Join(", ", best.ToArray()); }
            }
        }

        internal static void SetSite(List<TourSite> sites, TourSite s, bool on, TourSelection sel)
        {
            s.On = on; sel.Sites[s.Key] = on;
            foreach (var e in s.Exps) { sel.Off.Remove(TourSelection.K(s, e)); e.On = on && e.Gain > 0; }
            OneShots(sites);
        }
        internal static void SetExp(List<TourSite> sites, TourSite s, TourExp e, bool on, TourSelection sel)
        {
            if (on && e.Gain <= 0) return;
            if (on && !s.On)
            {   // ticking one row of an unchecked site checks the site with just that row
                s.On = true; sel.Sites[s.Key] = true;
                foreach (var x in s.Exps) if (x != e) { x.On = false; sel.Off.Add(TourSelection.K(s, x)); }
            }
            e.On = on; if (on) sel.Off.Remove(TourSelection.K(s, e)); else sel.Off.Add(TourSelection.K(s, e));
            OneShots(sites);
        }
        internal static void SetAll(List<TourSite> sites, bool on, TourSelection sel) { foreach (var s in sites) SetSite(sites, s, on, sel); }

        /// <summary>RS7f (Luke Oct 21: science earned in the career before the mod showed no check): done = ANY science earned for the subject
        /// in R&D (subject.science > 0, partly earned included - the row still shows the reduced remaining gain) or its data held on board.</summary>
        internal static bool DoneFrom(double earned, double cap, bool heldOnBoard)
        {
            return heldOnBoard || (!double.IsNaN(earned) && earned > 1e-4);
        }
        /// <summary>RS7f: the stock subject id: experiment@Body + Situation + biome without spaces (biome-agnostic experiments: no biome),
        /// e.g. mysteryGoo@KerbinSrfLandedLaunchPad, crewReport@KerbinSrfLandedR&D, bd_oso@KerbinSrfLanded.</summary>
        /// <summary>RS7j (Luke Oct 21 02:38: KSC Grass showed no checks although science ran there - the rows looked up ...SrfLandedKSC
        /// while the rover parked on terrain and filed everything under ...SrfLandedShores): the biome a site's rows use = the biome
        /// actually seen when the rover stopped there (learned), else the facility mini-biome, else the terrain biome at the stop point.
        /// "KSC" (KSC grounds) is never predicted: only landedAt on the KSC ground collider gives it.</summary>
        internal static string SiteBiome(string learned, string mini, string terrain)
        {
            if (!string.IsNullOrEmpty(learned)) return learned;
            if (!string.IsNullOrEmpty(mini) && mini != "KSC") return mini;
            return terrain ?? "";
        }
        /// <summary>RS7j: row label = site name, plus the biome when it is not the facility's own (e.g. "KSC Grass (Shores)").</summary>
        internal static string SiteLabel(string name, string mini, string biome)
        {
            if (string.IsNullOrEmpty(biome) || string.Equals(biome, mini, StringComparison.OrdinalIgnoreCase) || string.Equals(biome, name, StringComparison.OrdinalIgnoreCase)) return name;
            return name + " (" + biome + ")";
        }
        /// <summary>RS7k: learned stop biomes are saved with the parked point ("Runway@-0.0487,-74.7215"); a value more than 60 m from the stop point (the stop
        /// moved, or an old entry without a point) is ignored and learned again.</summary>
        internal static string LearnedEntry(string biome, double lat, double lon)
        {
            return biome + "@" + lat.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + "," + lon.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static string LearnedFor(string entry, double lat, double lon, double radius)
        {
            if (string.IsNullOrEmpty(entry)) return null; int at = entry.LastIndexOf('@'); if (at <= 0) return null;
            var p = entry.Substring(at + 1).Split(','); double la, lo;
            if (p.Length != 2 || !double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out la)
                || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lo)) return null;
            return NavigationMath.Distance(la, lo, lat, lon, radius) <= 60 ? entry.Substring(0, at) : null;   // parked within the arrival / creep slack
        }
        internal static string SubjectId(string expId, string body, string situation, string biome)
        {
            return expId + "@" + body + situation + (biome ?? "").Replace(" ", "");
        }
        /// <summary>RS7b: experiments that ran at a site on the tour: check mark, gain 0, out of the totals. RS7e: in memory only, until the
        /// next refresh derives it again from the game (held data / R&D).</summary>
        internal static void MarkRan(List<TourSite> sites, string siteKey, IEnumerable<string> expIds)
        {
            foreach (var id in expIds)
            {
                foreach (var s in sites) if (s.Key == siteKey) foreach (var e in s.Exps) if (e.Id == id) { e.Ran = true; e.Gain = 0; }
            }
            OneShots(sites);
        }
        /// <summary>A new tour starts with no check marks.</summary>
        internal static void ClearRan(List<TourSite> sites)
        {
            foreach (var s in sites) foreach (var e in s.Exps) if (e.Ran) { e.Ran = e.Done; e.Gain = e.LiveGain; }
            OneShots(sites);
        }

        internal static double CheckedTotal(IEnumerable<TourSite> sites) { double t = 0; foreach (var s in sites) t += s.Checked; return t; }
        /// <summary>Sites the tour drives to: checked, with at least one runnable experiment.</summary>
        internal static List<TourSite> RouteSites(IEnumerable<TourSite> sites) { return sites.Where(s => s.Visit).ToList(); }
        /// <summary>RS7l: sites whose stop has not run yet this flight (keyed by site Key).</summary>
        internal static List<TourSite> NotRun(IEnumerable<TourSite> sites, ICollection<string> ran) { return sites.Where(s => !ran.Contains(s.Key ?? s.Name)).ToList(); }
        /// <summary>Experiment ids to run at a site.</summary>
        internal static HashSet<string> RunIds(TourSite s) { return new HashSet<string>(s.Exps.Where(e => s.On && e.Runnable).Select(e => e.Id)); }
        /// <summary>RS7c: experiments tried at the stop: the ticked rows plus rows that are only off because their PREDICTED gain was 0 (not
        /// unticked, not run, not a one-shot saved for elsewhere). The rover parks in the real biome, which can differ from the site's
        /// nominal one (Oct 21: "KSC Grass" parked on Runway, "Runway" parked on Shores, so Goo / EVA report predicted 0 were never tried
        /// where they were worth science); the live value check in RunExperiments still skips anything worth 0 there.</summary>
        internal static HashSet<string> StopIds(TourSite s)
        {
            return new HashSet<string>(s.Exps.Where(e => s.On && (e.Runnable || (e.Gain <= 0 && !e.Ran && !e.UserOff && !e.Blocked && !e.OneShot))).Select(e => e.Id));
        }

        /// <summary>Shortest open driving path: start[i] = start -> site i, d[i,j] = site i -> site j (driving distances). Exact up to 12 sites.</summary>
        internal static List<int> Order(double[] start, double[,] d)
        {
            int n = start.Length; var r = new List<int>(); if (n == 0) return r;
            if (n <= 12)
            {
                int full = 1 << n; var c = new double[full, n]; var p = new int[full, n];
                for (int m = 0; m < full; m++) for (int i = 0; i < n; i++) { c[m, i] = double.MaxValue; p[m, i] = -1; }
                for (int i = 0; i < n; i++) c[1 << i, i] = start[i];
                for (int m = 1; m < full; m++) for (int i = 0; i < n; i++)
                {
                    if ((m & (1 << i)) == 0 || c[m, i] == double.MaxValue) continue;
                    for (int j = 0; j < n; j++) { if ((m & (1 << j)) != 0) continue; int nm = m | (1 << j); double v = c[m, i] + d[i, j]; if (v < c[nm, j]) { c[nm, j] = v; p[nm, j] = i; } }
                }
                int last = 0; for (int i = 1; i < n; i++) if (c[full - 1, i] < c[full - 1, last]) last = i;
                for (int m = full - 1, i = last; i >= 0;) { r.Insert(0, i); int pi = p[m, i]; m &= ~(1 << i); i = pi; }
                return r;
            }
            var left = new HashSet<int>(Enumerable.Range(0, n)); int cur = -1;
            while (left.Count > 0) { int b = -1; foreach (int j in left) if (b < 0 || (cur < 0 ? start[j] < start[b] : d[cur, j] < d[cur, b])) b = j; r.Add(b); left.Remove(b); cur = b; }
            Func<List<int>, double> len = o => { double s = start[o[0]]; for (int i = 1; i < o.Count; i++) s += d[o[i - 1], o[i]]; return s; };
            for (bool better = true; better;)
            {
                better = false;
                for (int i = 0; i < n - 1; i++) for (int j = i + 1; j < n; j++) { var t = new List<int>(r); t.Reverse(i, j - i + 1); if (len(t) < len(r) - 1e-9) { r = t; better = true; } }
            }
            return r;
        }
    }
}