// RS4: rover transmit / power / reserve / scientist-reset / AutomatedScienceSampler decisions (pure, tested). Game side: NativeRoverPower.cs.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal enum AssMode { None, OursAssPaused, OursRunFirst, LeftToOther }

    internal static class RoverPowerPolicy
    {
        internal const int TransmitAlways = 0, Transmit60 = 1, Transmit40 = 2, TransmitSave = 3;
        internal static readonly string[] TransmitNames = { "ALWAYS TRANSMIT", "TRANSMIT IF >= 60%", "TRANSMIT IF >= 40%", "SAVE ON BOARD" };

        /// <summary>Transmit one result? xmitFrac = transmit value / full (recovery) value. Never without a CommNet link, never data that
        /// cannot be transmitted (~0), never in SAVE. Science-Full-Transmit makes xmitFrac 1 for every experiment (MM patch).</summary>
        internal static bool ShouldTransmit(int choice, double xmitFrac, bool connected)
        {
            if (!connected || !(xmitFrac > .01) || choice == TransmitSave) return false;
            if (choice == Transmit60) return xmitFrac >= .6 - 1e-9;
            if (choice == Transmit40) return xmitFrac >= .4 - 1e-9;
            return true;
        }

        internal static string PolicyText(int choice, bool fullTransmit, bool relay)
        {
            string s = TransmitNames[Math.Max(0, Math.Min(3, choice))];
            s += fullTransmit ? " | Science-Full-Transmit: every experiment transmits at 100%" : " | no Science-Full-Transmit: partial-value data kept on board";
            s += relay ? " | ScienceRelay: relay to another vessel from its own menu" : " | no ScienceRelay";
            return s;
        }

        /// <summary>Power ladder order while charging.</summary>
        internal static List<string> Ladder(bool hasSolar, bool hasConverters, int warpChoice)
        {
            var r = new List<string> { "stop" };
            if (hasSolar) r.Add("solar");
            if (hasConverters) r.Add("converters");
            r.Add("wait");
            if (warpChoice > 0) r.Add("warp x" + (warpChoice + 1).ToString(CultureInfo.InvariantCulture));
            return r;
        }

        /// <summary>Charging hysteresis: start below lowPct, drive on at resumePct.</summary>
        internal static bool Charging(bool charging, double ecPct, double lowPct, double resumePct)
        {
            if (double.IsNaN(ecPct)) return false;
            return charging ? ecPct < Math.Max(lowPct, resumePct) : ecPct < lowPct;
        }

        /// <summary>Fuel kept for the drive home: distance x EC per metre x fuel per EC x (1 + reserve%).</summary>
        internal static double ReserveNeed(double homeMetres, double ecPerMetre, double fuelPerEc, double reservePct)
        {
            if (!(homeMetres > 0) || !(ecPerMetre > 0) || !(fuelPerEc > 0)) return 0;
            return homeMetres * ecPerMetre * fuelPerEc * (1 + Math.Max(0, reservePct) / 100);
        }
        internal static bool MayBurnFuel(double fuel, double need) { return fuel > need; }

        /// <summary>Only converters that output ElectricCharge and have every input on board.</summary>
        internal static bool ConverterOk(IList<string> outputs, IList<double> inputAmounts)
        {
            if (outputs == null || !outputs.Contains("ElectricCharge")) return false;
            if (inputAmounts != null) foreach (double a in inputAmounts) if (!(a > 1e-6)) return false;
            return true;
        }

        /// <summary>Physics warp rate index for the CRAFT choice (0 = off, 1..3 = x2..x4).</summary>
        internal static int WarpIndex(int choice) { return Math.Max(0, Math.Min(3, choice)); }

        /// <summary>Used non-rerunnable experiment: what to do after its data is safe (transmitted or in a container).</summary>
        internal static string ResetAction(bool rerunnable, bool inoperable, bool dataSafe, bool scientist, bool lab)
        {
            if (rerunnable || !inoperable) return null;
            if (!dataSafe) return "keep (data on the part, no container)";
            if (lab) return "lab clean";
            if (scientist) return "scientist reset";
            return "needs a scientist (EVA)";
        }

        /// <summary>AutomatedScienceSampler coexistence: OURS = pause it for this craft (or run first when it cannot be paused); OTHER = leave science to it.</summary>
        internal static AssMode Ass(bool installed, int scienceBy, bool canPause)
        {
            if (!installed) return scienceBy == 1 ? AssMode.LeftToOther : AssMode.None;
            if (scienceBy == 1) return AssMode.LeftToOther;
            return canPause ? AssMode.OursAssPaused : AssMode.OursRunFirst;
        }
        internal static bool WeRunScience(AssMode m) { return m != AssMode.LeftToOther; }
    }
}