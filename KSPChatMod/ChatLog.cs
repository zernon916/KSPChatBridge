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
        internal static string Line(double alt, double agl, double spd, double hdg, double bank, double pitch, double thr, double vs, bool brakes, bool gearDown, string ap)
        {
            return "alt=" + alt.ToString("0", Inv) + "m agl=" + agl.ToString("0", Inv) + " spd=" + spd.ToString("0", Inv) + " hdg=" + hdg.ToString("000", Inv)
                + " bank=" + bank.ToString("0", Inv) + " pit=" + pitch.ToString("0", Inv) + " thr=" + (thr * 100).ToString("0", Inv) + "% vs=" + (vs >= 0 ? "+" : "") + vs.ToString("0", Inv)
                + " brk=" + (brakes ? "1" : "0") + " gear=" + (gearDown ? "down" : "up") + " ap=" + (ap ?? "");
        }
    }
}
