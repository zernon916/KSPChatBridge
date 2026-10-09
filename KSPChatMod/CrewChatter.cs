using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Who voices replies in native mode (bridge chat.py: the kerbal at the controls speaks the AI's replies).</summary>
    internal static class CrewVoice
    {
        /// <summary>Pilot-trait crew first, else the first crew member; null with no crew.</summary>
        internal static KeyValuePair<string, string>? Pilot(IList<KeyValuePair<string, string>> crew)
        {
            if (crew == null || crew.Count == 0) return null;
            foreach (var c in crew) if (IntercomTalk.NormTrait(c.Value) == "pilot") return c;
            return crew[0];
        }

        /// <summary>Name shown for a reply: pilot first name, else the AI name, else "AICS".</summary>
        internal static string Speaker(IList<KeyValuePair<string, string>> crew, string aiName)
        {
            var p = Pilot(crew);
            if (p != null && IntercomTalk.First(p.Value.Key).Length > 0) return IntercomTalk.First(p.Value.Key);
            return string.IsNullOrEmpty(aiName) ? "AICS" : aiName;
        }

        /// <summary>chat.py: short plain confirmations get ", Captain." when a kerbal voices them.</summary>
        internal static string Captainize(string res)
        {
            if (string.IsNullOrEmpty(res)) return res;
            if (res.Length <= 40 && res.EndsWith(".") && !res.Contains("(") && !res.StartsWith("No") && !res.StartsWith("Not") && !res.EndsWith("Captain."))
                return res.Substring(0, res.Length - 1) + ", Captain.";
            return res;
        }

        /// <summary>Chat line for a reply: "Sidry: Gear down, Captain." (AICS when nobody is aboard).</summary>
        internal static string Line(string speaker, string reply)
        {
            string s = string.IsNullOrEmpty(speaker) ? "AICS" : speaker;
            return s + ": " + (s == "AICS" ? reply : Captainize(reply));
        }

        /// <summary>System-prompt lines that put the model in the pilot's seat (empty with no crew).</summary>
        internal static string Persona(string name, string trait, string description, string vessel)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string first = IntercomTalk.First(name), tr = IntercomTalk.NormTrait(trait);
            return "\nYou are " + first + ", the " + tr + " at the controls" + (string.IsNullOrEmpty(vessel) ? "" : " of " + vessel) +
                   ". Answer Luke (your Captain) in first person, in character, in one or two short sentences" +
                   (string.IsNullOrEmpty(description) ? "." : ". Personality: " + description) +
                   "\nNever call yourself AICS or an AI. Use tools to do things; report only what the tools returned.";
        }
    }

    /// <summary>Native port of crew.py (canned lines): emergency intercom reactions + 'are we there yet' trip chatter.</summary>
    internal sealed class CrewChatter
    {
        internal const double MemberGapS = 10, TripMinFlightS = 120, ShortTripS = 300, ShortTripP = 0.35,
            GapMinS = 180, GapMaxS = 360, FirstMinS = 90, CalmAfterEmergencyS = 60, PilotReplyP = 0.5;
        internal const int MaxPerEvent = 2;
        static readonly HashSet<string> Mech = new HashSet<string> { "parts", "flameout", "overheat", "prop_out", "rotor_brake", "rotor_torque", "heli_rpm", "heli_tail", "reverse" };
        internal static readonly Dictionary<string, string[]> Lines = new Dictionary<string, string[]>
        {
            { "scientist", new[] { "WHAT IS GOING ON?!", "These readings are off the charts! Is that GOOD?!", "I'm logging everything! For science! AAAH!",
                "G-meter says {g}! My stomach says more!", "Fascinating! Terrifying! Mostly terrifying!", "Somebody explain the data! Anybody!", "Is it supposed to make that noise?!" } },
            { "scientist_mech", new[] { "Sensors just lost {part_the}! WHAT HAPPENED?!", "The {part}?! That's not in my experiment plan!", "Readings dropped out on {part_the}! Engineers, talk to me!" } },
            { "engineer", new[] { "That's not supposed to do that!", "Hold together, baby, hold together...", "I just tightened those bolts!", "Everything's rattling back here!", "Who signed off on this design?! Oh. Me." } },
            { "engineer_mech", new[] { "The {part} is GONE! I'll have to fix that!", "{part} - that's MY {part}! I'll have to fix that!", "We lost the {part}! Somebody owes me a wrench!",
                "The {part}! I JUST serviced that!", "{part} is shot! Don't touch anything else!" } },
            { "tourist", new[] { "AAAAAAAAHHHHH!", "I WANT A REFUND!", "WAAAAAAAH!", "IS THIS PART OF THE TOUR?!", "MOMMY!", "EEEEEEEEEE!", "I'M NEVER FLYING AGAIN! AAAH!" } },
        };
        internal static readonly Dictionary<string, string[]> TripLines = new Dictionary<string, string[]>
        {
            { "scientist", new[] { "Are we there yet?", "How much longer? My samples are getting bored.", "Is this the scenic route?", "I've catalogued every cloud. Twice. Are we close?", "By my calculations we should be there by now." } },
            { "engineer", new[] { "Are we there yet? Engine sounds fine, I'm just bored.", "How long? I've tightened every bolt twice.", "Are we close? I'm running out of things to fix.", "Any chance we arrive this week?" } },
            { "tourist", new[] { "ARE WE THERE YET?", "Are we there yet? Are we? Are we?", "I need the bathroom. Are we close?", "Is that it? Is THAT it?", "This is the longest tour EVER." } },
            { "impatient", new[] { "ARE. WE. THERE. YET?!", "Seriously, how much LONGER?!", "I've asked {n} times! ARE WE THERE?!", "I'm walking the rest of the way!" } },
        };
        static readonly string[] PilotEta = { "About {eta} to go - sit tight.", "Roughly {eta}. Ask me again and I land here.", "{eta}, give or take. Enjoy the view." };
        static readonly string[] PilotNone = { "No ETA yet - enjoy the view.", "We get there when we get there.", "Sit back. I'll tell you when." };

        internal bool Enabled = true;
        readonly Random rng;
        readonly Dictionary<string, double> last = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string vid; double? flySince, due; int n; bool done; double lastEmerg = -1e9;
        internal CrewChatter(Random rng = null) { this.rng = rng ?? new Random(); }

        static List<KeyValuePair<string, string>> Others(IList<KeyValuePair<string, string>> crew, string pilot)
        {
            string pf = IntercomTalk.First(pilot).ToLowerInvariant();
            var o = new List<KeyValuePair<string, string>>();
            if (crew != null) foreach (var c in crew) if (IntercomTalk.First(c.Key).ToLowerInvariant() != pf) o.Add(new KeyValuePair<string, string>(c.Key, IntercomTalk.NormTrait(c.Value)));
            return o;
        }

        /// <summary>crew.py slots(): formatted intercom lines for one emergency (pilot excluded, rate-limited, 2 max).</summary>
        internal List<string> Emergency(string kind, string part, IList<KeyValuePair<string, string>> crew, string pilot, double now, double g = 1)
        {
            var outp = new List<string>();
            lastEmerg = now; due = null;
            if (!Enabled) return outp;
            bool mech = Mech.Contains(kind ?? "");
            var order = mech ? new Dictionary<string, int> { { "engineer", 0 }, { "scientist", 1 }, { "tourist", 2 } } : new Dictionary<string, int> { { "tourist", 0 }, { "scientist", 1 }, { "engineer", 2 } };
            var others = Others(crew, pilot);
            var keyed = new List<Tuple<int, double, KeyValuePair<string, string>>>();
            foreach (var o in others) { int r; keyed.Add(Tuple.Create(order.TryGetValue(o.Value, out r) ? r : 3, rng.NextDouble(), o)); }
            keyed.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
            var seen = new HashSet<string>();
            string p = string.IsNullOrEmpty(part) ? "part" : part, pthe = string.IsNullOrEmpty(part) ? "a part" : "the " + part;
            foreach (var k in keyed)
            {
                if (outp.Count >= MaxPerEvent) break;
                string name = k.Item3.Key, tr = k.Item3.Value; double l;
                if ((tr != "engineer" && tr != "scientist" && tr != "tourist") || (last.TryGetValue(name, out l) && now - l < MemberGapS)) continue;
                if (seen.Contains(tr) && others.Count > seen.Count + 1) continue;
                string[] pool = mech && Lines.ContainsKey(tr + "_mech") && kind != "reverse" ? Lines[tr + "_mech"] : Lines[tr];
                string text = pool[rng.Next(pool.Length)].Replace("{part}", p).Replace("{part_the}", pthe).Replace("{g}", g.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                outp.Add(IntercomTalk.Fmt(name, tr, char.ToUpperInvariant(text[0]) + text.Substring(1)));
                last[name] = now; seen.Add(tr);
            }
            return outp;
        }

        internal double? NextGap(double eta, int count)
        {
            if (!double.IsNaN(eta) && eta < ShortTripS)
            {
                if (count >= 1 || rng.NextDouble() > ShortTripP) return null;
                double lo0 = FirstMinS * .5; return lo0 + rng.NextDouble() * (Math.Max(lo0 + 1, Math.Min(eta * .6, FirstMinS * 2)) - lo0);
            }
            double lo = count == 0 ? FirstMinS : GapMinS, hi = count == 0 ? GapMaxS * .6 : GapMaxS;
            return (lo + rng.NextDouble() * (hi - lo)) * Math.Max(.6, 1 - .1 * count);
        }

        static string FmtEta(double s) { int i = (int)Math.Round(s); return i >= 120 ? i / 60 + " min" : i >= 60 ? i / 60 + " min " + i % 60 + " s" : i + " s"; }

        /// <summary>crew.py Trip.tick (canned): lines to post this tick (empty most of the time). eta NaN = unknown.</summary>
        internal List<string> TripTick(double now, string vesselId, bool flying, IList<KeyValuePair<string, string>> crew, string pilot, bool tripActive, bool emergency, double eta)
        {
            var outp = new List<string>();
            if (vesselId != vid || !flying) { vid = vesselId; flySince = null; due = null; n = 0; done = false; if (!flying) return outp; }
            if (flySince == null) flySince = now;
            if (emergency) { lastEmerg = now; due = null; return outp; }
            if (!Enabled || done || now - lastEmerg < CalmAfterEmergencyS) return outp;
            if (!(tripActive || now - flySince.Value >= TripMinFlightS)) return outp;
            var cands = new List<KeyValuePair<string, string>>(); double l;
            foreach (var o in Others(crew, pilot))
                if ((o.Value == "scientist" || o.Value == "engineer" || o.Value == "tourist") && !(last.TryGetValue(o.Key, out l) && now - l < MemberGapS)) cands.Add(o);
            if (cands.Count == 0) return outp;
            if (due == null) { var g0 = NextGap(eta, n); if (g0 == null) done = true; else due = now + g0.Value; return outp; }
            if (now < due.Value) return outp;
            var who = cands[rng.Next(cands.Count)]; last[who.Key] = now; n++;
            string[] pool = n >= 3 && rng.NextDouble() < .6 ? TripLines["impatient"] : TripLines[who.Value];
            outp.Add(IntercomTalk.Fmt(who.Key, who.Value, pool[rng.Next(pool.Length)].Replace("{n}", n.ToString())));
            var nxt = NextGap(eta, n); due = nxt == null ? (double?)null : now + nxt.Value; done = nxt == null;
            if (!string.IsNullOrEmpty(pilot) && rng.NextDouble() < PilotReplyP)
                outp.Add(IntercomTalk.Fmt(pilot, "pilot", double.IsNaN(eta) ? PilotNone[rng.Next(PilotNone.Length)] : PilotEta[rng.Next(PilotEta.Length)].Replace("{eta}", FmtEta(eta))));
            return outp;
        }
    }
}
