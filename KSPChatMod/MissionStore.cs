using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KSPChatBridge
{
    internal sealed class MissionLeg
    {
        public string From, To;
        public double PlannedDv;
    }

    internal sealed class MissionData
    {
        public int SchemaVersion = 1;
        public string TargetBodyName;
        public List<MissionLeg> Legs = new List<MissionLeg>();
        public double? DepartureUT;
    }

    /// <summary>One chosen mission per craft name or stringified vessel persistentId.</summary>
    internal static class MissionStore
    {
        internal static string PathOverride;

        static string FilePath(string craftKey)
        {
            if (string.IsNullOrWhiteSpace(craftKey)) throw new ArgumentException("Craft key is required.");
            // A fixed prefix also makes Windows device names (CON, AUX, etc.) safe.
            var name = new StringBuilder("mission_");
            foreach (char c in craftKey.Trim())
                name.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string dir = PathOverride ?? Path.Combine(AicsCore.PluginDataDirectory, "missions");
            return Path.Combine(dir, name + ".json");
        }

        internal static void Save(string craftKey, MissionData m)
        {
            if (m == null || m.SchemaVersion != 1 || string.IsNullOrWhiteSpace(m.TargetBodyName) || m.Legs == null)
                throw new ArgumentException("Invalid mission.");
            var legs = new List<object>();
            foreach (var leg in m.Legs)
            {
                if (leg == null || string.IsNullOrWhiteSpace(leg.From) || string.IsNullOrWhiteSpace(leg.To) || !ValidNumber(leg.PlannedDv))
                    throw new ArgumentException("Invalid mission leg.");
                legs.Add(new Dictionary<string, object> { { "from", leg.From }, { "to", leg.To }, { "plannedDv", leg.PlannedDv } });
            }
            var data = new Dictionary<string, object> {
                { "schemaVersion", m.SchemaVersion }, { "targetBodyName", m.TargetBodyName }, { "legs", legs }
            };
            if (m.DepartureUT.HasValue)
            {
                if (!ValidNumber(m.DepartureUT.Value)) throw new ArgumentException("Invalid departure UT.");
                data["departureUT"] = m.DepartureUT.Value;
            }
            string path = FilePath(craftKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AtomicFile.Write(path, MiniJson.Serialize(data));
        }

        internal static MissionData Load(string craftKey)
        {
            try
            {
                string path = FilePath(craftKey);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path);
                if (AtomicFile.Torn(text)) return null;
                // Wrapping the root makes MiniJson reject trailing non-JSON garbage too.
                var roots = MiniJson.DeserializeObject("[" + text + "]") as IList;
                if (roots == null || roots.Count != 1) return null;
                var data = roots[0] as Dictionary<string, object>;
                if (data == null || !(data["schemaVersion"] is int) || (int)data["schemaVersion"] != 1) return null;
                var m = new MissionData { TargetBodyName = data["targetBodyName"] as string };
                if (string.IsNullOrWhiteSpace(m.TargetBodyName)) return null;
                var legs = data["legs"] as IList;
                if (legs == null) return null;
                foreach (object entry in legs)
                {
                    var leg = entry as Dictionary<string, object>;
                    if (leg == null) return null;
                    string from = leg["from"] as string, to = leg["to"] as string;
                    double dv;
                    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to) || !ReadNumber(leg["plannedDv"], out dv)) return null;
                    m.Legs.Add(new MissionLeg { From = from, To = to, PlannedDv = dv });
                }
                object departure;
                if (data.TryGetValue("departureUT", out departure) && departure != null)
                {
                    double ut;
                    if (!ReadNumber(departure, out ut)) return null;
                    m.DepartureUT = ut;
                }
                return m;
            }
            catch (Exception) { return null; }
        }

        internal static void Delete(string craftKey)
        {
            File.Delete(FilePath(craftKey));
        }

        static bool ValidNumber(double value) { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0; }

        static bool ReadNumber(object value, out double number)
        {
            number = 0;
            if (!(value is int) && !(value is long) && !(value is double)) return false;
            number = Convert.ToDouble(value);
            return ValidNumber(number);
        }
    }
}
