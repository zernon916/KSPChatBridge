using System;

namespace KSPChatBridge
{
    internal sealed class HelicopterYaw
    {
        double integral, start = double.NaN, firstDemand, firstRate;
        bool consistent;
        int badWindows;
        internal double Sign = 1;
        internal double Step(double error, double rate, double dt)
        {
            integral = FlightPolicy.Clamp(integral + .01 * error * dt, -.6, .6);
            return FlightPolicy.Clamp(.03 * error - .05 * rate + integral, -1, 1);
        }
        internal bool Observe(double now, double demand, double rate)
        {
            if (double.IsNaN(start) || now < start) { start = now; firstDemand = demand; firstRate = rate; consistent = Math.Abs(demand) >= .3; return false; }
            consistent &= Math.Abs(demand) >= .3 && Math.Sign(demand) == Math.Sign(firstDemand);
            if (now - start < 1) return false;
            double response = (rate - firstRate) / (now - start) * firstDemand;
            if (consistent && response < -2) badWindows++;
            else if (consistent && response > 2) badWindows = 0;
            start = now; firstDemand = demand; firstRate = rate; consistent = Math.Abs(demand) >= .3;
            if (badWindows < 3) return false;
            Sign = -Sign; badWindows = 0; integral = 0; return true;
        }
    }
    internal sealed class RotorLayoutInfo
    {
        internal bool Heli, Compound, Coaxial, Counter, Multirotor, Locked;
        internal string Kind = "not a helicopter", Yaw = "reaction wheels / SAS";
        internal int[] Lift = new int[0], Tail = new int[0], Left = new int[0], Right = new int[0];
        internal int[][] Mains = new int[0][];
    }
    internal sealed class RotorDescriptor
    {
        internal double Up, Forward, Right, Side, North, East;
        internal int Dir; // +1 CW, -1 CCW, 0 unknown
        internal int Blades = -1; // -1 = unknown
        internal bool Locked;
    }
    internal static class HelicopterPolicy
    {
        const double CoaxSep = 1;
        const int MultiMin = 3;
        internal static string RotorRole(double up, double forward, double right, double side)
        {
            if (Math.Abs(up) >= .8) return "lift";
            if (Math.Abs(forward) >= .7) return side < -.25 ? "left" : side > .25 ? "right" : "forward";
            return Math.Abs(right) >= .7 ? "tail" : "canted";
        }
        // Pure port of heli.classify for layout/yaw strategy; blade counts optional.
        internal static RotorLayoutInfo Classify(RotorDescriptor[] rotors, bool wings)
        {
            var info = new RotorLayoutInfo();
            if (rotors == null || rotors.Length == 0) return info;
            bool known = false;
            for (int i = 0; i < rotors.Length; i++) if (rotors[i].Blades > 0) known = true;
            var bare = new System.Collections.Generic.List<int>();
            var lift = new System.Collections.Generic.List<int>();
            var horiz = new System.Collections.Generic.List<int>();
            for (int i = 0; i < rotors.Length; i++)
            {
                if (known && rotors[i].Blades == 0) { bare.Add(i); continue; }
                if (Math.Abs(rotors[i].Up) >= .8) lift.Add(i);
                else horiz.Add(i);
            }
            var tail = new System.Collections.Generic.List<int>();
            var left = new System.Collections.Generic.List<int>();
            var right = new System.Collections.Generic.List<int>();
            foreach (int i in horiz)
            {
                if (Math.Abs(rotors[i].Right) > .7) tail.Add(i);
                else if (Math.Abs(rotors[i].Forward) > .7)
                {
                    if (rotors[i].Side <= -.25) left.Add(i);
                    else if (rotors[i].Side >= .25) right.Add(i);
                }
            }
            var bladed = new System.Collections.Generic.List<int>();
            for (int i = 0; i < rotors.Length; i++) if (!bare.Contains(i)) bladed.Add(i);
            if (bladed.Count >= MultiMin && lift.Count == 0)
            {
                var soft = new System.Collections.Generic.List<int>();
                foreach (int i in bladed) if (Math.Abs(rotors[i].Up) >= .3) soft.Add(i);
                lift = soft.Count >= MultiMin ? soft : bladed;
                horiz.Clear(); tail.Clear(); left.Clear(); right.Clear();
            }
            info.Compound = lift.Count > 0 && left.Count > 0 && right.Count > 0;
            info.Heli = lift.Count > 0 && (info.Compound || !wings || tail.Count > 0 || lift.Count >= 2);
            var mains = new System.Collections.Generic.List<System.Collections.Generic.List<int>>();
            foreach (int i in lift)
            {
                System.Collections.Generic.List<int> hit = null;
                foreach (var cluster in mains)
                    foreach (int j in cluster)
                        if (Distance2(rotors[i], rotors[j]) < CoaxSep) { hit = cluster; break; }
                if (hit == null) mains.Add(new System.Collections.Generic.List<int> { i });
                else hit.Add(i);
            }
            info.Coaxial = false;
            foreach (var cluster in mains) if (cluster.Count > 1) info.Coaxial = true;
            var dirs = new System.Collections.Generic.HashSet<int>();
            foreach (int i in lift) if (rotors[i].Dir != 0) dirs.Add(rotors[i].Dir);
            info.Counter = dirs.Count > 1;
            info.Locked = false;
            foreach (int i in lift) if (rotors[i].Locked) info.Locked = true;
            info.Multirotor = !info.Compound && lift.Count >= MultiMin;
            if (!info.Heli) info.Kind = "not a helicopter";
            else if (info.Compound) info.Kind = "compound (" + (info.Coaxial ? "coaxial " : "") + "main rotor + side props)";
            else if (lift.Count >= 2)
                info.Kind = (info.Coaxial ? "coaxial" : lift.Count == 2 ? "tandem" : "multirotor (" + lift.Count + ")")
                    + (info.Counter ? ", counter-rotating" : "");
            else info.Kind = "single rotor" + (tail.Count > 0 ? " + tail rotor" : " (no tail rotor)");
            if (info.Compound) info.Yaw = "side props (differential pitch)";
            else if (tail.Count > 0) info.Yaw = "tail rotor";
            else if (info.Multirotor && info.Counter) info.Yaw = "differential torque (counter-rotating pairs)";
            else if (lift.Count >= 2 && info.Coaxial && info.Counter) info.Yaw = "coaxial differential torque";
            else info.Yaw = "reaction wheels / SAS";
            info.Lift = lift.ToArray(); info.Tail = tail.ToArray(); info.Left = left.ToArray(); info.Right = right.ToArray();
            info.Mains = new int[mains.Count][];
            for (int i = 0; i < mains.Count; i++) info.Mains[i] = mains[i].ToArray();
            return info;
        }
        static double Distance2(RotorDescriptor a, RotorDescriptor b)
        {
            double dn = a.North - b.North, de = a.East - b.East;
            return Math.Sqrt(dn * dn + de * de);
        }
        internal static string LayoutLabel(RotorLayoutInfo info, int index, double side)
        {
            if (info == null || !info.Heli) return "R" + (index + 1);
            if (info.Multirotor)
            {
                string code = side < -.1 ? "L" : side > .1 ? "R" : "C";
                for (int k = 0; k < info.Mains.Length; k++)
                    foreach (int i in info.Mains[k]) if (i == index) return code + (k + 1);
            }
            foreach (int i in info.Tail) if (i == index) return "TR";
            foreach (int i in info.Left) if (i == index) return "LP";
            foreach (int i in info.Right) if (i == index) return "RP";
            if (info.Lift.Length == 1 && info.Lift[0] == index) return "MR";
            for (int k = 0; k < info.Lift.Length; k++) if (info.Lift[k] == index) return "L" + (k + 1);
            return "R" + (index + 1);
        }
        internal static void HoldVelocity(double distance, double bearing, double heading, out double forward, out double right)
        {
            double speed = Math.Min(3, Math.Max(0, distance) * .1), angle = FlightPolicy.Wrap(bearing - heading) * Math.PI / 180;
            forward = speed * Math.Cos(angle); right = speed * Math.Sin(angle);
        }
        internal static double Autorotation(double agl, double vs, double rpm, double reference)
        { return agl < 6 && vs < -1 ? 21 : FlightPolicy.Clamp(1 + .02 * (rpm - .9 * reference), 0, 6); }
    }
    internal sealed class RotorEmergency
    {
        double? lostAt;
        internal string Mode = "normal";
        internal string Step(double now, bool known, double rpm, double limit, bool motor, bool locked, bool ground)
        {
            if (ground) { lostAt = null; Mode = "normal"; return Mode; }
            if (!known) { lostAt = null; return Mode; }
            bool lost = !motor || rpm < .45 * limit;
            if (!lost) { lostAt = null; Mode = "normal"; return Mode; }
            if (!lostAt.HasValue || now < lostAt.Value) lostAt = now;
            if (now - lostAt.Value >= 1) Mode = locked ? "locked rotor: controlled descent" : "autorotation";
            return Mode;
        }
    }
    internal sealed class TouchdownGate
    {
        double? since;
        internal bool Step(double now, bool grounded, double verticalSpeed)
        {
            if (!grounded || Math.Abs(verticalSpeed) >= .5) { since = null; return false; }
            if (!since.HasValue || now < since.Value) since = now;
            return now - since.Value >= 3;
        }
    }
}
