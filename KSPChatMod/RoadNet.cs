// RS3b: road network for rover routes (pure, tested) - nodes + edges (road / runway / crawlerway), Dijkstra between the projections of
// start and goal onto the nearest edges, so the rover drives on the road centreline and leaves it only for a short final leg to an
// off-road site. KSC default: runway centreline, the runway exit taxiway to the KSC hub, hub spokes to each facility front, the Crawlerway
// VAB -> pad. Built from in-game positions (NativeRoverRoads.cs); PluginData/charts/ROADS_KSC.json overrides it when present.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal sealed class RoadNet
    {
        internal sealed class Node { internal string Name; internal double Lat, Lon; }
        internal sealed class Edge { internal int A, B; internal string Kind; }
        internal readonly List<Node> Nodes = new List<Node>();
        internal readonly List<Edge> Edges = new List<Edge>();
        internal double Radius = 600000;
        /// <summary>Goal closer than this to the start: drive straight. Off-road start/goal legs longer than MaxOffRoad are still driven (short legs expected).</summary>
        internal const double StraightM = 60, OnRoadTol = 3;

        internal int Add(string name, double lat, double lon) { int i = Find(name); if (i >= 0) return i; Nodes.Add(new Node { Name = name, Lat = lat, Lon = lon }); return Nodes.Count - 1; }
        internal int Find(string name) { return Nodes.FindIndex(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)); }
        internal void Connect(int a, int b, string kind) { if (a < 0 || b < 0 || a == b) return; if (!Edges.Exists(e => (e.A == a && e.B == b) || (e.A == b && e.B == a))) Edges.Add(new Edge { A = a, B = b, Kind = kind ?? "road" }); }
        internal void Connect(string a, string b, string kind) { Connect(Find(a), Find(b), kind); }

        /// <summary>RS7d (Luke Oct 21: the rover drove the Mission Control -> hub spoke straight into the VAB): connect a to b, but when the
        /// straight segment passes inside a building footprint (radius m) add a bypass node FootprintMarginM outside it, on the side the
        /// segment already passes, and connect through it (recursive, depth 4). Buildings containing an endpoint are ignored.</summary>
        internal void ConnectAround(string a, string b, string kind, IList<KeyValuePair<KscRoverPolicy.P, double>> buildings, int depth)
        {
            int ia = Find(a), ib = Find(b); if (ia < 0 || ib < 0) return;
            if (buildings != null && depth < 4)
            {
                double k = Radius * Math.PI / 180, c = Math.Cos(Nodes[ia].Lat * Math.PI / 180);
                double ax = Nodes[ia].Lon * k * c, ay = Nodes[ia].Lat * k, bx = Nodes[ib].Lon * k * c, by = Nodes[ib].Lat * k;
                double worst = 0; double px = 0, py = 0, ox = 0, oy = 0, orad = 0;
                foreach (var bl in buildings)
                {
                    double cx = bl.Key.Lon * k * c, cy = bl.Key.Lat * k, r = bl.Value;
                    if (Math.Sqrt((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy)) < r || Math.Sqrt((bx - cx) * (bx - cx) + (by - cy) * (by - cy)) < r) continue;
                    double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy, t = l2 > 0 ? Math.Max(0, Math.Min(1, ((cx - ax) * dx + (cy - ay) * dy) / l2)) : 0;
                    double qx = ax + t * dx, qy = ay + t * dy, d = Math.Sqrt((qx - cx) * (qx - cx) + (qy - cy) * (qy - cy));
                    if (d < r && r - d > worst) { worst = r - d; px = qx; py = qy; ox = cx; oy = cy; orad = r; }
                }
                if (worst > 0)
                {
                    double vx = px - ox, vy = py - oy, vl = Math.Sqrt(vx * vx + vy * vy);
                    if (vl < 1e-6) { vx = -(by - ay); vy = bx - ax; vl = Math.Sqrt(vx * vx + vy * vy); }   // through the centre: go round on the left
                    double s = (orad + FootprintMarginM) / vl, nx = ox + vx * s, ny = oy + vy * s;
                    string mid = a + " ~ " + b; Add(mid, ny / k, nx / (k * c));
                    ConnectAround(a, mid, kind, buildings, depth + 1); ConnectAround(mid, b, kind, buildings, depth + 1); return;
                }
            }
            Connect(ia, ib, kind);
        }
        internal const double FootprintMarginM = 15;
        /// <summary>RS7d: footprint radius of a KSC building from its centre and its front (approach) point: 90% of that distance, 25..80 m.</summary>
        internal static double FootprintRadius(double centreToFrontM) { return Math.Max(25, Math.Min(100, centreToFrontM * .9)); }   // RS7i: up to 100 m (VAB stop 95 m out -> 85 m)

        double D(KscRoverPolicy.P a, KscRoverPolicy.P b) { return NavigationMath.Distance(a.Lat, a.Lon, b.Lat, b.Lon, Radius); }
        KscRoverPolicy.P Pt(int i) { return new KscRoverPolicy.P(Nodes[i].Lat, Nodes[i].Lon); }

        /// <summary>Projection of p on edge e (flat metres): t 0..1 along A->B, off = distance from the centreline, at = the point.</summary>
        internal void ProjectOn(int e, KscRoverPolicy.P p, out double t, out double off, out KscRoverPolicy.P at)
        {
            var a = Pt(Edges[e].A); var b = Pt(Edges[e].B);
            double k = Math.PI / 180 * Radius, c = Math.Cos(a.Lat * Math.PI / 180);
            double bx = (b.Lon - a.Lon) * k * c, by = (b.Lat - a.Lat) * k, px = (p.Lon - a.Lon) * k * c, py = (p.Lat - a.Lat) * k, l2 = bx * bx + by * by;
            t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (px * bx + py * by) / l2));
            double dx = px - t * bx, dy = py - t * by; off = Math.Sqrt(dx * dx + dy * dy);
            at = new KscRoverPolicy.P(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);
        }
        /// <summary>Nearest edge to p (-1 when there are no edges).</summary>
        internal int Nearest(KscRoverPolicy.P p, out double t, out double off, out KscRoverPolicy.P at)
        {
            int best = -1; t = 0; off = double.MaxValue; at = p;
            for (int e = 0; e < Edges.Count; e++) { double te, oe; KscRoverPolicy.P ae; ProjectOn(e, p, out te, out oe, out ae); if (oe < off) { best = e; off = oe; t = te; at = ae; } }
            return best;
        }
        internal bool OnRoad(KscRoverPolicy.P p, double tol) { double t, off; KscRoverPolicy.P at; return Nearest(p, out t, out off, out at) >= 0 && off <= tol; }

        /// <summary>Road route from -> to: [from's road point (when off-road)], road nodes, [to's road point], to. Null = no network (drive straight).</summary>
        internal List<KscRoverPolicy.P> Route(KscRoverPolicy.P from, KscRoverPolicy.P to)
        {
            if (Edges.Count == 0) return null;
            var r = new List<KscRoverPolicy.P>();
            if (D(from, to) <= StraightM) { r.Add(to); return r; }
            double tf, df, tt, dt; KscRoverPolicy.P pf, pt; int ef = Nearest(from, out tf, out df, out pf), et = Nearest(to, out tt, out dt, out pt);
            int n = Nodes.Count, S = n, T = n + 1; var adj = new List<KeyValuePair<int, double>>[n + 2];
            for (int i = 0; i < n + 2; i++) adj[i] = new List<KeyValuePair<int, double>>();
            Action<int, int, double> link = (a, b, w) => { adj[a].Add(new KeyValuePair<int, double>(b, w)); adj[b].Add(new KeyValuePair<int, double>(a, w)); };
            foreach (var e in Edges) link(e.A, e.B, D(Pt(e.A), Pt(e.B)));
            double lf = D(Pt(Edges[ef].A), Pt(Edges[ef].B)), lt = D(Pt(Edges[et].A), Pt(Edges[et].B));
            link(S, Edges[ef].A, tf * lf); link(S, Edges[ef].B, (1 - tf) * lf); link(T, Edges[et].A, tt * lt); link(T, Edges[et].B, (1 - tt) * lt);
            if (ef == et) link(S, T, Math.Abs(tt - tf) * lf);
            var dist = new double[n + 2]; var prev = new int[n + 2]; var done = new bool[n + 2];
            for (int i = 0; i < n + 2; i++) { dist[i] = double.MaxValue; prev[i] = -1; }
            dist[S] = 0;
            while (true)
            {
                int u = -1; for (int i = 0; i < n + 2; i++) if (!done[i] && dist[i] < double.MaxValue && (u < 0 || dist[i] < dist[u])) u = i;
                if (u < 0 || u == T) break; done[u] = true;
                foreach (var kv in adj[u]) { double nd = dist[u] + kv.Value; if (nd < dist[kv.Key]) { dist[kv.Key] = nd; prev[kv.Key] = u; } }
            }
            if (prev[T] < 0 && S != T) { r.Add(to); return r; }
            var seq = new List<int>(); for (int v = T; v >= 0; v = prev[v]) seq.Insert(0, v);
            if (df > OnRoadTol) r.Add(pf);
            foreach (int v in seq) { if (v == S) { if (df <= OnRoadTol) r.Add(pf); continue; } if (v == T) { r.Add(pt); continue; } r.Add(Pt(v)); }
            if (dt > OnRoadTol) r.Add(to);
            // drop duplicate points (a start/goal exactly on a node)
            for (int i = r.Count - 1; i > 0; i--) if (D(r[i], r[i - 1]) < .5) r.RemoveAt(i);
            return r;
        }

        /// <summary>Driving length of a route from a start point.</summary>
        internal double Length(KscRoverPolicy.P from, List<KscRoverPolicy.P> route)
        {
            if (route == null) return double.NaN; double s = 0; var p = from; foreach (var q in route) { s += D(p, q); p = q; } return s;
        }
        /// <summary>Driving distance along the roads (straight line without a network).</summary>
        internal double DriveDistance(KscRoverPolicy.P from, KscRoverPolicy.P to) { var r = Route(from, to); return r == null ? D(from, to) : Length(from, r); }

        /// <summary>Slow before a corner: turnDeg at distM ahead -> allowed speed (3-15 m/s at the corner, 1.5 m/s2 braking).</summary>
        internal static double CornerSpeed(double speed, double turnDeg, double distM)
        {
            double a = Math.Abs(turnDeg); if (a < 25 || double.IsNaN(a)) return speed;
            double vc = 3 + 12 * (1 - Math.Min(a, 120) / 120); return Math.Min(speed, Math.Sqrt(vc * vc + 2 * 1.5 * Math.Max(0, distM)));
        }

        internal string ToJson()
        {
            var ns = new List<object>(); foreach (var x in Nodes) ns.Add(new Dictionary<string, object> { { "name", x.Name }, { "lat", Math.Round(x.Lat, 7) }, { "lon", Math.Round(x.Lon, 7) } });
            var es = new List<object>(); foreach (var e in Edges) es.Add(new Dictionary<string, object> { { "a", Nodes[e.A].Name }, { "b", Nodes[e.B].Name }, { "kind", e.Kind } });
            return MiniJson.Serialize(new Dictionary<string, object> { { "nodes", ns }, { "edges", es } });
        }
        internal static RoadNet FromJson(string json, double radius)
        {
            var net = new RoadNet { Radius = radius }; Dictionary<string, object> root;
            try { root = MiniJson.Deserialize(json); } catch (Exception) { return null; }
            object o; if (root == null || !root.TryGetValue("nodes", out o) || !(o is System.Collections.IEnumerable)) return null;
            foreach (var it in (System.Collections.IEnumerable)o)
            {
                var d = it as Dictionary<string, object>; if (d == null) continue; object nm, la, lo;
                if (d.TryGetValue("name", out nm) && d.TryGetValue("lat", out la) && d.TryGetValue("lon", out lo))
                    try { net.Add(Convert.ToString(nm, CultureInfo.InvariantCulture), Convert.ToDouble(la, CultureInfo.InvariantCulture), Convert.ToDouble(lo, CultureInfo.InvariantCulture)); } catch (Exception) { }
            }
            if (root.TryGetValue("edges", out o) && o is System.Collections.IEnumerable)
                foreach (var it in (System.Collections.IEnumerable)o)
                {
                    var d = it as Dictionary<string, object>; if (d == null) continue; object a, b, k;
                    if (d.TryGetValue("a", out a) && d.TryGetValue("b", out b)) net.Connect(Convert.ToString(a), Convert.ToString(b), d.TryGetValue("kind", out k) ? Convert.ToString(k) : "road");
                }
            return net.Edges.Count > 0 ? net : null;
        }

        /// <summary>KSC default network from in-game positions: runway centreline (09 end - exit - 27 end), exit taxiway to the hub, hub spokes
        /// to each facility front (fronts = KscRoverMap approach points), Crawlerway VAB front - crawler - pad front.</summary>
        internal static RoadNet Ksc(KscRoverPolicy.P hub, IList<KeyValuePair<string, KscRoverPolicy.P>> fronts, double radius,
            IList<KeyValuePair<KscRoverPolicy.P, double>> buildings = null)   // RS7d: building centres + footprint radius (m); spokes bend around them
        {
            var net = new RoadNet { Radius = radius };
            double exitLat = KscRunway.LatAt(TaxiRoute.ExitLon);
            net.Add("RWY 09", KscRunway.LatAt(KscRunway.Lon09 + .002), KscRunway.Lon09 + .002); net.Add("RWY EXIT", exitLat, TaxiRoute.ExitLon); net.Add("RWY 27", KscRunway.LatAt(KscRunway.Lon27 - .002), KscRunway.Lon27 - .002);
            net.Connect("RWY 09", "RWY EXIT", "runway"); net.Connect("RWY EXIT", "RWY 27", "runway");
            net.Add("TWY", exitLat + (hub.Lat - exitLat) * .5, TaxiRoute.ExitLon + (hub.Lon - TaxiRoute.ExitLon) * .5); net.Add("HUB", hub.Lat, hub.Lon);
            net.ConnectAround("RWY EXIT", "TWY", "road", buildings, 0); net.ConnectAround("TWY", "HUB", "road", buildings, 0);   // RS7d: the exit taxiway also cut the VAB
            KscRoverPolicy.P vab = new KscRoverPolicy.P(double.NaN, 0), pad = vab, crawler = vab;
            foreach (var f in fronts)
            {
                if (f.Key == "Runway" || f.Key == "KSC Grass" || f.Key == "Shores" || f.Key.StartsWith("R&D - ", StringComparison.Ordinal)) continue;
                if (f.Key == "Launch Pad") { pad = f.Value; continue; }
                if (f.Key == "Crawlerway") { crawler = f.Value; continue; }
                if (f.Key == "VAB") vab = f.Value;
                net.Add(f.Key, f.Value.Lat, f.Value.Lon); net.ConnectAround("HUB", f.Key, "road", buildings, 0);
            }
            if (!double.IsNaN(vab.Lat) && !double.IsNaN(pad.Lat))
            {
                net.Add("Launch Pad", pad.Lat, pad.Lon);
                if (!double.IsNaN(crawler.Lat)) { net.Add("Crawlerway", crawler.Lat, crawler.Lon); net.ConnectAround("VAB", "Crawlerway", "crawlerway", buildings, 0); net.ConnectAround("Crawlerway", "Launch Pad", "crawlerway", buildings, 0); }
                else net.ConnectAround("VAB", "Launch Pad", "crawlerway", buildings, 0);
            }
            return net;
        }
    }
}