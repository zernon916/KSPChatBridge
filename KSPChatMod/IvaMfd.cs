// Native IVA AICS MFD (Phase 2, Luke 4:41 PM): a procedural cockpit screen prop. The face (bezel, 26 soft keys, screen) is
// recorded into an RtCanvas (MfdCanvas.cs) and flushed into a RenderTexture in one GL pass at 2-5 Hz, then mapped on a quad
// built in code (no Unity/Blender assets); one MeshCollider on the face hit-tests the keys and the screen by texture
// coordinate (IvaLayout). Same navigation as the outside MFD (MfdNav: HOME groups, paged items, BACK fixed bottom-left), and
// MAP / CHART / ILS draw the same real graphics as the outside MFD (MapWindow.DrawIva through that shared canvas, no mouse -
// the soft keys drive it); every other page is monospace text. Screen text fields (CHAT input, flight plan editor) take the
// keyboard while focused, with ship controls locked; Enter submits, Esc unfocuses.
// Added to stock cockpits by AICS_IVA.cfg (ModuleManager). RasterPropMonitor pages (AICS_RPM.cfg) are unaffected.
using System;using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    public class AicsIvaMfd : InternalModule
    {
        [KSPField] public float width = .20f, height = .1625f, lift = .008f;
        [KSPField] public bool flipX = false, flipY = false, hideModel = true;
        [KSPField] public float refreshHz = 4;
        [KSPField] public string screenTransform = "FlatMon40x10Screen";
        // Luke 6:40 PM: RPM's FlatMon40x10Screen is only the inner text rect; the visible glass opening is larger and its centre sits
        // up/right of it. Scale + offset (offset in units of the inner rect size, + = right / up) fill the whole glass.
        [KSPField] public float glassScaleX = 1.30f, glassScaleY = 1.12f, glassOffsetX = .10f, glassOffsetY = .07f;
        /// <summary>Oct 12 (Luke): global IVA nudge for every AICS screen (fraction of the screen size, + = right / up; scale multiplier), tunable without a rebuild:
        /// PluginData/iva_screen_offset.txt "offsetX offsetY scale" (default -0.03 0.03 1.04 = a bit left and up, slightly larger to cover RPM's glass).</summary>
        internal static float NudgeX = -.03f, NudgeY = .03f, NudgeScale = 1.04f; static bool nudgeRead;
        static void ReadNudge()
        {
            if (nudgeRead) return; nudgeRead = true; string p = Path.Combine(AicsCore.PluginDataDirectory, "iva_screen_offset.txt");
            try
            {
                if (!File.Exists(p)) { File.WriteAllText(p, "-0.03 0.03 1.04\n# offsetX offsetY scale for every AICS IVA screen: fraction of the screen, + = right / up; reloads on the next IVA load\n"); return; }
                var f = File.ReadAllLines(p)[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (f.Length > 0) NudgeX = float.Parse(f[0], ci); if (f.Length > 1) NudgeY = float.Parse(f[1], ci); if (f.Length > 2) NudgeScale = Mathf.Clamp(float.Parse(f[2], ci), .5f, 2f);
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] iva_screen_offset.txt unreadable: " + ex.Message); }
        }
        [KSPField] public bool rpmHost = false, autoOrient = false, autoSize = false;   // set on RasterPropMonitorBasicMFD by AICS_IVA.cfg
        internal bool Active { get { return built; } }
        /// <summary>Default page per screen, in prop order: MAP / ILS / AP / CHART / COMMS, then repeat.</summary>
        internal static string DefaultPage(int index) { return MfdNav.DefaultPage(index); }
        /// <summary>Setting: leave RPM's own MFD screens in place (no AICS screen on them).</summary>
        internal static bool KeepRpm
        {
            get { if (keepRpm == null) { try { keepRpm = File.Exists(KeepPath) && File.ReadAllText(KeepPath).Trim() == "1"; } catch (Exception) { keepRpm = false; } } return keepRpm.Value; }
            set { keepRpm = value; try { File.WriteAllText(KeepPath, value ? "1" : "0"); } catch (Exception) { } }
        }
        static bool? keepRpm; static string KeepPath { get { return Path.Combine(AicsCore.PluginDataDirectory, "rpm_keep_screens.txt"); } }
        void OpenItem(string id)
        {
            foreach (var g in MfdNav.Groups(Mj)) for (int pg = 0; pg < MfdNav.Pages(g); pg++) foreach (var it in MfdNav.PageItems(g, pg)) if (it.Id == id) { group = g.Id; item = id; page = pg; return; }
        }

        const string LockId = "AICS_IvaTyping";
        static AicsIvaMfd typingOwner; static int releaseFrame = -1;
        static readonly List<AicsIvaMfd> live = new List<AicsIvaMfd>();   // every built screen: who is showing which page
        /// <summary>A built screen is showing MAP / CHART / ILS: MapWindow then keeps building its terrain bitmap even with the
        /// outside MFD hidden, so those pages draw the real graphics instead of a text page.</summary>
        internal static bool WantsMap { get { foreach (var s in live) if (s.built && SharedPage(s.Item)) return true; return false; } }
        /// <summary>The pages drawn by the shared MAP / CHART / ILS code (MapWindow.DrawPages) instead of the IVA text page.</summary>
        static bool SharedPage(string it) { return it == "map" || it == "chart" || it == "ils"; }

        RenderTexture rt; RtCanvas cv; GameObject face; MeshCollider col; float nextDraw; bool dirty = true, built;   // cv: this screen's frame, flushed into rt
        string group, item; int page; double span = 20000;
        readonly MfdField chatF = new MfdField(false, 400), planF = new MfdField(true, 4000); MfdField focused;
        static readonly Color Bezel = new Color(.19f, .2f, .21f), KeyC = new Color(.27f, .28f, .3f), KeyEdge = new Color(.08f, .08f, .09f), Phos = new Color(.35f, 1f, .45f), Dim = new Color(.1f, .3f, .14f), Label = new Color(.88f, .92f, .88f);

        void Start()
        {
            if (!HighLogic.LoadedSceneIsFlight || internalProp == null) return;
            if (rpmHost && KeepRpm) { Debug.Log("[KSPChatBridge] IVA MFD: keeping RPM screen (setting)"); return; }
            try { Build(); built = true; live.Add(this); Debug.Log("[KSPChatBridge] IVA MFD built in " + (internalProp.internalModel != null ? internalProp.internalModel.internalName : "?")); }
            catch (Exception ex) { Debug.LogError("[KSPChatBridge] IVA MFD build failed: " + ex); }
        }

        void Build()
        {
            // The host monitor model must be GONE, not just hidden (Luke 5:33 PM: the stock DOCKING MODE screen stayed on top of ours).
            Bounds hb = new Bounds(); bool hasB = false;
            foreach (var r in internalProp.GetComponentsInChildren<Renderer>(true))
            {
                var b = r.bounds; foreach (var corner in new[] { b.min, b.max, new Vector3(b.min.x, b.max.y, b.min.z), new Vector3(b.max.x, b.min.y, b.max.z) })
                { var lp = internalProp.transform.InverseTransformPoint(corner); if (!hasB) { hb = new Bounds(lp, Vector3.zero); hasB = true; } else hb.Encapsulate(lp); }
            }
            if (rpmHost)
            {   // runtime hide of RPM's screen: renderers + colliders off, RPM modules stopped (Luke 6:17 PM)
                foreach (var r in internalProp.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (var cl in internalProp.GetComponentsInChildren<Collider>(true)) cl.enabled = false;
                foreach (var m in new List<InternalModule>(internalProp.internalModules)) if (!(m is AicsIvaMfd) && !(m is AicsRpmPages)) { m.enabled = false; internalProp.internalModules.Remove(m); Destroy(m); }   // RPM's modules gone: KSP calls OnUpdate even when disabled
            }
            else if (hideModel) { var kids = new List<GameObject>(); foreach (Transform k in internalProp.transform) kids.Add(k.gameObject); foreach (var k in kids) { k.SetActive(false); Destroy(k); } }
            if (rpmHost || autoOrient)
            {
                int idx = 0; if (internalProp.internalModel != null) foreach (var pr in internalProp.internalModel.props) { if (pr == internalProp) break; foreach (var x in pr.internalModules) if (x is AicsIvaMfd) { idx++; break; } }
                var im = internalProp.internalModel; Transform st = im != null && im.seats != null && im.seats.Count > 0 ? im.seats[0].seatTransform : null;
                if (st != null)
                {   // Oct 12: default page by left->right position seen from the pilot seat (prop order put every screen on the same few pages)
                    int n = 0, rank = 0; float me = Vector3.Dot(internalProp.transform.position - st.position, st.right);
                    foreach (var pr in im.props) { bool has = false; foreach (var x in pr.internalModules) if (x is AicsIvaMfd) { has = true; break; } if (!has) continue; n++; if (pr != internalProp && Vector3.Dot(pr.transform.position - st.position, st.right) < me) rank++; }
                    OpenItem(MfdNav.PositionPage(rank, n));
                }
                else OpenItem(DefaultPage(idx)); Debug.Log("[KSPChatBridge] IVA MFD on RPM screen #" + idx + " -> " + item);
            }
            MfdFont.Ensure();   // the shared face + unlit materials (the chrome and the shared MAP / CHART / ILS page draw alike)
            rt = new RenderTexture(IvaLayout.W, IvaLayout.H, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear, useMipMap = false }; rt.Create();
            cv = new RtCanvas(rt, IvaLayout.W, IvaLayout.H);   // the frame is recorded, then flushed into rt in one GL pass
            face = new GameObject("AICS_MFD_face"); face.layer = internalProp.gameObject.layer == 0 ? 20 : internalProp.gameObject.layer;   // internal-space layer (16/20)
            face.transform.SetParent(internalProp.transform, false);
            Transform seat0 = internalProp.internalModel != null && internalProp.internalModel.seats != null && internalProp.internalModel.seats.Count > 0 ? internalProp.internalModel.seats[0].seatTransform : null;
            Vector3 nL = Vector3.up, uL = Vector3.back;
            if (autoOrient && seat0 != null)
            {   // RPM's screen faces along a different local axis (the 90 deg error): pick the prop axis that faces the pilot, up = closest to the pilot's up
                var axes = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
                Vector3 toSeat = (seat0.position - internalProp.transform.position).normalized; float best = -2;
                foreach (var a in axes) { float d = Vector3.Dot(internalProp.transform.TransformDirection(a), toSeat); if (d > best) { best = d; nL = a; } }
                best = -2; foreach (var a in axes) { if (Mathf.Abs(Vector3.Dot(a, nL)) > .5f) continue; float d = Vector3.Dot(internalProp.transform.TransformDirection(a), seat0.up); if (d > best) { best = d; uL = a; } }
                face.transform.localRotation = Quaternion.LookRotation(-uL, nL);   // quad: normal +Y, texture up -Z
            }
            if (autoSize && hasB)
            {
                Vector3 rL = Vector3.Cross(nL, uL); float w = Mathf.Abs(Vector3.Dot(hb.size, rL)), h = Mathf.Abs(Vector3.Dot(hb.size, uL));
                if (w > .02f && h > .02f) { width = w * .92f; height = h * .92f; }
                face.transform.localPosition = hb.center + nL * (Mathf.Abs(Vector3.Dot(hb.extents, nL)) - lift * .5f);
            }
            if (rpmHost)
            {   // Luke 6:29 PM: sit FLUSH on RPM's own screen surface (its screenTransform mesh), not on the prop's bounds
                Transform scr = FindDeep(internalProp.transform, screenTransform); MeshFilter mf = scr != null ? scr.GetComponent<MeshFilter>() : null;
                if (mf != null && mf.sharedMesh != null && seat0 != null)
                {
                    var b = mf.sharedMesh.bounds; var axes = new[] { Vector3.right, Vector3.up, Vector3.forward }; int ni = 0;
                    for (int i = 1; i < 3; i++) if (Mathf.Abs(Vector3.Dot(b.size, axes[i])) < Mathf.Abs(Vector3.Dot(b.size, axes[ni]))) ni = i;
                    Vector3 n = axes[ni]; if (Vector3.Dot(scr.TransformDirection(n), seat0.position - scr.TransformPoint(b.center)) < 0) n = -n;
                    Vector3 u = Vector3.zero; float best = -2;
                    for (int i = 0; i < 3; i++) { if (i == ni) continue; foreach (var sg in new[] { 1f, -1f }) { float d = Vector3.Dot(scr.TransformDirection(axes[i] * sg), seat0.up); if (d > best) { best = d; u = axes[i] * sg; } } }
                    Vector3 rgt = Vector3.Cross(u, n);
                    face.transform.SetParent(scr, false);
                    face.transform.localRotation = Quaternion.LookRotation(-u, n);
                    width = Mathf.Abs(Vector3.Dot(b.size, rgt)); height = Mathf.Abs(Vector3.Dot(b.size, u));
                    float nScale = Mathf.Max(1e-4f, scr.TransformVector(n).magnitude); lift = 0;
                    Vector3 shift = -rgt * (glassOffsetX * width) + u * (glassOffsetY * height);   // texture right = face -X = -rgt
                    ReadNudge(); shift += -rgt * (NudgeX * width) + u * (NudgeY * height);   // Oct 12: global left/up nudge (iva_screen_offset.txt)
                    width *= glassScaleX * NudgeScale; height *= glassScaleY * NudgeScale;
                    face.transform.localPosition = b.center + shift + n * (Mathf.Abs(Vector3.Dot(b.extents, n)) + .0015f / nScale);   // 1.5 mm proud of RPM's glass
                    Debug.Log("[KSPChatBridge] IVA MFD flush on " + screenTransform + ": local size " + width.ToString("F4") + "x" + height.ToString("F4") + " n=" + n + " up=" + u);
                }
                else Debug.Log("[KSPChatBridge] IVA MFD: RPM screen transform '" + screenTransform + "' not found; using prop bounds");
            }
            var mesh = new Mesh(); float sx = flipX ? 1 : -1;   // prop face normal = local +Y; texture right = local -X, up = local -Z
            var v = new List<Vector3>(); var uv = new List<Vector2>();
            foreach (var c in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) })
            { v.Add(new Vector3(sx * (c.x - .5f) * width, lift, -(c.y - .5f) * height)); uv.Add(new Vector2(c.x, flipY ? 1 - c.y : c.y)); }
            mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }, 0);   // both sides
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            face.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = face.AddComponent<MeshRenderer>(); mr.material = FaceMaterial(rt);
            col = face.AddComponent<MeshCollider>(); col.sharedMesh = mesh;
            // Face the pilot: if the seat is behind the face normal, turn the quad 180 deg about its up axis (rigid, text stays readable).
            Transform seat = internalProp.internalModel != null && internalProp.internalModel.seats != null && internalProp.internalModel.seats.Count > 0 ? internalProp.internalModel.seats[0].seatTransform : null;
            if (!autoOrient && seat != null && Vector3.Dot(face.transform.up, seat.position - face.transform.position) < 0) face.transform.localRotation = Quaternion.Euler(0, 0, 180);
            Debug.Log("[KSPChatBridge] IVA MFD face: pos=" + face.transform.position.ToString("F4") + " normal=" + face.transform.up.ToString("F3") + " size=" + width + "x" + height + " lossyScale=" + face.transform.lossyScale.ToString("F2")
                + " layer=" + face.layer + " shader=" + mr.material.shader.name + " seat=" + (seat == null ? "none" : seat.position.ToString("F3") + " dot=" + Vector3.Dot(face.transform.up, seat.position - face.transform.position).ToString("F3")));
            face.AddComponent<AicsIvaClick>().Owner = this;
        }

        static Material FaceMaterial(Texture t)
        {
            Shader sh = null; foreach (var n in new[] { "KSP/Unlit", "Unlit/Texture", "KSP/Emissive/Diffuse" }) { sh = Shader.Find(n); if (sh != null) break; }
            var m = new Material(sh) { mainTexture = t };
            if (m.HasProperty("_Emissive")) { m.SetTexture("_Emissive", t); m.SetColor("_EmissiveColor", Color.white); }
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            return m;
        }

        void OnDestroy()
        {
            live.Remove(this);
            if (typingOwner == this) Unfocus(true);
            if (rt != null) { rt.Release(); Destroy(rt); }
        }

        static bool InIva { get { var cm = CameraManager.Instance; return cm != null && (cm.currentCameraMode == CameraManager.CameraMode.IVA || cm.currentCameraMode == CameraManager.CameraMode.Internal); } }

        static Transform FindDeep(Transform t, string name) { if (t.name == name) return t; foreach (Transform k in t) { var r = FindDeep(k, name); if (r != null) return r; } return null; }
        float nextHide;
        /// <summary>RPM keeps re-enabling its screen renderer; keep every host renderer except ours off.</summary>
        void LateUpdate()
        {
            if (!built || !rpmHost || Time.realtimeSinceStartup < nextHide) return; nextHide = Time.realtimeSinceStartup + .5f;
            foreach (var r in internalProp.GetComponentsInChildren<Renderer>(true)) if (face == null || r.gameObject != face) r.enabled = false;
        }
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
        MfdNav.Group Group { get { if (group == null) return null; foreach (var g in MfdNav.Groups(Mj)) if (g.Id == group) return g; return null; } }
        string Item { get { var g = Group; if (g == null) return null; foreach (var i in g.Items) if (i.Id == item) return item; return g.Items[0].Id; } }
        bool Comms { get { var g = Group; return g != null && g.Id == "comms"; } }

        string[] Left() { var g = Group; var l = new string[MfdNav.Side]; if (g == null) { Array.Copy(MfdNav.HomeKeys(Mj), l, MfdNav.Side); return l; } var p = MfdNav.PageItems(g, page); for (int i = 0; i < p.Count; i++) l[i] = (p[i].Id == Item ? ">" : "") + p[i].Label; return l; }
        string[] Right() { var g = Group; if (g == null) { var r = new string[MfdNav.Side]; Array.Copy(MfdNav.HomeKeys(Mj), MfdNav.Side, r, 0, MfdNav.Side); return r; } return MfdNav.Right(Item); }
        string[] Bottom() { var g = Group; return MfdNav.Bottom(g == null, Item, page, g == null ? 1 : MfdNav.Pages(g)); }

        void OpenGroup(string id) { foreach (var g in MfdNav.Groups(Mj)) if (g.Id == id) { group = id; item = g.Items[0].Id; page = 0; } }
        void Key(string id)
        {
            int k = id[1] - '0'; var g = Group; var gs = MfdNav.Groups(Mj);
            if (id == "H0") { OpenGroup("comms"); return; }
            if (id == "A0") { OpenGroup("sys"); item = "alarm"; return; }
            if (id[0] == 'T') { if (g != null) { var tl = MfdNav.Top(Item); if (k < tl.Length && tl[k] != null) AicsMenu.OnKey(Item, tl[k]); } return; }
            if (g == null)
            {
                int gi = id[0] == 'L' ? k : id[0] == 'R' ? MfdNav.Side + k : -1;
                if (gi >= 0 && gi < gs.Count) OpenGroup(gs[gi].Id);
                return;
            }
            if (id[0] == 'L') { var p = MfdNav.PageItems(g, page); if (k < p.Count) item = p[k].Id; return; }
            string label = id[0] == 'R' ? Right()[k] : Bottom()[k];
            if (label == null) return;
            if (id[0] == 'B' && k == MfdNav.BackKey) { group = null; item = null; page = 0; Unfocus(true); return; }
            if (id[0] == 'B' && label == "PREV") { page--; item = MfdNav.PageItems(g, page)[0].Id; return; }
            if (id[0] == 'B' && label == "NEXT") { page++; item = MfdNav.PageItems(g, page)[0].Id; return; }
            if (Item == "map" && (label == "ZOOM+" || label == "ZOOM-"))
            {   // the shared MAP page owns the span (MapWindow.ViewSpan): the same zoom the outside MFD uses
                if (MapWindow.HasMap) { if (label == "ZOOM+") MapWindow.ZoomIn(); else MapWindow.ZoomOut(); }
                else span = MapReveal.Zoom(span, label == "ZOOM+" ? .5 : 2);
                return;
            }
            if (Comms && label == "SEND") { focused = chatF; Submit(); return; }
            if (Comms && label == "CLEAR") { chatF.Set(""); return; }
            if (MfdNav.LearnKeyAction(Item, label) == "open") { OpenItem("learn"); return; }
            Pressing = true; try { AicsMenu.OnKey(Item, label); } finally { Pressing = false; }
        }
        internal static bool Pressing;

        // text-field rows on the screen (rows counted from the top of the screen box)
        int Cols { get { return (int)((IvaLayout.Screen.Wd - 12) / CellW(TextPx)); } }
        const int TextPx = 22;   // Oct 12 (Luke): larger IVA text, fewer lines per page (was 16)
        int Rows { get { return (int)((IvaLayout.Screen.Ht - 8) / (TextPx + 3)); } }
        void ScreenClick(float y)
        {
            int row = (int)((y - IvaLayout.Screen.Y - 4) / (TextPx + 3));
            if (Comms && row >= Rows - 3) { Focus(chatF); return; }
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
                o.AddRange(W(AicsRpmPages.StatusText().Replace("AICS AUTOPILOT\n\n", "")));
                if (StatusWindow.AlarmLevel != "") o.AddRange(W("ALARM " + StatusWindow.AlarmText));
                if (MultiplayerCheck.Warning != null) o.AddRange(W(MultiplayerCheck.Warning));
                var v = FlightGlobals.ActiveVessel;
                if (v != null) { o.Add(""); o.AddRange(W(v.vesselName)); o.Add("ALT " + Math.Round(v.altitude) + "  SPD " + Math.Round(v.srfSpeed) + "  HDG " + Math.Round(FlightGlobals.ship_heading).ToString("000")); }
                o.Add(""); o.AddRange(W("Soft keys pick a group. COMMS (top right): talk to the crew. BACK is always bottom-left."));
                return o;
            }
            switch (it)
            {
                case "intercom": case "system": case "pilot": case "all":
                {
                    var hist = new List<string>(); foreach (var l in ChatWindow.Recent(60)) if (MfdNav.ChatShows(it, l)) hist.AddRange(W(l));
                    string input = "> " + chatF.Text + (focused == chatF ? caret : "");
                    var inp = W(input); int keep = Math.Max(1, rows - 1 - Math.Min(2, inp.Count));
                    o.Add(focused == chatF ? "(typing: Enter sends, Esc stops)" : "(click the bottom line to type)");
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
                case "ils": o.AddRange(AicsRpmPages.IlsText(Math.Max(10, cols / 2), rows).Split('\n')); { string fl = NativeFlightController.FlapLine; if (fl.Length > 0) o.Add(fl); } return o;   // data block right of the needles
                case "alarm": foreach (var l in StatusWindow.AlarmLines()) o.AddRange(W(l)); return o;
                case "about": { var al = new List<string>(); foreach (var l in NativeFlightController.AboutLines()) al.AddRange(W(l)); int room = Math.Max(3, rows - 2), off = MfdNav.AboutWindow(al, room); if (off > 0) o.Add("  ^ more (SCROLL UP)"); o.AddRange(al.GetRange(off, Math.Min(room, al.Count - off))); if (off + room < al.Count) o.Add("  v more (SCROLL DN)"); return o; }
                case "learn": { var al = new List<string>(); foreach (var l in NativeFlightController.LearnLines()) al.AddRange(W(l)); int room = Math.Max(3, rows - 2), off = MfdNav.AboutWindow(al, room); if (off > 0) o.Add("  ^ more (SCROLL UP)"); o.AddRange(al.GetRange(off, Math.Min(room, al.Count - off))); if (off + room < al.Count) o.Add("  v more (SCROLL DN)"); return o; }
                case "systems": o.Add(StatusWindow.RotorsTab ? "ROTORS" : "OVERVIEW"); foreach (var r in StatusWindow.SystemRows()) o.AddRange(W((r[0] == "fail" ? "! " : r[0] == "caution" ? "* " : "  ") + r[1] + ": " + r[2])); return o;
                case "status": foreach (var r in StatusWindow.StatusRows()) o.AddRange(W(r[0] + ": " + r[1])); return o;
                case "mjatt": case "mjguide": o.Add(it == "mjatt" ? "MECHJEB SMARTASS" : "MECHJEB GUIDANCE"); o.AddRange(W(NativeFlightController.MjStatusNow)); return o;
                case "chart": o.Add("CHART"); o.AddRange(W("Active runway " + MapWindow.Title + ". Fix editing needs the mouse map: use the outside MFD (Alt+J) MAP > CHART. Here: RWY < / >, VARIANT, SAVE.")); return o;
            }
            var gi = Group; string label = it; if (gi != null) foreach (var x in gi.Items) if (x.Id == it) label = x.Label;
            o.Add(label); o.AddRange(W(AicsRpmPages.StatusText().Replace("AICS AUTOPILOT\n\n", "")));
            o.Add(""); o.AddRange(W("Keys on the right run this page's actions. The full panel (fields, sliders) is on the outside MFD: Alt+J."));
            return o;
        }

        // ---------------- drawing ----------------
        static float CellW(int px) { return MfdFont.CellW(px); }   // the shared monospace cell (the text pages lay columns out on it)

        void Render()
        {
            if (cv == null) return;
            cv.Begin(Bezel);   // the whole frame (bezel, keys, screen) is recorded, then flushed in one GL pass
            var s = IvaLayout.Screen;
            Fill(new Rect(s.X - 3, s.Y - 3, s.Wd + 6, s.Ht + 6), new Color(.05f, .05f, .05f)); Fill(new Rect(s.X, s.Y, s.Wd, s.Ht), Color.black);
            for (float x = s.X + 40; x < s.X + s.Wd; x += 40) Fill(new Rect(x, s.Y, 1, s.Ht), Dim);
            for (float y = s.Y + 40; y < s.Y + s.Ht; y += 40) Fill(new Rect(s.X, y, s.Wd, 1), Dim);
            // engraved title plate
            var g = Group; var pl = IvaLayout.Plate(IvaLayout.W, IvaLayout.H);
            string title = "AICS " + (group == null ? "HOME" : g.Label + (Item != null ? " / " + Item.ToUpperInvariant() : ""));
            Fill(new Rect(pl.X - 2, pl.Y - 2, pl.Wd + 4, pl.Ht + 4), new Color(.06f, .06f, .07f)); Fill(new Rect(pl.X, pl.Y, pl.Wd, pl.Ht), new Color(.56f, .57f, .59f));
            int tp = Math.Min(18, (int)((pl.Wd - 8) / (.6f * Math.Max(1, title.Length))));
            float tx = pl.X + (pl.Wd - CellW(tp) * title.Length) / 2, ty = pl.Y + (pl.Ht - tp) / 2;
            Text(title, tx + 1, ty + 1, tp, new Color(.85f, .86f, .88f)); Text(title, tx, ty, tp, new Color(.13f, .13f, .14f));
            string[] L = Left(), R = Right(), B = Bottom(); string lvl = StatusWindow.AlarmLevel; bool lamp = lvl != "" && (!StatusWindow.AlarmUnacked || (Time.realtimeSinceStartup % 1f) < .5f);
            foreach (var k in IvaLayout.Keys())
            {
                int n = k.Id[1] - '0';
                string lab = k.Id == "H0" ? "COMMS" : k.Id == "A0" ? (lvl == "warning" ? "MASTER WARNING" : lvl == "caution" ? "MASTER CAUTION" : "MASTER ALARM") : k.Id[0] == 'T' ? (Group == null ? null : MfdNav.Top(Item)[n]) : k.Id[0] == 'L' ? L[n] : k.Id[0] == 'R' ? R[n] : B[n];
                Color bg = k.Id == "A0" ? (!lamp ? new Color(.16f, .12f, .12f) : lvl == "warning" ? new Color(.85f, .1f, .08f) : new Color(.95f, .62f, .05f)) : string.IsNullOrEmpty(lab) ? Bezel : KeyC;
                Fill(new Rect(k.X, k.Y, k.Wd, k.Ht), KeyEdge); Fill(new Rect(k.X + 2, k.Y + 2, k.Wd - 4, k.Ht - 4), bg);
                if (string.IsNullOrEmpty(lab)) continue;
                int px = IvaLayout.KeyFont(lab, k.Wd, k.Ht, 15); var ls = IvaLayout.KeyLines(lab, k.Wd, px);   // uniform glyphs, wrapped: never squashed
                float y0 = k.Y + (k.Ht - ls.Count * (px + 2)) / 2;
                for (int i = 0; i < ls.Count; i++) cv.CellText(ls[i], k.X, y0 + i * (px + 2), px, k.Id == "A0" ? (lamp ? Color.black : new Color(.45f, .35f, .35f)) : Label, true, k.Wd);
            }
            int cols = Cols, rows = Rows; float x0 = s.X + 6;
            var shared = SharedPage(Item) && MapWindow.HasMap ? MapWindow.Inst : null;
            if (shared != null) DrawSharedPage(shared, new Rect(s.X, s.Y, s.Wd, s.Ht));   // MAP / CHART / ILS: the real graphics (MapWindow), not a text page
            else
            {   // no map in this scene: the text page (needs the map bitmap, so it stays as the fallback)
                if (Item == "ils") { float S = Math.Min(s.Ht - 8, s.Wd * .48f); DrawNeedles(new Rect(s.X + 4, s.Y + 4, S, S)); x0 = s.X + S + 12; cols = (int)((s.X + s.Wd - x0 - 4) / CellW(TextPx)); }
                var lines = ScreenLines(cols, rows);
                for (int i = 0; i < Math.Min(rows, lines.Count); i++) Text(lines[i].Length > cols ? lines[i].Substring(0, cols) : lines[i], x0, s.Y + 4 + i * (TextPx + 3), TextPx, Phos);
                if (focused != null) Fill(new Rect(s.X, s.Y + s.Ht - 3, s.Wd, 3), Phos);
            }
            cv.End();
        }

        /// <summary>The shared MAP / CHART / ILS page (MapWindow.DrawPages) inside this screen's rect at the screen's own size: the
        /// same graphics the outside MFD draws, no mouse (the bezel soft keys drive it), clipped to the screen.</summary>
        void DrawSharedPage(MapWindow m, Rect g) { cv.Group(g, () => m.DrawIva(cv, new Rect(0, 0, g.width, g.height), Item)); }

        /// <summary>ILS needles box (same deviations as the outside ILS page and the autopilot).</summary>
        void DrawNeedles(Rect b)
        {
            Fill(b, new Color(.02f, .05f, .03f)); Vector2 c = b.center; float S = b.width;
            for (int i = -2; i <= 2; i++) if (i != 0) { Fill(new Rect(c.x + i * S * .2f - 2, c.y - 2, 4, 4), Color.white); Fill(new Rect(c.x - 2, c.y + i * S * .2f - 2, 4, 4), Color.white); }
            Fill(new Rect(c.x - 3, c.y - 3, 6, 6), Color.yellow);
            var v = FlightGlobals.ActiveVessel; if (v == null) return;
            var act = NativeFlightController.ActiveRunway; Ils.Reading r;
            if (act != null && act.Coupled && act.Ils != null) r = act.Ils;
            else if (act != null) r = Ils.Compute(v.latitude, v.longitude, v.altitude, act.Lat, act.Lon, act.EndLat, act.EndLon, act.Elevation, v.mainBody.Radius, act.TouchdownM, act.FinalSlope);
            else r = Ils.Compute(v.latitude, v.longitude, v.altitude, KscRunway.Lat, KscRunway.Lon09, KscRunway.Lat, KscRunway.Lon27, 69.1, v.mainBody.Radius, 350);
            bool valid = r.Front && r.DmeM < 40000; Color nc = valid ? Color.magenta : Color.gray;
            float lx = c.x - (float)Ils.Needle(r.LocDeg, Ils.LocFullScale) * S * .4f, gy = c.y + (float)Ils.Needle(r.GsDeg, Ils.GsFullScale) * S * .4f;
            Fill(new Rect(lx - 1.5f, b.y + 8, 3, S - 16), nc); Fill(new Rect(b.x + 8, gy - 1.5f, S - 16, 3), nc);
        }
        void Fill(Rect r, Color c) { cv.Fill(r, c); }
        void Text(string str, float x, float y, int px, Color c) { cv.CellText(str, x, y, px, c, false, 0); }   // monospace cells, left aligned (the text pages)
    }

    /// <summary>Mouse click on the IVA MFD face (Unity OnMouseDown via the internal camera, like RPM's buttons).</summary>
    public class AicsIvaClick : MonoBehaviour
    {
        internal AicsIvaMfd Owner;
        void OnMouseDown() { if (Owner != null) Owner.Click(); }
    }
}