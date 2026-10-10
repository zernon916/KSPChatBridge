using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KSPChatBridge
{
    /// <summary>Round 3: destructive tools (eject, stage, abort, recover, cut engines, deorbit...) need Luke's explicit "yes" first,
    /// whether the pre-router or the model asked for them. Pure (clock injected).</summary>
    internal static class DestructiveConfirm
    {
        internal const double WindowS = 60;
        static string pendingName, pendingArgs; static double pendingAt = double.NegativeInfinity;
        static readonly Regex Yes = new Regex(@"^\s*(yes|y|yep|yeah|confirm(ed)?|affirmative|do it|go ahead|approved?)\b[\s.!]*", RegexOptions.IgnoreCase);
        static readonly Regex No = new Regex(@"^\s*(no|n|nope|cancel|abort that|don'?t|negative|belay that)\b", RegexOptions.IgnoreCase);

        internal static bool Needs(string tool) { return ToolRouter.Destructive.Contains(tool ?? "") || tool == "eject" || tool == "eject_kerbal" || tool == "deorbit_burn"; }
        internal static string Pending { get { return pendingName; } }

        /// <summary>Hold the request; returns the question for the player (and the model).</summary>
        internal static string Request(string tool, string args, double now)
        {
            pendingName = tool; pendingArgs = string.IsNullOrEmpty(args) ? "{}" : args; pendingAt = now;
            ChatLog.Write("confirm", "holding " + tool + " " + pendingArgs);
            return "NOT DONE - " + tool.Replace('_', ' ') + " needs Luke's confirmation. Ask him to reply yes (or no) within a minute.";
        }

        /// <summary>Player text: "yes" within the window -> (tool, args with confirmed=true) to run; "no" cancels. null otherwise.</summary>
        internal static KeyValuePair<string, string>? Answer(string text, double now, out string reply)
        {
            reply = null;
            if (pendingName == null) return null;
            if (now - pendingAt > WindowS) { pendingName = null; return null; }
            if (No.IsMatch(text ?? "")) { reply = "Cancelled: " + pendingName.Replace('_', ' ') + "."; pendingName = null; ChatLog.Write("confirm", "cancelled"); return null; }
            if (!Yes.IsMatch(text ?? "")) return null;
            string args = pendingArgs.Trim();
            args = args == "{}" ? "{\"confirmed\":true}" : args.TrimEnd('}') + ",\"confirmed\":true}";
            var r = new KeyValuePair<string, string>(pendingName, args); pendingName = null;
            ChatLog.Write("confirm", "yes -> " + r.Key);
            return r;
        }
        internal static void Clear() { pendingName = null; }
    }
}
