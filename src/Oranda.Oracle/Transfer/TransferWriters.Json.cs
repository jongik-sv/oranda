using System.Globalization;
using System.Text;
using System.Text.Json;
using Oranda.Core.Grid;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

internal static partial class TransferWriters
{
    // ---- JSON ----
    private sealed class JsonRowWriter : ITransferWriter
    {
        private readonly Utf8JsonWriter _w;
        private IReadOnlyList<ResultColumn> _columns = [];
        private string[] _names = [];

        public JsonRowWriter(Stream destination)
        {
            _w = new Utf8JsonWriter(destination, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }

        public IList<string> Warnings { get; } = [];

        public void Begin(IReadOnlyList<ResultColumn> columns)
        {
            _columns = columns;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            _names = columns.Select(c =>
            {
                seen.TryGetValue(c.Name, out var n);
                seen[c.Name] = n + 1;
                return n == 0 ? c.Name : c.Name + "_" + (n + 1).ToString(CultureInfo.InvariantCulture);
            }).ToArray();
            _w.WriteStartArray();
        }

        public void WriteRow(object?[] values)
        {
            _w.WriteStartObject();
            for (var i = 0; i < values.Length; i++)
            {
                _w.WritePropertyName(_names[i]);
                WriteValue(_columns[i], values[i]);
            }

            _w.WriteEndObject();
            if (_w.BytesPending > 32 * 1024)
            {
                _w.Flush();
            }
        }

        public void Finish()
        {
            _w.WriteEndArray();
            _w.Flush();
        }

        public void Dispose() => _w.Dispose();

        private void WriteValue(ResultColumn c, object? v)
        {
            switch (v)
            {
                case null or DBNull:
                    _w.WriteNullValue();
                    break;
                case bool b:
                    _w.WriteBooleanValue(b);
                    break;
                case decimal or long or int or short or byte or sbyte or ushort or uint or ulong:
                    _w.WriteRawValue(Convert.ToString(v, CultureInfo.InvariantCulture)!);
                    break;
                case double d when double.IsFinite(d):
                    _w.WriteRawValue(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case float f when float.IsFinite(f):
                    _w.WriteRawValue(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case string s when c.Kind == ColumnDataKind.Number && IsJsonNumber(s):
                    _w.WriteRawValue(s);
                    break;
                case string s when c.Kind == ColumnDataKind.Json && TryParseJson(s, out var doc):
                    using (doc)
                    {
                        doc.RootElement.WriteTo(_w);
                    }

                    break;
                case DateTime dt:
                    _w.WriteStringValue(dt.ToString(c.Kind == ColumnDataKind.Timestamp ? "yyyy-MM-dd'T'HH:mm:ss.ffffff" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                    break;
                case DateTimeOffset dto:
                    _w.WriteStringValue(dto.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture));
                    break;
                default:
                    _w.WriteStringValue(Text(c, v, Warnings));
                    break;
            }
        }

        private static bool IsJsonNumber(string s)
        {
            // JSON 숫자 문법: -?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?  (Oracle 의 .5, 5. 같은 표기는 글자로 둔다)
            var i = 0;
            if (i < s.Length && s[i] == '-')
            {
                i++;
            }

            var intStart = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            if (i == intStart || (s[intStart] == '0' && i - intStart > 1))
            {
                return false;
            }

            if (i < s.Length && s[i] == '.')
            {
                i++;
                var f = i;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                if (i == f)
                {
                    return false;
                }
            }

            if (i < s.Length && s[i] is 'e' or 'E')
            {
                i++;
                if (i < s.Length && s[i] is '+' or '-')
                {
                    i++;
                }

                var e = i;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                {
                    i++;
                }

                if (i == e)
                {
                    return false;
                }
            }

            return i == s.Length;
        }

        private static bool TryParseJson(string s, out JsonDocument doc)
        {
            try
            {
                doc = JsonDocument.Parse(s);
                return true;
            }
            catch (JsonException)
            {
                doc = null!;
                return false;
            }
        }
    }
}
