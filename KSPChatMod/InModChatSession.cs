using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace KSPChatBridge
{
    internal sealed class InModChatSession
    {
        const int MaxRounds = 8;
        readonly List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        static readonly string ToolsJson = @"[
{""type"":""function"",""function"":{""name"":""get_status"",""description"":""Vessel status"",""parameters"":{""type"":""object"",""properties"":{}}}},
{""type"":""function"",""function"":{""name"":""plane_hold"",""description"":""Engage aircraft holds"",""parameters"":{""type"":""object"",""properties"":{""engage"":{""type"":""boolean""},""altitude_m"":{""type"":""number""},""heading"":{""type"":""number""},""speed"":{""type"":""number""},""altitude_ref"":{""type"":""string""}}}}},
{""type"":""function"",""function"":{""name"":""takeoff"",""description"":""Aircraft takeoff"",""parameters"":{""type"":""object"",""properties"":{""altitude_m"":{""type"":""number""}}}}},
{""type"":""function"",""function"":{""name"":""land_here"",""description"":""Powered descent here"",""parameters"":{""type"":""object"",""properties"":{""touchdown_speed"":{""type"":""number""}}}}},
{""type"":""function"",""function"":{""name"":""land_at_spot"",""description"":""Runway land"",""parameters"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""runway"":{""type"":""string""}}}}},
{""type"":""function"",""function"":{""name"":""heli_control"",""description"":""Helicopter hover/fly/land"",""parameters"":{""type"":""object"",""properties"":{""mode"":{""type"":""string""},""altitude_m"":{""type"":""number""},""heading"":{""type"":""number""},""speed"":{""type"":""number""}}}}},
{""type"":""function"",""function"":{""name"":""abort"",""description"":""Hard abort"",""parameters"":{""type"":""object"",""properties"":{}}}},
{""type"":""function"",""function"":{""name"":""stop_current"",""description"":""Stop controller"",""parameters"":{""type"":""object"",""properties"":{}}}},
{""type"":""function"",""function"":{""name"":""set_gear"",""description"":""Gear"",""parameters"":{""type"":""object"",""properties"":{""down"":{""type"":""boolean""}}}}},
{""type"":""function"",""function"":{""name"":""set_brakes"",""description"":""Brakes"",""parameters"":{""type"":""object"",""properties"":{""on"":{""type"":""boolean""}}}}},
{""type"":""function"",""function"":{""name"":""taxi_to"",""description"":""Taxi"",""parameters"":{""type"":""object"",""properties"":{""name"":{""type"":""string""}}}}},
{""type"":""function"",""function"":{""name"":""save_craft_notes"",""description"":""Save trim and cruise notes for this craft"",""parameters"":{""type"":""object"",""properties"":{}}}},
{""type"":""function"",""function"":{""name"":""get_trim_state"",""description"":""Trim state"",""parameters"":{""type"":""object"",""properties"":{}}}},
{""type"":""function"",""function"":{""name"":""set_trim"",""description"":""Set trim axis/value"",""parameters"":{""type"":""object"",""properties"":{""axis"":{""type"":""string""},""value"":{""type"":""number""}}}}}
]";
        readonly Func<OpenAiBackend.Endpoint, string, string> complete;
        internal InModChatSession() : this(null) { }
        internal InModChatSession(Func<OpenAiBackend.Endpoint, string, string> completionsOverride)
        {
            complete = completionsOverride;
            messages.Add(Msg("system", "You are AICS flying Kerbal Space Program. Use tools for game actions. Be concise."));
        }
        static Dictionary<string, object> Msg(string role, string content)
        {
            return new Dictionary<string, object> { { "role", role }, { "content", content ?? "" } };
        }
        internal string Process(string userText, string provider, Func<string, string, string> executeTool)
        {
            var ep = OpenAiBackend.Resolve(provider);
            if (!ep.Ok) return ep.Error;
            messages.Add(Msg("user", userText));
            if (messages.Count > 40) messages.RemoveRange(1, messages.Count - 39);
            object tools = Json.DeserializeObject(ToolsJson);
            for (int round = 0; round < MaxRounds; round++)
            {
                var body = new Dictionary<string, object> {
                    { "model", ep.Model }, { "messages", messages }, { "temperature", 0.3 }, { "tools", tools }
                };
                string raw;
                try
                {
                    string payload = Json.Serialize(body);
                    raw = complete != null ? complete(ep, payload) : OpenAiBackend.ChatCompletions(ep, payload);
                }
                catch (Exception ex) { return ex.Message; }
                Dictionary<string, object> parsed;
                try { parsed = Json.Deserialize<Dictionary<string, object>>(raw); }
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
                    string result = executeTool != null ? executeTool(name, args) : "no tool executor";
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
