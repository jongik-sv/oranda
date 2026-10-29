using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>사람 말로 바꾼 도구 카드 한 줄: <paramref name="Title"/> 은 도구 이름, <paramref name="Detail"/> 은 핵심 인자(없으면 null).</summary>
public sealed record ToolCardLine(string Title, string? Detail);

/// <summary>
/// 도구 카드 문구 표(순수 C#). 드라이버가 넘긴 인자 요약(LLM 이 보낸 글 그대로이며 리터럴은 이미 <c>:vN</c> 으로 가려져 있다)에서
/// 도구별 핵심 인자를 골라 한 줄로 만들고, 게이트 사유 코드를 사람 말로 바꾼다.
/// 값은 길이·개수만 내보내고 풀어 쓰지 않는다(원래 값을 되돌리는 일이 없다).
/// </summary>
public static partial class ToolCardText
{
    [GeneratedRegex(@":v\d+", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("\"(?<key>[A-Za-z_]+)\"\\s*:\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)", RegexOptions.CultureInvariant)]
    private static partial Regex PartialStringPattern();

    /// <summary>도구 이름과 인자 요약으로 카드 한 줄을 만든다. 모르는 도구는 이름 그대로, 인자를 읽지 못해도 제목은 낸다.</summary>
    public static ToolCardLine Describe(string toolName, string argumentsSummary)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var args = Arguments.Parse(argumentsSummary);
        return toolName switch
        {
            AgentToolNames.DescribeObject => new("객체 구조 조회", args.Text(AgentFields.ObjectName)),
            AgentToolNames.GetIndexInfo => new("인덱스 정보 조회", args.Text(AgentFields.Table)),
            AgentToolNames.GetTableStats => new("테이블 통계", Join(args.Text(AgentFields.Table), Columns(args))),
            AgentToolNames.GetHistogram => new("컬럼 히스토그램", Join(args.Text(AgentFields.Table), args.Text(AgentFields.Column), "."))
            ,
            AgentToolNames.GetCursorPlan => new("실제 실행 계획(커서)", Join(args.Text(AgentFields.SqlId), Child(args))),
            AgentToolNames.ExplainPlan => new("실행 계획(EXPLAIN)", SqlText(args)),
            AgentToolNames.GetSqlStats => new("SQL 수행 통계", args.Text(AgentFields.SqlId)),
            AgentToolNames.GetTuningContext => new("튜닝 정보 묶음", TuningContextDetail(args)),
            AgentToolNames.BenchmarkQueries => new("변형 성능 비교", BenchmarkDetail(args)),
            _ => new(toolName, null),
        };
    }

    /// <summary>
    /// 인자 요약을 펼침 칸에 보일 글로 바꾼다. JSON 으로 읽히면 한 줄에 인자 하나(<c>이름: 값</c>, 글은 이스케이프를 풀어 줄바꿈 그대로),
    /// 읽히지 못하면(길어서 잘림) 받은 글 그대로. 가린 글(<c>:vN</c>)은 어느 쪽이든 그대로 남는다.
    /// </summary>
    public static string Expand(string argumentsSummary)
    {
        ArgumentNullException.ThrowIfNull(argumentsSummary);
        if (!TryParseObject(argumentsSummary, out var doc))
        {
            return argumentsSummary;
        }

        using (doc)
        {
            var sb = new StringBuilder();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
                sb.Append(property.Name).Append(": ").AppendLine(value);
            }

            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 게이트가 남긴 사유(<c>sql_unsafe</c>, <c>tool_failed: ORA-00942</c>, 뒤에 <c>, audit_failed</c> 가 붙을 수 있음)를 사람 말로.
    /// 사유가 없거나 의미가 없으면 null. 모르는 코드는 코드를 그대로 붙인 일반 문구.
    /// </summary>
    public static string? ReasonText(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var first = note.Split(',', 2)[0].Trim();
        var colon = first.IndexOf(':', StringComparison.Ordinal);
        var code = colon < 0 ? first : first[..colon];
        var kind = colon < 0 ? null : first[(colon + 1)..].Trim();
        if (code == "audit_failed")
        {
            return null;
        }

        var text = code switch
        {
            "sql_unsafe" => "안전하다고 판정할 수 없는 SQL 이라 보내지 않음",
            "sql_not_single_query" => "SELECT 문 하나가 아니라서 보내지 않음",
            "scope" => "이 요청의 범위 밖 객체라서 거부",
            "sql_id_scope" => "이 요청의 범위 밖 커서(sql_id)라서 거부",
            "no_statement" => "대상 문장이 없어 거부",
            "sql_schema_unresolved" => "현재 스키마가 접속 사용자와 달라 객체를 확정할 수 없어 거부",
            "level" => "접속의 AI 허용 수준이 모자라 거부",
            "environment" => "운영 접속에서는 쓸 수 없는 도구라 거부",
            "explain_plan_disabled" => "접속 설정에서 EXPLAIN PLAN 이 꺼져 있어 거부",
            "literal_mask_unavailable" => "리터럴을 가릴 수 없어 거부",
            "session_mismatch" => "요청한 접속과 세션이 달라 거부",
            "connection" => "현재 요청의 접속과 다른 접속이라 거부",
            "invalid_arguments" => "도구 인자 형식이 맞지 않아 거부",
            "unknown_tool" => "알 수 없는 도구라 거부",
            "run_revoked" => "이미 끝난 실행이라 거부",
            "unsafe_result" => "결과에 내보낼 수 없는 내용이 있어 보내지 않음",
            "canceled" => "취소됨",
            "internal_error" => "내부 오류로 실패",
            "not_implemented" => "아직 구현되지 않은 도구",
            "tool_failed" or "tool_exception" => "도구 실행 중 오류",
            _ => "사유 코드 " + code,
        };
        return string.IsNullOrEmpty(kind) ? text : $"{text} ({kind})";
    }

    private static string? Join(string? left, string? right, string separator = " ")
        => left is null ? right : right is null ? left : left + separator + right;

    private static string? Columns(Arguments args)
        => args.ArrayCount(AgentFields.Columns) is { } n ? string.Create(CultureInfo.InvariantCulture, $"(열 {n})") : null;

    private static string? Child(Arguments args)
        => args.Number("child") is { } n ? string.Create(CultureInfo.InvariantCulture, $"(자식 {n})") : null;

    private static string? SqlText(Arguments args)
        => args.TextLength(AgentFields.Sql) is { } sql ? SqlSummary(sql.Length, sql.Truncated, args.Text(AgentFields.Sql)) : null;

    private static string SqlSummary(int length, bool truncated, string? text)
    {
        var size = string.Create(CultureInfo.InvariantCulture, $"SQL {length:N0}자{(truncated ? " 이상" : string.Empty)}");
        var placeholders = text is null ? 0 : PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).Count();
        return placeholders == 0 ? size : size + string.Create(CultureInfo.InvariantCulture, $" · 자리표시 {placeholders}");
    }

    private static string? TuningContextDetail(Arguments args)
    {
        var sqlId = args.Text(AgentFields.SqlId);
        var sql = SqlText(args);
        return Join(sqlId, sql, " · ") ?? "요청의 대상 문장";
    }

    private static string? BenchmarkDetail(Arguments args)
    {
        var variants = args.ArrayCount(AgentFields.Variants);
        var repeat = args.Number("repeat");
        var parts = new List<string>();
        if (variants is { } v)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"변형 {v}개"));
        }

        if (repeat is { } r)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"반복 {r}회"));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static bool TryParseObject(string json, out JsonDocument doc)
    {
        doc = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                doc = parsed;
                return true;
            }

            parsed.Dispose();
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>인자 요약에서 값을 읽는 도우미. JSON 이 온전하면 JSON 으로, 길어서 잘렸으면 앞에서부터 읽을 수 있는 글 인자만 정규식으로.</summary>
    private sealed class Arguments
    {
        private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _partial = new(StringComparer.Ordinal);
        private readonly JsonDocument? _doc;

        private Arguments(JsonDocument? doc, string raw)
        {
            _doc = doc;
            if (doc is not null)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    _values[property.Name] = property.Value;
                }

                return;
            }

            foreach (Match match in PartialStringPattern().Matches(raw))
            {
                _partial.TryAdd(match.Groups["key"].Value, Unescape(match.Groups["value"].Value));
            }
        }

        public static Arguments Parse(string raw) => new(TryParseObject(raw, out var doc) ? doc : null, raw);

        public string? Text(string name)
        {
            if (_values.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() is { Length: > 0 } s ? s : null;
            }

            return _partial.TryGetValue(name, out var partial) && partial.Length > 0 ? partial : null;
        }

        /// <summary>글 인자의 길이. 잘린 요약에서 읽은 글이면 <c>Truncated</c>(실제는 이보다 길다).</summary>
        public (int Length, bool Truncated)? TextLength(string name)
        {
            if (_values.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                return (element.GetString()?.Length ?? 0, false);
            }

            return _partial.TryGetValue(name, out var partial) ? (partial.Length, true) : null;
        }

        public int? ArrayCount(string name)
            => _values.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Array ? element.GetArrayLength() : null;

        public int? Number(string name)
            => _values.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n) ? n : null;

        private static string Unescape(string value)
        {
            try
            {
                return JsonSerializer.Deserialize<string>("\"" + value + "\"") ?? value;
            }
            catch (JsonException)
            {
                return value; // 끝이 잘려 이스케이프가 덜 끝난 글
            }
        }
    }
}
