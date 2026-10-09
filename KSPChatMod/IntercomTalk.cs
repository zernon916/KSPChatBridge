using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace KSPChatBridge
{
    // Port of kspchat/talk.py essentials: intercom addressing (pure), reply cleanup, canned lines when the AI is
    // busy/unusable, per-kerbal short memory, and a bounded reply deadline. Unity-free; the LLM call is injected.
    internal static class IntercomTalk
    {
        internal const int MemN = 4;
        internal const int MaxWords = 45, MaxChars = 260;
        internal const int LlmTimeoutMs = 8000;

        static readonly Regex At = new Regex(@"^\s*@([A-Za-z][\w'-]*)[\s,:;!.-]*(.*)$", RegexOptions.Singleline);
        static readonly Regex Hey = new Regex(@"^\s*(?:hey|hi|hello|yo|oi|ok|okay|so|and|well)[\s,]+([A-Za-z][\w'-]*)\b[\s,:;!?.-]*(.*)$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        static readonly Regex Lead = new Regex(@"^\s*([A-Za-z][\w'-]*)\s*[:,]\s*(.+)$", RegexOptions.Singleline);
        static readonly Regex Tail = new Regex(@"^(.+?),\s*([A-Za-z][\w'-]*)\s*([?!.]*)\s*$", RegexOptions.Singleline);
        static readonly Regex Order = new Regex(
            @"^\s*(?:please\s+|can you\s+|could you\s+|would you\s+|go\s+(?:and\s+)?)?(?:land|take\s*off|launch|abort|stage|" +
            @"throttle|set|hold|climb|descend|hover|turn|fly|head|deploy|retract|raise|lower|gear|brakes?|engage|disengage|" +
            @"autopilot|sas|rcs|eject|dock|undock|burn|circularize|circularise|warp|stop|kill|cut|start|ignite|open|close|" +
            @"arm|release|extend|activate|toggle|face|sidestep|back\s+up|taxi|go\s+to|return|pitch|bank|roll|yaw|" +
            @"switch|lock|unlock|transfer|run|do|execute|calculate|plot|plan)\b", RegexOptions.IgnoreCase);
        static readonly Regex Action = new Regex(
            @"\bI(?:'ve|'ll| have| will| am|'m| just)?\s+(?:just\s+|already\s+)?(?:fix(?:ed)?|repair(?:ed)?|" +
            @"deploy(?:ed)?|engag(?:e|ed)|releas(?:e|ed)|activat(?:e|ed)|eject(?:ed)?|stag(?:e|ed)|land(?:ed)?|" +
            @"cut|turn(?:ed)?|switch(?:ed)?|pull(?:ed)?|press(?:ed)?|flip(?:ped)?|reset|set|start(?:ed)?|" +
            @"stop(?:ped)?|open(?:ed)?|clos(?:e|ed)|lower(?:ed)?|rais(?:e|ed)|fir(?:e|ed)|launch(?:ed)?|" +
            @"took|take|grab(?:bed)?|flew|fly(?:ing)?\s+(?:us|the))\b|\b(?:fixed|repaired|all clear|good as new|" +
            @"done[,!.]|on it[,!.])", RegexOptions.IgnoreCase);
        static readonly Regex Toolish = new Regex(@"[{}<>`]|(?<!\[)\[(?!INTERCOM|COMMS)|\b(?:tool|function call|json|as an ai|language model)\b",
            RegexOptions.IgnoreCase);
        static readonly Regex ThinkBlock = new Regex(@"(?s)<think.*?</think>");
        static readonly Regex CommsTag = new Regex(@"^\[(?:INTERCOM|COMMS)\]\s*");
        static readonly Regex NamePrefix = new Regex(@"^[\w .'-]{1,40}(?:\([\w .]{1,12}\))?:\s*");

        static readonly object Gate = new object();
        static readonly Dictionary<string, Queue<Tuple<string, string>>> Mem =
            new Dictionary<string, Queue<Tuple<string, string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>First name of a full kerbal name.</summary>
        internal static string First(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Trim().Split(' ')[0];
        }

        static KeyValuePair<string, string>? Match(string token, List<KeyValuePair<string, string>> crew)
        {
            string t = (token ?? "").Trim().TrimEnd('\'').ToLowerInvariant();
            foreach (var c in crew)
            {
                string f = First(c.Key).ToLowerInvariant();
                if (f.Length > 0 && (f == t || (t.Length >= 3 && f.StartsWith(t)))) return c;
            }
            return null;
        }

        /// <summary>Candidate addressings, most explicit first: (token, rest, explicit@).</summary>
        internal static List<Tuple<string, string, bool>> Address(string text)
        {
            var outp = new List<Tuple<string, string, bool>>();
            foreach (var rx in new[] { At, Hey, Lead })
            {
                var m = rx.Match(text ?? "");
                if (m.Success) outp.Add(Tuple.Create(m.Groups[1].Value, m.Groups[2].Value.Trim(), rx == At));
            }
            var tail = Tail.Match(text ?? "");
            if (tail.Success)
                outp.Add(Tuple.Create(tail.Groups[2].Value, (tail.Groups[1].Value + tail.Groups[3].Value).Trim(), false));
            return outp;
        }

        internal static bool IsOrder(string rest, Func<string, bool> parseDirect = null)
        {
            if (string.IsNullOrEmpty(rest)) return false;
            try { if (parseDirect != null && parseDirect(rest)) return true; }
            catch (Exception) { }
            return Order.IsMatch(rest);
        }
        /// <summary>
        /// crew: [(full name, trait)] aboard; pilot: pilot's first name; known: other kerbal full names.
        /// -&gt; null (normal chat) | ("kerbal", member, rest) | ("absent", firstName, rest) | ("order", member, rest).
        /// </summary>
        internal static Tuple<string, KeyValuePair<string, string>, string> Route(
            string text, List<KeyValuePair<string, string>> crew, string pilot, List<string> known,
            Func<string, bool> parseDirect = null)
        {
            text = text ?? "";
            if (text.Trim().Length == 0 || text.TrimStart().StartsWith("/")) return null;
            crew = crew ?? new List<KeyValuePair<string, string>>();
            var crewNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in crew) crewNames.Add(c.Key);
            var others = new List<KeyValuePair<string, string>>();
            foreach (string n in known ?? new List<string>())
                if (!crewNames.Contains(n)) others.Add(new KeyValuePair<string, string>(n, ""));
            foreach (var cand in Address(text))
            {
                var c = Match(cand.Item1, crew);
                if (c != null)
                {
                    var member = c.Value;
                    if (!string.IsNullOrEmpty(pilot) && First(member.Key).ToLowerInvariant() == pilot.ToLowerInvariant())
                        return null; // the pilot IS the chat's voice (with tools)
                    if (IsOrder(cand.Item2, parseDirect)) return Tuple.Create("order", member, cand.Item2);
                    return Tuple.Create("kerbal", member, cand.Item2.Length > 0 ? cand.Item2 : "Hey!");
                }
                var o = Match(cand.Item1, others);
                if (o != null || cand.Item3)
                    return Tuple.Create("absent",
                        new KeyValuePair<string, string>(First(o != null ? o.Value.Key : cand.Item1), ""), cand.Item2);
            }
            return null;
        }
        /// <summary>The kerbal's spoken reply, or null: think blocks/toolish text rejected, name prefix stripped,
        /// action-claiming sentences dropped, trimmed to MaxWords/MaxChars at a sentence end.</summary>
        internal static string Clean(string text)
        {
            string t = ThinkBlock.Replace(text ?? "", "").Trim();
            t = string.Join(" ", Array.FindAll(t.Split('\n'), x => x.Trim().Length > 0)).Trim();
            t = CommsTag.Replace(t, "");
            if (t.IndexOf(':') >= 0 && t.IndexOf(':') < 52) t = NamePrefix.Replace(t, "");
            t = t.Trim().Trim('"', '*', '_', ' ').Trim();
            if (t.Length == 0 || Toolish.IsMatch(t)) return null;
            var sents = Array.FindAll(Regex.Split(t, @"(?<=[.!?])\s+"), x => x.Trim().Length > 0);
            sents = Array.FindAll(sents, x => !Action.IsMatch(x));
            string outp = "";
            foreach (string raw in sents)
            {
                string s = raw.Trim();
                string nxt = (outp + " " + s).Trim();
                if (nxt.Split(' ').Length > MaxWords || nxt.Length > MaxChars) break;
                outp = nxt;
            }
            return outp.Length > 0 ? outp : null;
        }

        internal static readonly Dictionary<string, string[]> Canned = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "scientist", new[] { "Fascinating ride so far, Captain! I'm taking notes on everything.",
                                   "All good back here - just watching the instruments and trying not to touch any." } },
            { "engineer", new[] { "Everything's holding together back here, Captain. For now.",
                                  "Sounds good from back here. I'm keeping an ear on the creaks." } },
            { "tourist", new[] { "Is this normal? It's AMAZING. I think. Is it normal?",
                                 "Best trip ever! Mostly. Are there snacks?" } },
            { "pilot", new[] { "Copy that, Captain. Enjoying the ride from the other seat." } },
        };
        /// <summary>Canned line when the AI is busy/timed out — always answers.</summary>
        internal static string CannedLine(string name, string trait, string said)
        {
            string[] opts;
            if (!Canned.TryGetValue(NormTrait(trait), out opts)) opts = Canned["tourist"];
            return opts[(MemoryCount(name) + (said ?? "").Length) % opts.Length];
        }

        // crew.py norm_trait: map a KSP trait to pilot/scientist/engineer/tourist.
        internal static string NormTrait(string t)
        {
            string s = (t ?? "").Trim().ToLowerInvariant();
            foreach (string k in new[] { "pilot", "scientist", "engineer", "tourist" })
                if (s.Contains(k)) return k;
            return s.Length == 0 ? "tourist" : s;
        }

        internal static void Remember(string name, string said, string reply)
        {
            lock (Gate)
            {
                Queue<Tuple<string, string>> q;
                if (!Mem.TryGetValue(name ?? "", out q)) Mem[name ?? ""] = q = new Queue<Tuple<string, string>>();
                q.Enqueue(Tuple.Create(said ?? "", reply ?? ""));
                while (q.Count > MemN) q.Dequeue();
            }
        }

        internal static List<Tuple<string, string>> Memory(string name)
        {
            lock (Gate)
            {
                Queue<Tuple<string, string>> q;
                if (!Mem.TryGetValue(name ?? "", out q)) return new List<Tuple<string, string>>();
                return new List<Tuple<string, string>>(q);
            }
        }

        internal static int MemoryCount(string name)
        {
            lock (Gate)
            {
                Queue<Tuple<string, string>> q;
                return Mem.TryGetValue(name ?? "", out q) ? q.Count : 0;
            }
        }

        internal static void Reset()
        {
            lock (Gate) Mem.Clear();
        }

        /// <summary>
        /// The intercom line '[INTERCOM] Bob (Sci): ...' — gen(prompt) with a hard deadline, canned on
        /// timeout/bad output (crew busy never blocks chat). Remembered per kerbal.
        /// </summary>
        internal static string Reply(KeyValuePair<string, string> member, string said, string facts,
            Func<string, string> gen, string pilot = null, int timeoutMs = LlmTimeoutMs)
        {
            string name = member.Key, trait = member.Value;
            string line = null;
            if (gen != null)
            {
                string boxText = null;
                Exception boxErr = null;
                var th = new Thread(() =>
                {
                    try { boxText = gen(Prompt(name, trait, said, facts, Memory(name))); }
                    catch (Exception ex) { boxErr = ex; }
                });
                th.IsBackground = true;
                th.Start();
                bool finished = th.Join(timeoutMs);
                if (finished) line = Clean(boxText);
            }
            if (line == null) line = CannedLine(name, trait, said);
            string outp = Fmt(name, trait, line);
            Remember(name, said, outp.Contains(": ") ? outp.Substring(outp.IndexOf(": ") + 2) : outp);
            return outp;
        }

        // crew.py fmt: '[INTERCOM] Bob (Sci): ...' — engineers on [COMMS].
        internal static string Fmt(string name, string trait, string text)
        {
            string first = First(name);
            if (first.Length == 0) first = "Crew";
            string tr = NormTrait(trait);
            string tag = tr == "engineer" ? "[COMMS]" : "[INTERCOM]";
            string abbr = tr == "pilot" ? "Pilot" : tr == "scientist" ? "Sci" : tr == "engineer" ? "Eng" : "Tourist";
            return tag + " " + first + " (" + abbr + "): " + text;
        }

        internal static string Prompt(string name, string trait, string said, string facts,
            List<Tuple<string, string>> mem)
        {
            var lines = new List<string>
            {
                "You are " + KerbalPersonality.Describe(name, TraitWord(trait)) + ", a crew member (not the pilot) aboard " +
                "a Kerbal spacecraft. The captain, Luke, talks to you over the intercom.",
                "Real flight facts right now: " + (string.IsNullOrEmpty(facts) ? "nothing special" : facts) +
                ". Only mention facts from this list; never invent numbers, places or events.",
                "You can't fly, press buttons, repair or change anything - you can only talk. Reply in character, " +
                "one or two short sentences, no stage directions.",
            };
            if (mem != null && mem.Count > 0)
            {
                var pairs = new List<string>();
                foreach (var m in mem) pairs.Add("Luke: " + m.Item1 + " / You: " + m.Item2);
                lines.Add("Your recent chat with Luke: " + string.Join(" | ", pairs.ToArray()));
            }
            lines.Add("Luke says: " + (said ?? ""));
            return string.Join("\n", lines.ToArray());
        }

        internal static string TraitWord(string trait)
        {
            string tr = NormTrait(trait);
            return tr == "pilot" ? "pilot" : tr == "scientist" ? "scientist" : tr == "engineer" ? "engineer" : "tourist";
        }
    }
}