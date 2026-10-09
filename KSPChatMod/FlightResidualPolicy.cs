using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KSPChatBridge
{
    // P5-2: pure decisions for the flight residual ports (aliases, throttle/SAS/flaps parsing, confirm gates, reports).
    // No Unity/KSP types so the offline suite pins behaviour; NativeFlightResidual.cs applies them in game.
    internal static class FlightResidualPolicy
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>land(where): "grounded" | "heli" | "plane" | "vertical" | "spot".</summary>
        internal static string LandRoute(string where, bool grounded, bool rotorcraft, bool plane, bool spotKnown)
        {
            if (grounded) return "grounded";
            if (rotorcraft) return "heli";
            if (!string.IsNullOrEmpty(where) && where.Trim().Length > 0)
            {
                string w = where.Trim().ToLowerInvariant();
                if (plane && (spotKnown || w.Contains("runway") || w.Contains("island") || w == "ksc")) return "spot";
                if (!plane && (w == "here" || w == "now")) return "vertical";
                if (!plane) return "vertical";
            }
            return plane ? "plane" : "vertical";
        }

        /// <summary>Normalise a KSC alias to the built-in runway name the native autoland knows.</summary>
        internal static string RunwayAlias(string where)
        {
            string w = (where ?? "").Trim();
            return w.Equals("ksc", StringComparison.OrdinalIgnoreCase) || w.Length == 0 ? "KSC Runway" : w;
        }

        /// <summary>0..1 throttle; values above 1 are treated as percent.</summary>
        internal static double Throttle(double value)
        {
            if (double.IsNaN(value)) return 0;
            if (value > 1) value /= 100.0;
            return FlightPolicy.Clamp(value, 0, 1);
        }

        static readonly Dictionary<string, string> SasModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "stability", "StabilityAssist" }, { "stabilityassist", "StabilityAssist" }, { "hold", "StabilityAssist" },
            { "prograde", "Prograde" }, { "retrograde", "Retrograde" }, { "normal", "Normal" }, { "antinormal", "Antinormal" },
            { "radialin", "RadialIn" }, { "radial_in", "RadialIn" }, { "radialout", "RadialOut" }, { "radial_out", "RadialOut" },
            { "target", "Target" }, { "antitarget", "AntiTarget" }, { "anti_target", "AntiTarget" }, { "maneuver", "Maneuver" }, { "node", "Maneuver" },
        };
        /// <summary>Canonical VesselAutopilot.AutopilotMode name, or null.</summary>
        internal static string SasMode(string mode)
        {
            string key = (mode ?? "").Replace(" ", "").Replace("-", "").Trim();
            string v; return SasModes.TryGetValue(key, out v) ? v : null;
        }

        /// <summary>Flap setting to deploy degrees (0/10/20/30); -1 when invalid.</summary>
        internal static double FlapDegrees(string setting)
        {
            switch ((setting ?? "").Trim().ToLowerInvariant())
            {
                case "0": case "up": case "off": case "retract": return 0;
                case "1": return 10;
                case "2": return 20;
                case "3": case "full": case "down": case "landing": return 30;
                default: return -1;
            }
        }

        /// <summary>Signed bank for circle_here (left negative), limited by speed.</summary>
        internal static double CircleBank(string direction, double bank, double speed)
        {
            double b = FlightPolicy.Clamp(double.IsNaN(bank) ? 15 : Math.Abs(bank), 5, FlightPolicy.BankLimit(speed));
            return (direction ?? "left").Trim().ToLowerInvariant().StartsWith("r") ? b : -b;
        }

        /// <summary>Heading after turn(direction, degrees).</summary>
        internal static double Turn(double heading, string direction, double degrees)
        {
            double d = Math.Abs(double.IsNaN(degrees) ? 90 : degrees);
            double h = heading + ((direction ?? "left").Trim().ToLowerInvariant().StartsWith("r") ? d : -d);
            return (h % 360 + 360) % 360;
        }

        /// <summary>Destructive tools need confirmed=true; returns the refusal text or null.</summary>
        internal static string ConfirmGate(bool confirmed, string action)
        {
            return confirmed ? null : "Confirm first: " + action + " is irreversible. Ask again with confirmed=true.";
        }

        /// <summary>Custom action group 1..10, or 0 when invalid.</summary>
        internal static int ActionGroup(double group)
        {
            int g = (int)Math.Round(group);
            return g >= 1 && g <= 10 && Math.Abs(group - g) < 1e-9 ? g : 0;
        }

        /// <summary>Ideal rocket equation (m/s).</summary>
        internal static double DeltaV(double ispSeconds, double wetMass, double dryMass)
        {
            if (ispSeconds <= 0 || wetMass <= 0 || dryMass <= 0 || dryMass > wetMass) return 0;
            return ispSeconds * 9.80665 * Math.Log(wetMass / dryMass);
        }

        /// <summary>Seconds to touchdown at the current descent rate; NaN when not descending.</summary>
        internal static double LandingEta(double agl, double verticalSpeed)
        {
            if (agl <= 0) return 0;
            if (verticalSpeed >= -0.05) return double.NaN;
            return agl / -verticalSpeed;
        }

        internal static string Distance(double meters)
        {
            if (double.IsNaN(meters)) return "unknown";
            return meters >= 10000 ? (meters / 1000).ToString("0", Inv) + " km" : meters >= 1000 ? (meters / 1000).ToString("0.0", Inv) + " km" : meters.ToString("0", Inv) + " m";
        }

        internal static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "n/a";
            int s = (int)Math.Round(seconds);
            return s >= 3600 ? (s / 3600) + "h " + (s % 3600 / 60) + "m" : s >= 60 ? (s / 60) + "m " + (s % 60) + "s" : s + "s";
        }

        static readonly string[] FuelOrder = { "LiquidFuel", "Oxidizer", "MonoPropellant", "XenonGas", "SolidFuel", "ElectricCharge", "IntakeAir", "Ore" };
        /// <summary>"LiquidFuel 120/400 (30%)" lines for known propellants that the craft carries.</summary>
        internal static string FuelReport(IDictionary<string, double> amounts, IDictionary<string, double> capacities)
        {
            var sb = new StringBuilder();
            foreach (string r in FuelOrder)
            {
                double cap; if (capacities == null || !capacities.TryGetValue(r, out cap) || cap <= 0) continue;
                double amt; amounts.TryGetValue(r, out amt);
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(r).Append(' ').Append(amt.ToString("0", Inv)).Append('/').Append(cap.ToString("0", Inv))
                  .Append(" (").Append((100 * amt / cap).ToString("0", Inv)).Append("%)");
            }
            return sb.Length == 0 ? "No fuel tanks." : sb.ToString();
        }

        /// <summary>landing_check verdict from simple gates (gear, speed, descent rate, slope known).</summary>
        internal static string LandingCheck(bool hasWheels, bool gearDown, double speed, double verticalSpeed, double agl)
        {
            var issues = new List<string>();
            if (hasWheels && !gearDown) issues.Add("gear up");
            if (agl < 500 && verticalSpeed < -10) issues.Add("descent " + (-verticalSpeed).ToString("0", Inv) + " m/s too fast");
            if (agl < 200 && speed > 120) issues.Add("speed " + speed.ToString("0", Inv) + " m/s high for landing");
            return issues.Count == 0 ? "Landing check OK." : "Landing check: " + string.Join(", ", issues.ToArray()) + ".";
        }

        /// <summary>prop_control numeric field: "" = unchanged (NaN), else parsed and clamped.</summary>
        internal static double PropField(string value, double lo, double hi)
        {
            double v;
            if (string.IsNullOrEmpty(value) || !double.TryParse(value.Trim().TrimEnd('%'), NumberStyles.Float, Inv, out v)) return double.NaN;
            return FlightPolicy.Clamp(v, lo, hi);
        }

        /// <summary>prop_control on/off field: "" = unchanged (null).</summary>
        internal static bool? PropSwitch(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": case "yes": return true;
                case "off": case "false": case "0": case "no": return false;
                default: return null;
            }
        }
    }
}
