using System.Collections.Generic;

namespace KSPChatBridge
{
    internal static class StageReader
    {
        internal static List<StageInfo> Read(VesselDeltaV dv)
        {
            var stages = new List<StageInfo>();
            if (dv == null || !dv.IsReady) return stages;

            foreach (DeltaVStageInfo source in dv.OperatingStageInfo)
            {
                stages.Add(new StageInfo
                {
                    Stage = source.stage,
                    DvVac = source.deltaVinVac,
                    DvAsl = source.deltaVatASL,
                    DvActual = source.deltaVActual,
                    TwrVac = source.TWRVac,
                    TwrAsl = source.TWRASL,
                    BurnTime = source.stageBurnTime,
                    StartMass = source.startMass,
                    EndMass = source.endMass,
                    FuelMass = source.fuelMass,
                    IspVac = source.ispVac,
                    IspAsl = source.ispASL
                });
            }

            return stages;
        }
    }
}
