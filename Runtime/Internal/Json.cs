using System.Globalization;
using System.Text;

namespace M2C.Checkout.Internal
{
    /// <summary>
    /// Minimal allocation-light JSON object writer for the one small request body
    /// the SDK sends. We hand-build the request (rather than reflecting over a DTO)
    /// to stay IL2CPP-stripping-safe and to control number formatting; responses
    /// are parsed with UnityEngine.JsonUtility against explicit [Serializable] DTOs.
    /// </summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(128);
        private bool _first = true;

        public JsonWriter() { _sb.Append('{'); }

        private void Key(string key)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            _sb.Append('"');
            AppendEscaped(_sb, key);
            _sb.Append("\":");
        }

        /// <summary>Writes a string field, or skips it entirely when null/empty.</summary>
        public JsonWriter String(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return this;
            Key(key);
            _sb.Append('"');
            AppendEscaped(_sb, value);
            _sb.Append('"');
            return this;
        }

        /// <summary>Writes a numeric field with invariant, non-scientific formatting.</summary>
        public JsonWriter Number(string key, double value)
        {
            Key(key);
            // "0.######" keeps up to 6 decimals (the wire precision), no trailing
            // zeros, and never uses scientific notation - which the server's JSON
            // number parser and the major-unit money contract both require.
            _sb.Append(value.ToString("0.######", CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>Writes a string array field, or skips it when null/empty.</summary>
        public JsonWriter StringArray(string key, string[] values)
        {
            if (values == null || values.Length == 0) return this;
            Key(key);
            _sb.Append('[');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) _sb.Append(',');
                _sb.Append('"');
                AppendEscaped(_sb, values[i] ?? string.Empty);
                _sb.Append('"');
            }
            _sb.Append(']');
            return this;
        }

        public string Build()
        {
            _sb.Append('}');
            return _sb.ToString();
        }

        private static void AppendEscaped(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
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
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
    }
}
