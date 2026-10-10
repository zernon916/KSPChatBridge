using System;
using System.Collections.Generic;
using System.Text;

namespace KSPChatBridge
{
    /// <summary>Keyboard text field on the IVA MFD (chat input, plan editor). Feed it Unity's Input.inputString each frame while
    /// focused: printable chars append, backspace deletes, Enter submits (Shift+Enter = new line in multi-line fields), Esc
    /// cancels (unfocus). Pure; tested.</summary>
    internal sealed class MfdField
    {
        internal enum Result { None, Submit, Cancel }
        internal readonly StringBuilder Text = new StringBuilder(); internal readonly bool Multi; internal readonly int Max;
        internal MfdField(bool multi, int max = 2000) { Multi = multi; Max = max; }
        internal void Set(string s) { Text.Length = 0; Text.Append(s ?? ""); }
        internal Result Feed(string input, bool shift, bool escape)
        {
            if (escape) return Result.Cancel;
            foreach (char c in input ?? "")
            {
                if (c == '\b') { if (Text.Length > 0) Text.Length--; continue; }
                if (c == '\n' || c == '\r') { if (Multi && shift) { if (Text.Length < Max) Text.Append('\n'); continue; } return Result.Submit; }
                if (c == 27) return Result.Cancel;
                if (c >= ' ' && Text.Length < Max) Text.Append(c);
            }
            return Result.None;
        }
    }

    /// <summary>MFD face layout (outside panel and IVA texture share it), any size, origin top-left: header strip with the engraved
    /// title plate, MASTER ALARM annunciator (A0) and COMMS key (H0); a top row of 8 keys (T0-T7, spare); 8 keys down each side
    /// (L0-L7, R0-R7); 8 bottom keys (B0-B7, B0 = BACK); the screen in the middle. Pure; tested.</summary>
    internal static class IvaLayout
    {
        internal const int W = 640, H = 560;
        internal struct Box { internal string Id; internal float X, Y, Wd, Ht; internal bool Has(float x, float y) { return x >= X && x < X + Wd && y >= Y && y < Y + Ht; } }
        /// <summary>Uniform scale vs the 640 x 560 reference face (fonts, margins).</summary>
        internal static float Scale(float w, float h) { return Math.Max(.6f, Math.Min(w / W, h / H)); }
        static void Metrics(float w, float h, out float m, out float kw, out float head, out float th, out float bh, out float gap)
        {
            float k = Scale(w, h); m = 10 * k; kw = 80 * k; head = 34 * k; th = 26 * k; bh = 38 * k; gap = 5 * k;
        }
        internal static Box ScreenOf(float w, float h)
        {
            float m, kw, head, th, bh, gap; Metrics(w, h, out m, out kw, out head, out th, out bh, out gap);
            float y = m + head + th + gap + 4; return new Box { Id = "S", X = m + kw + 8, Y = y, Wd = w - 2 * (m + kw + 8), Ht = h - y - bh - m - 8 };
        }
        internal static Box Screen { get { return ScreenOf(W, H); } }
        internal static Box Plate(float w, float h) { float m, kw, head, th, bh, gap; Metrics(w, h, out m, out kw, out head, out th, out bh, out gap); return new Box { Id = "P", X = w * .25f, Y = m * .6f, Wd = w * .5f, Ht = head - 6 }; }
        internal static List<Box> KeysOf(float w, float h)
        {
            float m, kw, head, th, bh, gap; Metrics(w, h, out m, out kw, out head, out th, out bh, out gap);
            var r = new List<Box>(); var s = ScreenOf(w, h); float kh = (s.Ht - 7 * gap) / 8;
            for (int i = 0; i < 8; i++) { r.Add(new Box { Id = "L" + i, X = m, Y = s.Y + i * (kh + gap), Wd = kw, Ht = kh }); r.Add(new Box { Id = "R" + i, X = w - m - kw, Y = s.Y + i * (kh + gap), Wd = kw, Ht = kh }); }
            float bw = (w - 2 * m - 7 * gap) / 8f;
            for (int i = 0; i < 8; i++) r.Add(new Box { Id = "B" + i, X = m + i * (bw + gap), Y = h - m - bh, Wd = bw, Ht = bh });
            float tw = (s.Wd - 7 * gap) / 8f;
            for (int i = 0; i < 8; i++) r.Add(new Box { Id = "T" + i, X = s.X + i * (tw + gap), Y = m + head, Wd = tw, Ht = th });
            var p = Plate(w, h);
            r.Add(new Box { Id = "A0", X = m + 14 * Scale(w, h), Y = p.Y, Wd = Math.Min(kw * 1.6f, p.X - m - 20), Ht = p.Ht });   // MASTER ALARM annunciator
            r.Add(new Box { Id = "H0", X = w - m - kw * 1.6f, Y = p.Y, Wd = kw * 1.6f - 14 * Scale(w, h), Ht = p.Ht });          // COMMS
            return r;
        }
        internal static List<Box> Keys() { return KeysOf(W, H); }
        internal static string HitOf(float w, float h, float x, float y)
        {
            foreach (var k in KeysOf(w, h)) if (k.Has(x, y)) return k.Id;
            return ScreenOf(w, h).Has(x, y) ? "S" : null;
        }
        internal static string Hit(float x, float y) { return HitOf(W, H, x, y); }
        /// <summary>Font size that fits a label in a key on at most 2 lines (uniform glyph scale: never squashed).</summary>
        internal static int KeyFont(string label, float keyW, float keyH, int max)
        {
            if (string.IsNullOrEmpty(label)) return max; var lines = Wrap(label, Math.Max(4, label.Length)); int longest = 0;
            foreach (var w in label.Split(' ')) longest = Math.Max(longest, w.Length);
            int oneLine = (int)((keyW - 6) / (.6f * Math.Max(1, label.Length))), twoLine = (int)Math.Min((keyW - 6) / (.6f * Math.Max(1, Math.Max(longest, (label.Length + 1) / 2))), (keyH - 4) / 2.3f);
            return Math.Max(7, Math.Min(max, Math.Max(oneLine, twoLine)));
        }
        /// <summary>Key label split to fit at a font size (1 or 2 lines).</summary>
        internal static List<string> KeyLines(string label, float keyW, int px)
        {
            int cols = Math.Max(3, (int)((keyW - 6) / (.6f * px))); var l = Wrap(label, cols); if (l.Count > 2) l = l.GetRange(0, 2); return l;
        }
        /// <summary>Wrap text into rows of cols characters (keeps explicit new lines).</summary>
        internal static List<string> Wrap(string text, int cols)
        {
            var o = new List<string>(); cols = Math.Max(4, cols);
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string l = raw; while (l.Length > cols) { int b = l.LastIndexOf(' ', cols); if (b < cols / 2) b = cols; o.Add(l.Substring(0, b)); l = l.Substring(b).TrimStart(); }
                o.Add(l);
            }
            return o;
        }
    }
}