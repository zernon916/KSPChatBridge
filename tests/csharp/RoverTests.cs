using System;
using System.Collections.Generic;
using System.Linq;
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
        PlacesTests();
        StatusBarTests();
        RoadTests();
        TourTests();
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

    static void PlacesTests()
    {
        var s = new PlacesStore();
        var a = s.Add("Island Tower", "building", "Kerbin", -1.52, -71.9, 130, 90); var b = s.Add("", null, "Mun", 3, 4, double.NaN, double.NaN); var c = s.Add("Island Tower", "airport", "Kerbin", 1, 2, 0, 0);
        Check(a.Name == "Island Tower" && b.Name == "Pin 1" && c.Name == "Island Tower 1" && b.Kind == "site", "pin naming");
        a.Stop = true; a.Notes = "check";
        var r = PlacesStore.FromJson(s.ToJson()); var ra = r.Find("island tower");
        Check(r.Manual.Count == 3 && ra != null && ra.Stop && ra.Kind == "building" && Math.Abs(ra.Lat + 1.52) < 1e-9 && ra.Heading == 90 && ra.Notes == "check" && double.IsNaN(r.Find("Pin 1").Alt), "store round-trip");
        Check(PlacesStore.FromJson("not json").Manual.Count == 0 && PlacesStore.FromJson(null).Manual.Count == 0, "bad file = empty store");
        Check(r.Rename("Pin 1", "Mun Arch") && r.Find("Mun Arch") != null && !r.Rename("Mun Arch", "Island Tower") && !r.Rename("nope", "x"), "rename (no clash)");
        Check(r.Delete("Mun Arch") && r.Find("Mun Arch") == null && !r.Delete("Mun Arch"), "delete");
        var kk = new List<Place> { new Place { Name = "Island Tower", Lat = 9, Lon = 9, Source = "KerbalKonstructs" }, new Place { Name = "KSC2", Lat = 1, Lon = 1, Source = "KerbalKonstructs", Kind = "airport" }, new Place { Name = "ksc2", Lat = 2, Lon = 2 } };
        var m = r.Merged(kk); var it = m.Find(p => p.Name == "Island Tower");
        Check(m.Count == 3 && it.Source == "manual" && it.Lat == -1.52 && m.Exists(p => p.Name == "KSC2"), "merge: manual wins on name clash");
        Check(r.Merged(null).Count == 2, "merge without KK");
        var stops = PlacesStore.Stops(m, "Kerbin"); Check(stops.Count == 1 && stops[0].Name == "Island Tower" && PlacesStore.Stops(m, "Mun").Count == 0, "stop selection per body");
        var spots = new Dictionary<string, object> { { "Island 27", new Dictionary<string, object> { { "lat", -1.5 }, { "lon", -71.8 }, { "body", "Kerbin" }, { "mode", "H" } } }, { "bad", "x" } };
        var ls = PlacesStore.FromLandingSpots(spots); Check(ls.Count == 1 && ls[0].Name == "Island 27" && ls[0].Kind == "airport" && ls[0].Source == "landing_spots", "landing_spots migrate read-only");
        Check(PlacesStore.KindForKk("Runway") == "airport" && PlacesStore.KindForKk("RocketPad") == "site" && PlacesStore.NormalizeKind("BUILDING") == "building" && PlacesStore.NormalizeKind("x") == "site", "kinds");
    }

    static void StatusBarTests()
    {
        Check(StatusBarMath.Bars(false, 1) == 0 && StatusBarMath.Bars(true, 0) == 0 && StatusBarMath.Bars(true, .01) == 1 && StatusBarMath.Bars(true, .3) == 2 && StatusBarMath.Bars(true, .6) == 3 && StatusBarMath.Bars(true, .8) == 4 && StatusBarMath.Bars(true, 5) == 4 && StatusBarMath.Bars(true, double.NaN) == 0, "signal bars 0-4");
        Check(StatusBarMath.BarHeight(0) < StatusBarMath.BarHeight(1) && StatusBarMath.BarHeight(3) == 1f, "rising bars");
        Check(StatusBarMath.Battery(50, 100) == .5 && double.IsNaN(StatusBarMath.Battery(5, 0)) && StatusBarMath.Battery(150, 100) == 1, "battery fraction");
        Check(StatusBarMath.BatteryBand(.51) == 2 && StatusBarMath.BatteryBand(.5) == 1 && StatusBarMath.BatteryBand(.2) == 1 && StatusBarMath.BatteryBand(.19) == 0 && StatusBarMath.BatteryBand(double.NaN) == -1, "battery colours");
        Check(StatusBarMath.Label(true, .62, .125, true, .8) == "62% 125 ms R  80%" && StatusBarMath.Label(false, 0, 0, false, double.NaN) == "NO LINK  --", "status label");
    }

    static KscRoverPolicy.P Pp(double lat, double lon) { return new KscRoverPolicy.P(lat, lon); }

    static RoadNet TestRoads()
    {
        var fronts = new List<KeyValuePair<string, KscRoverPolicy.P>>
        {
            new KeyValuePair<string, KscRoverPolicy.P>("VAB", Pp(-0.0968, -74.6175)),
            new KeyValuePair<string, KscRoverPolicy.P>("SPH", Pp(-0.0890, -74.6110)),
            new KeyValuePair<string, KscRoverPolicy.P>("R&D", Pp(-0.1040, -74.6290)),
            new KeyValuePair<string, KscRoverPolicy.P>("Launch Pad", Pp(-0.0972, -74.5577)),
            new KeyValuePair<string, KscRoverPolicy.P>("Crawlerway", Pp(-0.0970, -74.5880)),
            new KeyValuePair<string, KscRoverPolicy.P>("Runway", Pp(KscRunway.LatAt(-74.65), -74.65)),
            new KeyValuePair<string, KscRoverPolicy.P>("R&D - Wind Tunnel", Pp(-0.1050, -74.6300)),
        };
        return RoadNet.Ksc(Pp(-0.0920, -74.6180), fronts, 600000);
    }

    // RS3b: every driven segment lies on the road network; only the final leg (to an off-road goal) and the first leg (from an off-road start) leave it
    static void RoadTests()
    {
        var net = TestRoads();
        Check(net.Edges.Count == 2 + 2 + 3 + 2 && net.Edges.Exists(e => e.Kind == "crawlerway") && net.Edges.Exists(e => e.Kind == "runway"), "roads: runway + taxiway + spokes + crawlerway");
        Check(net.Find("R&D - Wind Tunnel") < 0 && net.Find("Runway") < 0, "roads: R&D sub-buildings and Runway are no nodes");
        var onRunway = Pp(KscRunway.LatAt(-74.70), -74.70); var vab = Pp(-0.0968, -74.6175);
        var r = net.Route(onRunway, vab);
        Check(r != null && Math.Abs(r[r.Count - 1].Lat - vab.Lat) < 1e-9 && Math.Abs(r[r.Count - 1].Lon - vab.Lon) < 1e-9, "roads: runway -> VAB ends at the VAB front");
        bool all = true; var prev = onRunway;
        foreach (var q in r) { if (!net.OnRoad(q, 1)) all = false; if (!net.OnRoad(Pp((prev.Lat + q.Lat) / 2, (prev.Lon + q.Lon) / 2), 1)) all = false; prev = q; }
        Check(all, "roads: runway -> VAB every point and segment on the road");
        Check(r.Exists(q => Math.Abs(q.Lon - TaxiRoute.ExitLon) < 1e-9) && r.Exists(q => Math.Abs(q.Lat + 0.0920) < 1e-9 && Math.Abs(q.Lon + 74.6180) < 1e-9), "roads: runway -> VAB goes via the runway exit and the hub");
        double straight = NavigationMath.Distance(onRunway.Lat, onRunway.Lon, vab.Lat, vab.Lon, 600000), drive = net.DriveDistance(onRunway, vab);
        Check(drive > straight && Math.Abs(drive - net.Length(onRunway, r)) < 1e-6, "roads: driving distance longer than straight line");
        // off-road goal: 150 m south of the R&D front -> only the final leg leaves the road
        var site = Pp(-0.1040 - 150 / 10471.98, -74.6290);
        r = net.Route(onRunway, site);
        all = true; prev = onRunway;
        for (int i = 0; i < r.Count - 1; i++) { var q = r[i]; if (!net.OnRoad(q, 1) || !net.OnRoad(Pp((prev.Lat + q.Lat) / 2, (prev.Lon + q.Lon) / 2), 1)) all = false; prev = q; }
        double last = NavigationMath.Distance(r[r.Count - 2].Lat, r[r.Count - 2].Lon, site.Lat, site.Lon, 600000);
        Check(all && !net.OnRoad(site, 3), "roads: off-road site - all legs but the last on the road");
        Check(last > 100 && last < 200, "roads: off-road site - last leg short (~150 m)");
        // off-road start: first point is the projection onto the road
        var grass = Pp(-0.0990, -74.6400);
        r = net.Route(grass, vab);
        Check(net.OnRoad(r[0], 1) && !net.OnRoad(grass, 3), "roads: off-road start joins the nearest road first");
        // shortest path: VAB -> pad along the crawlerway, not back through the hub
        r = net.Route(vab, Pp(-0.0972, -74.5577));
        Check(r.Exists(q => Math.Abs(q.Lon + 74.5880) < 1e-9) && !r.Exists(q => Math.Abs(q.Lon + 74.6180) < 1e-9), "roads: VAB -> pad via the crawlerway");
        Check(net.Route(vab, Pp(vab.Lat + 30 / 10471.98, vab.Lon)).Count == 1, "roads: close goal drives straight");
        var back = RoadNet.FromJson(net.ToJson(), 600000);
        Check(back != null && back.Nodes.Count == net.Nodes.Count && back.Edges.Count == net.Edges.Count && back.Edges[back.Edges.Count - 1].Kind == net.Edges[net.Edges.Count - 1].Kind, "roads: json round trip");
        Check(new RoadNet().Route(onRunway, vab) == null, "roads: no network -> null route");
        Check(Math.Abs(RoadNet.CornerSpeed(20, 90, 0) - 6) < 1e-9 && RoadNet.CornerSpeed(20, 10, 0) == 20 && RoadNet.CornerSpeed(20, 90, 500) == 20, "roads: corner speed");
    }

    static TourSite Site(string name, double dist, params object[] exps)
    {
        var s = new TourSite { Key = name, Name = name, Distance = dist };
        for (int i = 0; i + 2 < exps.Length; i += 3) s.Exps.Add(new TourExp { Id = (string)exps[i], Title = (string)exps[i], Gain = (double)exps[i + 1], OneShot = (bool)exps[i + 2] });
        return s;
    }
    static List<TourSite> TourSample()
    {
        return new List<TourSite>
        {
            Site("Runway", 300, "goo", 4.2, true, "temp", 0.0, false, "baro", 1.5, false),
            Site("VAB", 900, "goo", 6.0, true, "temp", 2.0, false),
            Site("Nothing", 100, "temp", 0.0, false),
            Site("Pad", 2000, "goo", 5.0, true, "crew", 9.0, false),
        };
    }

    // RS7: checklist sorting, site/experiment linkage, zero-gain rows, one-shot rule, saved selections, route from checked items only
    static void TourTests()
    {
        var sites = TourSample(); var sel = new TourSelection();
        TourChecklist.Apply(sites, sel);
        Check(sites[0].Name == "Pad" && sites[1].Name == "VAB" && sites[2].Name == "Runway" && sites[3].Name == "Nothing", "tour: sites sorted by total, zero-total last");
        var rw = sites[2];
        Check(rw.Exps[0].Id == "goo" && rw.Exps[1].Id == "baro" && rw.Exps[2].Id == "temp", "tour: experiments sorted by gain, zero row last");
        Check(!rw.Exps[2].On && rw.Exps[2].Dim, "tour: zero-gain row unchecked and dimmed");
        Check(sites[3].On == false && sites.Take(3).All(s => s.On), "tour: new sites default checked when worth anything");
        // one-shot goo (one module): only at VAB (6.0), dimmed at Pad and Runway
        var gooPad = sites[0].Exps.First(e => e.Id == "goo"); var gooVab = sites[1].Exps.First(e => e.Id == "goo"); var gooRw = rw.Exps[0];
        Check(!gooVab.Blocked && gooPad.Blocked && gooRw.Blocked && gooRw.Dim && gooRw.RunsAt == "VAB", "tour: one-shot runs only at the checked site where it is worth most");
        Check(Math.Abs(TourChecklist.CheckedTotal(sites) - (9 + 6 + 2 + 1.5)) < 1e-9, "tour: checked total counts each one-shot once");
        TourChecklist.SetSite(sites, sites[1], false, sel);
        Check(!gooPad.Blocked && gooRw.Blocked && sites[1].Exps.All(e => !e.On), "tour: unchecking a site moves the one-shot to the next best site");
        Check(!TourChecklist.RouteSites(sites).Exists(s => s.Name == "VAB") && !TourChecklist.RouteSites(sites).Exists(s => s.Name == "Nothing"), "tour: route only from checked sites with something to run");
        TourChecklist.SetExp(sites, rw, rw.Exps[1], false, sel);
        Check(rw.On && !rw.Exps[1].On && sel.Off.Contains("Runway|baro"), "tour: unticking one experiment keeps the site");
        Check(!TourChecklist.RouteSites(sites).Exists(s => s.Name == "Runway"), "tour: site with only blocked/unticked rows is not visited");
        TourChecklist.SetSite(sites, rw, true, sel);
        Check(rw.Exps[1].On && !rw.Exps[2].On && !sel.Off.Contains("Runway|baro"), "tour: checking a site checks all its >0 experiments");
        Check(TourChecklist.RunIds(sites[0]).SetEquals(new[] { "goo", "crew" }), "tour: run ids = checked runnable experiments");
        TourChecklist.SetExp(sites, sites[1], sites[1].Exps.First(e => e.Id == "temp"), true, sel);
        Check(sites[1].On && sites[1].Exps.First(e => e.Id == "temp").On && !sites[1].Exps.First(e => e.Id == "goo").On, "tour: ticking a row of an unchecked site checks just that row");
        TourChecklist.SetAll(sites, false, sel);
        Check(TourChecklist.RouteSites(sites).Count == 0 && TourChecklist.CheckedTotal(sites) == 0, "tour: select none");
        TourChecklist.SetAll(sites, true, sel);
        Check(TourChecklist.RouteSites(sites).Count == 3, "tour: select all");
        // saved selections survive a round trip through the settings json
        TourChecklist.SetSite(sites, sites[0], false, sel); TourChecklist.SetExp(sites, rw, rw.Exps[0], false, sel);
        var back = TourSelection.FromData(MiniJson.Deserialize(MiniJson.Serialize(new Dictionary<string, object> { { "x", sel.ToData() } }))["x"]);
        var again = TourSample(); TourChecklist.Apply(again, back);
        Check(!again.First(s => s.Name == "Pad").On && !again.First(s => s.Name == "Runway").Exps.First(e => e.Id == "goo").On && again.First(s => s.Name == "VAB").On, "tour: saved selections restored");
        var fresh = TourSample(); fresh.Add(Site("New place", 50, "temp", 3.0, false)); TourChecklist.Apply(fresh, back);
        Check(fresh.First(s => s.Name == "New place").On, "tour: newly appearing site defaults to checked");
        // shortest drive order: start -> A(100) ... B is near C
        var st = new double[] { 100, 1000, 1100 }; var d = new double[,] { { 0, 900, 1000 }, { 900, 0, 100 }, { 1000, 100, 0 } };
        var o = TourChecklist.Order(st, d);
        Check(o.SequenceEqual(new[] { 0, 1, 2 }), "tour: shortest drive order");
        var st2 = new double[] { 500, 100, 400 }; var d2 = new double[,] { { 0, 600, 100 }, { 600, 0, 300 }, { 100, 300, 0 } };
        Check(TourChecklist.Order(st2, d2).SequenceEqual(new[] { 1, 2, 0 }), "tour: order uses driving distances, not list order");
        int big = 14; var sb = new double[big]; var db = new double[big, big];
        for (int i = 0; i < big; i++) { sb[i] = (i + 1) * 100; for (int j = 0; j < big; j++) db[i, j] = Math.Abs(i - j) * 100; }
        Check(TourChecklist.Order(sb, db).SequenceEqual(Enumerable.Range(0, big)), "tour: large tours ordered by nearest neighbour + 2-opt");
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