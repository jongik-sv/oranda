using System.Buffers;
using System.Text;
using System.Text.Json;
using Oranda.Core.Execution;

namespace Oranda.Core.Workspaces;

/// <summary>
/// .sql 파일 끝의 바인드 값 블록(EXEC-03). SQL 편집기가 .sql 을 저장할 때 본문 뒤에 붙이고, 열 때 떼어 바인드 변수 창에 넣는다.
/// <code>
/// -- @oranda-binds v1
/// -- {"name":"EMPNO","type":"Number","direction":"In","value":"1002","isNull":false}
/// </code>
/// 모든 줄이 <c>--</c> 줄 주석이라 SQL*Plus 등 다른 도구로 실행해도 문장이 되지 않는다. 형식·방향은 열거형 멤버 이름(data-model 1.3)이고
/// 읽을 때는 대소문자·공백·밑줄을 가리지 않는다(<c>NUMBER</c>, <c>IN OUT</c> 도 읽힌다).
/// JSON 은 기본 이스케이프라 줄바꿈·따옴표·<c>&amp;</c>·한글이 <c>\uXXXX</c> 꼴이 된다: 블록이 ASCII 한 줄씩이라 파일 인코딩(cp949·UTF-16)과
/// 상관없이 왕복하고, 주석 안의 <c>&amp;</c> 도 치환 변수로 묻는 SQL*Plus 에도 걸리지 않는다.
/// 본문과 블록 사이에는 늘 줄바꿈 하나를 넣고 뗄 때 그 하나만 뗀다(본문 끝 줄바꿈 유무까지 바이트 그대로 돌아온다).
/// 줄바꿈 종류는 본문을 따른다(CRLF 가 있으면 CRLF, 줄바꿈이 없으면 시스템 기본).
/// </summary>
public static class SqlFileBinds
{
    /// <summary>블록 첫 줄(고유 표지).</summary>
    public const string Marker = "-- @oranda-binds v1";

    private const string LinePrefix = "-- ";

    /// <summary>본문 끝에 블록을 붙인다. <paramref name="binds"/> 가 비면 본문 그대로(예전과 같은 파일).</summary>
    public static string Append(string body, IReadOnlyList<WorkspaceBind> binds)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(binds);
        if (binds.Count == 0)
        {
            return body;
        }

        var nl = NewLineOf(body);
        var sb = new StringBuilder(body.Length + (96 * (binds.Count + 1)));
        sb.Append(body).Append(nl).Append(Marker).Append(nl);
        foreach (var b in binds)
        {
            sb.Append(LinePrefix).Append(ToJson(b)).Append(nl);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 글 끝의 블록(표지 줄부터 끝까지 모든 줄이 <c>-- </c> 로 시작하고 JSON 으로 읽히는 경우만)을 떼어 본문과 값을 돌려준다.
    /// 블록이 없거나 한 줄이라도 읽히지 않으면 글 그대로와 null(사용자 글을 잘못 떼지 않는다).
    /// </summary>
    public static (string Body, IReadOnlyList<WorkspaceBind>? Binds) Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (FindMarker(text) is not { } marker)
        {
            return (text, null);
        }

        var binds = new List<WorkspaceBind>();
        var pos = marker.NextLine;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(pos, end - pos).TrimEnd('\r');
            if (!line.StartsWith(LinePrefix, StringComparison.Ordinal) || Parse(line[LinePrefix.Length..]) is not { } bind)
            {
                return (text, null);
            }

            binds.Add(bind);
            pos = nl < 0 ? text.Length : nl + 1;
        }

        // 붙일 때 넣은 줄바꿈 하나를 함께 뗀다.
        var bodyEnd = marker.Start;
        if (bodyEnd >= 2 && text[bodyEnd - 2] == '\r' && text[bodyEnd - 1] == '\n')
        {
            bodyEnd -= 2;
        }
        else if (bodyEnd >= 1 && text[bodyEnd - 1] == '\n')
        {
            bodyEnd--;
        }

        return (text[..bodyEnd], binds);
    }

    /// <summary>줄 처음에 있고 그 줄에 표지만 있는 마지막 표지. 시작 위치와 다음 줄 시작.</summary>
    private static (int Start, int NextLine)? FindMarker(string text)
    {
        for (var from = text.Length - 1; from >= 0;)
        {
            var at = text.LastIndexOf(Marker, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            if (at == 0 || text[at - 1] == '\n')
            {
                var nl = text.IndexOf('\n', at);
                var lineEnd = nl < 0 ? text.Length : nl;
                if (text.AsSpan(at + Marker.Length, lineEnd - at - Marker.Length).Trim().IsEmpty)
                {
                    return (at, nl < 0 ? text.Length : nl + 1);
                }
            }

            from = at - 1;
        }

        return null;
    }

    private static WorkspaceBind? Parse(ReadOnlySpan<char> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToString());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                return null;
            }

            var isNull = root.TryGetProperty("isNull", out var n) && n.ValueKind == JsonValueKind.True;
            var value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            return new WorkspaceBind
            {
                Name = name.GetString()!.Trim(),
                Type = EnumOf(root, "type", BindDataType.Varchar2),
                Direction = EnumOf(root, "direction", BindDirection.In),
                Value = isNull ? string.Empty : value,
                IsNull = isNull,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T EnumOf<T>(JsonElement root, string property, T fallback)
        where T : struct, Enum
    {
        if (root.TryGetProperty(property, out var e) && e.ValueKind == JsonValueKind.String)
        {
            var name = e.GetString()!.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
            if (Enum.TryParse<T>(name, ignoreCase: true, out var value) && Enum.IsDefined(value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string ToJson(WorkspaceBind b)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("name", b.Name);
            w.WriteString("type", b.Type.ToString());
            w.WriteString("direction", b.Direction.ToString());
            w.WriteString("value", b.IsNull ? string.Empty : b.Value);
            w.WriteBoolean("isNull", b.IsNull);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string NewLineOf(string body)
    {
        var lf = body.IndexOf('\n', StringComparison.Ordinal);
        if (lf < 0)
        {
            return Environment.NewLine;
        }

        return lf > 0 && body[lf - 1] == '\r' ? "\r\n" : "\n";
    }
}
