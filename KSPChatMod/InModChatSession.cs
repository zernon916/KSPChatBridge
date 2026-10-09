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
        readonly string baseSystem;
        internal string Persona = "";
        internal string Craft = "";        // plane | heli | rocket | rover | "" (tool filtering)
        internal bool TrimTools = true;    // offer only matched tools + find_tool (small local models)
        readonly Func<OpenAiBackend.Endpoint, string, string> complete;
        internal InModChatSession() : this(null) { }
        internal InModChatSession(Func<OpenAiBackend.Endpoint, string, string> completionsOverride)
        {
            complete = completionsOverride;
            string system = "You are AICS flying Kerbal Space Program. Use tools for game actions. Be concise.";
            string notes = PlaystyleNotes.NotesBlock();
            if (notes.Length > 0) system += "\n" + notes;
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
            messages[0]["content"] = baseSystem + (Persona ?? "");
            messages.Add(Msg("user", userText));
            if (messages.Count > 40) messages.RemoveRange(1, messages.Count - 39);
            var offered = TrimTools ? ToolRouter.Offer(userText, Craft) : new List<object>(((IList)MiniJson.DeserializeObject(ToolsJson)).Cast<object>());
            object tools = offered;
            var guard = new ToolLoopGuard(ChatOrchestrator.MaxToolLoops);   // tool budget per user request
            for (int round = 0; round < MaxRounds; round++)
            {
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
                catch (Exception ex) { return ex.Message; }
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
                    return string.IsNullOrEmpty(text) ? "(no reply)" : text;
                }
                var toolCalls = AsArray(toolCallsObj);
                if (toolCalls == null || toolCalls.Count == 0)
                {
                    object content; message.TryGetValue("content", out content);
                    return content == null ? "(no reply)" : content.ToString();
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
                    messages.Add(new Dictionary<string, object> {
                        { "role", "tool" }, { "tool_call_id", idObj == null ? "" : idObj.ToString() },
                        { "content", result ?? "" }
                    });
                }
            }
            return "In-mod AI: tool loop limit reached.";
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
