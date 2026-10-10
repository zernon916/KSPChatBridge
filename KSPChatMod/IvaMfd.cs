// Native IVA AICS MFD (Phase 2, Luke 4:41 PM): a procedural cockpit screen prop. The face (bezel, 26 soft keys, screen) is
// drawn into a RenderTexture at 4 Hz with a runtime monospace font and mapped on a quad built in code (no Unity/Blender
// assets); one MeshCollider on the face hit-tests the keys and the screen by texture coordinate (IvaLayout). Same
// navigation as the outside MFD (MfdNav: HOME groups, paged items, BACK fixed bottom-left). Screen text fields (CHAT input,
// flight plan editor) take the keyboard while focused, with ship controls locked; Enter submits, Esc unfocuses.
// Added to stock cockpits by AICS_IVA.cfg (ModuleManager). RasterPropMonitor pages (AICS_RPM.cfg) are unaffected.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    public class AicsIvaMfd : InternalModule
    {
        [KSPField] public float width = .20f, height = .1625f, lift = .004f;
        [KSPField] public bool flipX = false, flipY = false, hideModel = true;
        [KSPField] public float refreshHz = 4;

        const string LockId = "AICS_IvaTyping";
        static Font font; static Material fontMat, fillMat; const int FontPx = 32;
        static AicsIvaMfd typingOwner; static int releaseFrame = -1;

        RenderTexture rt; GameObject face; MeshCollider col; float nextDraw; bool dirty = true, built;
        string group, item; int page; double span = 20000;
        readonly MfdField chatF = new MfdField(false, 400), planF = new MfdField(true, 4000); MfdField focused;
        static readonly Color Bezel = new Color(.19f, .2f, .21f), KeyC = new Color(.27f, .28f, .3f), KeyEdge = new Color(.08f, .08f, .09f), Phos = new Color(.35f, 1f, .45f), Dim = new Color(.1f, .3f, .14f), Label = new Color(.88f, .92f, .88f);

        void Start()
        {
            if (!HighLogic.LoadedSceneIsFlight || internalProp == null) return;
            try { Build(); built = true; Debug.Log("[KSPChatBridge] IVA MFD built in " + (internalProp.internalModel != null ? internalProp.internalModel.internalName : "?")); }
            catch (Exception ex) { Debug.LogError("[KSPChatBridge] IVA MFD build failed: " + ex); }
        }

        void Build()
        {
            if (hideModel) foreach (var r in internalProp.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            if (font == null)
            {
                font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New", "DejaVu Sans Mono", "Liberation Mono" }, FontPx);
                fontMat = new Material(font.material);
                fillMat = new Material(Shader.Find("Hidden/Internal-Colored")); fillMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); fillMat.SetInt("_Cull", 0); fillMat.SetInt("_ZWrite", 0);
            }
            rt = new RenderTexture(IvaLayout.W, IvaLayout.H, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear, useMipMap = false }; rt.Create();
            face = new GameObject("AICS_MFD_face"); face.layer = internalProp.gameObject.layer;
            face.transform.SetParent(internalProp.transform, false);
            var mesh = new Mesh(); float sx = flipX ? 1 : -1;   // prop face normal = local +Y; texture right = local -X, up = local -Z
            var v = new List<Vector3>(); var uv = new List<Vector2>();
            foreach (var c in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) })
            { v.Add(new Vector3(sx * (c.x - .5f) * width, lift, -(c.y - .5f) * height)); uv.Add(new Vector2(c.x, flipY ? 1 - c.y : c.y)); }
            mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }, 0);   // both sides
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            face.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = face.AddComponent<MeshRenderer>(); mr.material = FaceMaterial(rt);
            col = face.AddComponent<MeshCollider>(); col.sharedMesh = mesh;
            face.AddComponent<AicsIvaClick>().Owner = this;
        }

        static Material FaceMaterial(Texture t)
        {
            Shader sh = null; foreach (var n in new[] { "KSP/Alpha/Unlit Transparent", "Unlit/Texture", "KSP/Emissive/Diffuse" }) { sh = Shader.Find(n); if (sh != null) break; }
            var m = new Material(sh) { mainTexture = t };
            if (m.HasProperty("_Emissive")) { m.SetTexture("_Emissive", t); m.SetColor("_EmissiveColor", Color.white); }
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            return m;
        }

        void OnDestroy()
        {
            if (typingOwner == this) Unfocus(true);
            if (rt != null) { rt.Release(); Destroy(rt); }
        }

        static bool InIva { get { var cm = CameraManager.Instance; return cm != null && (cm.currentCameraMode == CameraManager.CameraMode.IVA || cm.currentCameraMode == CameraManager.CameraMode.Internal); } }

        void Update()
        {
            if (releaseFrame >= 0 && Time.frameCount >= releaseFrame && !Input.GetKey(KeyCode.Escape)) { InputLockManager.RemoveControlLock(LockId); releaseFrame = -1; }
            if (!built) return;
            if (focused != null && typingOwner == this)
            {
                if (!InIva) { Unfocus(true); }
                else
                {
                    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    string inp = Input.inputString; var res = focused.Feed(inp, shift, Input.GetKeyDown(KeyCode.Escape));
                    if (inp.Length > 0) dirty = true;
                    if (res == MfdField.Result.Submit) Submit();
                    else if (res == MfdField.Result.Cancel) { if (focused == planF) AicsMenu.PlanText = planF.Text.ToString(); Unfocus(false); }
                }
            }
            if (!InIva) return;
            if (!dirty && Time.realtimeSinceStartup < nextDraw) return;
            nextDraw = Time.realtimeSinceStartup + 1f / Mathf.Clamp(refreshHz, 2, 5); dirty = false;
            try { Render(); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] IVA MFD render: " + ex.Message); nextDraw = Time.realtimeSinceStartup + 5; }
        }

        void Focus(MfdField f)
        {
            if (typingOwner != null && typingOwner != this) typingOwner.Unfocus(false);
            focused = f; typingOwner = this; releaseFrame = -1;
            if (f == planF) planF.Set(AicsMenu.PlanText);
            InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS | ControlTypes.PAUSE, LockId);   // ship keys (space = stage!) and Esc-pause blocked while typing
            dirty = true;
        }
        void Unfocus(bool now)
        {
            focused = null; if (typingOwner == this) typingOwner = null;
            if (now) InputLockManager.RemoveControlLock(LockId); else releaseFrame = Time.frameCount + 2;   // keep Esc from opening the pause menu
            dirty = true;
        }
        void Submit()
        {
            if (focused == chatF) { string t = chatF.Text.ToString().Trim(); chatF.Set(""); if (t.Length > 0) ChatWindow.SubmitText(t); }
            else if (focused == planF) { AicsMenu.PlanText = planF.Text.ToString(); AicsMenu.OnKey("flightplan", "CHECK"); }
            Unfocus(false);
        }

        // ---------------- clicks ----------------
        internal void Click()
        {
            var cam = InternalCamera.Instance != null ? InternalCamera.Instance.GetComponent<Camera>() : null; if (cam == null) return;
            RaycastHit hit; if (!col.Raycast(cam.ScreenPointToRay(Input.mousePosition), out hit, 5f)) return;
            Vector2 t = hit.textureCoord; float x = t.x * IvaLayout.W, y = (flipY ? t.y : 1 - t.y) * IvaLayout.H;
            string id = IvaLayout.Hit(x, y); dirty = true;
            if (id == null) return;
            if (id == "S") { ScreenClick(y); return; }
            if (focused != null) Unfocus(false);
            Key(id);
        }

        bool Mj { get { return MechJebLink.Installed; } }
        MfdNav.Group Group { get { if (group == null || group == "chat") return null; foreach (var g in MfdNav.Groups(Mj)) if (g.Id == group) return g; return null; } }
        string Item { get { var g = Group; if (g == null) return group == "chat" ? "chat" : null; foreach (var i in g.Items) if (i.Id == item) return item; return g.Items[0].Id; } }

        string[] Left() { var g = Group; var l = new string[6]; if (group == "chat") return l; if (g == null) { var h = MfdNav.HomeKeys(Mj); Array.Copy(h, l, 6); return l; } var p = MfdNav.PageItems(g, page); for (int i = 0; i < p.Count; i++) l[i] = (p[i].Id == Item ? ">" : "") + p[i].Label; return l; }
        string[] Right() { var g = Group; if (group == "chat") return new string[6]; if (g == null) { var h = MfdNav.HomeKeys(Mj); var r = new string[6]; Array.Copy(h, 6, r, 0, 6); r[5] = r[5] ?? "CHAT"; return r; } return MfdNav.Right(Item); }
        string[] Bottom() { var g = Group; if (group == "chat") { var b = new string[7]; b[MfdNav.BackKey] = "BACK"; return b; } return MfdNav.Bottom(g == null, Item, page, g == null ? 1 : MfdNav.Pages(g)); }

        void Key(string id)
        {
            int k = id[1] - '0'; var g = Group; var gs = MfdNav.Groups(Mj);
            if (id == "H0") { group = "chat"; return; }
            if (group == null)
            {
                int gi = id[0] == 'L' ? k : id[0] == 'R' ? 6 + k : -1;
                if (gi >= 0 && gi < gs.Count) { group = gs[gi].Id; item = gs[gi].Items[0].Id; page = 0; }
                else if (id == "R5") group = "chat";
                return;
            }
            if (id[0] == 'L') { if (g == null) return; var p = MfdNav.PageItems(g, page); if (k < p.Count) item = p[k].Id; return; }
            string label = id[0] == 'R' ? Right()[k] : Bottom()[k];
            if (label == null) return;
            if (id[0] == 'B' && k == MfdNav.BackKey) { group = null; item = null; page = 0; Unfocus(true); return; }
            if (id[0] == 'B' && label == "PREV") { page--; item = MfdNav.PageItems(g, page)[0].Id; return; }
            if (id[0] == 'B' && label == "NEXT") { page++; item = MfdNav.PageItems(g, page)[0].Id; return; }
            if (Item == "map" && (label == "ZOOM+" || label == "ZOOM-")) { span = MapReveal.Zoom(span, label == "ZOOM+" ? .5 : 2); return; }
            AicsMenu.OnKey(Item, label);
        }

        // text-field rows on the screen (rows counted from the top of the screen box)
        int Cols { get { return (int)((IvaLayout.Screen.Wd - 12) / CellW(18)); } }
        int Rows { get { return (int)((IvaLayout.Screen.Ht - 8) / 20); } }
        void ScreenClick(float y)
        {
            int row = (int)((y - IvaLayout.Screen.Y - 4) / 20);
            if (Item == "chat" && row >= Rows - 3) { Focus(chatF); return; }
            if (Item == "flightplan" && row >= 2 && row < Rows - 2) { Focus(planF); return; }
            if (focused != null) { if (focused == planF) AicsMenu.PlanText = planF.Text.ToString(); Unfocus(false); }
        }

        // ---------------- page text ----------------
        List<string> ScreenLines(int cols, int rows)
        {
            var o = new List<string>(); string it = Item; string caret = (Time.frameCount / 15) % 2 == 0 ? "_" : " ";
            Func<string, List<string>> W = s => IvaLayout.Wrap(s, cols);
            if (group == null)
            {
                o.Add("AICS CONTROL"); o.AddRange(W(AicsRpmPages.StatusText().Replace("AICS AUTOPILOT\n\n", "")));
                var v = FlightGlobals.ActiveVessel;
                if (v != null) { o.Add(""); o.AddRange(W(v.vesselName)); o.Add("ALT " + Math.Round(v.altitude) + "  SPD " + Math.Round(v.srfSpeed) + "  HDG " + Math.Round(FlightGlobals.ship_heading).ToString("000")); }
                o.Add(""); o.AddRange(W("Soft keys pick a group. CHAT: talk to the crew. BACK is always bottom-left."));
                return o;
            }
            switch (it)
            {
                case "chat":
                {
                    var hist = new List<string>(); foreach (var l in ChatWindow.Recent(30)) hist.AddRange(W(l));
                    string input = "> " + chatF.Text + (focused == chatF ? caret : "");
                    var inp = W(input); int keep = Math.Max(1, rows - 1 - Math.Min(2, inp.Count));
                    o.Add("CHAT" + (focused == chatF ? "  (typing: Enter sends, Esc stops)" : "  (click the bottom line to type)"));
                    o.AddRange(hist.GetRange(Math.Max(0, hist.Count - keep + 1), Math.Min(hist.Count, keep - 1)));
                    while (o.Count < rows - Math.Min(2, inp.Count)) o.Add("");
                    o.AddRange(inp.GetRange(Math.Max(0, inp.Count - 2), Math.Min(2, inp.Count)));
                    return o;
                }
                case "flightplan":
                {
                    o.Add("FLIGHT PLAN" + (focused == planF ? "  (Enter checks, Shift+Enter = new line, Esc)" : "  (click to edit)"));
                    o.AddRange(W(NativeFlightController.PlanStatus)); while (o.Count < 2) o.Add("");
                    string text = focused == planF ? planF.Text + caret : AicsMenu.PlanText;
                    var body = W(text.Length == 0 && focused != planF ? "(empty - click here and type, one step per line)" : text);
                    int room = rows - 4; o.AddRange(body.GetRange(Math.Max(0, body.Count - room), Math.Min(body.Count, room)));
                    while (o.Count < rows - 1) o.Add("");
                    o.Add("CHECK / FLY / STOP: right keys");
                    return o;
                }
                case "map": o.AddRange(AicsRpmPages.MapText(cols, rows - 1, span).Split('\n')); o.Add("span " + (span / 1000).ToString("0") + " km"); return o;
                case "ils": o.AddRange(AicsRpmPages.IlsText(cols, rows).Split('\n')); return o;
                case "mjatt": case "mjguide": o.Add(it == "mjatt" ? "MECHJEB SMARTASS" : "MECHJEB GUIDANCE"); o.AddRange(W(NativeFlightController.MjStatusNow)); return o;
                case "chart": o.Add("CHART"); o.AddRange(W("Active runway " + MapWindow.Title + ". Fix editing needs the mouse map: use the outside MFD (Alt+J) MAP > CHART. Here: RWY < / >, VARIANT, SAVE.")); return o;
            }
            var gi = Group; string label = it; if (gi != null) foreach (var x in gi.Items) if (x.Id == it) label = x.Label;
            o.Add(label); o.AddRange(W(AicsRpmPages.StatusText().Replace("AICS AUTOPILOT\n\n", "")));
            o.Add(""); o.AddRange(W("Keys on the right run this page's actions. The full panel (fields, sliders) is on the outside MFD: Alt+J."));
            return o;
        }

        // ---------------- drawing ----------------
        static float CellW(int px) { return Mathf.Ceil(px * .6f); }
        readonly List<KeyValuePair<Rect, Color>> fills = new List<KeyValuePair<Rect, Color>>();
        struct Txt { internal string S; internal float X, Y; internal int Px; internal Color C; internal bool Center; internal float Wd; }
        readonly List<Txt> texts = new List<Txt>();

        void Render()
        {
            fills.Clear(); texts.Clear();
            var s = IvaLayout.Screen;
            Fill(new Rect(s.X - 3, s.Y - 3, s.Wd + 6, s.Ht + 6), new Color(.05f, .05f, .05f)); Fill(new Rect(s.X, s.Y, s.Wd, s.Ht), Color.black);
            for (float x = s.X + 40; x < s.X + s.Wd; x += 40) Fill(new Rect(x, s.Y, 1, s.Ht), Dim);
            for (float y = s.Y + 40; y < s.Y + s.Ht; y += 40) Fill(new Rect(s.X, y, s.Wd, 1), Dim);
            var g = Group; string title = "AICS " + (group == null ? "HOME" : group == "chat" ? "CHAT" : g.Label + (Item != null ? " / " + Item.ToUpperInvariant() : ""));
            Text(title, IvaLayout.M + 4, 6, 20, Phos);
            string[] L = Left(), R = Right(), B = Bottom();
            foreach (var k in IvaLayout.Keys())
            {
                string lab = k.Id == "H0" ? "CHAT" : k.Id[0] == 'L' ? L[k.Id[1] - '0'] : k.Id[0] == 'R' ? R[k.Id[1] - '0'] : B[k.Id[1] - '0'];
                Fill(new Rect(k.X, k.Y, k.Wd, k.Ht), KeyEdge); Fill(new Rect(k.X + 2, k.Y + 2, k.Wd - 4, k.Ht - 4), string.IsNullOrEmpty(lab) ? Bezel : KeyC);
                if (!string.IsNullOrEmpty(lab)) { int px = lab.Length > 9 ? 13 : 16; texts.Add(new Txt { S = lab, X = k.X, Y = k.Y + k.Ht / 2 - px * .55f, Px = px, C = Label, Center = true, Wd = k.Wd }); }
            }
            int cols = Cols, rows = Rows; var lines = ScreenLines(cols, rows);
            for (int i = 0; i < Math.Min(rows, lines.Count); i++) Text(lines[i].Length > cols ? lines[i].Substring(0, cols) : lines[i], s.X + 6, s.Y + 4 + i * 20, 18, Phos);
            if (focused != null) Fill(new Rect(s.X, s.Y + s.Ht - 3, s.Wd, 3), Phos);
            Blit();
        }
        void Fill(Rect r, Color c) { fills.Add(new KeyValuePair<Rect, Color>(r, c)); }
        void Text(string str, float x, float y, int px, Color c) { texts.Add(new Txt { S = str, X = x, Y = y, Px = px, C = c }); }

        void Blit()
        {
            var prev = RenderTexture.active; RenderTexture.active = rt;
            GL.PushMatrix(); GL.LoadPixelMatrix(0, IvaLayout.W, IvaLayout.H, 0); GL.Clear(true, true, Bezel);
            fillMat.SetPass(0); GL.Begin(GL.QUADS);
            foreach (var f in fills) { var r = f.Key; GL.Color(f.Value); GL.Vertex3(r.xMin, r.yMin, 0); GL.Vertex3(r.xMax, r.yMin, 0); GL.Vertex3(r.xMax, r.yMax, 0); GL.Vertex3(r.xMin, r.yMax, 0); }
            GL.End();
            var sizes = new HashSet<int>(); foreach (var t in texts) sizes.Add(t.Px);
            foreach (int px in sizes) { var sb = new System.Text.StringBuilder(); foreach (var t in texts) if (t.Px == px) sb.Append(t.S); font.RequestCharactersInTexture(sb.ToString(), px); }
            fontMat.mainTexture = font.material.mainTexture; fontMat.SetPass(0); GL.Begin(GL.QUADS);
            foreach (var t in texts)
            {
                float cw = CellW(t.Px), x = t.Center ? t.X + Mathf.Max(2, (t.Wd - cw * t.S.Length) / 2) : t.X, baseY = t.Y + t.Px * .8f;
                if (t.Center && cw * t.S.Length > t.Wd - 4) cw = (t.Wd - 4) / t.S.Length;   // squeeze long key labels
                GL.Color(t.C);
                foreach (char ch in t.S)
                {
                    CharacterInfo ci;
                    if (ch != ' ' && font.GetCharacterInfo(ch, out ci, t.Px))
                    {
                        float x0 = x + ci.minX, x1 = x + ci.maxX, y0 = baseY - ci.maxY, y1 = baseY - ci.minY;
                        GL.TexCoord(ci.uvTopLeft); GL.Vertex3(x0, y0, 0); GL.TexCoord(ci.uvTopRight); GL.Vertex3(x1, y0, 0);
                        GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x1, y1, 0); GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(x0, y1, 0);
                    }
                    x += cw;
                }
            }
            GL.End(); GL.PopMatrix(); RenderTexture.active = prev;
        }
    }

    /// <summary>Mouse click on the IVA MFD face (Unity OnMouseDown via the internal camera, like RPM's buttons).</summary>
    public class AicsIvaClick : MonoBehaviour
    {
        internal AicsIvaMfd Owner;
        void OnMouseDown() { if (Owner != null) Owner.Click(); }
    }
}