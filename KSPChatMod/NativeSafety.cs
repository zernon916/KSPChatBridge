using System;

namespace KSPChatBridge
{
    // P5-1: native emergency / sabotage / parking / power coverage when the bridge is absent. Pure policy so the
    // offline suite can pin the decisions; the Unity side (NativeFlightController.Update) just follows them.
    internal static class NativeSafety
    {
        /// <summary>Safety services (power recovery, sabotage revert, parking, engine restart) must run whenever the
        /// bridge watcher cannot: AI off (bridge stopped by design) or AI on with no bridge answering.</summary>
        internal static bool ShouldRun(bool aiEnabled, bool bridgeResponding)
        {
            return !aiEnabled || !bridgeResponding;
        }

        /// <summary>A health result older than this counts as "bridge absent" (watchdog polls ~2 s; 3 missed checks).</summary>
        internal const double RespondingFreshSeconds = 10;

        internal static bool IsFresh(double lastOkUtcSeconds, double nowUtcSeconds)
        {
            return lastOkUtcSeconds > 0 && nowUtcSeconds - lastOkUtcSeconds <= RespondingFreshSeconds;
        }

        /// <summary>Sabotage revert runs in flight when the native controller is flying OR when this safety net is
        /// standing in for a missing bridge watcher (parity with emergency.py's in-flight checks).</summary>
        internal static bool ShouldRevert(bool safetyNet, bool nativeActive, bool flying)
        {
            return flying && (nativeActive || safetyNet);
        }

        /// <summary>Parking brake for wheeled grounded craft (parity with kspchat/parking.py):
        /// re-arm after landing from flight; set once on the ground when idle; a player brake-off releases
        /// for the session; takeoff/taxi/landing modes are exempt. Returns
        /// "rearm" | "set" | "released" | "none".</summary>
        internal static string ParkingAction(bool grounded, bool wasFlying, bool released, bool alreadySet,
            bool idleMode, bool hasWheels, bool brakesOn, bool brakesTurnedOff)
        {
            if (grounded && wasFlying) return "rearm";
            if (!grounded || released || !idleMode || !hasWheels) return "none";
            if (brakesTurnedOff) return "released";   // player turned the parking brake off (any time)
            if (!alreadySet && !brakesOn) return "set";
            return alreadySet && !brakesOn ? "released" : "none";   // belt-and-braces vs a missed transition
        }
    }
}