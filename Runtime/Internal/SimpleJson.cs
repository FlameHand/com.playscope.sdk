using System;
using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    // Minimal JSON reader for device.json and session.json only.
    // Handles flat objects with string values. No arrays, no nesting.
    internal static class SimpleJson
    {
        internal static Dictionary<string, object> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}")) return null;

            var result = new Dictionary<string, object>();
            json = json.Substring(1, json.Length - 2).Trim();

            int i = 0;
            while (i < json.Length)
            {
                // skip whitespace and commas
                while (i < json.Length && (json[i] == ',' || char.IsWhiteSpace(json[i]))) i++;
                if (i >= json.Length) break;

                // read key
                if (json[i] != '"') break;
                var key = ReadString(json, ref i);
                if (key == null) break;

                // skip colon
                while (i < json.Length && (json[i] == ':' || char.IsWhiteSpace(json[i]))) i++;
                if (i >= json.Length) break;

                // read value
                object value;
                if (json[i] == '"')
                    value = ReadString(json, ref i);
                else if (json[i] == 't' && i + 4 <= json.Length && json.Substring(i, 4) == "true") { value = true; i += 4; }
                else if (json[i] == 'f' && i + 5 <= json.Length && json.Substring(i, 5) == "false") { value = false; i += 5; }
                else if (json[i] == 'n' && i + 4 <= json.Length && json.Substring(i, 4) == "null") { value = null; i += 4; }
                else
                {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
                    value = json.Substring(start, i - start).Trim();
                }

                if (key != null)
                    result[key] = value;
            }

            return result;
        }

        private static string ReadString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') return null;
            i++; // skip opening quote
            var start = i;
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\') i++; // skip escaped char
                i++;
            }
            var result = json.Substring(start, i - start);
            if (i < json.Length) i++; // skip closing quote
            return result;
        }
    }
}
