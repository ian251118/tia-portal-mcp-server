using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer.Mcp
{
    /// <summary>
    /// Typed, forgiving accessor over a tool call's "arguments" object.
    ///
    /// Models do not reliably honour the declared JSON type: an integer arrives as
    /// "5" about as often as 5, a boolean as "true", an array as a JSON-encoded
    /// string. Every accessor coerces what it sensibly can and otherwise fails with
    /// a message that names the argument, instead of an opaque cast exception.
    /// </summary>
    internal sealed class Args
    {
        public JObject Raw { get; }

        public Args(JObject raw)
        {
            Raw = raw ?? new JObject();
        }

        private JToken Get(string key)
        {
            var t = Raw[key];
            return t == null || t.Type == JTokenType.Null ? null : t;
        }

        public bool Has(string key) => Get(key) != null;

        // ---- strings -----------------------------------------------------------

        /// <summary>Optional string; null (or <paramref name="def"/>) when absent or empty.</summary>
        public string Str(string key, string def = null)
        {
            var t = Get(key);
            if (t == null) return def;
            var s = t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
            return string.IsNullOrEmpty(s) ? def : s;
        }

        public string ReqStr(string key)
        {
            var s = Str(key);
            if (string.IsNullOrWhiteSpace(s))
                throw new ArgumentException($"Missing required argument '{key}'.");
            return s;
        }

        // ---- numbers -----------------------------------------------------------

        public int? IntN(string key)
        {
            var t = Get(key);
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer:
                    return (int)t;
                case JTokenType.Float:
                    return (int)Math.Round((double)t);
                case JTokenType.String:
                    var s = ((string)t).Trim();
                    if (s.Length == 0) return null;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
                    break;
            }
            throw new ArgumentException($"Argument '{key}' must be an integer, got: {t.ToString(Formatting.None)}");
        }

        public int Int(string key, int def) => IntN(key) ?? def;

        public int ReqInt(string key) =>
            IntN(key) ?? throw new ArgumentException($"Missing required argument '{key}'.");

        /// <summary>Non-negative integer as uint (pixel sizes etc.).</summary>
        public uint UInt(string key, uint def)
        {
            var v = IntN(key);
            if (v == null) return def;
            if (v.Value < 0) throw new ArgumentException($"Argument '{key}' must not be negative.");
            return (uint)v.Value;
        }

        public double? NumN(string key)
        {
            var t = Get(key);
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return (double)t;
                case JTokenType.String:
                    var s = ((string)t).Trim();
                    if (s.Length == 0) return null;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                    break;
            }
            throw new ArgumentException($"Argument '{key}' must be a number, got: {t.ToString(Formatting.None)}");
        }

        // ---- booleans ----------------------------------------------------------

        public bool? BoolN(string key)
        {
            var t = Get(key);
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Boolean:
                    return (bool)t;
                case JTokenType.Integer:
                    return (int)t != 0;
                case JTokenType.String:
                    var s = ((string)t).Trim().ToLowerInvariant();
                    if (s.Length == 0) return null;
                    if (s == "true" || s == "1" || s == "yes") return true;
                    if (s == "false" || s == "0" || s == "no") return false;
                    break;
            }
            throw new ArgumentException($"Argument '{key}' must be true or false, got: {t.ToString(Formatting.None)}");
        }

        public bool Bool(string key, bool def = false) => BoolN(key) ?? def;

        public bool ReqBool(string key) =>
            BoolN(key) ?? throw new ArgumentException($"Missing required argument '{key}'.");

        // ---- structured --------------------------------------------------------

        /// <summary>Array argument; also accepts a JSON-encoded string holding an array.</summary>
        public JArray Arr(string key)
        {
            var t = Get(key);
            if (t == null) return null;
            if (t is JArray arr) return arr;
            if (t.Type == JTokenType.String)
            {
                var s = ((string)t).Trim();
                if (s.StartsWith("["))
                {
                    try { return JArray.Parse(s); }
                    catch (JsonException ex)
                    {
                        throw new ArgumentException($"Argument '{key}' is not a valid JSON array: {ex.Message}");
                    }
                }
            }
            throw new ArgumentException($"Argument '{key}' must be an array.");
        }

        public JArray ReqArr(string key)
        {
            var a = Arr(key);
            if (a == null || a.Count == 0)
                throw new ArgumentException($"Missing required argument '{key}' (a non-empty array).");
            return a;
        }

        /// <summary>Object argument; also accepts a JSON-encoded string holding an object.</summary>
        public JObject Obj(string key)
        {
            var t = Get(key);
            if (t == null) return null;
            if (t is JObject obj) return obj;
            if (t.Type == JTokenType.String)
            {
                var s = ((string)t).Trim();
                if (s.StartsWith("{"))
                {
                    try { return JObject.Parse(s); }
                    catch (JsonException ex)
                    {
                        throw new ArgumentException($"Argument '{key}' is not a valid JSON object: {ex.Message}");
                    }
                }
            }
            throw new ArgumentException($"Argument '{key}' must be an object.");
        }

        /// <summary>Deserializes an array/object argument into a typed model.</summary>
        public T To<T>(string key) where T : class
        {
            var t = Get(key);
            if (t == null) return null;
            if (t.Type == JTokenType.String)
            {
                var s = ((string)t).Trim();
                if (s.StartsWith("[") || s.StartsWith("{"))
                {
                    try { t = JToken.Parse(s); }
                    catch (JsonException ex)
                    {
                        throw new ArgumentException($"Argument '{key}' is not valid JSON: {ex.Message}");
                    }
                }
            }
            try { return t.ToObject<T>(); }
            catch (JsonException ex)
            {
                throw new ArgumentException($"Argument '{key}' has the wrong shape: {ex.Message}");
            }
        }
    }
}
