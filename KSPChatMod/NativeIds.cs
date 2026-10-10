using System;
using System.Globalization;

namespace KSPChatBridge
{
    // P5-1.9: one identity story for native tools. Vessel = Vessel.id GUID ("D" form); part = Part.flightID (decimal,
    // invariant).
    internal static class NativeIds
    {
        internal static string Vessel(Guid id) { return id.ToString("D"); }
        internal static string Part(uint flightId) { return flightId.ToString(CultureInfo.InvariantCulture); }
        internal static bool SameVessel(string a, string b)
        {
            Guid ga, gb;
            return Guid.TryParse(a ?? "", out ga) && Guid.TryParse(b ?? "", out gb) && ga == gb;
        }
    }
}
