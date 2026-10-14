using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class ResourceLines
    {
        static readonly string[] ResourceNames =
        {
            "LiquidFuel", "Oxidizer", "MonoPropellant", "ElectricCharge", "Ore"
        };

        internal static List<string> Lines(Vessel v)
        {
            var lines = new List<string>();
            if (v == null) return lines;

            foreach (string name in ResourceNames)
            {
                var definition = PartResourceLibrary.Instance.GetDefinition(name);
                double amount, max;
                v.GetConnectedResourceTotals(definition.id, out amount, out max);
                lines.Add(name + " " + amount.ToString("F0", CultureInfo.InvariantCulture) +
                    "/" + max.ToString("F0", CultureInfo.InvariantCulture));
            }

            return lines;
        }
    }
}
