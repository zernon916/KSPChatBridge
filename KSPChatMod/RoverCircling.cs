// RS7b: rover circling detector, pass-by arrival and the reverse-steer rule of the stuck/circling escape (pure, tested).
// Root cause of Luke's Oct 20 circling: a destination inside the rover's turning circle (short off-road final leg after a sharp corner)
// can never come within the 10 m arrival radius - pure pursuit orbits it forever. RoverArrival also accepts the closest point of approach
// within 25 m once the rover starts moving away again; RoverCircling catches any other orbit / no-progress loop.
using System;

namespace KSPChatBridge
{
    /// <summary>Circling: the rover turned 360 deg or more without getting 15 m closer to its target (along-route distance), or made no
    /// 15 m progress for 20 s while moving. Standing still is the stuck detector's job (time below 1 m/s does not count).</summary>
    internal sealed class RoverCircling
    {
        internal const double TurnDeg = 360, ProgressM = 15, StallS = 20, MinSpeed = 1;
        double turned, prevH = double.NaN, best = double.NaN, since = double.NaN, improvedAt = double.NaN;
        internal double Turned { get { return turned; } }
        internal void Reset() { turned = 0; prevH = best = since = improvedAt = double.NaN; }

        /// <summary>null = fine, else why it is circling.</summary>
        internal string Sample(double now, double headingDeg, double distToTarget, double speed)
        {
            if (double.IsNaN(distToTarget) || double.IsNaN(headingDeg)) return null;
            if (double.IsNaN(best)) { best = distToTarget; since = improvedAt = now; prevH = headingDeg; turned = 0; return null; }
            if (speed < MinSpeed) { prevH = headingDeg; improvedAt = Math.Max(improvedAt, now - .001); return null; }
            if (!double.IsNaN(prevH)) turned += Math.Abs(FlightPolicy.Wrap(headingDeg - prevH));
            prevH = headingDeg;
            if (distToTarget < best - ProgressM) { best = distToTarget; since = improvedAt = now; turned = 0; return null; }
            // RS7l: close to the goal (< 60 m) 270 deg of turning without getting closer is already an orbit
            if (turned >= (best < 60 ? 270 : TurnDeg)) return "turned " + Math.Round(turned) + " deg in " + Math.Round(now - since) + " s with " + Math.Round(Math.Max(0, best - distToTarget)) + " m progress (" + Math.Round(distToTarget) + " m to go)";
            if (now - improvedAt >= StallS) return "no progress toward the target for " + Math.Round(now - improvedAt) + " s (" + Math.Round(distToTarget) + " m to go)";
            return null;
        }
    }

    /// <summary>Arrival: inside 10 m, or passed the closest point of approach within 25 m (distance grew 3 m again).</summary>
    internal sealed class RoverArrival
    {
        internal const double RadiusM = 10, PassM = 25, MarginM = 3;
        double min = double.NaN;
        internal void Reset() { min = double.NaN; }
        internal bool Sample(double distance)
        {
            if (double.IsNaN(distance)) return false;
            if (distance <= RadiusM) return true;
            if (double.IsNaN(min) || distance < min) min = distance;
            return min <= PassM && distance > min + MarginM;
        }
    }

    internal static class RoverEscape
    {
        internal const int MaxRetries = 3;
        /// <summary>Reversing turns the nose the other way: steer opposite to the forward command to point the nose at the next waypoint.</summary>
        internal static double BackSteer(double forwardSteer) { return -forwardSteer; }
        /// <summary>Retries exhausted: stop (escape attempts beyond MaxRetries).</summary>
        internal static bool GiveUp(int attempts) { return attempts > MaxRetries; }
    }
}