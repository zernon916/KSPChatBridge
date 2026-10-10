using System;

namespace KSPChatBridge
{
    // Native emergency / sabotage / parking / power policy. Pure policy so the
    // offline suite can pin the decisions; the Unity side (NativeFlightController.Update) just follows them.
    internal static class NativeSafety
    {
        /// <summary>Live bug Oct 9: takeoff never released the parking brake. Release while the takeoff roll is on the ground.</summary>
        internal static bool ReleaseForTakeoff(string mode, string phase, bool grounded, bool brakesOn)
        {
            return mode == "takeoff" && grounded && brakesOn && (phase == null || phase == "roll" || phase == "rotate");
        }

        /// <summary>Parking may only touch the brakes with no command / plan running.</summary>
        internal static bool ParkingIdle(string mode, bool commandActive) { return mode == "idle" && !commandActive; }
        /// <summary>Sabotage revert runs in flight while the native controller is flying.</summary>
        internal static bool ShouldRevert(bool nativeActive, bool flying) { return flying && nativeActive; }

        /// <summary>Parking brake for wheeled grounded craft:
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
