using System;
using System.Reflection;

namespace KSPChatBridge
{
    internal static class RotorMeasurements
    {
        // KSP 1.12.5: UpdatePAWUI copies BaseServo.transformRateOfMotion into
        // currentRPM. Read the physics measurement even when the PAW is closed.
        internal static float Rpm(object rotor)
        {
            for (Type t = rotor.GetType(); t != null; t = t.BaseType)
            {
                var field = t.GetField("transformRateOfMotion", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) return Math.Abs(Convert.ToSingle(field.GetValue(rotor)));
            }
            return float.NaN; // never substitute setpoint or stale display field
        }
    }
}
