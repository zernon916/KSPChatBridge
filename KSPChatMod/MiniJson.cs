using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KSPChatBridge
{
    /// <summary>Tiny JSON for Unity/KSP Mono — no System.Web.Extensions.</summary>
    internal static class MiniJson
    {
        internal static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        internal static object DeserializeObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            return ParseValue(json, ref i);
        }

        internal static Dictionary<string, object> Deserialize(string json)
        {
            object o = DeserializeObject(json);
            var dict = o as Dictionary<string, object>;
            if (dict != null) return dict;
            throw new ArgumentException("JSON root is not an object");
        }

        static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is int || value is long || value is short || value is byte || value is uint || value is ulong)
            { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (value is float || value is double || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = value as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, e.Value);
                }
                sb.Append('}');
                return;
            }
            var list = value as IList;
            if (list != null)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, list[i]);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        static object ParseValue(string json, ref int i)
        {
            SkipWs(json, ref i);
            if (i >= json.Length) return null;
            char c = json[i];
            if (c == '{') return ParseObject(json, ref i);
            if (c == '[') return ParseArray(json, ref i);
            if (c == '"') return ParseString(json, ref i);
            if (c == 't' || c == 'f') return ParseBool(json, ref i);
            if (c == 'n') { Expect(json, ref i, "null"); return null; }
            return ParseNumber(json, ref i);
        }

        static Dictionary<string, object> ParseObject(string json, ref int i)
        {
            var dict = new Dictionary<string, object>();
            i++; // {
            while (true)
            {
                SkipWs(json, ref i);
                if (i >= json.Length) throw new ArgumentException("Unterminated object");
                if (json[i] == '}') { i++; return dict; }
                string key = ParseString(json, ref i);
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':') throw new ArgumentException("Expected ':'");
                i++;
                dict[key] = ParseValue(json, ref i);
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                if (i < json.Length && json[i] == '}') { i++; return dict; }
                throw new ArgumentException("Expected ',' or '}'");
            }
        }

        static ArrayList ParseArray(string json, ref int i)
        {
            var list = new ArrayList();
            i++; // [
            while (true)
            {
                SkipWs(json, ref i);
                if (i >= json.Length) throw new ArgumentException("Unterminated array");
                if (json[i] == ']') { i++; return list; }
                list.Add(ParseValue(json, ref i));
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                if (i < json.Length && json[i] == ']') { i++; return list; }
                throw new ArgumentException("Expected ',' or ']'");
            }
        }

        static string ParseString(string json, ref int i)
        {
            if (json[i] != '"') throw new ArgumentException("Expected string");
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= json.Length) break;
                char e = json[i++];
                switch (e)
                {
                    case '"': case '\\': case '/': sb.Append(e); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length) throw new ArgumentException("Bad unicode escape");
                        sb.Append((char)int.Parse(json.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new ArgumentException("Unterminated string");
        }

        static object ParseBool(string json, ref int i)
        {
            if (json[i] == 't') { Expect(json, ref i, "true"); return true; }
            Expect(json, ref i, "false");
            return false;
        }

        static object ParseNumber(string json, ref int i)
        {
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            while (i < json.Length && char.IsDigit(json[i])) i++;
            bool frac = false;
            if (i < json.Length && json[i] == '.')
            {
                frac = true; i++;
                while (i < json.Length && char.IsDigit(json[i])) i++;
            }
            if (i < json.Length && (json[i] == 'e' || json[i] == 'E'))
            {
                frac = true; i++;
                if (i < json.Length && (json[i] == '+' || json[i] == '-')) i++;
                while (i < json.Length && char.IsDigit(json[i])) i++;
            }
            string token = json.Substring(start, i - start);
            if (!frac)
            {
                long l;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)
                    && l >= int.MinValue && l <= int.MaxValue)
                    return (int)l;
                if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return l;
            }
            return double.Parse(token, CultureInfo.InvariantCulture);
        }

        static void Expect(string json, ref int i, string word)
        {
            if (i + word.Length > json.Length || json.Substring(i, word.Length) != word)
                throw new ArgumentException("Expected " + word);
            i += word.Length;
        }

        static void SkipWs(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }
    }
}
