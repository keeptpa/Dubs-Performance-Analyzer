using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Analyzer.Profiling.Web
{
    /// <summary>
    /// Deliberately tiny, allocation-frugal JSON writer.
    /// RimWorld ships no JSON library we can rely on, and Unity's JsonUtility cannot
    /// express dictionaries or arbitrary nesting, so we roll our own.
    /// Single-threaded by design: build the payload on the main thread, hand the finished
    /// string to the web server thread.
    /// </summary>
    public sealed class JsonWriter
    {
        private static readonly NumberFormatInfo Inv = NumberFormatInfo.InvariantInfo;

        private readonly StringBuilder sb;
        private readonly Stack<bool> scopes = new Stack<bool>(8);
        private bool needComma;

        public JsonWriter(int capacity = 4096)
        {
            sb = new StringBuilder(capacity);
        }

        public int Length => sb.Length;

        private void Separate()
        {
            if (needComma) sb.Append(',');
            needComma = true;
        }

        /// <summary>Begins a `{ }` object. Pass a key when nested inside another object or array.</summary>
        public JsonWriter BeginObject(string key = null)
        {
            WriteKeyIfPresent(key);
            sb.Append('{');
            scopes.Push(true);
            needComma = false;
            return this;
        }

        public JsonWriter EndObject()
        {
            sb.Append('}');
            scopes.Pop();
            needComma = true;
            return this;
        }

        public JsonWriter BeginArray(string key = null)
        {
            WriteKeyIfPresent(key);
            sb.Append('[');
            scopes.Push(false);
            needComma = false;
            return this;
        }

        public JsonWriter EndArray()
        {
            sb.Append(']');
            scopes.Pop();
            needComma = true;
            return this;
        }

        /// <summary>
        /// Emits the `"key":` prefix, or nothing at all when this is an array element.
        /// The comma must be written in both cases - a keyless container start is an element
        /// like any other, and skipping the separator here produces `{..}{..}` which no parser
        /// will accept.
        /// </summary>
        private void WriteKeyIfPresent(string key)
        {
            Separate();
            if (key == null) return;

            WriteEscaped(key);
            sb.Append(':');
            needComma = false;
        }

        public JsonWriter Prop(string key, string value)
        {
            Separate();
            WriteEscaped(key);
            sb.Append(':');
            WriteEscaped(value);
            needComma = true;
            return this;
        }

        public JsonWriter Prop(string key, bool value)
        {
            Separate();
            WriteEscaped(key);
            sb.Append(':').Append(value ? "true" : "false");
            needComma = true;
            return this;
        }

        public JsonWriter Prop(string key, int value)
        {
            Separate();
            WriteEscaped(key);
            sb.Append(':').Append(value.ToString(Inv));
            needComma = true;
            return this;
        }

        public JsonWriter Prop(string key, long value)
        {
            Separate();
            WriteEscaped(key);
            sb.Append(':').Append(value.ToString(Inv));
            needComma = true;
            return this;
        }

        /// <summary>Writes a finite double. NaN/Infinity become null, which is what JS expects.</summary>
        public JsonWriter Prop(string key, double value)
        {
            Separate();
            WriteEscaped(key);
            sb.Append(':');
            WriteNumber(value);
            needComma = true;
            return this;
        }

        public JsonWriter Prop(string key, float value) => Prop(key, (double)value);

        /// <summary>Emits a pre-serialised fragment (another JsonWriter's output, or a literal).</summary>
        public JsonWriter PropRaw(string key, string rawJson)
        {
            Separate();
            WriteEscaped(key);
            sb.Append(':').Append(rawJson ?? "null");
            needComma = true;
            return this;
        }

        /// <summary>Writes an array element (no key).</summary>
        public JsonWriter Item(double value)
        {
            Separate();
            WriteNumber(value);
            needComma = true;
            return this;
        }

        public JsonWriter Item(double? value)
        {
            Separate();
            if (value.HasValue) WriteNumber(value.Value);
            else sb.Append("null");
            needComma = true;
            return this;
        }

        public JsonWriter Item(int value)
        {
            Separate();
            sb.Append(value.ToString(Inv));
            needComma = true;
            return this;
        }

        public JsonWriter Item(string value)
        {
            Separate();
            WriteEscaped(value);
            needComma = true;
            return this;
        }

        public JsonWriter ItemRaw(string rawJson)
        {
            Separate();
            sb.Append(rawJson ?? "null");
            needComma = true;
            return this;
        }

        private void WriteNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("null");
                return;
            }

            // Round to a sane precision - this payload is sent several times a second
            // and the extra digits are pure bandwidth.
            sb.Append(Math.Round(value, 3).ToString(Inv));
        }

        private void WriteEscaped(string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
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

        public override string ToString() => sb.ToString();

        /// <summary>Reuses the writer across snapshots so we are not reallocating a huge buffer twice a second.</summary>
        public void Clear()
        {
            sb.Length = 0;
            scopes.Clear();
            needComma = false;
        }
    }
}
