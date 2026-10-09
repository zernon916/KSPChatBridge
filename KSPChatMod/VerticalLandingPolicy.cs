using System;

namespace KSPChatBridge
{
    // Slow, upright powered descent. Orbital entry and lateral guidance are separate modes.
    internal sealed class VerticalLandingPolicy
    {
        internal double Throttle { get; private set; }
        internal string Phase { get; private set; } = "descent";
        readonly double touchdown;
        double lastStep;
        readonly TouchdownGate contact = new TouchdownGate();
        internal VerticalLandingPolicy(double throttle, double touchdown, double now)
        {
            if (!Finite(throttle) || !Finite(touchdown) || touchdown < .5 || touchdown > 3)
                throw new ArgumentException("Invalid powered descent settings.");
            Throttle = FlightPolicy.Clamp(throttle, .05, 1); this.touchdown = touchdown; lastStep = now;
        }
        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        internal static string Feasibility(double height, double vs, double horizontal, double acceleration, double gravity, double up, double throttle)
        {
            foreach (double x in new[] { height, vs, horizontal, acceleration, gravity, up, throttle })
                if (!Finite(x)) return "Powered descent needs valid height, velocity and thrust measurements.";
            if (height < 10 || height > 5000 || Math.Abs(vs) > 15 || horizontal > 3 || up < .98)
                return "Powered descent requires 10-5000 m clearance, vertical speed within 15 m/s, sideways speed below 3 m/s and an upright craft.";
            if (gravity <= 0 || acceleration * up < gravity * 1.2 || throttle < .05 || throttle > 1)
                return "Powered descent needs a burning controllable engine and at least 1.2 thrust-to-weight.";
            // Worst-case height loss while respecting the 5%/3-second throttle ramp.
            double t = throttle, velocity = vs, loss = 0, worst = 0;
            for (int i = 0; i < 20; i++)
            {
                double net = acceleration * up * t - gravity;
                double next = velocity + net * 3;
                double interval = net > 0 && velocity < 0 && next > 0 ? -velocity / net : 3;
                loss -= velocity * interval + .5 * net * interval * interval;
                worst = Math.Max(worst, loss);
                if (next >= 0 && net > 0) break;
                velocity = next; t = Math.Min(1, t + .05);
            }
            return height <= worst + 10 ? "Insufficient clearance for the required throttle ramp." : null;
        }
        internal double Step(double now, double height, double vs, double acceleration, double gravity, double up, bool grounded)
        {
            foreach (double x in new[] { now, height, vs, acceleration, gravity, up })
                if (!Finite(x)) { Phase = "sensor unavailable"; return Throttle; }
            if (contact.Step(now, grounded, vs)) { Phase = "landed"; return Throttle = 0; }
            if (grounded) { Phase = "touchdown"; return Throttle = 0; }
            double vertical = acceleration * Math.Max(0, up);
            if (vertical < gravity * 1.05)
            {
                Phase = "insufficient thrust";
                return Throttle = FlightPolicy.Throttle(Throttle, 1, true, now, ref lastStep);
            }
            Phase = height < 40 ? "final" : "descent";
            double predictedHeight = Math.Max(0, height + Math.Min(0, vs) * 6 - 5);
            double desiredVs = -Math.Min(10, touchdown + Math.Sqrt(.4 * (vertical - gravity) * predictedHeight));
            double desired = (gravity + (desiredVs - vs) / 6) / vertical;
            return Throttle = FlightPolicy.Throttle(Math.Max(.05, Throttle), FlightPolicy.Clamp(desired, .05, 1), true, now, ref lastStep);
        }
    }
}
