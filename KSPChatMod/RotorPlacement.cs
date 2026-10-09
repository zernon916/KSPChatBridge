using System;
namespace KSPChatBridge
{
    internal static class RotorPlacement
    {
        // Coordinates in the selected control frame: right, forward, up.
        internal static string Label(double right, double forward, double axisRight, double axisForward, double axisUp, int liftCount)
        {
            if (Math.Abs(axisForward) >= .7) return "PU - Pusher/Puller";
            if (Math.Abs(axisRight) >= .7) return "TR - Tail Rotor";
            if (Math.Abs(axisUp) < .7) return "C - Canted Rotor";
            if (liftCount == 1) return "MR - Main Rotor";
            string side = right < -.25 ? "L" : right > .25 ? "R" : "";
            string end = forward < -.25 ? "R" : forward > .25 ? "F" : "";
            string words = (side == "L" ? "Left" : side == "R" ? "Right" : "")
                + (end == "F" ? " Front" : end == "R" ? " Rear" : "");
            return side + end == "" ? "C - Center" : side + end + " - " + words.Trim();
        }
        internal static string Status(double rpm, double limit, bool motor, double brake, bool flying)
        {
            if (double.IsNaN(rpm) || double.IsNaN(limit)) return "caution";
            if (!motor || brake > .5 || (limit > 1 && rpm < .5 * limit)) return flying ? "fail" : "caution";
            return limit > 1 && rpm < .9 * limit ? "caution" : "ok";
        }
    }
}
