namespace KSPChatBridge
{
    internal sealed class LiveRocket
    {
        public double Alt;
        public double RadarAlt;
        public double Vs;
        public double Hs;
        public double QKpa;
        public double StaticPressure;
        public double Density;
        public double Mass;
        public double LocalG;
        public double MaxTempRatio;
        public double AngRate;
        public int Stage;

        internal static LiveRocket Read(Vessel v)
        {
            if (v == null) return null;

            double maxTempRatio = 0;
            foreach (Part part in v.parts)
                if (part.maxTemp > 0)
                    maxTempRatio = System.Math.Max(maxTempRatio, part.temperature / part.maxTemp);

            return new LiveRocket
            {
                Alt = v.altitude,
                RadarAlt = v.radarAltitude,
                Vs = v.verticalSpeed,
                Hs = v.horizontalSrfSpeed,
                QKpa = v.dynamicPressurekPa,
                StaticPressure = v.staticPressurekPa,
                Density = v.atmDensity,
                Mass = v.totalMass,
                LocalG = v.graviticAcceleration.magnitude,
                MaxTempRatio = maxTempRatio,
                AngRate = v.angularVelocity.magnitude,
                Stage = v.currentStage
            };
        }
    }
}
