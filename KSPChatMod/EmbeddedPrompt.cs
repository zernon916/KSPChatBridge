using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace KSPChatBridge
{
    // P5-5: OpenAI-shaped request <-> Qwen2.5 ChatML (Hermes-style tools) for the embedded provider. Pure, so the
    // offline suite pins prompt rendering and <tool_call> parsing; InModChatSession stays provider-agnostic.
    internal static class EmbeddedPrompt
    {
        const string ToolsPreamble = "\n\n# Tools\n\nYou may call one or more functions to assist with the user query.\n\nYou are provided with function signatures within <tools></tools> XML tags:\n<tools>";
        const string ToolsPostamble = "\n</tools>\n\nFor each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:\n<tool_call>\n{\"name\": <function-name>, \"arguments\": <args-json-object>}\n</tool_call>";

        static IList List(object o) { return o as IList; }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        /// <summary>Render messages (+ optional tools array) to a ChatML prompt ending with the assistant header.</summary>
        internal static string Render(IList messages, IList tools)
        {
            var sb = new StringBuilder();
            int i = 0;
            string system = "You are a helpful assistant.";
            if (messages.Count > 0 && S(messages[0] as Dictionary<string, object>, "role") == "system") { system = S((Dictionary<string, object>)messages[0], "content"); i = 1; }
            sb.Append("<|im_start|>system\n").Append(system);
            if (tools != null && tools.Count > 0)
            {
                sb.Append(ToolsPreamble);
                foreach (object t in tools) sb.Append('\n').Append(MiniJson.Serialize(t));
                sb.Append(ToolsPostamble);
            }
            sb.Append("<|im_end|>\n");
            bool inToolBlock = false;
            for (; i < messages.Count; i++)
            {
                var m = messages[i] as Dictionary<string, object>; if (m == null) continue;
                string role = S(m, "role"), content = S(m, "content");
                if (role == "tool")
                {
                    if (!inToolBlock) { sb.Append("<|im_start|>user"); inToolBlock = true; }
                    sb.Append("\n<tool_response>\n").Append(content).Append("\n</tool_response>");
                    bool nextIsTool = i + 1 < messages.Count && S(messages[i + 1] as Dictionary<string, object>, "role") == "tool";
                    if (!nextIsTool) { sb.Append("<|im_end|>\n"); inToolBlock = false; }
                    continue;
                }
                sb.Append("<|im_start|>").Append(role == "assistant" ? "assistant" : role == "system" ? "system" : "user").Append('\n').Append(content);
                object calls;
                if (role == "assistant" && m.TryGetValue("tool_calls", out calls) && List(calls) != null)
                    foreach (object c in List(calls))
                    {
                        var fn = (c as Dictionary<string, object>) == null ? null : ((Dictionary<string, object>)c).ContainsKey("function") ? ((Dictionary<string, object>)c)["function"] as Dictionary<string, object> : null;
                        if (fn == null) continue;
                        string args = S(fn, "arguments"); if (args.Trim().Length == 0) args = "{}";
                        sb.Append(content.Length > 0 ? "\n" : "").Append("<tool_call>\n{\"name\": ").Append(MiniJson.Serialize(S(fn, "name"))).Append(", \"arguments\": ").Append(args).Append("}\n</tool_call>");
                        content = "x";   // later calls get a newline separator
                    }
                sb.Append("<|im_end|>\n");
            }
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        static readonly Regex Call = new Regex(@"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline);

        /// <summary>Model text -> OpenAI chat.completions JSON (content + tool_calls).</summary>
        internal static string ToOpenAi(string text)
        {
            text = (text ?? "").Replace("<|im_end|>", "").Replace("<|endoftext|>", "");
            var calls = new ArrayList(); int n = 0;
            foreach (Match m in Call.Matches(text))
            {
                Dictionary<string, object> obj;
                try { obj = MiniJson.Deserialize(m.Groups[1].Value); } catch (Exception) { continue; }
                object name, args; obj.TryGetValue("name", out name); obj.TryGetValue("arguments", out args);
                if (name == null) continue;
                string argJson = args == null ? "{}" : args is string ? (string)args : MiniJson.Serialize(args);
                calls.Add(new Dictionary<string, object> { { "id", "call_" + (++n) }, { "type", "function" },
                    { "function", new Dictionary<string, object> { { "name", name.ToString() }, { "arguments", argJson } } } });
            }
            string content = Call.Replace(text, "").Trim();
            // a dangling, unterminated <tool_call> means the reply was cut off; don't show raw JSON to the player
            int open = content.IndexOf("<tool_call>", StringComparison.Ordinal);
            if (open >= 0) content = content.Substring(0, open).Trim();
            var message = new Dictionary<string, object> { { "role", "assistant" }, { "content", content } };
            if (calls.Count > 0) message["tool_calls"] = calls;
            return MiniJson.Serialize(new Dictionary<string, object> { { "choices", new ArrayList { new Dictionary<string, object> { { "message", message } } } } });
        }
    }
}
