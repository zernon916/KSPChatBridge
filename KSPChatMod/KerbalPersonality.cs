using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KSPChatBridge
{
    // Port of kspchat/memory.py (playstyle notes) — PluginData persistence. Pure logic + injectable paths so the
    // offline suite can test it; Unity-free.
    internal sealed class PlaystyleNotes
    {
        internal const int MaxNotes = 100;
        internal static Func<string> PathProvider = DefaultPath;
        static string DefaultPath() { return Path.Combine(BridgeLauncher.DataDirectory, "playstyle_notes.md"); }

        internal static List<string> Load()
        {
            var notes = new List<string>();
            try
            {
                string path = PathProvider();
                if (!File.Exists(path)) return notes;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("- ") && line.Substring(2).Trim().Length > 0) notes.Add(line.Substring(2).Trim());
                }
            }
            catch (Exception) { }
            return notes;
        }

        static void Save(List<string> notes)
        {
            var sb = new StringBuilder();
            sb.Append("# Luke's KSP playstyle notes\n# One '- ' bullet per preference. Injected into the system prompt. Max ")
              .Append(MaxNotes).Append(" notes.\n");
            foreach (string n in notes) sb.Append("- ").Append(n).Append('\n');
            File.WriteAllText(PathProvider(), sb.ToString(), Encoding.UTF8);
        }

        /// <summary>Remember a preference; near-duplicates (&gt;0.85 similarity) refresh instead of piling up.</summary>
        internal static string Remember(string note)
        {
            note = Collapse(note);
            if (note.Length > 300) note = note.Substring(0, 300);
            if (note.Length < 3) return "Note too short, nothing saved.";
            var notes = Load();
            string low = note.ToLowerInvariant();
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].ToLowerInvariant() == low || Similarity(notes[i].ToLowerInvariant(), low) > 0.85)
                {
                    notes[i] = note;
                    Save(notes);
                    return "Updated existing note: " + note;
                }
            }
            notes.Add(note);
            int dropped = notes.Count > MaxNotes ? notes.Count - MaxNotes : 0;
            if (dropped > 0) notes.RemoveRange(0, dropped);
            Save(notes);
            return "Remembered: " + note + (dropped > 0 ? " (dropped " + dropped + " oldest)" : "");
        }

        internal static string NotesBlock()
        {
            var notes = Load();
            if (notes.Count == 0) return "";
            var sb = new StringBuilder("Luke's known playstyle preferences (respect these):\n");
            for (int i = 0; i < notes.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append("- ").Append(notes[i]);
            }
            return sb.ToString();
        }

        internal static string Collapse(string s)
        {
            var parts = (s ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        // difflib.SequenceMatcher-style ratio stand-in: normalized Levenshtein similarity.
        internal static double Similarity(string a, string b)
        {
            if (a == b) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;
            int dist = Levenshtein(a, b);
            return 1.0 - (double)dist / Math.Max(a.Length, b.Length);
        }

        static int Levenshtein(string a, string b)
        {
            int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                int[] t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }

    // Port of kspchat/personality.py — deterministic per-name temperament, two likes, one dislike; persisted once
    // per name to kerbal_personalities.json (shared with the bridge). RNG here is a seeded xorshift (Python uses its
    // MT seeded from the same crc32): both deterministic per name; the shared JSON keeps them consistent.
    internal static class KerbalPersonality
    {
        internal static readonly string[] Likes = {
            "loves snacks", "loves explosions", "collects moon rocks", "loves going fast", "loves the view",
            "hums while working", "adores Jeb", "loves science", "tells bad puns", "loves shiny buttons",
            "loves boosters", "collects struts", "loves stargazing", "loves duct tape", "waves at every cloud",
            "names every rock", "loves a good sandwich", "keeps a lucky sock", "loves dad jokes" };
        internal static readonly string[] Dislikes = {
            "hates flying", "is afraid of heights", "gets airsick", "hates rockets", "is terrified of space",
            "hates loud noises", "hates turbulence", "hates paperwork", "hates being upside down", "hates landings",
            "hates staging", "hates G-forces", "hates waiting", "hates cold coffee" };
        internal static readonly string[] Calm = {
            "chatty", "cheerful", "optimistic", "upbeat", "curious", "dry-witted", "grumpy", "fussy" };
        internal const string Unflappable = "unflappable", Seasoned = "seasoned";
        internal static readonly string[] Nervous = { "nervous", "panicky", "jumpy" };
        internal static readonly string[] Goofy = { "goofy", "scatterbrained" };

        internal static Func<string> PathProvider = DefaultPath;
        static string DefaultPath() { return Path.Combine(BridgeLauncher.DataDirectory, "kerbal_personalities.json"); }
        // stats: courage < 0.3 nervous; stupidity > 0.7 goofy; badass -> unflappable; veteran -> seasoned.
        internal static Dictionary<string, object> Generate(string name, Dictionary<string, object> stats)
        {
            var rng = new XorShift(Crc32(name));
            stats = stats ?? new Dictionary<string, object>();
            var temper = new List<string>();
            object num;
            if (Truthy(stats, "badass")) temper.Add(Unflappable);
            else if (TryNum(stats, "courage", out num) && (double)num < 0.3) temper.Add(Nervous[rng.Next(Nervous.Length)]);
            if (TryNum(stats, "stupidity", out num) && (double)num > 0.7) temper.Add(Goofy[rng.Next(Goofy.Length)]);
            if (Truthy(stats, "veteran")) temper.Add(Seasoned);
            if (temper.Count == 0) temper.Add(Calm[rng.Next(Calm.Length)]);
            if (temper.Count > 2) temper.RemoveRange(2, temper.Count - 2);
            int l0 = rng.Next(Likes.Length), l1 = rng.Next(Likes.Length);
            while (l1 == l0 && Likes.Length > 1) l1 = rng.Next(Likes.Length);
            var likes = new List<string> { Likes[l0], Likes[l1] };
            var dislikes = new List<string> { Dislikes[rng.Next(Dislikes.Length)] };
            return new Dictionary<string, object>
            {
                { "temper", temper }, { "likes", likes }, { "dislikes", dislikes }, { "stats", stats },
            };
        }

        /// <summary>The kerbal's personality, generated and saved on first sight.</summary>
        internal static Dictionary<string, object> Ensure(string name, Func<Dictionary<string, object>> statsFn)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var data = LoadAll();
            object raw;
            if (!data.TryGetValue(name, out raw) || !(raw is Dictionary<string, object>))
            {
                var mine = Generate(name, statsFn != null ? statsFn() : null);
                data[name] = mine;
                SaveAll(data);
                return mine;
            }
            return (Dictionary<string, object>)raw;
        }
        /// <summary>'a nervous scientist who loves rocks and snacks and hates heights'.</summary>
        internal static string Describe(string name, string trait)
        {
            var p = Ensure(name, null) ?? new Dictionary<string, object>();
            var temper = Strings(p, "temper");
            var likes = Strings(p, "likes");
            var dislikes = Strings(p, "dislikes");
            string t = temper.Count > 0 ? string.Join(" ", temper.ToArray()) + " " : "";
            var sb = new StringBuilder("a " + t + trait);
            if (likes.Count > 0)
            {
                sb.Append(" who ");
                for (int i = 0; i < likes.Count; i++)
                {
                    if (i > 0) sb.Append(i == likes.Count - 1 ? " and " : ", ");
                    sb.Append(likes[i]);
                }
            }
            if (dislikes.Count > 0)
                sb.Append(likes.Count > 0 ? " and" : " who").Append(' ').Append(string.Join(" and ", dislikes.ToArray()));
            return sb.ToString();
        }

        internal static bool LikesPhrase(string name, string phrase)
        {
            return Strings(Ensure(name, null), "likes").Contains(phrase);
        }

        internal static Dictionary<string, object> LoadAll()
        {
            var data = new Dictionary<string, object>();
            try
            {
                string path = PathProvider();
                if (!File.Exists(path)) return data;
                var parsed = MiniJson.Deserialize(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                if (parsed != null) foreach (var kv in parsed) data[kv.Key] = kv.Value;
            }
            catch (Exception) { }
            return data;
        }

        static void SaveAll(Dictionary<string, object> data)
        {
            try { File.WriteAllText(PathProvider(), MiniJson.Serialize(data), Encoding.UTF8); }
            catch (Exception) { }
        }

        internal static List<string> Strings(Dictionary<string, object> node, string key)
        {
            var outp = new List<string>();
            if (node == null || !node.ContainsKey(key)) return outp;
            var list = node[key] as System.Collections.IList;
            if (list != null) foreach (object o in list) outp.Add(o == null ? "" : o.ToString());
            return outp;
        }
        static bool Truthy(Dictionary<string, object> stats, string key)
        {
            object v;
            if (!stats.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            double d;
            return double.TryParse(v.ToString(), out d) ? d != 0 : v.ToString().Trim().Length > 0;
        }

        static bool TryNum(Dictionary<string, object> stats, string key, out object num)
        {
            num = null;
            object v;
            if (!stats.TryGetValue(key, out v) || v == null) return false;
            double d;
            if (!double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out d)) return false;
            num = d;
            return true;
        }

        internal static uint Crc32(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
            }
            return ~crc;
        }

        internal sealed class XorShift
        {
            uint state;
            internal XorShift(uint seed) { state = seed == 0 ? 0x9E3779B9 : seed; }
            internal int Next(int n)
            {
                if (n <= 1) return 0;
                uint x = state;
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                state = x;
                return (int)(x % (uint)n);
            }
        }
    }
}