using System;
using System.Globalization;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        double lateralBankAsk = double.NaN;
        float lateralLogNext;
        string lateralLogKey;

        // Observation only: measure real surface track without changing Track() or guidance.
        // Outputs are this plugin's final asks, not proof of physical actuator response.
        void LogLandingLateral(FlightCtrlState controls)
        {
            if (mode != "landing" || runway == null || vessel == null) return;
            string key = vessel.id + "|" + runway.Key + "|" + runway.Phase;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (key == lateralLogKey && now < lateralLogNext) return;
            lateralLogKey = key; lateralLogNext = now + 2;
            double east = Vector3d.Dot(vessel.srf_velocity, vessel.east);
            double north = Vector3d.Dot(vessel.srf_velocity, vessel.north);
            double horizontal = Math.Sqrt(east * east + north * north);
            double track = horizontal > 1 ? (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360 : double.NaN;
            ChatLog.Write("landing-lateral", string.Format(CultureInfo.InvariantCulture,
                "v={0} rw={1} phase={2} ut={3:F2} lat={4:F8} lon={5:F8} " +
                "thr={6:F8},{7:F8} end={8:F8},{9:F8} cross_m={10:F2} along_m={11:F2} " +
                "groundTrack_deg={12:F2} controlTrack_deg={13:F2} heading_deg={14:F2} " +
                "headingAsk_deg={15:F2} bankAsk_deg={16:F2} bank_deg={17:F2} " +
                "rollOut={18:F3} yawOut={19:F3} wheelOut={20:F3} rudderOwned={21} " +
                "learn={22} landed={23} horizontal_mps={24:F2}",
                vessel.id, runway.Key, runway.Phase, Planetarium.GetUniversalTime(),
                vessel.latitude, vessel.longitude, runway.Lat, runway.Lon, runway.EndLat, runway.EndLon,
                runway.Cross, runway.Along, track, Track(), FlightGlobals.ship_heading,
                heading, lateralBankAsk, Roll(), controls.roll, controls.yaw, controls.wheelSteer,
                rudderOn, learn == null ? "off" : learn.Phase, vessel.LandedOrSplashed, horizontal));
        }
    }
}
