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
        public double Thrust;
        public double FuelFlow;
        public int Stage;

        internal static LiveRocket Read(Vessel v)
        {
            if (v == null) return null;

            double maxTempRatio = 0;
            double thrust = 0;
            double fuelFlow = 0;
            foreach (Part part in v.parts)
            {
                if (part.maxTemp > 0)
                    maxTempRatio = System.Math.Max(maxTempRatio, part.temperature / part.maxTemp);
                foreach (PartModule module in part.Modules)
                {
                    ModuleEngines e = module as ModuleEngines;
                    if (e != null && e.EngineIgnited && !e.flameout)
                    {
                        thrust += e.GetMaxThrust() * e.thrustPercentage / 100;
                        fuelFlow += e.maxFuelFlow * e.thrustPercentage / 100;
                    }
                }
            }

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
                Thrust = thrust,
                FuelFlow = fuelFlow,
                Stage = v.currentStage
            };
        }
    }
}
