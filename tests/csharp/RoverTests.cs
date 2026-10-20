using System;
using System.Collections.Generic;
using KSPChatBridge;

/// <summary>ROVER_SCIENCE_PLAN RS1-RS6 pure-policy tests.</summary>
internal static class RoverTests
{
    static int passes;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("RoverTests: " + name); passes++; }

    internal static void Run()
    {
        passes = 0;
        VehicleClassTests();
        ScienceInventoryTests();
        Console.WriteLine("RoverTests: " + passes + " checks passed");
    }

    static ScienceItem X(double value, bool rerun = true, bool avail = true, int stored = 0, bool held = false, bool inop = false)
    { return new ScienceItem { Id = "goo", Part = "Goo", Source = ScienceSource.Stock, ValueHere = value, Rerunnable = rerun, Available = avail, Stored = stored, Held = held, Inoperable = inop }; }

    static void ScienceInventoryTests()
    {
        Check(ScienceInventory.Decide(X(0), 0, false, false, double.NaN) == "0 sci", "value 0 => skip");
        Check(ScienceInventory.Decide(X(.05), 0, false, false, double.NaN) == null, "any science above 0 => run (Luke: > 0)");
        Check(ScienceInventory.Decide(X(.4), .5, false, false, double.NaN) == "0 sci" && ScienceInventory.Decide(X(.6), .5, false, false, double.NaN) == null, "value vs rover_min_science");
        Check(ScienceInventory.Decide(X(5, held: true), 0, false, false, double.NaN) == "already on board", "held already => skip");
        Check(ScienceInventory.Decide(X(5, stored: 1), 0, false, false, double.NaN) == "holding data", "module holding data => skip");
        Check(ScienceInventory.Decide(X(5), 0, true, false, double.NaN) == "spare copy", "one module per experiment type");
        Check(ScienceInventory.Decide(X(5, avail: false), 0, false, false, double.NaN) == "not available here", "not available");
        Check(ScienceInventory.Decide(X(2, rerun: false), 0, false, false, 9) == "one-shot kept for a better spot", "non-rerunnable without scientist kept for the best spot");
        Check(ScienceInventory.Decide(X(2, rerun: false), 0, false, true, 9) == null && ScienceInventory.Decide(X(2, rerun: false), 0, false, false, double.NaN) == null, "one-shot runs with a scientist reset or no better spot known");
        Check(ScienceInventory.Decide(X(5, inop: true), 0, false, false, double.NaN) != null && ScienceInventory.Decide(X(5, inop: true), 0, false, true, double.NaN) == null, "used one-shot needs a reset");
        Check(ScienceInventory.SourceOf("ModuleScienceExperiment", "Assembly-CSharp", true, false, false) == ScienceSource.Stock
            && ScienceInventory.SourceOf("DMModuleScienceAnimate", "DMagic", true, false, false) == ScienceSource.DMagic
            && ScienceInventory.SourceOf("StationExperiment", "StationScience", true, false, false) == ScienceSource.StationScience
            && ScienceInventory.SourceOf("ExoExperiment", "ExoInstruments", true, false, false) == ScienceSource.ModExperiment
            && ScienceInventory.SourceOf("ModuleScienceContainer", "Assembly-CSharp", false, true, false) == ScienceSource.Container
            && ScienceInventory.SourceOf("ModuleScienceLab", "Assembly-CSharp", false, false, true) == ScienceSource.Lab
            && ScienceInventory.SourceOf("Whatever", "X", false, false, false) == ScienceSource.Holder, "science part sources");
        Check(ScienceInventory.SilentGather(ScienceSource.Stock) && !ScienceInventory.SilentGather(ScienceSource.DMagic), "mod experiments use their own deploy");
        Check(ScienceInventory.KscMiniBiome("VehicleAssemblyBuilding") == "VAB" && ScienceInventory.KscMiniBiome("Shores") == null, "KSC mini-biome names");
        Check(ScienceInventory.Line(X(3.2), null).Contains("worth 3.2") && ScienceInventory.Line(X(3.2), null).EndsWith("RUN"), "inventory line");
        Check(SciencePolicy.AutoSkipReason(false, false, true, false, .3, false, false, true) == "0 sci" && SciencePolicy.AutoSkipReason(false, false, true, false, .3, false, false, true, 0) == null, "plane watcher keeps 0.5, rover min 0");
    }

    static VehicleKind K(int wings = 0, int rotors = 0, int props = 0, int wheels = 0, int skis = 0, int motors = 0, int engines = 0)
    {
        string why; var k = VehicleClass.Classify(new VehicleCounts { Wings = wings, Rotors = rotors, Props = props, Wheels = wheels, Skis = skis, Motors = motors, Engines = engines }, out why);
        Check(!string.IsNullOrEmpty(why), "classifier gives a reason"); return k;
    }

    static void VehicleClassTests()
    {
        Check(K(wheels: 4, motors: 4) == VehicleKind.Rover, "stock rover = ROVER");
        Check(K(wheels: 2, skis: 2, motors: 2) == VehicleKind.Rover && K(skis: 3) == VehicleKind.Rover, "rover with skis = ROVER");
        Check(K(wings: 6, wheels: 3, engines: 1) == VehicleKind.Plane, "plane with wheels = PLANE");
        Check(K(wings: 4, props: 2, wheels: 3) == VehicleKind.Plane, "prop plane = PLANE");
        Check(K(rotors: 1, props: 4, wheels: 3) == VehicleKind.Heli, "heli with wheels = HELI");
        Check(K(engines: 2) == VehicleKind.Rocket, "rocket = ROCKET");
        Check(K(engines: 1) == VehicleKind.Rocket, "rocket on landing legs (legs are not wheels) = ROCKET");
        Check(K(wheels: 4, motors: 4) == VehicleKind.Rover, "rover with RTG and antenna = ROVER");
        string why; Check(VehicleClass.Classify(new VehicleCounts { Wheels = 4, Engines = 1 }, out why) == VehicleKind.Rover && why.Contains("WARNING") && why.Contains("engine"), "jet rover without wings = ROVER with warning");
        Check(VehicleClass.LogLine("Rover A", VehicleKind.Rover, "wheels 4").StartsWith("Rover A: ROVER (wheels 4"), "log line");
        Check(VehicleClass.IsSki("landingSki", "", "") && VehicleClass.IsSki("x", "Big Skis", "") && VehicleClass.IsSki("x", "y", "gear ski"), "skis by name/title/tag");
        Check(!VehicleClass.IsSki("skinPanel", "Skipper Engine", "skirt"), "ski is a whole word only");
        Check(VehicleClass.IsPropName("largePropeller", "") && VehicleClass.IsPropName("mediumHeliBlade", "") && VehicleClass.IsPropName("KAXelectricProp", ""), "prop/blade names");
        Check(!VehicleClass.IsPropName("fuelTankPropellant", "Propellant tank"), "propellant is not a prop");
        Check(VehicleClass.IsPropModule("FSpropellerTweak") && VehicleClass.IsPropModule("FSpropellerAtmosphericNerf") && !VehicleClass.IsPropModule("ModuleEnginesFX") && !VehicleClass.IsPropModule(null), "prop modules");
        var rov = CraftOverrides.Find(CraftOverrides.RoverKey); var land = CraftOverrides.Find("flare_start_m"); var rms = CraftOverrides.Find("rover_max_speed");
        Check(rov != null && !CraftOverrides.Visible(rov, true) && !CraftOverrides.Visible(rov, false), "ROVER switch is a header, not a row");
        Check(CraftOverrides.Visible(land, false) && !CraftOverrides.Visible(land, true) && CraftOverrides.Visible(rms, true) && !CraftOverrides.Visible(rms, false), "plane rows OFF / rover rows ON");
        var s = new Dictionary<string, object>(); CraftOverrides.Set(s, "R", CraftOverrides.RoverKey, 1); CraftOverrides.Set(s, "R", CraftOverrides.RoverTransmit, 2.6);
        Check(CraftOverrides.Get(s, "R", CraftOverrides.RoverKey) == 1 && CraftOverrides.Get(s, "R", CraftOverrides.RoverTransmit) == 3, "switch and choice values stored unclamped by the speed range");
    }
}