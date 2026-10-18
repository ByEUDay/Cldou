using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cldou.Core.Json
{
    /// <summary>
    /// 极简 JSON 读写器。
    ///
    /// 设计动机：Cldou 需要在不引入任何第三方依赖（Newtonsoft / System.Text.Json 均不保证在
    /// Unity Mono 运行时可用）的前提下，读写 mod 清单与状态快照。支持的类型映射：
    ///   object  { }        -> Dictionary&lt;string, object&gt;
    ///   array   [ ]        -> List&lt;object&gt;
    ///   string             -> string
    ///   number             -> double
    ///   true / false       -> bool
    ///   null               -> null
    /// </summary>
    public static class MiniJson
    {
        // ---------------------------------------------------------------
        // Parse
        // ---------------------------------------------------------------

        public static object Parse(string json)
        {
            object value;
            string error;
            if (!TryParse(json, out value, out error))
            {
                throw new FormatException(error);
            }
            return value;
        }

        public static bool TryParse(string json, out object value, out string error)
        {
            value = null;
            error = null;

            if (json == null)
            {
                error = "JSON 文本为 null";
                return false;
            }

            int index = 0;
            try
            {
                SkipWhitespace(json, ref index);
                value = ParseValue(json, ref index);
                SkipWhitespace(json, ref index);
                if (index != json.Length)
                {
                    error = "JSON 末尾存在多余字符（位置 " + index + "）";
                    return false;
                }
                return true;
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            if (i >= s.Length)
            {
                throw new FormatException("JSON 意外结束（位置 " + i + "）");
            }

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWhitespace(s, ref i);

            if (i < s.Length && s[i] == '}')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                {
                    throw new FormatException("JSON 对象缺少键名（位置 " + i + "）");
                }

                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);

                if (i >= s.Length || s[i] != ':')
                {
                    throw new FormatException("JSON 对象键 '" + key + "' 后缺少 ':'（位置 " + i + "）");
                }

                i++;
                SkipWhitespace(s, ref i);
                result[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);

                if (i >= s.Length)
                {
                    throw new FormatException("JSON 对象未闭合（位置 " + i + "）");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == '}')
                {
                    i++;
                    return result;
                }

                throw new FormatException("JSON 对象中出现意外字符 '" + s[i] + "'（位置 " + i + "）");
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);

            if (i < s.Length && s[i] == ']')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(s, ref i);
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);

                if (i >= s.Length)
                {
                    throw new FormatException("JSON 数组未闭合（位置 " + i + "）");
                }

                if (s[i] == ',')
                {
                    i++;
                    continue;
                }

                if (s[i] == ']')
                {
                    i++;
                    return result;
                }

                throw new FormatException("JSON 数组中出现意外字符 '" + s[i] + "'（位置 " + i + "）");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();

            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c == '\\')
                {
                    if (i >= s.Length)
                    {
                        break;
                    }

                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length)
                            {
                                throw new FormatException("JSON 转义 \\u 不完整（位置 " + i + "）");
                            }
                            string hex = s.Substring(i, 4);
                            ushort code;
                            if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                throw new FormatException("JSON 转义 \\u" + hex + " 非法");
                            }
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default:
                            throw new FormatException("JSON 非法转义 '\\" + e + "'（位置 " + (i - 1) + "）");
                    }
                    continue;
                }

                if (c < 0x20)
                {
                    throw new FormatException("JSON 字符串中出现控制字符（位置 " + (i - 1) + "）");
                }

                sb.Append(c);
            }

            throw new FormatException("JSON 字符串未闭合（位置 " + i + "）");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                i++;
            }

            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                {
                    i++;
                }
                else
                {
                    break;
                }
            }

            if (i == start)
            {
                throw new FormatException("JSON 非法值（位置 " + i + "）");
            }

            string token = s.Substring(start, i - start);
            double number;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                throw new FormatException("JSON 数字 '" + token + "' 解析失败");
            }
            return number;
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("JSON 期望 '" + literal + "'（位置 " + i + "）");
            }
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                {
                    i++;
                }
                else
                {
                    break;
                }
            }
        }

        // ---------------------------------------------------------------
        // Serialize
        // ---------------------------------------------------------------

        public static string Serialize(object value, bool pretty = true)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value, pretty ? 0 : -1, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, int indent, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (value is string)
            {
                WriteString(sb, (string)value);
                return;
            }

            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is IDictionary)
            {
                WriteObject(sb, (IDictionary)value, indent, depth);
                return;
            }

            if (value is IEnumerable && !(value is string))
            {
                WriteArray(sb, (IEnumerable)value, indent, depth);
                return;
            }

            // 其余按数字处理
            if (value is float) { sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is double) { WriteNumber(sb, (double)value); return; }
            if (value is decimal) { sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return; }

            WriteNumber(sb, Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
                return;
            }

            if (d == Math.Floor(d) && Math.Abs(d) < 9.007199254740992E15)
            {
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
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
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private static void WriteObject(StringBuilder sb, IDictionary dict, int indent, int depth)
        {
            sb.Append('{');
            bool first = true;

            foreach (DictionaryEntry entry in dict)
            {
                string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;

                if (indent >= 0)
                {
                    sb.Append('\n').Append(' ', (depth + 1) * indent);
                }

                WriteString(sb, key);
                sb.Append(':');
                if (indent >= 0)
                {
                    sb.Append(' ');
                }
                WriteValue(sb, entry.Value, indent, depth + 1);
            }

            if (!first && indent >= 0)
            {
                sb.Append('\n').Append(' ', depth * indent);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable items, int indent, int depth)
        {
            sb.Append('[');
            bool first = true;

            foreach (object item in items)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;

                if (indent >= 0)
                {
                    sb.Append('\n').Append(' ', (depth + 1) * indent);
                }
                WriteValue(sb, item, indent, depth + 1);
            }

            if (!first && indent >= 0)
            {
                sb.Append('\n').Append(' ', depth * indent);
            }
            sb.Append(']');
        }

        // ---------------------------------------------------------------
        // 便捷读取助手
        // ---------------------------------------------------------------

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            return value as List<object>;
        }

        public static string GetString(Dictionary<string, object> obj, string key, string fallback = null)
        {
            object value;
            if (obj != null && obj.TryGetValue(key, out value) && value is string)
            {
                return (string)value;
            }
            return fallback;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool fallback = false)
        {
            object value;
            if (obj != null && obj.TryGetValue(key, out value))
            {
                if (value is bool) return (bool)value;
                if (value is double) return ((double)value) != 0.0;
            }
            return fallback;
        }

        public static double GetDouble(Dictionary<string, object> obj, string key, double fallback = 0.0)
        {
            object value;
            if (obj != null && obj.TryGetValue(key, out value) && value is double)
            {
                return (double)value;
            }
            return fallback;
        }

        public static int GetInt(Dictionary<string, object> obj, string key, int fallback = 0)
        {
            object value;
            if (obj != null && obj.TryGetValue(key, out value) && value is double)
            {
                return (int)(double)value;
            }
            return fallback;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj != null && obj.TryGetValue(key, out value))
            {
                return value as Dictionary<string, object>;
            }
            return null;
        }

        public static List<object> GetArray(Dictionary<string, object> obj, string key)
        {
            object value;
            if (obj != null && obj.TryGetValue(key, out value))
            {
                return value as List<object>;
            }
            return null;
        }
    }
}
