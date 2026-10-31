using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oranda.Core.Ai.Schemas;

/// <summary>
/// AI 튜닝의 구조 결과(<c>claude -p --json-schema</c> 로 요구하는 모양, 설계 §5.5·연구서 §8).
/// 화면은 이것을 받아 재작성 후보는 편집기 제안으로, 인덱스 문안은 새 탭으로만 넣는다(실행하지 않는다).
/// </summary>
public sealed record TuningResult(
    string Summary,
    IReadOnlyList<TuningRewrite> Rewrites,
    IReadOnlyList<TuningIndexSuggestion> IndexSuggestions,
    IReadOnlyList<TuningEvidence> Evidence)
{
    /// <summary>JSON 스키마 글(<c>--json-schema</c> 인자와 API 제공자의 구조 출력에 그대로 쓴다).</summary>
    public const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "rewrites", "index_suggestions", "evidence"],
          "properties": {
            "summary": { "type": "string", "description": "The chat answer in Markdown, following the response style in the prompt." },
            "rewrites": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["label", "sql", "rationale"],
                "properties": {
                  "label": { "type": "string" },
                  "sql": { "type": "string", "description": "Complete replacement statement. Keep every comment of the original statement unchanged, including comment placeholders such as /*c1*/ or --c1." },
                  "rationale": { "type": "string", "description": "One short line, about 40 characters." },
                  "benchmark": { "type": "string", "description": "If measured with benchmark_queries: elapsed as '141.5 ms → 89.1 ms' (original → this rewrite), then other numbers." }
                }
              }
            },
            "index_suggestions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["action", "table", "columns", "ddl", "rationale"],
                "properties": {
                  "action": { "type": "string", "enum": ["modify", "create"], "description": "modify = change an existing index of the table (add a column or reorder); create = a new index, only when no existing index fits." },
                  "existing_index": { "type": "string", "description": "When action is modify: the existing index to change, as OWNER.NAME." },
                  "existing_columns": { "type": "array", "items": { "type": "string" }, "description": "When action is modify: the columns of that existing index, in order." },
                  "table": { "type": "string" },
                  "columns": { "type": "array", "items": { "type": "string" }, "description": "Columns of the index after the change (the whole list, not only the added ones)." },
                  "ddl": { "type": "string", "description": "Index DDL text. For modify: DROP INDEX then CREATE INDEX with the same name. For create: CREATE INDEX. Never executed by Oranda." },
                  "rationale": { "type": "string" },
                  "risk": { "type": "string", "description": "Write overhead, plan changes of other statements; for modify, other SQL that uses the existing index." }
                }
              }
            },
            "evidence": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["tool", "finding"],
                "properties": {
                  "tool": { "type": "string" },
                  "finding": { "type": "string" }
                }
              }
            }
          }
        }
        """;

    /// <summary>스키마 글을 <see cref="JsonElement"/> 로(<c>AiRequest.OutputSchema</c> 용).</summary>
    public static JsonElement Schema()
    {
        using var doc = JsonDocument.Parse(SchemaJson);
        return doc.RootElement.Clone();
    }

    /// <summary>드라이버가 돌려준 구조 결과를 읽는다. 모양이 다르면 <see cref="JsonException"/>.</summary>
    public static TuningResult Parse(JsonElement element) =>
        element.Deserialize(AgentSchemaJsonContext.Default.TuningResult)
        ?? throw new JsonException("튜닝 결과가 비어 있다.");
}

public sealed record TuningRewrite(string Label, string Sql, string Rationale, string? Benchmark = null);

/// <summary>
/// 인덱스 후보 하나. <see cref="Action"/> 이 <see cref="ActionModify"/> 면 <see cref="ExistingIndex"/>(OWNER.NAME)·<see cref="ExistingColumns"/> 의 기존 인덱스를 바꾸는 안이고,
/// <see cref="ActionCreate"/>(기본, 옛 결과 호환)면 새 인덱스다.
/// </summary>
public sealed record TuningIndexSuggestion(
    string Table,
    IReadOnlyList<string> Columns,
    string Ddl,
    string Rationale,
    string? Risk = null,
    string Action = TuningIndexSuggestion.ActionCreate,
    string? ExistingIndex = null,
    IReadOnlyList<string>? ExistingColumns = null)
{
    /// <summary>기존 인덱스 변형(열 추가·순서 조정).</summary>
    public const string ActionModify = "modify";

    /// <summary>새 인덱스.</summary>
    public const string ActionCreate = "create";

    /// <summary>기존 인덱스를 바꾸는 안인가.</summary>
    public bool IsModify => string.Equals(Action, ActionModify, StringComparison.OrdinalIgnoreCase);
}

public sealed record TuningEvidence(string Tool, string Finding);

/// <summary>튜닝 결과의 직렬화 문맥. 스키마와 같은 snake_case 이름을 쓴다.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TuningResult))]
public sealed partial class AgentSchemaJsonContext : JsonSerializerContext
{
}
