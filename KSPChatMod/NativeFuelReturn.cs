using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    // POST-TESTING: fuel_check_return - fly home automatically when fuel equals what the return trip needs.
    public partial class NativeFlightController
    {
        bool fuelWatch, fuelReturning, fuelLandAsked; string fuelHome = "KSC"; double fuelReserve = 15, fuelPrevUnits = double.NaN, fuelRate = double.NaN;
        float fuelPrevT, nextFuelCheck;

        double FuelUnits()
        {
            double lf = 0, other = 0;
            foreach (Part p in vessel.parts)
                foreach (PartResource r in p.Resources)
                {
                    if (r.resourceName == "LiquidFuel") lf += r.amount;
                    else if (r.resourceName == "Oxidizer" || r.resourceName == "MonoPropellant") other += r.amount;
                }
            return lf > 0 ? lf : other;
        }

        bool HomePoint(out double lat, out double lon)
        {
            lat = lon = 0;
            TaxiMission.Point p = null;
            try { p = spots.Point(fuelHome, vessel.mainBody.bodyName); } catch (ArgumentException) { }
            if (p != null) { lat = p.Lat; lon = p.Lon; return true; }
            if (vessel.mainBody.bodyName == "Kerbin" && (fuelHome.Equals("KSC", StringComparison.OrdinalIgnoreCase) || FlightResidualPolicy.BuiltInRunway(fuelHome, "") != null))
            { lat = -.0485997; lon = -74.724375; return true; }
            return false;
        }

        string FuelReturnArm(Dictionary<string, object> a)
        {
            if (Bool(a, "off", false)) { bool was = fuelWatch; fuelWatch = fuelReturning = false; return was ? "Bingo-fuel watch off." : "Bingo-fuel watch was off."; }
            if (!IsPlane() || props.HasLift(vessel)) return "fuel_check_return is for planes for now.";
            fuelHome = Str(a, "home", "KSC"); if (fuelHome.Trim().Length == 0) fuelHome = "KSC";
            fuelReserve = Math.Max(0, Math.Min(50, Num(a, "reserve_pct", 15)));
            double lat, lon; if (!HomePoint(out lat, out lon)) return "Unknown home: " + fuelHome + ". Save it as a landing spot first.";
            fuelWatch = true; fuelReturning = fuelLandAsked = false; fuelPrevUnits = double.NaN; fuelRate = double.NaN; nextFuelCheck = 0;
            double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, lat, lon, vessel.mainBody.Radius);
            return "Bingo-fuel watch on: I'll head for " + fuelHome + " (" + FlightResidualPolicy.Distance(d) + " now) when fuel just covers the trip + " + fuelReserve.ToString("0", Inv) + "%.";
        }

        partial void FuelReturnTick()
        {
            if (!fuelWatch) return;
            if (vessel.LandedOrSplashed) { if (fuelReturning) { fuelWatch = fuelReturning = false; } return; }
            if (Time.realtimeSinceStartup < nextFuelCheck) return;
            float now = Time.realtimeSinceStartup; nextFuelCheck = now + 2;
            if (fuelReturning)
            {
                if (!fuelLandAsked && !flying2 && mode == "hold")   // fly_to arrived and is circling -> land
                {
                    fuelLandAsked = true; fuelWatch = false;
                    ChatWindow.Notice(Voice().Key + ": Home. Bringing her in: " + Command("land", Args("where", fuelHome)));
                }
                return;
            }
            double units = FuelUnits();
            fuelRate = FlightExtrasPolicy.BurnRate(fuelPrevUnits, units, now - fuelPrevT, fuelRate); fuelPrevUnits = units; fuelPrevT = now;
            double lat, lon; if (!HomePoint(out lat, out lon)) return;
            double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, lat, lon, vessel.mainBody.Radius);
            double range = FlightExtrasPolicy.Range(units, fuelRate, vessel.horizontalSrfSpeed), reservePct = fuelReserve;
            if (profile != null && profile.Perf.HasCruise)
            {   // learned profile (LEARN THIS PLANE): range at the measured cruise burn, reserve = trip home + one go-around
                double fr, tot; FuelState(out fr, out tot); double go = profile.Perf.ReserveFrac(0);
                range = Math.Min(range, profile.Perf.RangeKm(Math.Max(0, fr - (double.IsNaN(go) ? 0 : go))) * 1000);
            }
            if (!FlightExtrasPolicy.ReturnNow(range, d, reservePct)) return;
            fuelReturning = true;
            ChatWindow.Notice(Voice().Key + ": Bingo fuel, Captain. Turning for " + fuelHome + ": " + Command("fly_to", Args("name", fuelHome)));
        }
    }
}
