// RS3: rover driving safety for the AUTOPILOT only (never limits manual driving): speed caps per surface (learned per rover from the
// plan defaults), slope / roll / lateral-acceleration limits, runway crossing hold, stuck detection, value-weighted stop order. Pure, tested.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal enum RoverSurface { OffRoad, Road, Runway }

    /// <summary>Speed caps per surface (m/s). Defaults = ROVER_SCIENCE_PLAN RS3; raised by RoverSpeedLearner, saved per craft (rover_caps).</summary>
    internal sealed class RoverCaps
    {
        internal const double DefRoad = 12, DefRunway = 20, DefOffRoad = 6, Floor = 2, Ceiling = 25;   // 25 = the drive_speed limit
        internal double Road = DefRoad, Runway = DefRunway, OffRoad = DefOffRoad;
        internal double Get(RoverSurface s) { return s == RoverSurface.Runway ? Runway : s == RoverSurface.Road ? Road : OffRoad; }
        internal void Set(RoverSurface s, double v) { v = Math.Max(Floor, Math.Min(Ceiling, v)); if (s == RoverSurface.Runway) Runway = v; else if (s == RoverSurface.Road) Road = v; else OffRoad = v; }
        internal static double Default(RoverSurface s) { return s == RoverSurface.Runway ? DefRunway : s == RoverSurface.Road ? DefRoad : DefOffRoad; }
        internal Dictionary<string, object> ToDict() { return new Dictionary<string, object> { { "road", Math.Round(Road, 2) }, { "runway", Math.Round(Runway, 2) }, { "offroad", Math.Round(OffRoad, 2) } }; }
        internal static RoverCaps FromDict(Dictionary<string, object> d)
        {
            var c = new RoverCaps(); if (d == null) return c;
            c.Road = N(d, "road", DefRoad); c.Runway = N(d, "runway", DefRunway); c.OffRoad = N(d, "offroad", DefOffRoad); return c;
        }
        static double N(Dictionary<string, object> d, string k, double def)
        {
            object o; if (!d.TryGetValue(k, out o) || o == null) return def;
            try { double v = Convert.ToDouble(o, CultureInfo.InvariantCulture); return double.IsNaN(v) || double.IsInfinity(v) ? def : Math.Max(Floor, Math.Min(Ceiling, v)); } catch (Exception) { return def; }
        }
    }

    internal static class RoverDrivePolicy
    {
        internal const double G = 9.81, LatAccelG = .25, BrakeRollDeg = 30, CrawlSpeed = 2, RunwayHalfWidth = 40, AircraftRange = 2000, AircraftAlt = 1000;

        /// <summary>Surface under the rover: runway (KSC/Island landedAt or on the KSC runway strip), KSC grounds/roads (any landedAt), else off-road.</summary>
        internal static RoverSurface Surface(string landedAt, double lat, double lon, double radius)
        {
            string a = landedAt ?? "";
            if (a.IndexOf("Runway", StringComparison.OrdinalIgnoreCase) >= 0 || OnKscRunway(lat, lon, radius)) return RoverSurface.Runway;
            return a.Length > 0 ? RoverSurface.Road : RoverSurface.OffRoad;
        }
        internal static bool OnKscRunway(double lat, double lon, double radius)
        {
            double lo = Math.Min(KscRunway.Lon09, KscRunway.Lon27), hi = Math.Max(KscRunway.Lon09, KscRunway.Lon27);
            if (lon < lo || lon > hi) return false;
            return Math.Abs(lat - KscRunway.LatAt(lon)) * Math.PI / 180 * radius <= RunwayHalfWidth;
        }
        internal static double SlopeDeg(double hHere, double hAhead, double dist) { return dist <= 0 || double.IsNaN(hHere) || double.IsNaN(hAhead) ? 0 : Math.Atan2(hAhead - hHere, dist) * 180 / Math.PI; }
        /// <summary>Steeper than maxSlope ahead = crawl; above 70% of it = 60% speed.</summary>
        internal static double SlopeCap(double speed, double slopeDeg, double maxSlope)
        {
            double a = Math.Abs(slopeDeg); if (a > maxSlope) return Math.Min(speed, CrawlSpeed); if (a > .7 * maxSlope) return speed * .6; return speed;
        }
        internal static double RollCap(double speed, double rollDeg, double rollSlowDeg) { return Math.Abs(rollDeg) > rollSlowDeg ? speed * .5 : speed; }
        internal static bool BrakeForRoll(double rollDeg) { return Math.Abs(rollDeg) > BrakeRollDeg; }
        /// <summary>Lateral acceleration v * yawRate <= 0.25 g.</summary>
        internal static double TurnCap(double speed, double yawRateRad) { double w = Math.Abs(yawRateRad); return w < 1e-3 ? speed : Math.Min(speed, LatAccelG * G / w); }
        /// <summary>All limits together (the autopilot's target speed in, the safe one out).</summary>
        internal static double Cap(double target, double surfaceCap, double slopeDeg, double maxSlope, double rollDeg, double rollSlowDeg, double yawRateRad)
        {
            double v = Math.Min(target, surfaceCap); v = SlopeCap(v, slopeDeg, maxSlope); v = RollCap(v, rollDeg, rollSlowDeg); return Math.Max(0, TurnCap(v, yawRateRad));
        }
        /// <summary>Hold short of a runway while an aircraft is airborne within 2 km below 1000 m (the rover is not on the runway yet).</summary>
        internal static bool HoldShort(bool onRunway, bool runwayAhead, double aircraftDist, double aircraftAlt)
        {
            return !onRunway && runwayAhead && aircraftDist <= AircraftRange && aircraftAlt <= AircraftAlt;
        }
        /// <summary>RS7d: a 5.8 t rover only managed ~1.5 m/s2 on brakes (Oct 21: 14 -> 12 m/s over the last 33 m into the VAB), so the
        /// braking distance assumes BrakeDecel; the ray looks LookMarginM beyond it.</summary>
        internal const double BrakeDecel = 1.5, BrakeMarginM = 8, LookMarginM = 15;
        internal static double BrakeDistance(double speed) { return speed * speed / (2 * BrakeDecel); }
        /// <summary>Obstacle ray length (m): braking distance plus a margin, at least 15 m.</summary>
        internal static double LookAhead(double speed) { return Math.Max(15, BrakeDistance(speed) + LookMarginM); }
        /// <summary>RS7d: an obstacle well beyond the goal (the stop's own building) does not need a brake stop.</summary>
        internal static bool BeyondGoal(double hit, double goalLeft) { return !double.IsNaN(goalLeft) && hit > goalLeft + 10; }
        /// <summary>RS7d: something this close behind (m from the centre of mass) stops a back-up escape.</summary>
        internal static bool RearBlocked(double rearHit) { return !double.IsNaN(rearHit) && rearHit < RoverStuck.BackMetres + 4; }
        /// <summary>Obstacle ray result -> steer bias (deg, + = right) or 0; brake when inside the braking distance.</summary>
        internal static double Detour(double centerHit, double leftHit, double rightHit, double speed, out bool brake)
        {
            brake = !double.IsNaN(centerHit) && centerHit < BrakeDistance(speed) + BrakeMarginM;
            if (double.IsNaN(centerHit)) return 0;
            double l = double.IsNaN(leftHit) ? double.MaxValue : leftHit, r = double.IsNaN(rightHit) ? double.MaxValue : rightHit;
            return r >= l ? 35 : -35;
        }

        /// <summary>Stops by nearest neighbour weighted by science value: next = min(distance / (1 + value)).</summary>
        internal static List<int> WeightedOrder(double lat, double lon, IList<double[]> stops, double radius)
        {
            var order = new List<int>(); var left = new List<int>(); for (int i = 0; i < stops.Count; i++) left.Add(i);
            double cl = lat, co = lon;
            while (left.Count > 0)
            {
                int best = -1; double bs = double.MaxValue;
                foreach (int i in left)
                {
                    double d = NavigationMath.Distance(cl, co, stops[i][0], stops[i][1], radius), v = stops[i].Length > 2 ? Math.Max(0, stops[i][2]) : 0, s = d / (1 + v);
                    if (s < bs) { bs = s; best = i; }
                }
                order.Add(best); left.Remove(best); cl = stops[best][0]; co = stops[best][1];
            }
            return order;
        }
    }

    /// <summary>RS7i (Luke Oct 21 02:37: rover hung on the VAB scaffolding poles, front wheels in the air, guard flipping detour/brake, never
    /// "stuck"): while the autopilot drives, at least half the wheels off the ground and slow for HungS, or no progress toward the goal for
    /// NoProgressS = stop the autopilot and the tour with an alert instead of grinding the wheels and battery.</summary>
    internal sealed class RoverHang
    {
        internal const double HungS = 3, NoProgressS = 20, ProgressM = 2, HungSpeed = 1;
        double hungSince = double.NaN, since = double.NaN, bestLeft = double.NaN;
        internal void Reset() { hungSince = since = bestLeft = double.NaN; }
        internal string Sample(double now, int wheels, int grounded, double speed, double goalLeft)
        {
            bool hung = wheels > 0 && grounded * 2 <= wheels && speed < HungSpeed;
            if (!hung) hungSince = double.NaN; else if (double.IsNaN(hungSince)) hungSince = now;
            if (hung && now - hungSince >= HungS) return (wheels - grounded) + " of " + wheels + " wheels off the ground";
            if (double.IsNaN(goalLeft)) { since = bestLeft = double.NaN; return null; }
            if (double.IsNaN(since) || goalLeft < bestLeft - ProgressM) { since = now; bestLeft = goalLeft; return null; }
            if (now - since >= NoProgressS) return "no progress for " + NoProgressS.ToString("0") + " s with the autopilot driving";
            return null;
        }
    }

    /// <summary>No progress for 10 s while the autopilot wants to move = stuck: back up 5 m (max 8 s), then re-route with a side bias.</summary>
    internal sealed class RoverStuck
    {
        internal const double Window = 10, BackMetres = 5, BackMaxS = 8, MinSpeed = .3;
        double slowSince = double.NaN, backSince = double.NaN, backDist;
        internal int Count;
        internal bool Backing { get { return !double.IsNaN(backSince); } }
        internal void Reset() { slowSince = backSince = double.NaN; backDist = 0; }
        /// <summary>Blocked by an obstacle at a standstill: back up now.</summary>
        internal void ForceBack(double now) { backSince = now; backDist = 0; slowSince = double.NaN; Count++; }
        /// <summary>RS7d: end a back-up early (obstacle behind).</summary>
        internal void EndBack() { backSince = double.NaN; slowSince = double.NaN; }
        /// <summary>Returns true while backing up (the caller drives in reverse).</summary>
        internal bool Sample(double now, double wantSpeed, double speed, double dt)
        {
            if (Backing)
            {
                backDist += Math.Abs(speed) * dt;
                if (backDist >= BackMetres || now - backSince >= BackMaxS) { backSince = double.NaN; slowSince = double.NaN; return false; }
                return true;
            }
            if (wantSpeed < 1 || speed > MinSpeed) { slowSince = double.NaN; return false; }
            if (double.IsNaN(slowSince)) slowSince = now;
            if (now - slowSince < Window) return false;
            backSince = now; backDist = 0; Count++; return true;
        }
    }

    /// <summary>RS3: learn a faster cap per rover and surface: after 30 s stable at >= 90% of the cap (roll under half the slow-down roll,
    /// no safety event) raise it 1 m/s; a safety event near the cap drops it 2 m/s and stops raising for the session. A user cap is never changed.</summary>
    internal sealed class RoverSpeedLearner
    {
        internal const double StableS = 30, Step = 1, Drop = 2;
        double stableSince = double.NaN; readonly HashSet<RoverSurface> frozen = new HashSet<RoverSurface>();
        internal string Sample(double now, RoverSurface s, RoverCaps caps, bool userCap, double speed, double rollDeg, double rollSlowDeg, bool safetyEvent)
        {
            if (userCap) { stableSince = double.NaN; return null; }
            double cap = caps.Get(s);
            if (safetyEvent && speed > .8 * cap)
            {
                stableSince = double.NaN; frozen.Add(s); double nv = Math.Max(RoverCaps.Default(s) * .5, cap - Drop); if (nv >= cap) return null; caps.Set(s, nv);
                return s + " cap " + cap.ToString("0.#", CultureInfo.InvariantCulture) + " -> " + caps.Get(s).ToString("0.#", CultureInfo.InvariantCulture) + " m/s (safety event at " + speed.ToString("0.0", CultureInfo.InvariantCulture) + ")";
            }
            if (frozen.Contains(s) || speed < .9 * cap || Math.Abs(rollDeg) > rollSlowDeg * .5) { stableSince = double.NaN; return null; }
            if (double.IsNaN(stableSince)) { stableSince = now; return null; }
            if (now - stableSince < StableS || cap >= RoverCaps.Ceiling) return null;
            stableSince = now; caps.Set(s, cap + Step);
            return s + " cap " + cap.ToString("0.#", CultureInfo.InvariantCulture) + " -> " + caps.Get(s).ToString("0.#", CultureInfo.InvariantCulture) + " m/s (stable " + StableS.ToString("0", CultureInfo.InvariantCulture) + " s)";
        }
    }
}