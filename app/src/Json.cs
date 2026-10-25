using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DreTunes
{
    /// <summary>
    /// A small, strict JSON reader and writer.
    ///
    /// Why not JavaScriptSerializer / DataContractJsonSerializer: both are framework-wide
    /// contracts with their own surprises (dates, dictionary keys, numbers-as-strings), and the
    /// bridge's payload is plain JSON with four shapes. This is 200 lines, has no assembly
    /// dependency, and its behaviour is fixed by the self-test in SelfTest.cs - including the
    /// case where a truncated payload must throw instead of returning a half object.
    ///
    /// Values come back as: Dictionary&lt;string,object&gt;, List&lt;object&gt;, string, double,
    /// bool, or null. Numbers are always double (the bridge never sends integers above 2^53).
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new JsonException("null text");
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new JsonException("trailing text at " + i);
            return v;
        }

        public sealed class JsonException : Exception
        {
            public JsonException(string m) : base(m) { }
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new JsonException("unexpected end of input");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length) throw new JsonException("truncated literal at " + i);
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new JsonException("expected " + word + " at " + i);
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;                                   // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new JsonException("expected key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new JsonException("expected ':' at " + i);
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new JsonException("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> a = new List<object>();
            i++;                                   // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new JsonException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder b = new StringBuilder();
            i++;                                   // opening quote
            while (true)
            {
                if (i >= s.Length) throw new JsonException("unterminated string");
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                if (i >= s.Length) throw new JsonException("unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new JsonException("truncated \\u escape");
                        b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                 CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new JsonException("bad escape \\" + e);
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { i++; continue; }
                break;
            }
            if (i == start) throw new JsonException("expected a value at " + start);
            double v;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out v))
                throw new JsonException("bad number '" + s.Substring(start, i - start) + "'");
            return v;
        }

        // ------------------------------------------------------------------ accessors

        public static Dictionary<string, object> Obj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> Arr(object o)
        {
            return o as List<object>;
        }

        public static object Get(Dictionary<string, object> o, string key)
        {
            if (o == null) return null;
            object v;
            return o.TryGetValue(key, out v) ? v : null;
        }

        public static string S(Dictionary<string, object> o, string key, string dflt)
        {
            object v = Get(o, key);
            return v is string ? (string)v : dflt;
        }

        public static double D(Dictionary<string, object> o, string key, double dflt)
        {
            object v = Get(o, key);
            if (v is double) return (double)v;
            if (v is bool) return ((bool)v) ? 1.0 : 0.0;
            if (v is string)
            {
                double p;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out p)) return p;
            }
            return dflt;
        }

        public static int I(Dictionary<string, object> o, string key, int dflt)
        {
            double v = D(o, key, dflt);
            return (int)Math.Round(v, MidpointRounding.AwayFromZero);
        }

        public static bool B(Dictionary<string, object> o, string key, bool dflt)
        {
            object v = Get(o, key);
            if (v is bool) return (bool)v;
            if (v is double) return ((double)v) != 0.0;
            return dflt;
        }

        public static double[] DArr(Dictionary<string, object> o, string key)
        {
            List<object> a = Arr(Get(o, key));
            if (a == null) return null;
            double[] r = new double[a.Count];
            for (int i = 0; i < a.Count; i++)
                r[i] = (a[i] is double) ? (double)a[i] : 0.0;
            return r;
        }

        public static bool[] BArr(Dictionary<string, object> o, string key)
        {
            List<object> a = Arr(Get(o, key));
            if (a == null) return null;
            bool[] r = new bool[a.Count];
            for (int i = 0; i < a.Count; i++)
            {
                object v = a[i];
                r[i] = (v is bool) ? (bool)v : (v is double && (double)v != 0.0);
            }
            return r;
        }

        // ------------------------------------------------------------------ writer

        /// <summary>Compact JSON. Used for the bridge request bodies and the app's own files.</summary>
        public static string Write(object o)
        {
            StringBuilder b = new StringBuilder();
            WriteValue(b, o);
            return b.ToString();
        }

        private static void WriteValue(StringBuilder b, object o)
        {
            if (o == null) { b.Append("null"); return; }
            if (o is string) { WriteString(b, (string)o); return; }
            if (o is bool) { b.Append(((bool)o) ? "true" : "false"); return; }
            if (o is double) { b.Append(Num((double)o)); return; }
            if (o is float) { b.Append(Num((float)o)); return; }
            if (o is int) { b.Append(((int)o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is long) { b.Append(((long)o).ToString(CultureInfo.InvariantCulture)); return; }
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null)
            {
                b.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) b.Append(',');
                    first = false;
                    WriteString(b, kv.Key);
                    b.Append(':');
                    WriteValue(b, kv.Value);
                }
                b.Append('}');
                return;
            }
            List<object> a = o as List<object>;
            if (a != null)
            {
                b.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) b.Append(',');
                    WriteValue(b, a[i]);
                }
                b.Append(']');
                return;
            }
            double[] da = o as double[];
            if (da != null)
            {
                b.Append('[');
                for (int i = 0; i < da.Length; i++)
                {
                    if (i > 0) b.Append(',');
                    b.Append(Num(da[i]));
                }
                b.Append(']');
                return;
            }
            bool[] ba = o as bool[];
            if (ba != null)
            {
                b.Append('[');
                for (int i = 0; i < ba.Length; i++)
                {
                    if (i > 0) b.Append(',');
                    b.Append(ba[i] ? "true" : "false");
                }
                b.Append(']');
                return;
            }
            int[] ia = o as int[];
            if (ia != null)
            {
                b.Append('[');
                for (int i = 0; i < ia.Length; i++)
                {
                    if (i > 0) b.Append(',');
                    b.Append(ia[i].ToString(CultureInfo.InvariantCulture));
                }
                b.Append(']');
                return;
            }
            throw new JsonException("cannot serialise " + o.GetType().Name);
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            // "R" round-trips; the invariant culture is mandatory or a German locale sends "4,8".
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteString(StringBuilder b, string s)
        {
            b.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
        }

        /// <summary>Convenience: a request body from alternating key/value pairs.</summary>
        public static Dictionary<string, object> Body(params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2)
                d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }
}
