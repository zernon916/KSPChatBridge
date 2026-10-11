// One drawing surface for the MAP / CHART / ILS pages, shared by the outside MFD window and every IVA screen: the page code
// (MapWindow.DrawPages) draws through IMfdCanvas, so the real map / chart / ILS graphics render either through IMGUI or into a
// cockpit screen's RenderTexture at any size, from one source. Pixel coordinates, origin top left, y down (IMGUI's space).
// Overlay text always sits on a dark backing box (MapWindow.Back) and never uses a colour close to the map / fog grey.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>Where the MAP / CHART / ILS pages draw: the outside MFD (GuiCanvas, IMGUI) or an IVA screen (RtCanvas, a
    /// RenderTexture). Fills and dots are in the caller's coordinates; Group translates + clips like GUI.BeginGroup.</summary>
    internal interface IMfdCanvas
    {
        void Fill(Rect r, Color c);
        void Image(Rect r, Texture t);
        void Dot(Vector2 p, float size, Color c);
        void Group(Rect r, Action inside);
        float TextHeight(string text, float width, int px, bool wrap);
        float TextWidth(string text, int px);
        void Text(Rect r, string text, int px, Color c, TextAnchor align, bool wrap);
    }

    /// <summary>Runtime monospace font + unlit materials, shared by the IVA screens and the RenderTexture canvas.</summary>
    internal static class MfdFont
    {
        internal static Font Face; internal static Material FontMat, FillMat;
        internal const int Px = 32;
        internal static void Ensure()
        {
            if (Face != null) return;
            Face = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New", "DejaVu Sans Mono", "Liberation Mono" }, Px);
            FontMat = new Material(Face.material) { hideFlags = HideFlags.HideAndDontSave };
            FillMat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            FillMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); FillMat.SetInt("_Cull", 0); FillMat.SetInt("_ZWrite", 0);
        }
        /// <summary>Fixed cell width of the monospace face (the IVA text pages lay columns out on it).</summary>
        internal static float CellW(int px) { return Mathf.Ceil(px * .6f); }
    }

    /// <summary>Overlay ink: readable colours and the shared dot-chain line (the taxi centreline dashes are step 3).</summary>
    internal static class MfdInk
    {
        /// <summary>Overlay text must never match the map / fog grey (MfdNav.TextColorOk); fall back to white.</summary>
        internal static Color Readable(Color c) { return MfdNav.TextColorOk(c.r, c.g, c.b) ? c : Color.white; }
        internal static Color Hex(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != 6) return fallback;
            try
            {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16), g = Convert.ToInt32(hex.Substring(2, 2), 16), b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return new Color(r / 255f, g / 255f, b / 255f);
            }
            catch (Exception) { return fallback; }
        }
        /// <summary>Split the "&lt;color=#RRGGBB&gt;text&lt;/color&gt;" markup (MapWindow.Col) into (text, hex or null) runs.</summary>
        internal static List<KeyValuePair<string, string>> RichRuns(string s)
        {
            var o = new List<KeyValuePair<string, string>>(); if (string.IsNullOrEmpty(s)) return o;
            int i = 0;
            while (i < s.Length)
            {
                int t = s.IndexOf("<color=#", i, StringComparison.Ordinal);
                if (t < 0) { o.Add(new KeyValuePair<string, string>(s.Substring(i), null)); break; }
                if (t > i) o.Add(new KeyValuePair<string, string>(s.Substring(i, t - i), null));
                int e = s.IndexOf('>', t); if (e < 0) { o.Add(new KeyValuePair<string, string>(s.Substring(t), null)); break; }
                string hex = s.Substring(t + 8, Math.Min(6, Math.Max(0, e - t - 8)));
                int c = s.IndexOf("</color>", e, StringComparison.Ordinal);
                if (c < 0) { o.Add(new KeyValuePair<string, string>(s.Substring(e + 1), hex)); break; }
                o.Add(new KeyValuePair<string, string>(s.Substring(e + 1, c - e - 1), hex));
                i = c + 8;
            }
            return o;
        }
        /// <summary>A line drawn as a chain of 2 px dots, exactly as the outside window always drew it.</summary>
        internal static void Line(IMfdCanvas c, Vector2 a, Vector2 b, Color col, float step)
        {
            float d = Vector2.Distance(a, b); int n = Mathf.Min(400, Mathf.Max(1, (int)(d / Mathf.Max(.5f, step))));
            for (int i = 0; i <= n; i++) c.Dot(Vector2.Lerp(a, b, i / (float)n), 2, col);
        }
    }

    /// <summary>The outside MFD window: draws straight through IMGUI (the page code runs inside OnGUI, so the map mouse still
    /// works: GUI.BeginGroup makes the mouse position map-square relative).</summary>
    internal sealed class GuiCanvas : IMfdCanvas
    {
        internal static readonly GuiCanvas Shared = new GuiCanvas();
        static GUIStyle style; static int stylePx = -1; static bool styleWrap;
        static GUIStyle Style(int px, bool wrap)
        {
            if (style == null || stylePx != px || styleWrap != wrap)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(8, px), wordWrap = wrap, clipping = TextClipping.Clip, richText = true };
                stylePx = px; styleWrap = wrap;
            }
            return style;
        }
        public void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
        public void Image(Rect r, Texture t) { if (t != null) GUI.DrawTexture(r, t); }
        public void Dot(Vector2 p, float size, Color c) { Fill(new Rect(p.x - size / 2, p.y - size / 2, size, size), c); }
        public void Group(Rect r, Action inside) { GUI.BeginGroup(r); try { inside(); } finally { GUI.EndGroup(); } }
        public float TextHeight(string text, float width, int px, bool wrap) { return Style(px, wrap).CalcHeight(new GUIContent(text ?? ""), Mathf.Max(1, width)); }
        public float TextWidth(string text, int px) { return Style(px, false).CalcSize(new GUIContent(text ?? "")).x; }
        public void Text(Rect r, string text, int px, Color c, TextAnchor align, bool wrap)
        {
            if (string.IsNullOrEmpty(text)) return;
            var s = Style(px, wrap); s.alignment = align; s.normal.textColor = MfdInk.Readable(c);
            GUI.Label(r, text, s);
        }
    }

    /// <summary>An IVA cockpit screen: the frame is recorded, then flushed into the screen's RenderTexture with GL (the technique
    /// the IVA text pages always used), so the shared MAP / CHART / ILS pass can draw at any size. Draw order is kept: fills,
    /// images and text are emitted in the order the page code asked for them.</summary>
    internal sealed class RtCanvas : IMfdCanvas
    {
        enum Kind { Fill, Image, Text }
        struct Op { internal Kind K; internal Rect R; internal Color C; internal Texture T; internal string S; internal int Px; internal TextAnchor A; internal bool Wrap; internal float Cell; }
        struct Ch { internal char C; internal Color K; }
        readonly List<Op> ops = new List<Op>();
        readonly RenderTexture rt; readonly int w, h;
        Color clear = Color.black; Vector2 off; Rect clip; int clipN;
        static readonly Dictionary<Texture, Material> texMats = new Dictionary<Texture, Material>();
        internal const float LineK = 1.3f;   // wrapped line height, in font sizes (IMGUI's label leading)

        internal RtCanvas(RenderTexture rt, int width, int height) { this.rt = rt; w = width; h = height; }
        internal void Begin(Color clearColour) { ops.Clear(); off = Vector2.zero; clipN = 0; clear = clearColour; }

        public void Fill(Rect r, Color c) { ops.Add(new Op { K = Kind.Fill, R = new Rect(r.x + off.x, r.y + off.y, r.width, r.height), C = c }); }
        public void Image(Rect r, Texture t) { if (t != null) ops.Add(new Op { K = Kind.Image, R = new Rect(r.x + off.x, r.y + off.y, r.width, r.height), T = t }); }
        public void Dot(Vector2 p, float size, Color c) { Fill(new Rect(p.x - size / 2, p.y - size / 2, size, size), c); }
        public void Group(Rect r, Action inside)
        {
            var root = new Rect(r.x + off.x, r.y + off.y, r.width, r.height);
            var po = off; var pc = clip; int pn = clipN;
            off = new Vector2(root.x, root.y);
            clip = pn > 0 ? Rect.MinMaxRect(Mathf.Max(pc.xMin, root.xMin), Mathf.Max(pc.yMin, root.yMin), Mathf.Min(pc.xMax, root.xMax), Mathf.Min(pc.yMax, root.yMax)) : root;
            clipN = pn + 1;
            try { inside(); } finally { off = po; clip = pc; clipN = pn; }
        }
        public void Text(Rect r, string text, int px, Color c, TextAnchor align, bool wrap)
        {
            if (string.IsNullOrEmpty(text)) return;
            ops.Add(new Op { K = Kind.Text, R = new Rect(r.x + off.x, r.y + off.y, r.width, r.height), S = text, Px = Mathf.Max(8, px), C = MfdInk.Readable(c), A = align, Wrap = wrap });
        }
        /// <summary>The IVA text pages (AP / COMMS / ABOUT / LEARN): monospace cells, optional centring in a key - unchanged look.</summary>
        internal void CellText(string s, float x, float y, int px, Color c, bool center, float width)
        {
            if (string.IsNullOrEmpty(s)) return;
            ops.Add(new Op { K = Kind.Text, R = new Rect(x + off.x, y + off.y, width, px), S = s, Px = Mathf.Max(8, px), C = c, A = center ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, Cell = MfdFont.CellW(px) });
        }
        static List<Ch> Runs(string s, Color baseCol)
        {
            var o = new List<Ch>();
            foreach (var run in MfdInk.RichRuns(s))
            {
                var col = run.Value == null ? baseCol : MfdInk.Readable(MfdInk.Hex(run.Value, baseCol));
                foreach (char ch in run.Key) o.Add(new Ch { C = ch, K = col });
            }
            return o;
        }
        static float Adv(char ch, int px, float cell)
        {
            if (cell > 0) return cell;
            CharacterInfo ci;
            return MfdFont.Face.GetCharacterInfo(ch, out ci, px) ? ci.advance : MfdFont.CellW(px);
        }
        static float WidthOf(List<Ch> line, int px, float cell) { float w = 0; foreach (var ch in line) w += Adv(ch.C, px, cell); return w; }
        /// <summary>Greedy word wrap (breaks at the last space, long words overflow and are clipped).</summary>
        static List<List<Ch>> Wrap(List<Ch> src, float width, int px, float cell)
        {
            var lines = new List<List<Ch>>(); var cur = new List<Ch>(); float w = 0; int space = -1;
            foreach (var ch in src)
            {
                if (ch.C == '\n') { lines.Add(cur); cur = new List<Ch>(); w = 0; space = -1; continue; }
                float a = Adv(ch.C, px, cell);
                if (width > 0 && w + a > width && cur.Count > 0)
                {
                    if (space > 0) { lines.Add(cur.GetRange(0, space)); cur = cur.GetRange(space + 1, cur.Count - space - 1); w = WidthOf(cur, px, cell); }
                    else { lines.Add(cur); cur = new List<Ch>(); w = 0; }
                    space = -1;
                }
                if (ch.C == ' ') space = cur.Count;
                cur.Add(ch); w += a;
            }
            lines.Add(cur); return lines;
        }
        public float TextHeight(string text, float width, int px, bool wrap)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            px = Mathf.Max(8, px); MfdFont.Face.RequestCharactersInTexture(text, px);
            var ls = Wrap(Runs(text, Color.white), Mathf.Max(1, width), px, 0);
            return ls.Count * px * LineK + 2;
        }
        public float TextWidth(string text, int px)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            px = Mathf.Max(8, px); MfdFont.Face.RequestCharactersInTexture(text, px);
            return WidthOf(Runs(text, Color.white), px, 0);
        }

        static Material TexMat(Texture t)
        {
            if (t == null) return null;
            Material m;
            if (texMats.TryGetValue(t, out m) && m != null) { m.mainTexture = t; return m; }
            Shader sh = null;
            foreach (var n in new[] { "Unlit/Texture", "Sprites/Default", "Unlit/Transparent", "KSP/Unlit" }) { sh = Shader.Find(n); if (sh != null) break; }
            if (sh == null) return null;
            m = new Material(sh) { mainTexture = t, hideFlags = HideFlags.HideAndDontSave };
            texMats[t] = m; return m;
        }
        /// <summary>Clip to the current Group (IMGUI's BeginGroup clips too); true = nothing left to draw.</summary>
        bool Clipped(Rect r, out Rect q)
        {
            q = r; if (clipN == 0) return false;
            float x0 = Mathf.Max(r.xMin, clip.xMin), x1 = Mathf.Min(r.xMax, clip.xMax);
            float y0 = Mathf.Max(r.yMin, clip.yMin), y1 = Mathf.Min(r.yMax, clip.yMax);
            if (x1 <= x0 || y1 <= y0) return true;
            q = Rect.MinMaxRect(x0, y0, x1, y1); return false;
        }
        void Quad(Rect r, Color c)
        {
            Rect q; if (Clipped(r, out q)) return;
            GL.Color(c); GL.Vertex3(q.xMin, q.yMin, 0); GL.Vertex3(q.xMax, q.yMin, 0); GL.Vertex3(q.xMax, q.yMax, 0); GL.Vertex3(q.xMin, q.yMax, 0);
        }
        void Image(Op o)
        {
            Rect r = o.R, q; if (Clipped(r, out q)) return;
            float wd = Mathf.Max(1e-4f, r.width), ht = Mathf.Max(1e-4f, r.height);
            float u0 = (q.xMin - r.xMin) / wd, u1 = (q.xMax - r.xMin) / wd, v1 = 1 - (q.yMin - r.yMin) / ht, v0 = 1 - (q.yMax - r.yMin) / ht;
            GL.Color(Color.white);   // a clipped sub-rect keeps its part of the map bitmap (north stays up)
            GL.TexCoord2(u0, v1); GL.Vertex3(q.xMin, q.yMin, 0); GL.TexCoord2(u1, v1); GL.Vertex3(q.xMax, q.yMin, 0);
            GL.TexCoord2(u1, v0); GL.Vertex3(q.xMax, q.yMax, 0); GL.TexCoord2(u0, v0); GL.Vertex3(q.xMin, q.yMax, 0);
        }
        void Text(Op o)
        {
            var src = Runs(o.S, o.C);
            var lines = o.Wrap || o.S.IndexOf('\n') >= 0 ? Wrap(src, o.R.width, o.Px, o.Cell) : new List<List<Ch>> { src };
            float lh = o.Px * (o.Cell > 0 ? 1f : LineK);   // the text pages stack rows themselves (TextPx + 3)
            for (int i = 0; i < lines.Count; i++)
            {
                float top = o.R.y + i * lh; if (clipN > 0 && top > clip.yMax) break;
                var line = lines[i]; float lw = WidthOf(line, o.Px, o.Cell);
                float x = o.A == TextAnchor.MiddleRight ? o.R.xMax - lw
                    : o.A == TextAnchor.MiddleCenter ? o.R.x + (o.Cell > 0 ? Mathf.Max(2, (o.R.width - lw) / 2) : (o.R.width - lw) / 2)
                    : o.R.x;
                float baseY = top + o.Px * .8f;
                foreach (var ch in line)
                {
                    float a = Adv(ch.C, o.Px, o.Cell); CharacterInfo ci;
                    if (ch.C != ' ' && MfdFont.Face.GetCharacterInfo(ch.C, out ci, o.Px))
                    {
                        float x0 = x + ci.minX, x1 = x + ci.maxX, y0 = baseY - ci.maxY, y1 = baseY - ci.minY;
                        if (clipN == 0 || !(x1 < clip.xMin || x0 > clip.xMax || y1 < clip.yMin || y0 > clip.yMax))
                        {
                            GL.Color(ch.K);
                            GL.TexCoord(ci.uvTopLeft); GL.Vertex3(x0, y0, 0); GL.TexCoord(ci.uvTopRight); GL.Vertex3(x1, y0, 0);
                            GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x1, y1, 0); GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(x0, y1, 0);
                        }
                    }
                    x += a;
                }
            }
        }
        /// <summary>Flush the recorded frame into the RenderTexture (one GL pass, batching consecutive same-material draws).</summary>
        internal void End()
        {
            if (rt == null) return;
            MfdFont.Ensure();
            var sizes = new HashSet<int>(); foreach (var o in ops) if (o.K == Kind.Text) sizes.Add(o.Px);
            foreach (int px in sizes)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var o in ops) if (o.K == Kind.Text && o.Px == px) sb.Append(o.S);
                MfdFont.Face.RequestCharactersInTexture(sb.ToString(), px);
            }
            MfdFont.FontMat.mainTexture = MfdFont.Face.material.mainTexture;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            GL.PushMatrix(); GL.LoadPixelMatrix(0, w, h, 0); GL.Clear(true, true, clear);
            Material cur = null;
            foreach (var o in ops)
            {
                Material m = o.K == Kind.Fill ? MfdFont.FillMat : o.K == Kind.Image ? TexMat(o.T) : MfdFont.FontMat;
                if (m == null) continue;
                if (m != cur) { if (cur != null) GL.End(); m.SetPass(0); GL.Begin(GL.QUADS); cur = m; }
                if (o.K == Kind.Fill) Quad(o.R, o.C); else if (o.K == Kind.Image) Image(o); else Text(o);
            }
            if (cur != null) GL.End();
            GL.PopMatrix(); RenderTexture.active = prev;
        }
    }
}
