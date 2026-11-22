using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oranda.Agent.Cli;

/// <summary>
/// 결과를 쓴다(설계 §4.1, E-19). 기본 = stdout 에 JSON 한 개(UTF-8, 한 줄). <c>--text</c> = 사람이 읽는 글.
/// 한글은 <c>\uXXXX</c> 로 바꾸지 않는다(LLM 이 읽기 쉽게; HTML 에 넣지 않는다).
/// </summary>
internal static class CliOutput
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>JSON 한 줄.</summary>
    public static string Json(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    /// <summary>사람이 읽는 글. 객체 = <c>이름: 값</c> 줄, 객체 배열 = 표, 여러 줄 글 = 이름 다음 줄부터 그대로.</summary>
    public static string Text(JsonNode? node)
    {
        var text = new StringBuilder();
        Write(text, node, 0);
        return text.ToString().TrimEnd('\n') + "\n";
    }

    private static void Write(StringBuilder text, JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    WriteProperty(text, key, value, depth);
                }

                break;
            case JsonArray array when array.Count > 0 && array.All(i => i is JsonObject):
                WriteTable(text, array, depth);
                break;
            default:
                text.Append(Indent(depth)).Append(Scalar(node)).Append('\n');
                break;
        }
    }

    private static void WriteProperty(StringBuilder text, string key, JsonNode? value, int depth)
    {
        var pad = Indent(depth);
        switch (value)
        {
            case JsonObject obj:
                text.Append(pad).Append(key).Append(":\n");
                Write(text, obj, depth + 1);
                break;
            case JsonArray array when array.Count > 0 && array.All(i => i is JsonObject):
                text.Append(pad).Append(key).Append(":\n");
                WriteTable(text, array, depth + 1);
                break;
            case JsonArray array:
                text.Append(pad).Append(key).Append(": ").Append(string.Join(", ", array.Select(Scalar))).Append('\n');
                break;
            default:
                var scalar = Scalar(value);
                if (scalar.Contains('\n', StringComparison.Ordinal))
                {
                    text.Append(pad).Append(key).Append(":\n").Append(scalar);
                    if (!scalar.EndsWith('\n'))
                    {
                        text.Append('\n');
                    }
                }
                else
                {
                    text.Append(pad).Append(key).Append(": ").Append(scalar).Append('\n');
                }

                break;
        }
    }

    private static void WriteTable(StringBuilder text, JsonArray rows, int depth)
    {
        var columns = new List<string>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            foreach (var (key, _) in row)
            {
                if (!columns.Contains(key))
                {
                    columns.Add(key);
                }
            }
        }

        var cells = rows.OfType<JsonObject>()
            .Select(row => columns.Select(c => Cell(row.TryGetPropertyValue(c, out var v) ? v : null)).ToArray())
            .ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length))).ToArray();
        var pad = Indent(depth);
        text.Append(pad).Append(string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))).TrimEnd()).Append('\n');
        foreach (var row in cells)
        {
            text.Append(pad).Append(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd()).Append('\n');
        }
    }

    private static string Cell(JsonNode? node) => node is JsonObject or JsonArray
        ? Json(node)
        : Scalar(node).Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');

    private static string Scalar(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue(out string? s) => s,
        JsonValue value when value.TryGetValue(out bool b) => b ? "true" : "false",
        JsonValue value => Convert.ToString(value.ToJsonString(Compact), CultureInfo.InvariantCulture),
        _ => Json(node),
    };

    private static string Indent(int depth) => new(' ', depth * 2);
}
