using System.Collections.Generic;

// Minimal game boundary for exercising production NativeRecovery without starting KSP.
class Vessel { public List<Part> parts = new List<Part>(); }
class Part
{
    public Vessel vessel;
    public List<PartModule> Modules = new List<PartModule>();
    public PartInfo partInfo = new PartInfo();
}
class PartInfo { public string title = "Wing"; }
class PartModule { public Part part; }
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
class ModuleEngines : PartModule { public float thrustPercentage = 100; }
class MultiModeEngine : PartModule
{
    public string primaryEngineID = "Forward", secondaryEngineID = "Reverse", primaryEngineModeDisplayName = "", secondaryEngineModeDisplayName = "";
    public bool runningPrimary = true;
    public void ToggleMode() { runningPrimary = !runningPrimary; }
}
