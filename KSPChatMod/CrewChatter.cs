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

    /// <summary>One intercom line to say: the model writes it from Prompt (in the kerbal's archetype voice); Canned is
    /// the fallback on timeout / busy model / unusable output. AllowedNumbers != null = reject invented numbers.</summary>
    internal sealed class CrewLine
    {
        internal double Delay;
        internal string Name, Trait, Canned, Prompt;
        internal HashSet<string> AllowedNumbers;
        internal string Format(string text) { return IntercomTalk.Fmt(Name, Trait, string.IsNullOrEmpty(text) ? Canned : text); }
        public override string ToString() { return Format(null); }

        static readonly System.Text.RegularExpressions.Regex Num = new System.Text.RegularExpressions.Regex(@"\d+(?:\.\d+)?");
        internal static HashSet<string> Numbers(string s)
        {
            var h = new HashSet<string>();
            foreach (System.Text.RegularExpressions.Match m in Num.Matches(s ?? "")) h.Add(m.Value);
            return h;
        }
        /// <summary>crew.py clean + numbers_ok: the model's line, or null (use Canned).</summary>
        internal string Accept(string modelText)
        {
            string line = IntercomTalk.Clean(modelText);
            if (line == null || line.Split(' ').Length > CrewPrompts.MaxWords) return null;
            if (AllowedNumbers != null) foreach (string n in Numbers(line)) if (!AllowedNumbers.Contains(n)) return null;
            return line;
        }
    }

    /// <summary>crew.py prompts (Luke's simple role-play style) + the short personalities.txt archetype as system prompt.</summary>
    internal static class CrewPrompts
    {
        internal const int MaxWords = 22, MaxTokens = 40;

        /// <summary>Personality pool: personalities.txt shipped next to the plugin (Luke can edit it), else built in.</summary>
        internal static string[][] Pool = PersonalityPrompts.All;
        static readonly System.Text.RegularExpressions.Regex Row = new System.Text.RegularExpressions.Regex("^\\s*\\d+\\.\\s*(.+?)\\s+(?:\u2014|-{1,2})\\s+.*?PROMPT:\\s*\"(.+)\"\\s*$");
        /// <summary>Parse personalities.txt lines ("N. Title ? blurb. PROMPT: \"...\""); role from keywords.</summary>
        internal static string[][] Parse(IEnumerable<string> lines)
        {
            var outp = new List<string[]>();
            foreach (string line in lines)
            {
                var m = Row.Match(line ?? ""); if (!m.Success) continue;
                string title = m.Groups[1].Value.Trim(), prompt = m.Groups[2].Value.Trim(), low = (title + " " + prompt).ToLowerInvariant();
                string role = "any";
                foreach (var kv in new[] {
                    new KeyValuePair<string, string>("pilot", @"\b(pilot|copilot|commander|navigator|veteran|cadet|rookie|racer|daredevil)\b"),
                    new KeyValuePair<string, string>("scientist", @"\b(scientist|geologist|meteorologist|explorer)\b"),
                    new KeyValuePair<string, string>("engineer", @"\b(engineer|mechanic|tinkerer|technician|inspector)\b"),
                    new KeyValuePair<string, string>("tourist", @"\b(tourist|actor|musician|cook|gambler|parent|intern)\b") })
                    if (System.Text.RegularExpressions.Regex.IsMatch(low, kv.Value)) { role = kv.Key; break; }
                outp.Add(new[] { title, role, prompt });
            }
            return outp.ToArray();
        }

        /// <summary>Roll a fresh personality for one kerbal in one save: {primary (trait-matching or generic), secondary
        /// (a different one, any role)} so crews feel unique. Stored per save by AicsCrewScenario.</summary>
        internal static int[] Roll(string trait, Random rng)
        {
            var all = Pool; string tr = IntercomTalk.NormTrait(trait);
            var pool = new List<int>();
            for (int i = 0; i < all.Length; i++) if (all[i][1] == tr || all[i][1] == "any") pool.Add(i);
            if (pool.Count == 0) for (int i = 0; i < all.Length; i++) pool.Add(i);
            int a = pool[rng.Next(pool.Count)], b = all.Length > 1 ? rng.Next(all.Length - 1) : a;
            if (all.Length > 1 && b >= a) b++;
            return new[] { a, b };
        }

        /// <summary>System prompt for a rolled pair: the primary's short prompt + a hint of the secondary (kept short for 3B).</summary>
        internal static string SystemFor(int[] pair)
        {
            var all = Pool;
            if (pair == null || pair.Length == 0 || pair[0] < 0 || pair[0] >= all.Length) return SystemPrompt("You are a kerbal crew member. Short lines.");
            string s = all[pair[0]][2];
            if (pair.Length > 1 && pair[1] >= 0 && pair[1] < all.Length && pair[1] != pair[0]) s += " Also a bit of a " + all[pair[1]][0].ToLowerInvariant() + ".";
            return SystemPrompt(s);
        }
        internal static string Title(int[] pair)
        {
            var all = Pool;
            if (pair == null || pair.Length == 0 || pair[0] < 0 || pair[0] >= all.Length) return "";
            return all[pair[0]][0] + (pair.Length > 1 && pair[1] >= 0 && pair[1] < all.Length && pair[1] != pair[0] ? " + " + all[pair[1]][0] : "");
        }

        internal static string SystemPrompt(string archetypePrompt)
        {
            return archetypePrompt + " Answer with the line only, at most 12 words. You can only talk. Never mention being an AI.";
        }
        internal static string Who(string name, string description)
        {
            string first = IntercomTalk.First(name); if (first.Length == 0) first = "Crew";
            return first + (string.IsNullOrEmpty(description) ? "" : ", " + description);
        }
        internal static string Emergency(string who, string facts)
        { return "You are " + who + ", aboard a Kerbal spacecraft. This is happening: " + facts + ". What do you shout over the intercom? One short line."; }
        internal static string Trip(string who, string facts, int asked)
        { return "You are " + who + ", aboard a Kerbal spacecraft. This is happening: " + facts + (asked > 0 ? "; you've already asked " + asked + " times" : "") + ". What do you say over the intercom? One short line."; }
        internal static string Joke(string who, string facts)
        { return "You are " + who + ", aboard a Kerbal spacecraft. This is happening: " + facts + ". Tell one short, clean dad joke over the intercom. One short line."; }
        internal static string PilotAnswer(string who, string facts)
        { return "You are " + who + ", flying a Kerbal spacecraft. A passenger just asked 'are we there yet?'. This is happening: " + facts + ". What do you answer over the intercom? One short line; only use the numbers given."; }
        internal static string Talk(string who, string other, string topic, IList<KeyValuePair<string, string>> transcript)
        {
            string b = "You are " + who + ", aboard a Kerbal spacecraft on a quiet cruise. ";
            if (transcript == null || transcript.Count == 0) return b + "You start a chat with " + other + " about " + topic + ". What do you say over the intercom? One short line.";
            var parts = new List<string>();
            for (int i = Math.Max(0, transcript.Count - 4); i < transcript.Count; i++) parts.Add(transcript[i].Key + ": \"" + transcript[i].Value + "\"");
            return b + "You're chatting with " + other + " about " + topic + ". So far: " + string.Join(" ", parts.ToArray()) + " What do you reply? One short line.";
        }
        internal static readonly Dictionary<string, string> Facts = new Dictionary<string, string>
        {
            { "parts", "the ship just lost the {what}" }, { "alarm", "a ship alarm just went off: {what}" }, { "landing", "the landing approach went wrong: {what}" }, { "tamper", "someone just changed the {what} settings mid-flight and you are putting them back" }, { "flameout", "an engine ({what}) just flamed out" },
            { "power", "the batteries are almost flat" }, { "overheat", "the {what} is overheating" },
        };
        internal static string FactsFor(string kind, string what, double g)
        {
            string f; if (!Facts.TryGetValue(kind ?? "", out f)) f = "an emergency";
            f = f.Replace("{what}", string.IsNullOrEmpty(what) ? "a part" : what);
            if (g > 3) f += ", pulling " + g.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " G";
            return f;
        }
        internal static string TripFacts(string dest, double eta, double distKm)
        {
            var bits = new List<string> { "a long, quiet cruise" };
            if (!string.IsNullOrEmpty(dest)) bits.Add("heading to " + dest);
            if (!double.IsNaN(eta) && eta > 0) bits.Add("about " + CrewChatter.FmtEta(eta) + " to go");
            if (!double.IsNaN(distKm) && distKm > 0) bits.Add(distKm >= 10 ? distKm.ToString("0") + " km away" : distKm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km away");
            return string.Join(", ", bits.ToArray());
        }
    }

    /// <summary>When a crew line may use the model (pure).</summary>
    internal static class ChatterPolicy
    {
        /// <summary>Model idle and no player chat waiting: otherwise the canned line (player chat always first).</summary>
        internal static bool ModelFree(bool busy, int pending) { return !busy && pending == 0; }
        /// <summary>Embedded: only if already loaded (chatter never triggers a multi-GB load). HTTP providers: yes.</summary>
        internal static bool ProviderReady(bool embedded, bool loaded) { return !embedded || loaded; }
        /// <summary>choices[0].message.content of an OpenAI-style reply, or null.</summary>
        internal static string Content(string raw)
        {
            try
            {
                var d = MiniJson.Deserialize(raw ?? ""); object ch;
                if (d == null || !d.TryGetValue("choices", out ch)) return null;
                var list = ch as System.Collections.IList; if (list == null || list.Count == 0) return null;
                var c0 = list[0] as Dictionary<string, object>; object m; if (c0 == null || !c0.TryGetValue("message", out m)) return null;
                var msg = m as Dictionary<string, object>; object content; if (msg == null || !msg.TryGetValue("content", out content) || content == null) return null;
                return content.ToString();
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>A calm-cruise conversation to run: alternating lines between Pair, canned script fallback per line.</summary>
    internal sealed class CrewTalk
    {
        internal KeyValuePair<string, string>[] Pair; internal string Topic; internal string[] Script; internal int Lines;
    }

    /// <summary>Native port of crew.py: emergency reactions, 'are we there yet' trip chatter, small talk. Pure scheduling:
    /// returns lines/conversations to say; NativeCrew asks the model (never over player chat) and falls back to canned.</summary>
    internal sealed class CrewChatter
    {
        internal const double MemberGapS = 10, TripMinFlightS = 120, ShortTripS = 300, ShortTripP = 0.35,
            GapMinS = 180, GapMaxS = 360, FirstMinS = 90, CalmAfterEmergencyS = 60, PilotReplyP = 0.5,
            DadJokeP = 0.35, DadJokeGapS = 900, TalkGapMinS = 480, TalkGapMaxS = 900, TalkFirstMinS = 240, TalkFirstMaxS = 600, TalkShortP = 0.2;
        internal const int MaxPerEvent = 8, TalkLinesMin = 4, TalkLinesMax = 6;
        static readonly HashSet<string> Mech = new HashSet<string> { "parts", "flameout", "overheat", "prop_out", "rotor_brake", "rotor_torque", "heli_rpm", "heli_tail", "reverse", "tamper" };
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
        static readonly string[] DadJokes = { "Why don't rockets ever get lonely? They always have a launch buddy.", "I tried to grab the Mun. It was out of my orbit.",
            "What do kerbals eat in space? Launch-meat.", "I'd tell you a joke about boosters, but it would take off." };
        static readonly string[] PilotEta = { "About {eta} to go - sit tight.", "Roughly {eta}. Ask me again and I land here.", "{eta}, give or take. Enjoy the view." };
        static readonly string[] PilotNone = { "No ETA yet - enjoy the view.", "We get there when we get there.", "Sit back. I'll tell you when." };
        internal static readonly Dictionary<string, string[]> Topics = new Dictionary<string, string[]>
        {
            { "the new stew in the cafeteria", new[] { "Have you tried the new stew in the cafeteria?", "The green one? It moved.", "It's supposed to move. Extra protein.", "I'm sticking to snacks, thanks.", "More stew for me then!", "You're braver than any pilot." } },
            { "snacks", new[] { "Who ate the last snack bar?", "Define 'ate'.", "You hid it in the strut bin again?", "Snacks are mission critical. I'm protecting them.", "From me?", "Especially from you." } },
            { "Jeb", new[] { "Did you hear what Jeb did last week?", "The thing with the boosters?", "No, the OTHER thing.", "Gene's still finding pieces of the hangar.", "Legend.", "Don't tell him that, he'll do it again." } },
            { "the view", new[] { "You ever just look out the window?", "All the time. Clouds look like snacks.", "That one looks like Jeb.", "That one IS on fire.", "...Probably just the sunset.", "Probably." } },
            { "science", new[] { "I logged three new readings today!", "What do they mean?", "No idea. That's the fun part.", "Will it get us more funding?", "If it explodes, definitely.", "Let's not." } },
            { "sleeping in the bunks", new[] { "Did you sleep at all last night?", "Somebody snores like a mainsail.", "That was the coolant pump.", "Then the coolant pump snores.", "I'll fix it after the trip.", "Bring earplugs next time." } },
        };

        internal bool Enabled = true;
        internal Func<string, string> Describe = n => "";        // name -> personality description (KerbalPersonality)
        internal Func<string, bool> LovesDadJokes = n => false;
        readonly Random rng;
        readonly Dictionary<string, double> last = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string vid; double? flySince, due, talkDue; int n, talks; bool done, talkDone; double lastEmerg = -1e9, lastJoke = -1e9;
        internal bool TalkRunning;   // set by the runner while a conversation is being said
        internal CrewChatter(Random rng = null) { this.rng = rng ?? new Random(); }

        internal void Spoke(string name, double now) { last[name] = now; }
        internal bool RecentlyEmergency(double now) { return now - lastEmerg < CalmAfterEmergencyS; }

        static List<KeyValuePair<string, string>> Others(IList<KeyValuePair<string, string>> crew, string pilot)
        {
            string pf = IntercomTalk.First(pilot).ToLowerInvariant();
            var o = new List<KeyValuePair<string, string>>();
            if (crew != null) foreach (var c in crew) if (IntercomTalk.First(c.Key).ToLowerInvariant() != pf) o.Add(new KeyValuePair<string, string>(c.Key, IntercomTalk.NormTrait(c.Value)));
            return o;
        }
        string Who(string name) { return CrewPrompts.Who(name, Describe(name)); }

        /// <summary>crew.py slots(): who reacts to one emergency (pilot excluded, rate-limited, 2 max).</summary>
        internal List<CrewLine> Emergency(string kind, string part, IList<KeyValuePair<string, string>> crew, string pilot, double now, double g = 1)
        {
            var outp = new List<CrewLine>();
            lastEmerg = now; due = null; talkDue = null;
            if (!Enabled) return outp;
            bool mech = Mech.Contains(kind ?? "");
            string p = string.IsNullOrEmpty(part) ? "part" : part, pthe = string.IsNullOrEmpty(part) ? "a part" : "the " + part;
            string facts = CrewPrompts.FactsFor(kind, part, g);
            double l;
            // Luke: every problem goes to the pilot first, then every other named kerbal aboard, each in their own voice.
            if (!string.IsNullOrEmpty(pilot) && (!last.TryGetValue(pilot, out l) || now - l >= MemberGapS))
            {
                string[] pool = PilotLines.ContainsKey(kind ?? "") ? PilotLines[kind] : PilotLines["any"];
                string text = pool[rng.Next(pool.Length)].Replace("{part}", p).Replace("{part_the}", pthe);
                outp.Add(new CrewLine { Name = pilot, Trait = "pilot", Canned = text, Prompt = CrewPrompts.Emergency(Who(pilot), facts) });
                last[pilot] = now;
            }
            var order = mech ? new Dictionary<string, int> { { "engineer", 0 }, { "scientist", 1 }, { "tourist", 2 } } : new Dictionary<string, int> { { "tourist", 0 }, { "scientist", 1 }, { "engineer", 2 } };
            var keyed = new List<Tuple<int, double, KeyValuePair<string, string>>>();
            foreach (var o in Others(crew, pilot)) { int r; keyed.Add(Tuple.Create(order.TryGetValue(o.Value, out r) ? r : 3, rng.NextDouble(), o)); }
            keyed.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
            foreach (var k in keyed)
            {
                if (outp.Count >= MaxPerEvent) break;
                string name = k.Item3.Key, tr = k.Item3.Value;
                if (last.TryGetValue(name, out l) && now - l < MemberGapS) continue;
                string key = Lines.ContainsKey(tr) ? tr : "tourist";
                string[] pool = mech && Lines.ContainsKey(key + "_mech") && kind != "reverse" ? Lines[key + "_mech"] : Lines[key];
                string text = pool[rng.Next(pool.Length)].Replace("{part}", p).Replace("{part_the}", pthe).Replace("{g}", g.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                outp.Add(new CrewLine { Name = name, Trait = tr, Canned = char.ToUpperInvariant(text[0]) + text.Substring(1), Prompt = CrewPrompts.Emergency(Who(name), facts),
                    Delay = outp.Count == 0 ? 0 : 2 + rng.NextDouble() * 3 });
                last[name] = now;
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
        internal double? TalkGap(double eta, int count)
        {
            if (!double.IsNaN(eta) && eta < ShortTripS)
            {
                if (count >= 1 || rng.NextDouble() > TalkShortP) return null;
                double lo0 = FirstMinS * .5; return lo0 + rng.NextDouble() * (Math.Max(lo0 + 1, Math.Min(eta * .5, FirstMinS * 2)) - lo0);
            }
            return count == 0 ? TalkFirstMinS + rng.NextDouble() * (TalkFirstMaxS - TalkFirstMinS) : TalkGapMinS + rng.NextDouble() * (TalkGapMaxS - TalkGapMinS);
        }

        internal static string FmtEta(double s) { int i = (int)Math.Round(s); return i >= 120 ? i / 60 + " min" : i >= 60 ? i / 60 + " min " + i % 60 + " s" : i + " s"; }

        static readonly Dictionary<string, string[]> PilotLines = new Dictionary<string, string[]>
        {
            { "flameout", new[] { "Engine out! Hang on, relighting {part_the}...", "Lost {part_the}! Working the restart.", "Flameout! Give me a second, Captain." } },
            { "alarm", new[] { "Alarm - {part}. I'm on it, Captain.", "Got a warning light: {part}. Watching it closely." } },
            { "landing", new[] { "Not happy with that approach - {part}. Going around.", "Missed it - {part}. Bringing her around again." } },
            { "tamper", new[] { "Hey - who flipped {part_the}? Hang on, setting it back...", "Huh, {part_the} just changed on its own. Fixing it, give me a sec." } },
            { "parts", new[] { "We just lost {part_the}! Holding her steady.", "Something tore off - {part_the}. Still flying." } },
            { "overheat", new[] { "{part} is running hot, easing off.", "Temperature warning on {part_the}!" } },
            { "any", new[] { "Whoa - that wasn't good. On it, Captain.", "Problem with {part_the}! I've got it." } },
        };
        internal const double SoloMinS = 120, SoloMaxS = 360;
        internal string LastSkip = "";
        double? soloDue;
        static readonly string[] SoloCanned = { "Smooth air up here, Captain.", "She's flying nicely today.", "Nice view of the coast from here.", "All gauges green. Enjoying this one.", "Wind's calm. Couldn't ask for better." };
        /// <summary>Solo pilot idle remark every 4-8 min of calm flight (no passengers to chat with).</summary>
        internal CrewLine SoloTick(double now, bool flying, IList<KeyValuePair<string, string>> crew, string pilot, string facts)
        {
            if (!Enabled || !flying || string.IsNullOrEmpty(pilot)) { soloDue = null; LastSkip = !Enabled ? "chatter off" : !flying ? "not flying" : "no pilot"; return null; }
            if (now - lastEmerg < CalmAfterEmergencyS) { LastSkip = "calm after emergency"; return null; }
            var all = new List<KeyValuePair<string, string>>(crew); if (all.Count == 0) all.Add(new KeyValuePair<string, string>(pilot, "pilot"));
            if (soloDue == null) { soloDue = now + SoloMinS + rng.NextDouble() * (SoloMaxS - SoloMinS); return null; }
            if (now < soloDue.Value) return null;
            soloDue = now + SoloMinS + rng.NextDouble() * (SoloMaxS - SoloMinS);
            var who = all[rng.Next(all.Count)]; LastSkip = "";   // randomized ambient line: any named kerbal aboard
            if (who.Key != pilot) return new CrewLine { Name = who.Key, Trait = who.Value, Canned = SoloCanned[rng.Next(SoloCanned.Length)], AllowedNumbers = CrewLine.Numbers(facts),
                Prompt = "Say one short, in-character remark to the crew about the flight. Facts: " + facts + ". Use no other numbers." };
            return new CrewLine { Name = pilot, Trait = "pilot", Canned = SoloCanned[rng.Next(SoloCanned.Length)], AllowedNumbers = CrewLine.Numbers(facts),
                Prompt = (all.Count > 1 ? "" : "You are flying alone. ") + " Say one short, in-character remark to the Captain about the flight. Facts: " + facts + ". Use no other numbers." };
        }

        bool Eligible(double now, string vesselId, bool flying, bool tripActive, bool emergency)
        {
            if (vesselId != vid || !flying) { vid = vesselId; flySince = null; due = null; talkDue = null; n = 0; talks = 0; done = false; talkDone = false; if (!flying) return false; }
            if (flySince == null) flySince = now;
            if (emergency) { lastEmerg = now; due = null; talkDue = null; return false; }
            if (!Enabled || now - lastEmerg < CalmAfterEmergencyS) return false;
            return tripActive || now - flySince.Value >= TripMinFlightS;
        }

        /// <summary>crew.py Trip.tick: 'are we there yet' (or a dad joke), maybe a pilot answer. eta/dist NaN = unknown.</summary>
        internal List<CrewLine> TripTick(double now, string vesselId, bool flying, IList<KeyValuePair<string, string>> crew, string pilot, bool tripActive, bool emergency, double eta, string dest = null, double distKm = double.NaN)
        {
            var outp = new List<CrewLine>();
            if (!Eligible(now, vesselId, flying, tripActive, emergency) || done || TalkRunning) return outp;
            var cands = new List<KeyValuePair<string, string>>(); double l;
            foreach (var o in Others(crew, pilot))
                if ((o.Value == "scientist" || o.Value == "engineer" || o.Value == "tourist") && !(last.TryGetValue(o.Key, out l) && now - l < MemberGapS)) cands.Add(o);
            if (cands.Count == 0) return outp;
            if (due == null) { var g0 = NextGap(eta, n); if (g0 == null) done = true; else due = now + g0.Value; return outp; }
            if (now < due.Value) return outp;
            var who = cands[rng.Next(cands.Count)]; last[who.Key] = now;
            string facts = CrewPrompts.TripFacts(dest, eta, distKm);
            if (now - lastJoke >= DadJokeGapS && rng.NextDouble() < DadJokeP && LovesDadJokes(who.Key))
            {
                lastJoke = now;
                outp.Add(new CrewLine { Name = who.Key, Trait = who.Value, Canned = DadJokes[rng.Next(DadJokes.Length)], Prompt = CrewPrompts.Joke(Who(who.Key), facts) });
                var nj = NextGap(eta, Math.Max(n, 1)); due = nj == null ? (double?)null : now + nj.Value;
                return outp;
            }
            n++;
            string[] pool = n >= 3 && rng.NextDouble() < .6 ? TripLines["impatient"] : TripLines[who.Value];
            outp.Add(new CrewLine { Name = who.Key, Trait = who.Value, Canned = pool[rng.Next(pool.Length)].Replace("{n}", n.ToString()), Prompt = CrewPrompts.Trip(Who(who.Key), facts, n - 1) });
            var nxt = NextGap(eta, n); due = nxt == null ? (double?)null : now + nxt.Value; done = nxt == null;
            if (!string.IsNullOrEmpty(pilot) && rng.NextDouble() < PilotReplyP)
            {
                string canned = double.IsNaN(eta) ? PilotNone[rng.Next(PilotNone.Length)] : PilotEta[rng.Next(PilotEta.Length)].Replace("{eta}", FmtEta(eta));
                outp.Add(new CrewLine { Name = pilot, Trait = "pilot", Canned = canned, Prompt = CrewPrompts.PilotAnswer(Who(pilot), facts), AllowedNumbers = CrewLine.Numbers(facts + " " + canned) });
            }
            return outp;
        }

        /// <summary>crew.py SmallTalk.tick: a conversation to start (null most ticks). Two non-pilot crew, else pilot + one.</summary>
        internal CrewTalk TalkTick(double now, string vesselId, bool flying, IList<KeyValuePair<string, string>> crew, string pilot, bool tripActive, bool emergency, double eta)
        {
            if (!Eligible(now, vesselId, flying, tripActive, emergency) || talkDone || TalkRunning) return null;
            var others = Others(crew, pilot);
            KeyValuePair<string, string>[] pair = null;
            if (others.Count >= 2) { int a = rng.Next(others.Count), b = rng.Next(others.Count - 1); if (b >= a) b++; pair = new[] { others[a], others[b] }; }
            else if (others.Count == 1 && !string.IsNullOrEmpty(pilot)) { var pk = new KeyValuePair<string, string>(pilot, "pilot"); pair = rng.NextDouble() < .5 ? new[] { pk, others[0] } : new[] { others[0], pk }; }
            if (pair == null) return null;
            if (talkDue == null) { var g0 = TalkGap(eta, talks); if (g0 == null) talkDone = true; else talkDue = now + g0.Value; return null; }
            if (now < talkDue.Value) return null;
            talks++;
            var nxt = TalkGap(eta, talks); talkDue = nxt == null ? (double?)null : now + nxt.Value; talkDone = nxt == null;
            var keys = new List<string>(Topics.Keys); string topic = keys[rng.Next(keys.Count)];
            return new CrewTalk { Pair = pair, Topic = topic, Script = Topics[topic], Lines = TalkLinesMin + rng.Next(TalkLinesMax - TalkLinesMin + 1) };
        }

        /// <summary>Line i of a conversation (model prompt from the transcript so far, canned script fallback).</summary>
        internal CrewLine TalkLine(CrewTalk talk, int i, IList<KeyValuePair<string, string>> transcript)
        {
            var sp = talk.Pair[i % 2]; string other = IntercomTalk.First(talk.Pair[(i + 1) % 2].Key);
            return new CrewLine { Name = sp.Key, Trait = sp.Value, Canned = talk.Script[i % talk.Script.Length], Prompt = CrewPrompts.Talk(Who(sp.Key), other, talk.Topic, transcript) };
        }

        /// <summary>'/crew chatter on|off' (also '/crew on|off'; '/crew' = status).</summary>
        internal string Command(string arg)
        {
            string a = string.Join(" ", (arg ?? "").ToLowerInvariant().Replace("chatter", "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (a == "on" || a == "off") { Enabled = a == "on"; if (!Enabled) TalkRunning = false; return "Crew intercom chatter " + a + "."; }
            return "Crew intercom chatter is " + (Enabled ? "on" : "off") + ". Use /crew chatter on|off.";
        }
    }
}
