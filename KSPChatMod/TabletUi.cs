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
            Text = new Color(.90f, .93f, .96f), Dim = new Color(.78f, .82f, .87f), Accent = new Color(.25f, .75f, 1f), Good = new Color(.30f, .85f, .45f),
            Warn = new Color(1f, .70f, .20f), Bad = new Color(1f, .33f, .30f), Grid = new Color(1, 1, 1, .07f);
        internal static readonly Color[] Series = { new Color(.25f, .75f, 1f), new Color(1f, .70f, .20f), new Color(.30f, .85f, .45f), new Color(.85f, .45f, 1f) };
        static Texture2D round, ring; static GUIStyle cardS, title, label, value, unit, small, btn, btnOn, chip, rightLabel, field, toggleCap; static int builtFor = -1;
        internal const float BtnH = 54, RowH = 46, ToggleW = 132, MinToggleW = 92, FieldH = 46, SwitchW = 74;
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
            btnOn = new GUIStyle(btn); btnOn.normal.textColor = btnOn.hover.textColor = btnOn.active.textColor = btnOn.focused.textColor = Color.black;
            chip = new GUIStyle(btn) { fontSize = Mathf.Max(10, font - 5) }; chip.normal.textColor = Color.black;
            rightLabel = new GUIStyle(label) { alignment = TextAnchor.MiddleRight };
            // holds/settings pages: touch text field + switch row. The field paints its own rounded bed, so GUI.color only tints the text.
            field = new GUIStyle { fontSize = font, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(10, 8, 4, 4) };
            field.normal.textColor = field.hover.textColor = field.focused.textColor = Text;   // this Unity's GUIStyle has no caret/selection colour, so the caret stays the engine default
            toggleCap = new GUIStyle(label) { alignment = TextAnchor.MiddleLeft };
            wrapS = new GUIStyle(label) { wordWrap = true, clipping = TextClipping.Overflow, alignment = TextAnchor.UpperLeft };   // UI1
        }
        internal static GUIStyle TextStyle { get { return label; } }
        static GUIStyle wrapS;   // UI1: word-wrapped label, measured height
        /// <summary>UI2: width a (bold) button needs for its text, padding included.</summary>
        internal static float ButtonWidth(string text) { return btnOn == null ? 0 : btnOn.CalcSize(new GUIContent(text ?? "")).x + 20; }
        internal static float TextWidth(string text) { return label == null ? 0 : label.CalcSize(new GUIContent(text ?? "")).x; }
        internal static float WrapHeight(string text, float width) { return wrapS == null ? 0 : wrapS.CalcHeight(new GUIContent(text ?? ""), Mathf.Max(10, width)); }
        internal static void LabelWrap(Rect r, string text, bool dim = false) { if (wrapS == null) { Label(r, text, dim); return; } var o = wrapS.normal.textColor; wrapS.normal.textColor = dim ? Dim : Text; GUI.Label(r, text, wrapS); wrapS.normal.textColor = o; }
        internal static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, round == null ? Texture2D.whiteTexture : (Texture)round); GUI.color = o; }
        internal static void Rect(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
        /// <summary>UI3: one setting's box - subtle panel darker than the card with a 1 px lighter edge (cached rounded style, no per-frame allocation).</summary>
        internal static readonly Color RowBoxFill = new Color(.10f, .115f, .14f), RowBoxEdge = new Color(.24f, .27f, .32f);
        internal static void RowBox(Rect r) { Box(r, RowBoxEdge); Box(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), RowBoxFill); }
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
            GUI.Label(new Rect(r.x + r.width * .4f, r.y, r.width * .6f, lh), text, rightLabel);
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
        internal static void Graph(Rect r, string[] names, float[][] data, int n, int head, string[] units, float[] xs = null)
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
                if (n < 1 || (n < 2 && xs == null)) continue; if (hi - lo < 1e-3f) { hi += 1; lo -= 1; }
                Vector2 last = Vector2.zero;
                for (int i = 0; i < n; i++)
                {
                    float v = buf[(head - n + i + cap * 2) % cap];
                    float x = xs == null ? (i + cap - n) / (cap - 1f)
                        : n == 1 || xs[n - 1] == xs[0] ? .5f : (xs[i] - xs[0]) / (xs[n - 1] - xs[0]);
                    var pt = new Vector2(p.x + p.width * x, p.yMax - p.height * (v - lo) / (hi - lo));
                    if (xs != null) Rect(new Rect(pt.x - 2, pt.y - 2, 4, 4), c);
                    if (i > 0) Line(last, pt, 2, c); last = pt;
                }
            }
            if (n == 0 || (n < 2 && xs == null)) GUI.Label(new Rect(p.x, p.center.y - Font, p.width, Font * 2), "collecting data...", new GUIStyle(label) { alignment = TextAnchor.MiddleCenter });
        }
        /// <summary>Segmented control; returns the (possibly new) selected index.</summary>
        internal static int Segments(Rect r, string[] items, int sel)
        {
            float w = r.width / items.Length;
            for (int i = 0; i < items.Length; i++) if (Button(new Rect(r.x + i * w + 2, r.y, w - 4, r.height), items[i], i == sel, true)) sel = i;
            return sel;
        }
        /// <summary>GROK LAND2: drop-down - a header button (current item + v); open = the items listed below it. Returns the picked index or -1.</summary>
        internal static int Dropdown(Rect r, string[] labels, int sel, ref bool open, bool enabled = true)
        {
            if (Button(r, (sel >= 0 && sel < labels.Length ? labels[sel] : "-") + (open ? "   ^" : "   v"), open, enabled)) open = !open;
            if (!open || !enabled) return -1;
            for (int i = 0; i < labels.Length; i++) if (Button(new Rect(r.x + 12, r.yMax + 2 + i * (r.height + 2), r.width - 12, r.height), labels[i], i == sel, true)) { open = false; return i; }
            return -1;
        }
        internal static float DropdownHeight(float rowH, int n, bool open) { return rowH + (open ? n * (rowH + 2) : 0); }
        /// <summary>Touch-size rounded button (min 44 px high recommended); disabled = greyed, never returns true.</summary>
        internal static bool Button(Rect r, string text, bool on = false, bool enabled = true)
        {
            // Tint only the background: GUI.color also multiplies the label and made inactive tabs almost black.
            var oldColor = GUI.color; var oldBackground = GUI.backgroundColor; var oldContent = GUI.contentColor;
            var st = on ? btnOn : btn; var normal = st.normal.textColor; var hover = st.hover.textColor;
            try
            {
                GUI.color = GUI.contentColor = Color.white;
                GUI.backgroundColor = !enabled ? new Color(.25f, .27f, .30f) : on ? Accent : CardHi;
                if (!enabled) st.normal.textColor = st.hover.textColor = Dim;
                return GUI.Button(r, text, st) && enabled;
            }
            finally
            {
                GUI.color = oldColor; GUI.backgroundColor = oldBackground; GUI.contentColor = oldContent;
                st.normal.textColor = normal; st.hover.textColor = hover;
            }
        }
        /// <summary>GROK TAB1: plain status text (no box, no button look): a coloured dot + the label.</summary>
        internal static void Status(Rect r, string text, Color c)
        {
            float d = Mathf.Min(14, r.height * .45f); var o = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(r.x + 2, r.y + (r.height - d) / 2, d, d), Texture2D.whiteTexture); GUI.color = o;
            var oc = label.normal.textColor; label.normal.textColor = c; GUI.Label(new Rect(r.x + d + 10, r.y, r.width - d - 10, r.height), text, label); label.normal.textColor = oc;
        }
        /// <summary>GROK TAB1: action button (starts something on the craft) - green fill, bold ">> " prefix; nav buttons stay plain.</summary>
        internal static bool ActionButton(Rect r, string text, bool enabled = true, bool arrows = true)
        {
            var oldBackground = GUI.backgroundColor; var normal = btnOn.normal.textColor; var hover = btnOn.hover.textColor; var fs = btnOn.fontStyle;
            try
            {
                GUI.backgroundColor = enabled ? new Color(.20f, .62f, .32f) : new Color(.25f, .27f, .30f);
                btnOn.fontStyle = FontStyle.Bold; if (!enabled) btnOn.normal.textColor = btnOn.hover.textColor = Dim;
                return GUI.Button(r, arrows ? ">> " + text : text, btnOn) && enabled;
            }
            finally { GUI.backgroundColor = oldBackground; btnOn.normal.textColor = normal; btnOn.hover.textColor = hover; btnOn.fontStyle = fs; }
        }
        /// <summary>GROK TAB1: tappable status light (dot + text); returns true on a tap when enabled.</summary>
        internal static bool Light(Rect r, string text, bool on, bool enabled)
        {
            Status(r, text, on ? Good : Dim);
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }
        /// <summary>Small coloured status chip ("DONE", "QUEUED", ...).</summary>
        internal static void Chip(Rect r, string text, Color c) { var o = GUI.color; GUI.color = c; GUI.Box(r, GUIContent.none, cardS); GUI.color = o; GUI.Label(r, text, chip); }
        internal static void Label(Rect r, string text, bool dim = false) { var o = label.normal.textColor; label.normal.textColor = dim ? Dim : Text; GUI.Label(r, text, label); label.normal.textColor = o; }
        /// <summary>Same, right-aligned (unit / trailing column).</summary>
        internal static void LabelRight(Rect r, string text, bool dim = false) { var o = rightLabel.normal.textColor; rightLabel.normal.textColor = dim ? Dim : Text; GUI.Label(r, text, rightLabel); rightLabel.normal.textColor = o; }
        /// <summary>Centre-zero deviation bar: frac -1..1 fills away from the middle marker (hold errors, trim, docking offsets).</summary>
        internal static void DevBar(Rect r, string caption, float frac, string text, Color c, bool dim = false)
        {
            float lh = Font * 1.3f; Label(new Rect(r.x, r.y, r.width * .55f, lh), caption, dim);
            var ocR = rightLabel.normal.textColor; rightLabel.normal.textColor = dim ? Dim : Text; GUI.Label(new Rect(r.x + r.width * .4f, r.y, r.width * .6f, lh), text, rightLabel); rightLabel.normal.textColor = ocR;
            Rect tr = new Rect(r.x, r.y + lh + 2, r.width, Mathf.Max(8, r.height - lh - 4)); Fill(tr, CardHi);
            float f = Mathf.Clamp(frac, -1f, 1f), cx = tr.x + tr.width * .5f, w = tr.width * .5f * f;
            if (!dim && Mathf.Abs(w) > 1) Rect(new Rect(Mathf.Min(cx, cx + w), tr.y, Mathf.Abs(w), tr.height), c);
            Rect(new Rect(cx - 1, tr.y - 2, 2, tr.height + 4), Text);   // zero marker
        }
        /// <summary>Touch text field on a rounded dark bed; disabled = dimmed bed and grey text. Returns the (possibly edited) text.</summary>
        internal static string Field(Rect r, string text, bool enabled = true)
        {
            Fill(r, enabled ? CardHi : Card);
            var o = GUI.color; GUI.color = enabled ? Text : Dim; string res = GUI.TextField(r, text ?? "", field); GUI.color = o; return res;
        }
        /// <summary>Touch switch row: caption left, rounded track + knob right; disabled greys out and never changes. Returns the new value.</summary>
        internal static bool Toggle(Rect r, string caption, bool on, bool enabled = true)
        {
            var ocT = toggleCap.normal.textColor; toggleCap.normal.textColor = !enabled || !on ? Dim : Text; GUI.Label(new Rect(r.x, r.y, r.width - MinToggleW - 8, r.height), caption, toggleCap); toggleCap.normal.textColor = ocT;
            float th = Mathf.Min(r.height, Font * 1.9f), tw = Mathf.Min(MinToggleW * .55f, r.width * .4f);
            Rect tr = new Rect(r.xMax - tw, r.y + (r.height - th) / 2, tw, th);
            var o = GUI.color; GUI.color = !enabled ? new Color(.35f, .37f, .40f) : on ? Accent : CardHi; GUI.Box(tr, GUIContent.none, cardS);
            float k = th - 6, kx = on ? tr.xMax - k - 3 : tr.x + 3; GUI.color = !enabled ? Dim : Card; GUI.DrawTexture(new Rect(kx, tr.y + 3, k, k), round); GUI.color = o;
            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            return enabled && hit ? !on : on;
        }
    }
}
