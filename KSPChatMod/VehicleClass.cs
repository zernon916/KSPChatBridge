// RS1: vehicle classification (pure, tested). The per-craft ROVER switch at the top of AUTOPILOT / CRAFT is the source of truth for the
// tablet pages; this classifier is the hint shown beside it, the [class] log line and NativeFlightController.CraftKind().
using System;
using System.Collections.Generic;
using System.Text;

namespace KSPChatBridge
{
    internal enum VehicleKind { Plane, Heli, Rover, Rocket }

    internal struct VehicleCounts
    {
        internal int Wings, Rotors, Props, Wheels, Skis, Motors, Engines;
    }

    internal static class VehicleClass
    {
        /// <summary>No wings, no rotors, no props and wheels (or skis) = ROVER; rotors/props without wings = HELI; wings = PLANE; else ROCKET.</summary>
        internal static VehicleKind Classify(VehicleCounts c, out string reason)
        {
            bool spin = c.Rotors > 0 || c.Props > 0;
            if (c.Wings == 0 && !spin && (c.Wheels > 0 || c.Skis > 0))
            {
                reason = "wheels " + c.Wheels + (c.Skis > 0 ? ", skis " + c.Skis : "") + ", motors " + c.Motors + ", no wings/rotors/props"
                    + (c.Motors == 0 ? "; WARNING no wheel motors" : "") + (c.Engines > 0 ? "; WARNING " + c.Engines + " engine(s) on a rover" : "");
                return VehicleKind.Rover;
            }
            if (spin && c.Wings == 0) { reason = "rotors " + c.Rotors + ", props " + c.Props + ", no wings"; return VehicleKind.Heli; }
            if (c.Wings > 0) { reason = "wings " + c.Wings + (spin ? ", props/rotors " + (c.Rotors + c.Props) : "") + (c.Wheels > 0 ? ", wheels " + c.Wheels : ""); return VehicleKind.Plane; }
            reason = "no wings, rotors, props or wheels"; return VehicleKind.Rocket;
        }

        internal static string Name(VehicleKind k) { return k.ToString().ToUpperInvariant(); }
        internal static string LogLine(string craft, VehicleKind k, string reason) { return craft + ": " + Name(k) + " (" + reason + ")"; }

        /// <summary>Words of a part name / title / tag split on separators and camelCase ("landingSki" -> landing, ski).</summary>
        internal static List<string> Words(string s)
        {
            var r = new List<string>(); if (string.IsNullOrEmpty(s)) return r; var b = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (!char.IsLetter(ch)) { Flush(r, b); continue; }
                if (char.IsUpper(ch) && b.Length > 0 && char.IsLower(s[i - 1])) Flush(r, b);
                b.Append(char.ToLowerInvariant(ch));
            }
            Flush(r, b); return r;
        }
        static void Flush(List<string> r, StringBuilder b) { if (b.Length > 0) { r.Add(b.ToString()); b.Length = 0; } }
        static bool HasWord(string s, params string[] words) { foreach (string w in Words(s)) foreach (string x in words) if (w == x) return true; return false; }

        /// <summary>Skis: a part whose name, title or tag has the word "ski"/"skis" (no stock skis; mod parts only).</summary>
        internal static bool IsSki(string name, string title, string tags) { return HasWord(name, "ski", "skis") || HasWord(title, "ski", "skis") || HasWord(tags, "ski", "skis"); }

        /// <summary>Propeller modules: Firespitter (FSpropellerTweak, FSpropellerAtmosphericNerf, ...) and any module named *propeller* / *prop* engine.</summary>
        internal static bool IsPropModule(string module)
        {
            if (string.IsNullOrEmpty(module)) return false; string m = module.ToLowerInvariant();
            return m.Contains("propeller") || m.StartsWith("fsprop", StringComparison.Ordinal) || m == "modulepropeller" || m.Contains("airscrew");
        }

        /// <summary>Propeller / blade parts by name: stock Breaking Ground (largePropeller, mediumHeliBlade, ...) and generic mod names.</summary>
        internal static bool IsPropName(string name, string title)
        {
            return HasWord(name, "propeller", "prop", "props", "blade", "blades", "airscrew") || HasWord(title, "propeller", "prop", "blade", "blades", "airscrew");
        }
    }
}