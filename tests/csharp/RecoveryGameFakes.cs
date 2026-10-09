using System.Collections.Generic;

// Minimal game boundary for exercising production NativeRecovery without starting KSP.
class Vessel { public List<Part> parts = new List<Part>(); public ActionGroupList ActionGroups = new ActionGroupList(); }
enum KSPActionGroup { Light, Gear, Brakes, SAS, RCS, Custom01, Stage }
class ActionGroupList
{
    readonly Dictionary<KSPActionGroup, bool> states = new Dictionary<KSPActionGroup, bool>();
    public bool this[KSPActionGroup group] { get { bool value; return states.TryGetValue(group, out value) && value; } }
    public void SetGroup(KSPActionGroup group, bool value) { states[group] = value; }
}
class PartResource { public Part part; public bool flowState = true; }
class Part
{
    public Vessel vessel;
    public List<PartModule> Modules = new List<PartModule>();
    public List<PartResource> Resources = new List<PartResource>();
    public PartInfo partInfo = new PartInfo();
}
class PartInfo { public string title = "Wing"; }
class PartModule { public Part part; public List<BaseEvent> Events = new List<BaseEvent>(); }
class BaseEvent
{
    public bool active = true, guiActive = true;
    public string name = "", guiName = "";
    public System.Action action;
    public void Invoke() { action(); }
}
class ModuleControlSurface : PartModule
{
    public float deployAngle, authorityLimiter = 100;
    public bool deploy, deployInvert, partDeployInvert, ignorePitch, ignoreRoll, ignoreYaw;
}
class ModuleResourceIntake : PartModule
{
    public bool intakeEnabled = true;
    public void Activate() { intakeEnabled = true; }
    public void Deactivate() { intakeEnabled = false; }
}
class ModuleEngines : PartModule { public float thrustPercentage = 100; public bool EngineIgnited = true; }
class MultiModeEngine : PartModule
{
    public string primaryEngineID = "Forward", secondaryEngineID = "Reverse", primaryEngineModeDisplayName = "", secondaryEngineModeDisplayName = "";
    public bool runningPrimary = true;
    public void ToggleMode() { runningPrimary = !runningPrimary; }
}
