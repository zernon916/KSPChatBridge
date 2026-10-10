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

    /// <summary>IVA MFD face layout in texture pixels (origin top-left): 6 left keys, 6 right keys, 7 bottom keys, the screen in
    /// the middle and a header strip (CHAT / HOME). Key ids: L0-L5, R0-R5, B0-B6 (B0 = BACK), H0 = CHAT. Pure; tested.</summary>
    internal static class IvaLayout
    {
        internal const int W = 640, H = 520, M = 10, KW = 86, BH = 44, Head = 30, Gap = 6;
        internal struct Box { internal string Id; internal float X, Y, Wd, Ht; internal bool Has(float x, float y) { return x >= X && x < X + Wd && y >= Y && y < Y + Ht; } }
        internal static Box Screen { get { return new Box { Id = "S", X = M + KW + 8, Y = M + Head, Wd = W - 2 * (M + KW + 8), Ht = H - M - Head - BH - M - 8 }; } }
        internal static List<Box> Keys()
        {
            var r = new List<Box>(); var s = Screen; float kh = (s.Ht - 5 * Gap) / 6;
            for (int i = 0; i < 6; i++) { r.Add(new Box { Id = "L" + i, X = M, Y = s.Y + i * (kh + Gap), Wd = KW, Ht = kh }); r.Add(new Box { Id = "R" + i, X = W - M - KW, Y = s.Y + i * (kh + Gap), Wd = KW, Ht = kh }); }
            float bw = (W - 2 * M - 6 * Gap) / 7f;
            for (int i = 0; i < 7; i++) r.Add(new Box { Id = "B" + i, X = M + i * (bw + Gap), Y = H - M - BH, Wd = bw, Ht = BH });
            r.Add(new Box { Id = "H0", X = W - M - KW, Y = 4, Wd = KW, Ht = Head - 8 });   // CHAT
            return r;
        }
        /// <summary>Which key / screen a texture-space point hits (null = bezel).</summary>
        internal static string Hit(float x, float y)
        {
            foreach (var k in Keys()) if (k.Has(x, y)) return k.Id;
            return Screen.Has(x, y) ? "S" : null;
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