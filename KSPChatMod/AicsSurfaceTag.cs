using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>Per-part surface role for the autopilot (Luke 9:27 PM). Added to every control surface by AICS_SurfaceTag.cfg (ModuleManager).
    /// In the SPH: right-click the surface -> "AICS role" -> auto / flap / slat / spoiler / brake / aileron / none (symmetry copies follow).
    /// A kOS name tag containing flap / slat / brake / spoiler / aileron is honoured too. Any tag on the craft = tags only, no auto-detect.</summary>
    public class AicsSurfaceTag : PartModule
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "AICS role")]
        [UI_ChooseOption(scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All, options = new[] { "auto", "flap", "slat", "spoiler", "brake", "aileron", "roll/flaps", "none" }, display = new[] { "auto", "flap", "slat", "spoiler", "brake", "aileron", "ROLL / FLAPS", "none" })]   // GROK RF1: ROLL / FLAPS = aileron that droops as a flap
        public string role = "auto";
        string fileRole;

        /// <summary>AICS2: in flight, re-apply a role chosen earlier in flight for this craft (survives test-zone RETRY / respawn) and remember new picks.</summary>
        public override void OnStart(StartState state)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            fileRole = role;
            try
            {
                var ui = Fields["role"].uiControlFlight as UI_ChooseOption; if (ui != null) ui.onFieldChanged = OnRoleChanged;
                string saved = SurfaceRoleStore.Get(Load(), CraftKey(), part.craftID, fileRole); if (!string.IsNullOrEmpty(saved) && saved != role) role = saved;
            }
            catch (System.Exception ex) { Debug.LogWarning("[KSPChatBridge] AICS role restore: " + ex.Message); }
        }
        void OnRoleChanged(BaseField f, object old)
        {
            try
            {
                var root = Load(); string craft = CraftKey();
                SurfaceRoleStore.Set(root, craft, part.craftID, fileRole, role);
                foreach (Part s in part.symmetryCounterparts) { var m = s.FindModuleImplementing<AicsSurfaceTag>(); if (m != null) { m.role = role; SurfaceRoleStore.Set(root, craft, s.craftID, m.fileRole ?? "auto", role); } }
                System.IO.File.WriteAllText(StorePath, MiniJson.Serialize(root));
                NativeFlightController.RescanSurfaces();
                ChatLog.Write("aics", "role " + (part.partInfo != null ? part.partInfo.title : part.name) + " -> " + role + (part.symmetryCounterparts.Count > 0 ? " (+" + part.symmetryCounterparts.Count + " symmetry)" : "") + ", kept for " + craft + " in flight");
            }
            catch (System.Exception ex) { Debug.LogWarning("[KSPChatBridge] AICS role save: " + ex.Message); }
        }
        string CraftKey() { string n = vessel != null ? vessel.vesselName : ""; try { n = KSP.Localization.Localizer.Format(n); } catch (System.Exception) { } return n; }
        static string StorePath { get { return System.IO.Path.Combine(LandingTestRun.PluginData, "surface_roles.json"); } }
        static System.Collections.Generic.Dictionary<string, object> Load() { return System.IO.File.Exists(StorePath) ? SurfaceRoleStore.Parse(System.IO.File.ReadAllText(StorePath)) : new System.Collections.Generic.Dictionary<string, object>(); }

        /// <summary>Tag text for this part: the AICS role (unless auto), else a kOS name tag, else "".</summary>
        internal static string TagOf(Part p)
        {
            if (p == null) return "";
            var m = p.FindModuleImplementing<AicsSurfaceTag>();
            if (m != null && !string.IsNullOrEmpty(m.role) && m.role != "auto") return m.role;
            if (p.Modules.Contains("KOSNameTag")) { try { var v = p.Modules["KOSNameTag"].Fields.GetValue("nameTag") as string; if (!string.IsNullOrEmpty(v)) return v; } catch (System.Exception) { } }
            return "";
        }
    }
}