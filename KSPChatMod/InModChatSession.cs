using System.Linq;
using System;
using System.Collections;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class InModChatSession
    {
        const int MaxRounds = 8;
        readonly List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
        static string ToolsJson { get { return NativeToolSchemas.Json; } }   // every ported tool (generated)
        readonly string baseSystem; string notesRaw = "";
        internal string Persona = "";
        internal string Craft = "";        // plane | heli | rocket | rover | "" (tool filtering)
        internal bool TrimTools = true;    // offer only matched tools + find_tool (small local models)
        readonly Func<OpenAiBackend.Endpoint, string, string> complete;
        internal InModChatSession() : this(null) { }
        internal InModChatSession(Func<OpenAiBackend.Endpoint, string, string> completionsOverride)
        {
            complete = completionsOverride;
            string system = "You are AICS flying Kerbal Space Program. Use tools for game actions. Be concise.";
            notesRaw = PlaystyleNotes.NotesBlock();
            baseSystem = system;
            messages.Add(Msg("system", system));
        }
        static Dictionary<string, object> Msg(string role, string content)
        {
            return new Dictionary<string, object> { { "role", role }, { "content", content ?? "" } };
        }
        internal string Process(string userText, string provider, Func<string, string, string> executeTool)
        {
            var ep = OpenAiBackend.Resolve(provider);
            if (!ep.Ok) return ep.Error;
            string notes = PilotPolicy.FilterNotes(notesRaw, Craft);   // planes don't get parking-orbit / fuel-transfer notes
            messages[0]["content"] = baseSystem + (notes.Length > 0 ? "\n" + notes : "") + PilotPolicy.ToolRules
                + (string.IsNullOrEmpty(Craft) ? "" : "\nCraft: " + Craft + ".") + (Persona ?? "");
            if (TrimTools) CompactHistory();   // small models copy old narrated replies instead of calling tools
            messages.Add(Msg("user", userText));
            ChatLog.Write("player", userText + (string.IsNullOrEmpty(Craft) ? "" : " (craft " + Craft + ")"));
            var results = new List<string>(); bool nudged = false;
            if (messages.Count > 40) messages.RemoveRange(1, messages.Count - 39);
            var offered = TrimTools ? ToolRouter.Offer(userText, Craft) : new List<object>(((IList)MiniJson.DeserializeObject(ToolsJson)).Cast<object>());
            object tools = offered;
            var guard = new ToolLoopGuard(ChatOrchestrator.MaxToolLoops);   // tool budget per user request
            for (int round = 0; round < MaxRounds; round++)
            {
                ChatLog.Write("tools", ToolNames(offered));
                var body = new Dictionary<string, object> {
                    { "model", ep.Model }, { "messages", messages }, { "temperature", 0.3 }, { "stream", false }, { "tools", tools }
                };
                string raw;
                try
                {
                    string payload = MiniJson.Serialize(body);
                    raw = complete != null ? complete(ep, payload)
                        : ep.Url == OpenAiBackend.EmbeddedUrl ? EmbeddedLlm.Complete(ep, payload)
                        : OpenAiBackend.ChatCompletions(ep, payload);
                }
                catch (Exception ex) { ChatLog.Write("error", ex.Message); return ex.Message; }
                ChatLog.Write("raw", raw);
                Dictionary<string, object> parsed;
                try { parsed = MiniJson.Deserialize(raw); }
                catch (Exception) { return "In-mod AI: bad JSON from provider."; }
                object choicesObj;
                if (!parsed.TryGetValue("choices", out choicesObj)) return "In-mod AI: empty response.";
                var choices = AsArray(choicesObj);
                if (choices == null || choices.Count == 0) return "In-mod AI: empty choices.";
                var choice = choices[0] as Dictionary<string, object>;
                if (choice == null) return "In-mod AI: bad choice.";
                object msgObj; choice.TryGetValue("message", out msgObj);
                var message = msgObj as Dictionary<string, object>;
                if (message == null) return "In-mod AI: no message.";
                messages.Add(message);
                object toolCallsObj;
                if (!message.TryGetValue("tool_calls", out toolCallsObj) || toolCallsObj == null)
                {
                    object content; message.TryGetValue("content", out content);
                    string text = content == null ? "" : content.ToString();
                    if (TrimTools && round == 0 && !nudged)   // talked instead of acting: one retry with only the best tool
                    {
                        var top = ToolRouter.Rank(userText, ToolRouter.Descriptions, Craft, 1);
                        if (top.Count > 0 && top[0].Value >= 3)
                        {
                            nudged = true; messages.RemoveAt(messages.Count - 1);
                            offered = new List<object> { ToolRouter.Schemas[top[0].Key], MiniJson.DeserializeObject(ToolRouter.FindToolSchema) }; tools = offered;
                            messages.Add(Msg("user", "Do it now: call the " + top[0].Key + " tool for \"" + userText + "\". Do not just describe it."));
                            ChatLog.Write("nudge", top[0].Key);
                            continue;
                        }
                    }
                    return Final(text, results);
                }
                var toolCalls = AsArray(toolCallsObj);
                if (toolCalls == null || toolCalls.Count == 0)
                {
                    object content; message.TryGetValue("content", out content);
                    return Final(content == null ? "" : content.ToString(), results);
                }
                foreach (object tcObj in toolCalls)
                {
                    var tc = tcObj as Dictionary<string, object>;
                    if (tc == null) continue;
                    object idObj, fnObj; tc.TryGetValue("id", out idObj); tc.TryGetValue("function", out fnObj);
                    var fn = fnObj as Dictionary<string, object>;
                    string name = "", args = "{}";
                    if (fn != null)
                    {
                        object n, a; fn.TryGetValue("name", out n); fn.TryGetValue("arguments", out a);
                        name = n == null ? "" : n.ToString();
                        args = a == null || string.IsNullOrEmpty(a.ToString()) ? "{}" : a.ToString();
                    }
                    string result;
                    if (name == "find_tool")
                    {
                        string q = ""; try { object qv; var ad = MiniJson.Deserialize(args); if (ad != null && ad.TryGetValue("query", out qv) && qv != null) q = qv.ToString(); } catch (Exception) { }
                        List<object> found; result = ToolRouter.Find(q.Length > 0 ? q : userText, Craft, out found);
                        foreach (object f in found) if (!offered.Contains(f)) offered.Insert(offered.Count - 1, f);   // 2-step: real tools next round
                    }
                    else result = executeTool != null && guard.TryStep() ? executeTool(name, args) : "tool budget spent";
                    ChatLog.Write("call", name + " " + args + " -> " + result);
                    if (name != "find_tool") results.Add(result ?? "");
                    messages.Add(new Dictionary<string, object> {
                        { "role", "tool" }, { "tool_call_id", idObj == null ? "" : idObj.ToString() },
                        { "content", result ?? "" }
                    });
                }
            }
            return Final("In-mod AI: tool loop limit reached.", results);
        }

        /// <summary>The pilot's reply always carries the real tool results (no more "ok" when the command failed).</summary>
        internal static string Final(string text, List<string> results)
        {
            text = PilotPolicy.GuardReply((text ?? "").Trim(), results);
            var sb = new System.Text.StringBuilder(text.Length == 0 && results.Count == 0 ? "(no reply)" : text);
            foreach (string r in results)
            {
                string s = (r ?? "").Trim(); if (s.Length == 0) continue;
                if (s.Length > 160) s = s.Substring(0, 157) + "...";
                if (text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                sb.Append(sb.Length > 0 ? " [" : "[").Append(s).Append("]");
            }
            string final = sb.ToString();
            ChatLog.Write("reply", final);
            return final;
        }
        static string ToolNames(List<object> offered)
        {
            var names = new List<string>();
            foreach (object o in offered) { var d = o as Dictionary<string, object>; object f; if (d != null && d.TryGetValue("function", out f)) { var fd = f as Dictionary<string, object>; if (fd != null) names.Add(fd["name"].ToString()); } }
            return string.Join(",", names.ToArray());
        }
        /// <summary>Keep the system prompt + the last 2 player messages (no old narrated replies / stale tool results).</summary>
        void CompactHistory()
        {
            var keep = new List<Dictionary<string, object>>(); int users = 0;
            for (int i = messages.Count - 1; i >= 1 && users < 2; i--)
                if (messages[i].ContainsKey("role") && (string)messages[i]["role"] == "user" && !((string)messages[i]["content"]).StartsWith("Do it now:")) { keep.Insert(0, messages[i]); users++; }
            var sys = messages[0]; messages.Clear(); messages.Add(sys); messages.AddRange(keep);
        }

        static ArrayList AsArray(object value)
        {
            if (value == null) return null;
            var list = value as ArrayList;
            if (list != null) return list;
            var arr = value as object[];
            if (arr == null) return null;
            return new ArrayList(arr);
        }
    }
}
