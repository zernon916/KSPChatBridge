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