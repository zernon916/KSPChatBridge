using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class NativeCraftNotes
    {
        readonly Dictionary<string, object> records;
        internal NativeCraftNotes(Dictionary<string, object> settings)
        {
            object data; records = settings.TryGetValue("craft_notes", out data) ? data as Dictionary<string, object> : null;
            if (records == null) settings["craft_notes"] = records = new Dictionary<string, object>();
        }
        internal static bool Named(string name)
        { return !string.IsNullOrWhiteSpace(name) && name.Trim() != "Untitled Space Craft"; }
        internal Dictionary<string, object> Load(string name)
        {
            object record;
            var row = Named(name) && records.TryGetValue(name.Trim(), out record) ? record as Dictionary<string, object> : null;
            return row == null ? new Dictionary<string, object>() : new Dictionary<string, object>(row);
        }
        internal void Merge(string name, Dictionary<string, object> fields)
        {
            if (!Named(name)) throw new ArgumentException("Rename the craft before saving notes.");
            var row = Load(name);
            foreach (string key in new[] { "pitch_trim", "roll_trim", "yaw_trim", "pitch_deploy_bias", "elevator_deploy", "rotation_speed", "cruise_speed", "quirks", "rotor" })
            { object value; if (fields.TryGetValue(key, out value) && value != null) row[key] = value; }
            records[name.Trim()] = row;
        }
    }
}
