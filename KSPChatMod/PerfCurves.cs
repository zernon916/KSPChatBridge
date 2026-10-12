using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Sampled performance curve (Luke 11:10 AM): points are independent vars..., y, mass.
    /// Lookup finds the nearest neighbors in x-space, interpolates y, then scales by mass (speed ~ sqrt(m/m0),
    /// accel ~ m0/m, thrust/throttle ~ m/m0, rate/none unchanged). Empty curve -> NaN; callers fall back to the old single value.</summary>
    internal sealed class PerfCurve
    {
        internal string Name = "", XLabel = "", YLabel = "", Scale = "none";
        /// <summary>Each point: x0..xN-1, y, mass. Dim = number of independent variables.</summary>
        internal readonly List<double[]> Points = new List<double[]>();
        internal int Dim { get { return Points.Count == 0 ? 0 : Math.Max(0, Points[0].Length - 2); } }
        internal bool Empty { get { return Points.Count == 0; } }

        internal PerfCurve() { }
        internal PerfCurve(string name, string xLabel, string yLabel, string scale) { Name = name; XLabel = xLabel; YLabel = yLabel; Scale = scale; }

        internal void Add(double y, double mass, params double[] xs)
        {
            if (double.IsNaN(y) || double.IsNaN(mass)) return;
            var p = new double[xs.Length + 2];
            for (int i = 0; i < xs.Length; i++) { if (double.IsNaN(xs[i])) return; p[i] = xs[i]; }
            p[xs.Length] = y; p[xs.Length + 1] = mass; Points.Add(p);
        }

        /// <summary>Nearest-neighbor blend in x-space among up to 4 closest points, mass-scaled to the query mass.</summary>
        internal double Lookup(double mass, params double[] xs)
        {
            if (Points.Count == 0 || xs == null || xs.Length == 0) return double.NaN;
            int d = Math.Min(xs.Length, Dim > 0 ? Dim : xs.Length);
            // gather up to 4 nearest (by squared distance in x)
            double[] bestD = { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue };
            double[] bestY = new double[4]; double[] bestM = new double[4]; int n = 0;
            foreach (var p in Points)
            {
                if (p.Length < d + 2) continue;
                double dd = 0; for (int i = 0; i < d; i++) { double e = p[i] - xs[i]; dd += e * e; }
                // insert sorted
                if (n < 4 || dd < bestD[3])
                {
                    int slot = n < 4 ? n++ : 3;
                    while (slot > 0 && dd < bestD[slot - 1]) { bestD[slot] = bestD[slot - 1]; bestY[slot] = bestY[slot - 1]; bestM[slot] = bestM[slot - 1]; slot--; }
                    bestD[slot] = dd; bestY[slot] = p[p.Length - 2]; bestM[slot] = p[p.Length - 1];
                }
            }
            if (n == 0) return double.NaN;
            if (bestD[0] < 1e-12) return ApplyScale(bestY[0], bestM[0], mass);
            double wSum = 0, ySum = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 1 / Math.Max(1e-9, Math.Sqrt(bestD[i]));
                ySum += w * ApplyScale(bestY[i], bestM[i], mass); wSum += w;
            }
            return ySum / wSum;
        }
        internal double LookupOr(double fallback, double mass, params double[] xs)
        {
            double v = Lookup(mass, xs); return double.IsNaN(v) ? fallback : v;
        }

        double ApplyScale(double y, double m0, double m)
        {
            if (double.IsNaN(m0) || m0 <= 0 || double.IsNaN(m) || m <= 0 || Scale == "none" || Scale == "rate") return y;
            if (Scale == "speed") return y * Math.Sqrt(m / m0);
            if (Scale == "accel") return y * m0 / m;
            if (Scale == "thrust") return FlightPolicy.Clamp(y * m / m0, 0, 1);
            return y;
        }

        /// <summary>For graphing: unique sorted x0 values with mean y (unscaled).</summary>
        internal List<double[]> Series1D()
        {
            var map = new SortedDictionary<double, double[]>(); // x -> [sum, n]
            foreach (var p in Points)
            {
                if (p.Length < 3) continue; double x = p[0], y = p[p.Length - 2];
                double[] a; if (!map.TryGetValue(x, out a)) map[x] = a = new double[2];
                a[0] += y; a[1]++;
            }
            var o = new List<double[]>(); foreach (var kv in map) o.Add(new[] { kv.Key, kv.Value[0] / Math.Max(1, kv.Value[1]) });
            return o;
        }

        /// <summary>For graphing: y averaged into x bins of width bin (climb graph: altitude bins, not every raw sample).</summary>
        internal List<double[]> Binned(double bin)
        {
            if (!(bin > 0)) return Series1D();
            var map = new SortedDictionary<long, double[]>(); // bin -> [sumX, sumY, n]
            foreach (var p in Points)
            {
                if (p.Length < 3) continue; long k = (long)Math.Floor(p[0] / bin);
                double[] a; if (!map.TryGetValue(k, out a)) map[k] = a = new double[3];
                a[0] += p[0]; a[1] += p[p.Length - 2]; a[2]++;
            }
            var o = new List<double[]>(); foreach (var kv in map) o.Add(new[] { kv.Value[0] / kv.Value[2], kv.Value[1] / kv.Value[2] });
            return o;
        }
        /// <summary>For graphing 2-D turn curves (bank, spd): one series per speed pass. A pass starts when bank stops increasing
        /// (the ladder restarts at 10 deg). Each series: label speed (mean measured spd of the pass) and (bank, y) points.</summary>
        internal List<KeyValuePair<double, List<double[]>>> PassSeries()
        {
            var o = new List<KeyValuePair<double, List<double[]>>>(); List<double[]> cur = null; double prevX = double.NaN, spdSum = 0; int n = 0;
            foreach (var p in Points)
            {
                if (p.Length < 4) continue;
                if (cur == null || !(p[0] > prevX)) { if (cur != null) o.Add(new KeyValuePair<double, List<double[]>>(spdSum / Math.Max(1, n), cur)); cur = new List<double[]>(); spdSum = 0; n = 0; }
                cur.Add(new[] { p[0], p[p.Length - 2] }); spdSum += p[1]; n++; prevX = p[0];
            }
            if (cur != null) o.Add(new KeyValuePair<double, List<double[]>>(spdSum / Math.Max(1, n), cur));
            return o;
        }
        /// <summary>Series the About page draws: turn curves per pass, climb averaged into 0.5 km bins, others 1-D means.</summary>
        internal List<KeyValuePair<double, List<double[]>>> GraphSeries()
        {
            if (Dim >= 2 && XLabel.StartsWith("bank")) return PassSeries();
            var s = Name == "climb" ? Binned(.5) : Series1D();
            return new List<KeyValuePair<double, List<double[]>>> { new KeyValuePair<double, List<double[]>>(double.NaN, s) };
        }
        internal List<object> ToList()
        {
            var o = new List<object>();
            foreach (var p in Points)
            {
                var row = new List<object>();
                for (int i = 0; i < p.Length; i++) row.Add(double.IsNaN(p[i]) ? null : (object)Math.Round(p[i], 3));
                o.Add(row);
            }
            return o;
        }
        internal static PerfCurve FromList(string name, string xLabel, string yLabel, string scale, System.Collections.IList rows)
        {
            var c = new PerfCurve(name, xLabel, yLabel, scale);
            if (rows == null) return c;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var r in rows)
            {
                var l = r as System.Collections.IList; if (l == null || l.Count < 3) continue;
                try
                {
                    var p = new double[l.Count];
                    for (int j = 0; j < l.Count; j++) p[j] = l[j] == null ? double.NaN : Convert.ToDouble(l[j], ci);
                    c.Points.Add(p);
                }
                catch (Exception) { }
            }
            return c;
        }
    }

    /// <summary>Named performance curves for one plane profile. Old single-value fields remain; empty curves fall back to them.</summary>
    internal sealed class CurveBook
    {
        internal readonly PerfCurve Stall = new PerfCurve("stall", "flaps", "m/s", "speed");          // x=flaps 0..3
        internal readonly PerfCurve Sink = new PerfCurve("sink", "thr,spd,flaps", "m/s", "none");     // sink rate (+down)
        internal readonly PerfCurve TurnRadius = new PerfCurve("turn_r", "bank,spd", "m", "none");
        internal readonly PerfCurve TurnRate = new PerfCurve("turn_rate", "bank,spd", "deg/s", "rate");
        internal readonly PerfCurve TurnG = new PerfCurve("turn_g", "bank,spd", "g", "none");
        internal readonly PerfCurve Climb = new PerfCurve("climb", "alt_km", "m/s", "none");
        internal readonly PerfCurve Accel = new PerfCurve("accel", "spd", "m/s2", "accel");
        internal readonly PerfCurve Decel = new PerfCurve("decel", "spd", "m/s2", "accel");
        internal readonly PerfCurve FuelBurn = new PerfCurve("burn", "spd,alt,thr", "%/s", "none");
        internal readonly PerfCurve ThrottleLag = new PerfCurve("thr_lag", "dthr", "s", "none");
        /// <summary>Oct 11: course rate (velocity-vector track change) next to the heading rate (TurnRate), and the separately measured
        /// roll / pitch rates vs speed.</summary>
        internal readonly PerfCurve CourseRate = new PerfCurve("course_rate", "bank,spd", "deg/s", "rate");
        internal readonly PerfCurve RollRate = new PerfCurve("roll_rate", "spd", "deg/s", "rate");
        internal readonly PerfCurve PitchRate = new PerfCurve("pitch_rate", "spd", "deg/s", "rate");

        internal IEnumerable<PerfCurve> All
        {
            get
            {
                yield return Stall; yield return Sink; yield return TurnRadius; yield return TurnRate; yield return TurnG;
                yield return Climb; yield return Accel; yield return Decel; yield return FuelBurn; yield return ThrottleLag;
                yield return CourseRate; yield return RollRate; yield return PitchRate;
            }
        }

        /// <summary>Graph page titles (one curve per page). Only non-empty curves are listed.</summary>
        internal List<PerfCurve> Graphable()
        {
            var o = new List<PerfCurve>(); foreach (var c in All) if (!c.Empty) o.Add(c); return o;
        }

        internal Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            foreach (var c in All) if (!c.Empty) d[c.Name] = new Dictionary<string, object> {
                { "x", c.XLabel }, { "y", c.YLabel }, { "scale", c.Scale }, { "points", c.ToList() }
            };
            return d;
        }
        internal void FromDict(Dictionary<string, object> d)
        {
            if (d == null) return;
            Load(Stall, d); Load(Sink, d); Load(TurnRadius, d); Load(TurnRate, d); Load(TurnG, d);
            Load(Climb, d); Load(Accel, d); Load(Decel, d); Load(FuelBurn, d); Load(ThrottleLag, d);
            Load(CourseRate, d); Load(RollRate, d); Load(PitchRate, d);
        }
        static void Load(PerfCurve c, Dictionary<string, object> d)
        {
            object o; if (!d.TryGetValue(c.Name, out o)) return;
            var m = o as Dictionary<string, object>; if (m == null) return;
            object pts; if (!m.TryGetValue("points", out pts)) return;
            var loaded = PerfCurve.FromList(c.Name, c.XLabel, c.YLabel, c.Scale, pts as System.Collections.IList);
            c.Points.Clear(); foreach (var p in loaded.Points) c.Points.Add(p);
            object s; if (m.TryGetValue("scale", out s) && s != null) c.Scale = s.ToString();
            if (m.TryGetValue("x", out s) && s != null) c.XLabel = s.ToString();
            if (m.TryGetValue("y", out s) && s != null) c.YLabel = s.ToString();
        }

        /// <summary>Seed curves from legacy list fields so old profiles still graph and look up.</summary>
        internal void SeedFromLegacy(PlaneProfile p)
        {
            if (!Stall.Empty) { /* keep */ } else
            {
                if (!double.IsNaN(p.Stall)) Stall.Add(p.Stall, p.M0, 0);
                if (!double.IsNaN(p.FlapStall)) Stall.Add(p.FlapStall, p.M0, 3);
            }
            if (Sink.Empty) foreach (var s in p.Sink) if (s.Length >= 4) Sink.Add(s[0], s[3], s[1], 0, 0); // thr known; speed unknown -> 0
            if (TurnRadius.Empty) foreach (var t in p.Turns) if (t.Length >= 9 && !double.IsNaN(t[8]))
            { double spd = t.Length > 7 && !double.IsNaN(t[7]) ? t[7] : 0, m = t.Length > 5 && !double.IsNaN(t[5]) ? t[5] : p.M0;
              TurnRadius.Add(t[8], m, t[0], spd); TurnRate.Add(t[2], m, t[0], spd); TurnG.Add(t[1], m, t[0], spd); }
            if (Climb.Empty) foreach (var c in p.Perf.Climb) Climb.Add(c[1], c[2], c[0]);
            if (Accel.Empty && !double.IsNaN(p.Accel)) Accel.Add(p.Accel, p.M0, double.IsNaN(p.EconSpeed) ? 100 : p.EconSpeed);
            if (Decel.Empty && !double.IsNaN(p.Decel)) Decel.Add(p.Decel, p.M0, double.IsNaN(p.EconSpeed) ? 100 : p.EconSpeed);
            if (ThrottleLag.Empty && !double.IsNaN(p.Lag)) ThrottleLag.Add(p.Lag, p.M0, 1);
        }
    }
}
