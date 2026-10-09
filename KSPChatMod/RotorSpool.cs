using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class RotorSpool
    {
        internal sealed class Measurement
        {
            internal string Id;
            internal double Rpm, Limit, Brake, Torque;
            internal bool Motor;
        }
        readonly HashSet<string> required;
        readonly Dictionary<string, double> previous = new Dictionary<string, double>();
        readonly Dictionary<string, int> stable = new Dictionary<string, int>();
        readonly double started;
        long sample = -1;
        internal string Result { get; private set; }
        internal RotorSpool(IEnumerable<string> ids, double now) { required = new HashSet<string>(ids); started = now; }
        internal double Torque(double now) { return Math.Min(100, Math.Max(0, Math.Floor(now - started) * 20)); }
        internal bool Tick(double now, long sequence, IList<Measurement> measurements)
        {
            if (Result != null) return Result == "ready";
            if (required.Count == 0) { Result = "no rotors"; return false; }
            if (now - started > 50) { Result = "RPM spool timeout"; return false; }
            if (now - started < 5 || sequence == sample) return false;
            sample = sequence;
            var seen = new HashSet<string>(); bool ready = true;
            foreach (var m in measurements)
            {
                if (!required.Contains(m.Id)) continue;
                seen.Add(m.Id);
                double before; bool climbing = previous.TryGetValue(m.Id, out before) && m.Rpm > before + .5;
                bool good = !double.IsNaN(m.Rpm) && !double.IsInfinity(m.Rpm) && m.Limit > 0 && m.Rpm >= .9 * m.Limit && m.Motor && m.Brake <= .5 && m.Torque >= 5;
                int count; stable.TryGetValue(m.Id, out count); stable[m.Id] = good ? count + 1 : 0;
                previous[m.Id] = m.Rpm; ready &= good && (climbing || stable[m.Id] >= 2);
            }
            if (!seen.SetEquals(required)) { Result = "rotor disappeared during spool"; return false; }
            if (ready) Result = "ready";
            return ready;
        }
    }

    internal sealed class CollectiveController
    {
        double integral, output;
        internal CollectiveController(double initial = 4) { integral = output = FlightPolicy.Clamp(initial, 0, 30); }
        internal void Bias(double amount) { integral = FlightPolicy.Clamp(integral + amount, 2, 30); }
        internal double Step(double targetVs, double vs, double dt, bool ground)
        {
            if (ground && targetVs < 0)
            { integral = 0; output = Math.Max(0, output - 3 * dt); return output; }
            double error = targetVs - vs;
            integral += ground && targetVs > 0 && vs < .3 ? dt : .15 * error * dt;
            integral = FlightPolicy.Clamp(integral, 2, 30);
            double desired = FlightPolicy.Clamp(integral + .6 * error, 2, 30);
            output += FlightPolicy.Clamp(desired - output, -3 * dt, 3 * dt);
            return output;
        }
    }
    internal sealed class RotorPark
    {
        readonly double started;
        internal RotorPark(double now) { started = now; }
        internal string Step(double now, bool grounded, System.Collections.Generic.IEnumerable<RotorSpool.Measurement> rotors)
        {
            if (!grounded) return "cancel";
            if (now - started > 30) return "timeout";
            int count = 0;
            foreach (var rotor in rotors)
            {
                count++;
                if (double.IsNaN(rotor.Rpm) || double.IsInfinity(rotor.Rpm) || rotor.Rpm >= 20 || rotor.Motor) return "wait";
            }
            return count > 0 ? "brake" : "wait";
        }
    }
}
