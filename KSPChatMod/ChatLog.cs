using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace KSPChatBridge
{
    /// <summary>Chat/tool trace: PluginData/logs/chat_YYYYMMDD.log (daily file, 4 MB cap -> .1 roll), secrets redacted. Thread-safe.</summary>
    internal static class ChatLog
    {
        internal static string Dir;                 // set by the host (PluginData/logs); null = off
        internal const long MaxBytes = 4L * 1024 * 1024;
        internal static Func<DateTime> Now = () => DateTime.Now;
        static readonly object Gate = new object();
        static readonly Regex Secret = new Regex(@"(sk-[A-Za-z0-9_\-]{8,}|gsk_[A-Za-z0-9]{8,}|xai-[A-Za-z0-9]{8,}|AIza[0-9A-Za-z_\-]{20,}|hf_[A-Za-z0-9]{8,}|(?i:bearer)\s+[A-Za-z0-9._\-]{8,}|(?i:(api[_-]?key|token|authorization)""?\s*[:=]\s*""?)[^\s"",}]{6,})");

        internal static string Redact(string s) { return string.IsNullOrEmpty(s) ? s ?? "" : Secret.Replace(s, "[redacted]"); }
        internal static string PathFor(DateTime t) { return Dir == null ? null : System.IO.Path.Combine(Dir, "chat_" + t.ToString("yyyyMMdd") + ".log"); }

        internal static void Write(string kind, string text)
        {
            if (Dir == null) return;
            try
            {
                var t = Now();
                string line = t.ToString("HH:mm:ss.fff") + " [" + kind + "] " + Redact((text ?? "").Replace("\r", "").Replace("\n", "\\n")) + "\n";
                if (line.Length > 8000) line = line.Substring(0, 8000) + "...\n";
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    string p = PathFor(t);
                    if (File.Exists(p) && new FileInfo(p).Length + line.Length > MaxBytes)
                    { string old = p + ".1"; if (File.Exists(old)) File.Delete(old); File.Move(p, old); }
                    File.AppendAllText(p, line, Encoding.UTF8);
                }
            }
            catch (Exception) { }
        }
    }

    internal static class ChatTelemetry
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        /// <summary>One shorthand line: "T alt=1020m agl=980 spd=182 hdg=271 bank=-12 pit=6 thr=65% vs=+3 brk=0 gear=up ap=circle".</summary>
        internal static string Line(double alt, double agl, double spd, double hdg, double bank, double pitch, double thr, double vs, bool brakes, bool gearDown, string ap, string extra = "")
        {
            return "alt=" + alt.ToString("0", Inv) + "m agl=" + agl.ToString("0", Inv) + " spd=" + spd.ToString("0", Inv) + " hdg=" + hdg.ToString("000", Inv)
                + " bank=" + bank.ToString("0", Inv) + " pit=" + pitch.ToString("0", Inv) + " thr=" + (thr * 100).ToString("0", Inv) + "% vs=" + (vs >= 0 ? "+" : "") + vs.ToString("0", Inv)
                + " brk=" + (brakes ? "1" : "0") + " gear=" + (gearDown ? "down" : "up") + " ap=" + (ap ?? "") + (string.IsNullOrEmpty(extra) ? "" : " " + extra);
        }
    }
}

namespace KSPChatBridge
{
    /// <summary>Recent flight events injected into the model's context (sabotage reverts etc.).</summary>
    internal static class PilotEvents
    {
        static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>> Items = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<double, string>>();
        internal const double KeepS = 300;
        internal static double Now { get { return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency; } }
        internal static void Add(string text, double now) { lock (Items) { Items.Add(new System.Collections.Generic.KeyValuePair<double, string>(now, text)); if (Items.Count > 8) Items.RemoveAt(0); } }
        internal static string Context(double now)
        {
            lock (Items)
            {
                Items.RemoveAll(i => now - i.Key > KeepS || now < i.Key);
                if (Items.Count == 0) return "";
                var parts = new System.Collections.Generic.List<string>(); foreach (var i in Items) parts.Add(i.Value);
                return " Recent events: " + string.Join("; ", parts.ToArray()) + ".";
            }
        }
        internal static void Clear() { lock (Items) Items.Clear(); }
    }
}

namespace KSPChatBridge
{
    /// <summary>Telemetry position + target runway part ("lat=-0.0486 lon=-74.7244 rwy=12.3km brg=270").</summary>
    internal static class TelemetryPos
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        internal static string Part(double lat, double lon, double rwyKm, double rwyBrg)
        {
            string s = "lat=" + lat.ToString("0.0000", Inv) + " lon=" + lon.ToString("0.0000", Inv);
            if (!double.IsNaN(rwyKm)) s += " rwy=" + rwyKm.ToString("0.0", Inv) + "km brg=" + ((rwyBrg + 360) % 360).ToString("000", Inv);
            return s;
        }
    }
    /// <summary>System alert de-spam: same alarm kind at most every 30 s unless it escalates (caution -> warning).</summary>
    internal sealed class AlertGate
    {
        internal const double GapS = 30;
        readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.KeyValuePair<double, int>> last = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.KeyValuePair<double, int>>();
        static int Rank(string level) { level = (level ?? "").ToLowerInvariant(); return level.StartsWith("warn") || level.StartsWith("crit") || level.StartsWith("alarm") ? 2 : 1; }
        internal bool Allow(string level, string alarm, double now)
        {
            string kind = (alarm ?? "").Split(':')[0].Trim().ToLowerInvariant(); int r = Rank(level);
            System.Collections.Generic.KeyValuePair<double, int> prev;
            if (last.TryGetValue(kind, out prev) && now - prev.Key < GapS && now >= prev.Key && r <= prev.Value) return false;
            last[kind] = new System.Collections.Generic.KeyValuePair<double, int>(now, r); return true;
        }
    }
    /// <summary>Multi-line crew scene: one model call writes "Name: line" lines; split, map to crew, post 2-3 s apart.</summary>
    internal static class CrewScene
    {
        internal static string Prompt(string facts, string pilot, System.Collections.Generic.IList<string> others)
        {
            string cast = pilot + (others.Count > 0 ? ", " + string.Join(", ", System.Linq.Enumerable.ToArray(others)) : "");
            return "Emergency aboard: " + facts + ". Write a short intercom scene of 4-6 lines, each formatted exactly 'Name: line', using only these kerbals: " + cast + ". "
                + pilot + " (the pilot) announces the problem, then reports a partial fix, an update, and finally that it is fixed"
                + (others.Count > 0 ? "; the others react in their own personalities (worried, panicking, joking) in between" : "")
                + ". One short sentence per line. No numbers that are not in the facts. No narration.";
        }
        /// <summary>Parse "Name: text" lines; names matched to crew by full or first name. Unknown speakers dropped.</summary>
        internal static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> Parse(string text, System.Collections.Generic.IList<string> crew, int max = 8)
        {
            var outp = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return outp;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(raw.Trim(), @"^[\*\-\s]*\**([A-Za-z][A-Za-z' ]{0,30}?)\**\s*(?:\([^)]*\))?\s*:\s*(.+)$");
                if (!m.Success) continue;
                string who = m.Groups[1].Value.Trim().ToLowerInvariant(), line = m.Groups[2].Value.Trim().Trim('"');
                if (line.Length == 0) continue;
                string hit = null;
                foreach (string c in crew) { string cl = c.ToLowerInvariant(); if (cl == who || cl.Split(' ')[0] == who.Split(' ')[0]) { hit = c; break; } }
                if (hit == null) continue;
                if (line.Length > 200) line = line.Substring(0, 200);
                outp.Add(new System.Collections.Generic.KeyValuePair<string, string>(hit, line));
                if (outp.Count >= max) break;
            }
            return outp;
        }
        internal static double Gap(System.Random rng) { return 2 + rng.NextDouble(); }
    }
}
