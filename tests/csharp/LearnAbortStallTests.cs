using System;
using KSPChatBridge;

// Luke 14:18 log (Aeris 3A, 5.3 t): step 6 pulled 8.6 g at vmax and aborted; after the abort the plane drifted in hold
// with bank capped at -5 deg and false throttle emergencies, because the stall in use was missing/implausible.
internal static class LearnAbortStallTests
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL learn/stall: " + what); } }
    static LearnIn In(double t, double spd, double g = 1) { return new LearnIn { T = t, Speed = spd, Alt = 5600, Agl = 5600, Vs = 0, Pitch = 3, G = g, GLimit = 9, FuelFrac = .9, Mass = 5.3, StallGuess = 72, Throttle = .8 }; }

    internal static void Run()
    {
        // 1) step 6: vmax ends -> ratesprep (slow to a moderate speed), never straight to the pull
        var lf = new LearnFlight(); lf.Phase = "vmax"; double t = 0;
        for (; t < 40 && lf.Phase == "vmax"; t += .5) lf.Step(In(t, 300));
        Check(lf.Phase == "ratesprep", "after vmax expected ratesprep, got " + lf.Phase);
        Check(lf.StepNo == 6, "ratesprep is step 6");
        lf.Step(In(t, 300)); t += .5;   // first ratesprep frame sets the speed target
        Check(!double.IsNaN(lf.SpeedTarget) && lf.SpeedTarget <= 2 * 72 + .1 && lf.SpeedTarget >= 1.6 * 72 - .1, "rates speed ~2x stall, got " + lf.SpeedTarget);
        double vT = lf.SpeedTarget;
        for (double s = 300; t < 400 && lf.Phase == "ratesprep"; t += .5) { s = Math.Max(vT, s - 3); lf.Step(In(t, s)); }
        Check(lf.Phase == "rates" && t < 120, "ratesprep settles into rates (not by timeout), got " + lf.Phase + " at " + t);
        Check(lf.SpeedTarget == vT, "roll check holds the rate-check speed");
        // pull: G rises with elevator; must cut before 60% of the limit is exceeded much, never abort
        double t0 = t, maxElev = 0; bool sawCut = false;
        while (lf.Phase == "rates" && t - t0 < 11)
        {
            double el = double.IsNaN(lf.Elevator) ? 0 : lf.Elevator; maxElev = Math.Max(maxElev, el);
            double g = 1 + el * 12;   // stiff plane: 60% elevator would be 8.2 g
            if (t - t0 > 7.2 && el == 0 && maxElev > 0) sawCut = true;
            lf.Step(In(t, vT, g)); t += .1;
        }
        Check(!lf.Aborted, "pitch check must not abort: " + lf.Reason);
        Check(sawCut, "elevator cut once G passed 60% of the limit");
        Check(maxElev < .6, "elevator ramped, not stepped to 60% (max " + maxElev.ToString("0.00") + ")");
        bool cut = false; Check(LearnFlight.RatesElevator(.3, .1, 5.5, 9, ref cut) == 0 && cut, "cut above 0.6 x limit");
        cut = false; double e1 = LearnFlight.RatesElevator(0, .5, 1, 9, ref cut); Check(e1 > 0 && e1 <= .21 && !cut, "ramp rate");
        cut = false; Check(LearnFlight.RatesElevator(.59, 1, 2, 0, ref cut) == .6, "elevator capped at 60%");
        var lf2 = new LearnFlight(); lf2.P.Perf.CruiseSpeed = 120; Check(Math.Abs(lf2.RatesSpeed(72) - 120) < .1, "cruise used when lower than 2x stall");
        Check(Math.Abs(new LearnFlight().RatesSpeed(double.NaN) - 120) < .1, "NaN stall -> safe default speed");

        // 2) abort keeps the partial profile and asks for the landing
        var ab = new LearnFlight(); ab.Phase = "vmax"; ab.P.Decel = 6.1; ab.P.Liftoff = 89.8;
        ab.Step(In(0, 300, 8.6));
        Check(ab.Done && ab.Aborted, "8.6 g of 9 aborts");
        Check(ab.P.Decel == 6.1 && ab.P.Liftoff == 89.8 && !ab.P.Complete && ab.P.Abort.Contains("g limit"), "partial profile kept");
        Check(ab.Status.Contains("KSC 27"), "abort status names the landing");
        Check(double.IsNaN(ab.Bank) && double.IsNaN(ab.Pitch) && double.IsNaN(ab.Elevator), "abort releases all learn overrides");
        Check(PilotPolicy.LandingEngaged("hold", true), "ferry pending counts as landing engaged");
        Check(PilotPolicy.LandingEngaged("landing", false), "approach counts");
        Check(!PilotPolicy.LandingEngaged("hold", false), "plain hold after abort is NOT a landing (retry)");

        // 3) stall fallback and emergency guard
        Check(PilotPolicy.SaneStall(double.NaN, 72) == 72, "NaN stall -> estimate");
        Check(PilotPolicy.SaneStall(0, 72) == 72, "0 stall -> estimate");
        Check(PilotPolicy.SaneStall(200, 72) == 72, "huge stall -> estimate");
        Check(PilotPolicy.SaneStall(double.PositiveInfinity, 72) == 72, "inf stall -> estimate");
        Check(PilotPolicy.SaneStall(80, 72) == 80, "plausible stall kept");
        Check(PilotPolicy.StallEstimate(46, 89.8) > 71 && PilotPolicy.StallEstimate(46, 89.8) < 72.1, "estimate raised to liftoff/1.25");
        Check(PilotPolicy.StallEstimate(double.NaN, double.NaN) == 45, "no estimate -> 45");
        foreach (double ias in new[] { 210.0, 280, 306 })
            Check(!PilotPolicy.StallEmergency(ias, 300, 200, 72), "no emergency at " + ias + " m/s with a bogus 200 stall");
        Check(!PilotPolicy.StallEmergency(double.NaN, 300, double.NaN, 72), "NaN ias/stall -> no emergency at 300 m/s");
        Check(!PilotPolicy.StallEmergency(0, 300, 72, 72), "ias glitch 0 -> uses surface speed");
        Check(!PilotPolicy.StallEmergency(115, 115, 140, 72), "never above 1.5x estimate");
        Check(PilotPolicy.StallEmergency(75, 80, double.NaN, 72), "real near-stall still slams power");
        // bank: no 5 deg cap at cruise with a bogus stall once sanitized
        Check(Math.Abs(PilotPolicy.SafeBank(-30, 210, PilotPolicy.SaneStall(200, 72), 0)) > 5, "bank not held at 5 deg at 210 m/s IAS");
        Console.WriteLine(fails == 0 ? "learn/stall abort tests: ok" : "learn/stall abort tests: " + fails + " FAILED");
        if (fails > 0) Environment.ExitCode = 1;
    }
}
