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
        DrivePolicyTests();
        PowerPolicyTests();
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

    static void DrivePolicyTests()
    {
        double R = 600000;
        Check(RoverDrivePolicy.Surface("Runway", 0, 0, R) == RoverSurface.Runway && RoverDrivePolicy.Surface("", -.0495, -74.6, R) == RoverSurface.Runway
            && RoverDrivePolicy.Surface("KSC_Crawlerway", -.08, -74.6, R) == RoverSurface.Road && RoverDrivePolicy.Surface("", 1, -70, R) == RoverSurface.OffRoad, "surface classes");
        var caps = new RoverCaps(); Check(caps.Get(RoverSurface.Road) == 12 && caps.Get(RoverSurface.Runway) == 20 && caps.Get(RoverSurface.OffRoad) == 6, "plan default caps");
        var c2 = RoverCaps.FromDict(caps.ToDict()); c2.Set(RoverSurface.OffRoad, 99); Check(c2.OffRoad == RoverCaps.Ceiling && RoverCaps.FromDict(null).Road == 12, "caps round-trip and clamp");
        Check(RoverDrivePolicy.SlopeCap(10, 20, 15) == RoverDrivePolicy.CrawlSpeed && Math.Abs(RoverDrivePolicy.SlopeCap(10, 12, 15) - 6) < 1e-9 && RoverDrivePolicy.SlopeCap(10, 5, 15) == 10, "slope limit");
        Check(Math.Abs(RoverDrivePolicy.SlopeDeg(0, 20, 20) - 45) < 1e-9 && RoverDrivePolicy.SlopeDeg(double.NaN, 1, 20) == 0, "slope from terrain samples");
        Check(RoverDrivePolicy.RollCap(10, 25, 20) == 5 && RoverDrivePolicy.RollCap(10, 10, 20) == 10 && RoverDrivePolicy.BrakeForRoll(31) && !RoverDrivePolicy.BrakeForRoll(29), "tip-over limits");
        Check(Math.Abs(RoverDrivePolicy.TurnCap(20, .5) - .25 * 9.81 / .5) < 1e-9 && RoverDrivePolicy.TurnCap(20, 0) == 20, "lateral accel <= 0.25 g");
        Check(RoverDrivePolicy.Cap(30, 12, 0, 15, 0, 20, 0) == 12 && RoverDrivePolicy.Cap(30, 12, 0, 15, 25, 20, 0) == 6, "combined cap");
        Check(RoverDrivePolicy.HoldShort(false, true, 1500, 300) && !RoverDrivePolicy.HoldShort(true, true, 1500, 300) && !RoverDrivePolicy.HoldShort(false, true, 3000, 300) && !RoverDrivePolicy.HoldShort(false, false, 100, 50), "runway hold-short");
        bool brake; Check(RoverDrivePolicy.Detour(double.NaN, 5, 5, 10, out brake) == 0 && !brake, "clear ahead");
        Check(RoverDrivePolicy.Detour(30, 10, double.NaN, 5, out brake) == 35 && !brake && RoverDrivePolicy.Detour(30, double.NaN, 10, 5, out brake) == -35, "detour to the freer side");
        RoverDrivePolicy.Detour(5, 1, 1, 8, out brake); Check(brake, "brake inside braking distance");
        var st = new RoverStuck(); bool back = false; for (double t = 0; t <= 9.5; t += .5) back |= st.Sample(t, 5, .1, .5);
        Check(!back && st.Sample(10.5, 5, .1, .5) && st.Backing && st.Count == 1, "stuck after 10 s without progress");
        double tt = 11; while (st.Sample(tt, 5, 1, .5)) tt += .5; Check(!st.Backing && tt - 11 <= 5.5, "backs up 5 m then re-routes");
        Check(!new RoverStuck().Sample(20, 0, 0, .5), "not stuck when stopped on purpose");
        var stops = new List<double[]> { new[] { 0, .01, 0d }, new[] { 0, .012, 50d }, new[] { 0, .002, 0d } };
        var ord = RoverDrivePolicy.WeightedOrder(0, 0, stops, R); Check(ord.Count == 3 && ord[0] == 1 && ord[1] == 0 && ord[2] == 2, "value-weighted nearest neighbour (rich stop first, then nearest)");
        var lc = new RoverCaps(); var ln = new RoverSpeedLearner(); string ch = null; for (double t = 0; t <= 31; t += 1) ch = ln.Sample(t, RoverSurface.Road, lc, false, 11.5, 2, 20, false) ?? ch;
        Check(ch != null && lc.Road == 13, "learns a faster cap after 30 s stable");
        ln.Sample(40, RoverSurface.Road, lc, false, 12.5, 25, 20, true); Check(lc.Road == 11, "safety event drops the cap");
        for (double t = 41; t < 120; t += 1) ln.Sample(t, RoverSurface.Road, lc, false, 11, 2, 20, false); Check(lc.Road == 11, "no more raising after a safety event");
        var uc = new RoverCaps(); var lu = new RoverSpeedLearner(); for (double t = 0; t < 100; t += 1) lu.Sample(t, RoverSurface.Road, uc, true, 12, 0, 20, false); Check(uc.Road == 12, "user cap never learned over");
    }

    static void PowerPolicyTests()
    {
        Check(RoverPowerPolicy.ShouldTransmit(0, .3, true) && !RoverPowerPolicy.ShouldTransmit(0, .3, false), "ALWAYS transmits when linked only");
        Check(RoverPowerPolicy.ShouldTransmit(1, .6, true) && !RoverPowerPolicy.ShouldTransmit(1, .5, true), "IF >= 60%");
        Check(RoverPowerPolicy.ShouldTransmit(2, .4, true) && !RoverPowerPolicy.ShouldTransmit(2, .35, true), "IF >= 40%");
        Check(!RoverPowerPolicy.ShouldTransmit(3, 1, true), "SAVE keeps everything");
        Check(!RoverPowerPolicy.ShouldTransmit(0, 0, true), "never transmit non-transmittable data");
        Check(RoverPowerPolicy.PolicyText(2, true, false).Contains("Science-Full-Transmit") && RoverPowerPolicy.PolicyText(3, false, true).Contains("ScienceRelay"), "effective policy text");
        var l = RoverPowerPolicy.Ladder(true, true, 3); Check(string.Join(",", l.ToArray()) == "stop,solar,converters,wait,warp x4", "power ladder order");
        Check(string.Join(",", RoverPowerPolicy.Ladder(false, false, 0).ToArray()) == "stop,wait", "ladder without sources, warp off by default");
        Check(RoverPowerPolicy.Charging(false, 20, 25, 80) && !RoverPowerPolicy.Charging(false, 30, 25, 80) && RoverPowerPolicy.Charging(true, 60, 25, 80) && !RoverPowerPolicy.Charging(true, 81, 25, 80), "charging hysteresis");
        Check(Math.Abs(RoverPowerPolicy.ReserveNeed(1000, .5, .01, 20) - 6) < 1e-9 && RoverPowerPolicy.ReserveNeed(1000, double.NaN, .01, 20) == 0, "reserve maths");
        Check(RoverPowerPolicy.MayBurnFuel(7, 6) && !RoverPowerPolicy.MayBurnFuel(5, 6), "fuel kept for the drive home");
        Check(RoverPowerPolicy.ConverterOk(new List<string> { "ElectricCharge", "Water" }, new List<double> { 3, 1 }) && !RoverPowerPolicy.ConverterOk(new List<string> { "LiquidFuel" }, new List<double> { 3 })
            && !RoverPowerPolicy.ConverterOk(new List<string> { "ElectricCharge" }, new List<double> { 3, 0 }), "only EC converters with their inputs");
        Check(RoverPowerPolicy.WarpIndex(3) == 3 && RoverPowerPolicy.WarpIndex(9) == 3 && RoverPowerPolicy.WarpIndex(0) == 0, "physics warp up to x4");
        Check(RoverPowerPolicy.ResetAction(true, true, true, true, false) == null && RoverPowerPolicy.ResetAction(false, true, true, true, false) == "scientist reset"
            && RoverPowerPolicy.ResetAction(false, true, true, true, true) == "lab clean" && RoverPowerPolicy.ResetAction(false, true, false, true, false).StartsWith("keep")
            && RoverPowerPolicy.ResetAction(false, true, true, false, false).Contains("EVA"), "scientist reset decisions");
        Check(RoverPowerPolicy.Ass(true, 0, true) == AssMode.OursAssPaused && RoverPowerPolicy.Ass(true, 0, false) == AssMode.OursRunFirst && RoverPowerPolicy.Ass(true, 1, true) == AssMode.LeftToOther
            && RoverPowerPolicy.Ass(false, 0, false) == AssMode.None, "ASS detection flag");
        Check(RoverPowerPolicy.WeRunScience(AssMode.OursRunFirst) && !RoverPowerPolicy.WeRunScience(AssMode.LeftToOther), "no double runs: other mods mode leaves science to ASS");
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