using System;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        TaxiMission.Point heliWaypoint;
        bool heliWaypointArrived;

        void HeliWaypointVelocity(ref double forward, ref double right)
        {
            if (heliWaypoint == null || vessel == null || vessel.mainBody == null || helicopterLanding) return;
            double distance = NavigationMath.Distance(vessel.latitude, vessel.longitude,
                heliWaypoint.Lat, heliWaypoint.Lon, vessel.mainBody.Radius);
            double bearing = NavigationMath.Bearing(vessel.latitude, vessel.longitude,
                heliWaypoint.Lat, heliWaypoint.Lon);
            if (distance < 5 && vessel.horizontalSrfSpeed < 1 && !heliWaypointArrived)
            {
                heliWaypointArrived = true;
                ChatLog.Write("heli", "arrived at " + heliWaypoint.Name + "; position hold");
            }
            if (heliWaypointArrived)
            {
                HelicopterPolicy.HoldVelocity(distance, bearing, FlightGlobals.ship_heading, out forward, out right);
                return;
            }
            double target = Math.Min(speed, Math.Max(0, distance) * speed / 200);
            double angle = FlightPolicy.Wrap(bearing - FlightGlobals.ship_heading) * Math.PI / 180;
            forward = target * Math.Cos(angle);
            right = target * Math.Sin(angle);
            if (distance > 10) heading = bearing;
        }
    }
}
