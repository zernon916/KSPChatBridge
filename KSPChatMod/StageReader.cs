using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    internal static class StageReader
    {
        static double lastNotReady = double.NegativeInfinity;

        internal static List<StageInfo> Read(VesselDeltaV dv)
        {
            var stages = new List<StageInfo>();
            if (dv == null || !dv.IsReady)
            {
                double now = Time.realtimeSinceStartup;
                if (now - lastNotReady >= 1)
                {
                    ChatLog.Write("rocket", "stage dv not ready");
                    lastNotReady = now;
                }
                return stages;
            }

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
