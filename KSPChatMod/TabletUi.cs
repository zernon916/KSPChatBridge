// TabletUi: small modern IMGUI kit for the AICS tablet ONLY (the control box MFD and the IVA/cockpit screens stay as they are).
// Dark palette, rounded cards, value tiles, bar + dial gauges, a real line graph (auto-scale, grid, several series), segment
// and touch buttons. Textures and styles are cached; draw calls allocate nothing per frame except caller strings.
using System;
using UnityEngine;

namespace KSPChatBridge
{
    internal static class TabletUi
    {
        internal static readonly Color Bg = new Color(.07f, .08f, .10f), Card = new Color(.13f, .15f, .18f), CardHi = new Color(.18f, .21f, .25f),
            Text = new Color(.90f, .93f, .96f), Dim = new Color(.55f, .60f, .66f), Accent = new Color(.25f, .75f, 1f), Good = new Color(.30f, .85f, .45f),
            Warn = new Color(1f, .70f, .20f), Bad = new Color(1f, .33f, .30f), Grid = new Color(1, 1, 1, .07f);
        internal static readonly Color[] Series = { new Color(.25f, .75f, 1f), new Color(1f, .70f, .20f), new Color(.30f, .85f, .45f), new Color(.85f, .45f, 1f) };
        static Texture2D round, ring; static GUIStyle cardS, title, label, value, unit, small, btn, btnOn, chip; static int builtFor = -1;
        internal static int Font = 18;

        static Texture2D Round(int n, float r)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + .5f, r, n - r), cy = Mathf.Clamp(y + .5f, r, n - r);
                    float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(cx, cy)); t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(r - d + .5f)));
                }
            t.Apply(); return t;
        }
        /// <summary>Dial arc mask: 270 degree ring, alpha = 1 inside the band (drawn tinted, twice: track + value via clip).</summary>
        static Texture2D Ring(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = n / 2f, ro = c - 1, ri = c * .72f;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float dx = x + .5f - c, dy = y + .5f - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(ro - d + .5f) * Mathf.Clamp01(d - ri + .5f); t.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            t.Apply(); return t;
        }
        internal static void Ensure(int font)
        {
            if (round == null) { round = Round(32, 10); ring = Ring(128); builtFor = -1; }
            if (builtFor == font && cardS != null) return; builtFor = font; Font = font;
            cardS = new GUIStyle { border = new RectOffset(11, 11, 11, 11) }; cardS.normal.background = round;
            title = new GUIStyle(GUI.skin.label) { fontSize = font - 2, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip }; title.normal.textColor = Dim;
            label = new GUIStyle(title) { fontStyle = FontStyle.Normal, fontSize = font - 3 };
            value = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(font * 1.7f), fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip }; value.normal.textColor = Text;
            unit = new GUIStyle(label) { alignment = TextAnchor.LowerLeft };
            small = new GUIStyle(label) { fontSize = Mathf.Max(10, font - 5) };
            btn = new GUIStyle { alignment = TextAnchor.MiddleCenter, fontSize = font, fontStyle = FontStyle.Bold, border = new RectOffset(11, 11, 11, 11), clipping = TextClipping.Clip };
            btn.normal.background = btn.hover.background = btn.active.background = round; btn.normal.textColor = btn.hover.textColor = Text; btn.active.textColor = Accent;
            btnOn = new GUIStyle(btn); btnOn.normal.textColor = btnOn.hover.textColor = Color.black;
            chip = new GUIStyle(btn) { fontSize = Mathf.Max(10, font - 5) }; chip.normal.textColor = Color.black;
        }
        internal static GUIStyle TextStyle { get { return label; } }
        internal static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, round == null ? Texture2D.whiteTexture : (Texture)round); GUI.color = o; }
        internal static void Rect(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
        static void Box(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.Box(r, GUIContent.none, cardS); GUI.color = o; }

        /// <summary>Rounded card with a title; returns the inner content rect.</summary>
        internal static Rect CardPanel(Rect r, string caption)
        {
            Box(r, Card); float th = caption == null ? 0 : Font * 1.6f;
            if (caption != null) GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, th), caption.ToUpperInvariant(), title);
            return new Rect(r.x + 12, r.y + 8 + th, r.width - 24, r.height - 16 - th);
        }
        /// <summary>Big value tile: small label, large value, unit; optional accent stripe colour.</summary>
        internal static void Tile(Rect r, string caption, string val, string un, Color accent)
        {
            Box(r, Card); Rect(new Rect(r.x + 1, r.y + 8, 3, r.height - 16), accent);
            GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 18, Font * 1.4f), caption, label);
            var vc = new GUIContent(val); float vw = Mathf.Min(r.width - 24, value.CalcSize(vc).x);
            float vy = r.y + Font * 1.3f; GUI.Label(new Rect(r.x + 12, vy, r.width - 24, Font * 2.2f), vc, value);
            if (!string.IsNullOrEmpty(un)) GUI.Label(new Rect(r.x + 16 + vw, vy, Mathf.Max(0, r.width - vw - 26), Font * 2f), un, unit);
        }
        /// <summary>Horizontal bar gauge 0..1 with label and value text.</summary>
        internal static void Bar(Rect r, string caption, float frac, string text, Color c)
        {
            float lh = Font * 1.3f; GUI.Label(new Rect(r.x, r.y, r.width * .6f, lh), caption, label);
            var rs = new GUIStyle(label) { alignment = TextAnchor.UpperRight }; GUI.Label(new Rect(r.x + r.width * .4f, r.y, r.width * .6f, lh), text, rs);
            Rect tr = new Rect(r.x, r.y + lh + 2, r.width, Mathf.Max(8, r.height - lh - 4)); Fill(tr, CardHi);
            float f = Mathf.Clamp01(frac); if (f > .005f) Fill(new Rect(tr.x, tr.y, Mathf.Max(tr.height, tr.width * f), tr.height), c);
        }
        /// <summary>270-degree dial: track ring, value needle + cap, centred value text. frac 0..1.</summary>
        internal static void Dial(Rect r, string caption, float frac, string text, Color c)
        {
            float s = Mathf.Min(r.width, r.height - Font * 1.3f); Rect d = new Rect(r.x + (r.width - s) / 2, r.y, s, s);
            var o = GUI.color; GUI.color = CardHi; GUI.DrawTexture(d, ring); GUI.color = o;
            float a0 = 135, a = a0 + 270 * Mathf.Clamp01(frac); Vector2 ctr = d.center;
            for (float t = a0; t < a; t += 6) { float rad = t * Mathf.Deg2Rad, rr = s * .43f; Fill(new Rect(ctr.x + Mathf.Cos(rad) * rr - s * .06f, ctr.y + Mathf.Sin(rad) * rr - s * .06f, s * .12f, s * .12f), c); }
            Line(ctr, ctr + new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)) * s * .36f, 3, Text);
            var vs = new GUIStyle(value) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(Font * 1.2f) };
            GUI.Label(new Rect(d.x, d.y + s * .55f, s, Font * 1.8f), text, vs);
            GUI.Label(new Rect(r.x, d.yMax, r.width, Font * 1.3f), caption, new GUIStyle(label) { alignment = TextAnchor.UpperCenter });
        }
        internal static void Line(Vector2 a, Vector2 b, float th, Color c)
        {
            var dv = b - a; float len = dv.magnitude; if (len < .3f) return;
            var prev = GUI.matrix; var o = GUI.color; GUI.color = c;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(dv.y, dv.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - th / 2, len, th), Texture2D.whiteTexture);
            GUI.matrix = prev; GUI.color = o;
        }
        /// <summary>Line graph: each series auto-scaled to its own range (left axis = series 0, right = series 1), grid, legend.
        /// data[s] is a ring buffer of n valid samples ending at head (exclusive).</summary>
        internal static void Graph(Rect r, string[] names, float[][] data, int n, int head, string[] units)
        {
            Fill(r, Bg); Rect p = new Rect(r.x + 8, r.y + Font * 1.5f, r.width - 16, r.height - Font * 1.5f - 8);
            for (int i = 1; i < 4; i++) { Rect(new Rect(p.x, p.y + p.height * i / 4, p.width, 1), Grid); Rect(new Rect(p.x + p.width * i / 4, p.y, 1, p.height), Grid); }
            float lx = r.x + 10;
            for (int s = 0; s < data.Length; s++)
            {
                var buf = data[s]; int cap = buf.Length; Color c = Series[s % Series.Length];
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < n; i++) { float v = buf[(head - n + i + cap * 2) % cap]; if (v < lo) lo = v; if (v > hi) hi = v; }
                string leg = names[s] + (n > 0 ? "  " + lo.ToString("0") + "-" + hi.ToString("0") + " " + units[s] : "");
                Rect(new Rect(lx, r.y + Font * .6f, 14, 4), c); GUI.Label(new Rect(lx + 18, r.y + 2, 400, Font * 1.4f), leg, small); lx += small.CalcSize(new GUIContent(leg)).x + 36;
                if (n < 2) continue; if (hi - lo < 1e-3f) { hi += 1; lo -= 1; }
                Vector2 last = Vector2.zero;
                for (int i = 0; i < n; i++)
                {
                    float v = buf[(head - n + i + cap * 2) % cap];
                    var pt = new Vector2(p.x + p.width * i / (cap - 1f) + p.width * (cap - n) / (cap - 1f), p.yMax - p.height * (v - lo) / (hi - lo));
                    if (i > 0) Line(last, pt, 2, c); last = pt;
                }
            }
            if (n < 2) GUI.Label(new Rect(p.x, p.center.y - Font, p.width, Font * 2), "collecting data...", new GUIStyle(label) { alignment = TextAnchor.MiddleCenter });
        }
        /// <summary>Segmented control; returns the (possibly new) selected index.</summary>
        internal static int Segments(Rect r, string[] items, int sel)
        {
            float w = r.width / items.Length;
            for (int i = 0; i < items.Length; i++) if (Button(new Rect(r.x + i * w + 2, r.y, w - 4, r.height), items[i], i == sel, true)) sel = i;
            return sel;
        }
        /// <summary>Touch-size rounded button (min 44 px high recommended); disabled = greyed, never returns true.</summary>
        internal static bool Button(Rect r, string text, bool on = false, bool enabled = true)
        {
            var o = GUI.color; GUI.color = !enabled ? new Color(.35f, .37f, .40f) : on ? Accent : CardHi;
            var st = on ? btnOn : btn; var oc = st.normal.textColor; if (!enabled) st.normal.textColor = st.hover.textColor = Dim;
            bool hit = GUI.Button(r, text, st); GUI.color = o; if (!enabled) st.normal.textColor = st.hover.textColor = oc;
            return hit && enabled;
        }
        /// <summary>Small coloured status chip ("DONE", "QUEUED", ...).</summary>
        internal static void Chip(Rect r, string text, Color c) { var o = GUI.color; GUI.color = c; GUI.Box(r, GUIContent.none, cardS); GUI.color = o; GUI.Label(r, text, chip); }
        internal static void Label(Rect r, string text, bool dim = false) { var o = label.normal.textColor; label.normal.textColor = dim ? Dim : Text; GUI.Label(r, text, label); label.normal.textColor = o; }
    }
}