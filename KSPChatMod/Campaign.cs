using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace KSPChatBridge
{
    internal sealed class CampaignPhase
    {
        public string Goal, Craft, Status, VesselId;
        public double Alt, Inc;
    }

    internal sealed class Campaign
    {
        public int SchemaVersion = 1;
        public string Name = "New campaign";
        public List<CampaignPhase> Phases = new List<CampaignPhase>();

        internal void AddPhase(CampaignPhase phase)
        {
            ValidatePhase(phase);
            Phases.Add(phase);
        }

        internal void UpdatePhase(int index, CampaignPhase phase)
        {
            ValidatePhase(phase);
            if (Phases == null || index < 0 || index >= Phases.Count)
                throw new ArgumentOutOfRangeException("index");
            Phases[index] = phase;
        }

        internal bool MovePhase(int index, int direction)
        {
            if (Phases == null || (direction != -1 && direction != 1) ||
                index < 0 || index >= Phases.Count)
                return false;
            int target = index + direction;
            if (target < 0 || target >= Phases.Count) return false;
            CampaignPhase phase = Phases[index];
            Phases[index] = Phases[target];
            Phases[target] = phase;
            return true;
        }

        internal Dictionary<string, object> ToJson()
        {
            Validate();
            var phases = new List<object>();
            foreach (CampaignPhase phase in Phases)
                phases.Add(new Dictionary<string, object>
                {
                    { "goal", phase.Goal }, { "craft", phase.Craft }, { "status", phase.Status },
                    { "vesselId", phase.VesselId }, { "alt", phase.Alt }, { "inc", phase.Inc }
                });
            return new Dictionary<string, object>
            {
                { "schemaVersion", SchemaVersion }, { "name", Name }, { "phases", phases }
            };
        }

        internal static Campaign FromJson(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentException("Campaign data is required.", "data");
            object version;
            if (!data.TryGetValue("schemaVersion", out version) || !IsNumber(version) ||
                Convert.ToDouble(version) != 1)
                throw new InvalidDataException("Unsupported campaign schema.");
            object nameValue, phasesValue;
            string name;
            if (!data.TryGetValue("name", out nameValue) || (name = nameValue as string) == null ||
                string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("Campaign name is missing.");
            if (!data.TryGetValue("phases", out phasesValue))
                throw new InvalidDataException("Campaign phases are missing.");
            IList phaseData = phasesValue as IList;
            if (phaseData == null) throw new InvalidDataException("Campaign phases are invalid.");

            var campaign = new Campaign { SchemaVersion = 1, Name = name };
            foreach (object entry in phaseData)
            {
                Dictionary<string, object> fields = entry as Dictionary<string, object>;
                if (fields == null) throw new InvalidDataException("Campaign phase is invalid.");
                object goalValue, craftValue, statusValue, vesselValue, altValue, incValue;
                double alt, inc;
                if (!fields.TryGetValue("goal", out goalValue) ||
                    !fields.TryGetValue("craft", out craftValue) ||
                    !fields.TryGetValue("status", out statusValue) ||
                    !fields.TryGetValue("vesselId", out vesselValue) ||
                    !OptionalString(statusValue) || !OptionalString(vesselValue) ||
                    !fields.TryGetValue("alt", out altValue) || !ReadNumber(altValue, out alt) ||
                    !fields.TryGetValue("inc", out incValue) || !ReadNumber(incValue, out inc))
                    throw new InvalidDataException("Campaign phase fields are invalid.");
                var phase = new CampaignPhase
                {
                    Goal = goalValue as string,
                    Craft = craftValue as string,
                    Status = statusValue as string,
                    VesselId = vesselValue as string,
                    Alt = alt,
                    Inc = inc
                };
                ValidatePhase(phase);
                campaign.Phases.Add(phase);
            }
            return campaign;
        }

        void Validate()
        {
            if (SchemaVersion != 1) throw new InvalidDataException("Unsupported campaign schema.");
            if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("Campaign name is required.");
            if (Phases == null) throw new InvalidDataException("Campaign phases are required.");
            foreach (CampaignPhase phase in Phases) ValidatePhase(phase);
        }

        static void ValidatePhase(CampaignPhase phase)
        {
            if (phase == null || string.IsNullOrWhiteSpace(phase.Goal) ||
                string.IsNullOrWhiteSpace(phase.Craft) || !ValidNumber(phase.Alt) || phase.Alt < 0 ||
                !ValidNumber(phase.Inc) || phase.Inc < 0 || phase.Inc > 180)
                throw new ArgumentException("Phase requires a goal, craft, non-negative altitude, and inclination from 0 to 180 degrees.");
        }

        static bool IsNumber(object value)
        {
            return value is int || value is long || value is double;
        }

        static bool OptionalString(object value)
        {
            return value == null || value is string;
        }

        static bool ReadNumber(object value, out double number)
        {
            number = 0;
            if (!IsNumber(value)) return false;
            number = Convert.ToDouble(value);
            return ValidNumber(number);
        }

        static bool ValidNumber(double number)
        {
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
    }

    internal static class CampaignStore
    {
        internal static Campaign Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Campaign path is required.", "path");
            if (!File.Exists(path)) return new Campaign();
            string text = File.ReadAllText(path);
            if (string.IsNullOrEmpty(text) || text.Trim('\0', ' ', '\r', '\n', '\t').Length == 0)
                throw new InvalidDataException("Campaign file is empty or incomplete.");
            IList roots = MiniJson.DeserializeObject("[" + text + "]") as IList;
            if (roots == null || roots.Count != 1)
                throw new InvalidDataException("Campaign file is not valid JSON.");
            Dictionary<string, object> data = roots[0] as Dictionary<string, object>;
            if (data == null) throw new InvalidDataException("Campaign file has an invalid root.");
            return Campaign.FromJson(data);
        }

        internal static void Save(string path, Campaign campaign)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Campaign path is required.", "path");
            if (campaign == null) throw new ArgumentNullException("campaign");
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, MiniJson.Serialize(campaign.ToJson()));
            if (File.Exists(path)) File.Replace(tempPath, path, null);
            else File.Move(tempPath, path);
        }
    }
}
