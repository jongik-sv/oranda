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
                "required": ["table", "columns", "ddl", "rationale"],
                "properties": {
                  "table": { "type": "string" },
                  "columns": { "type": "array", "items": { "type": "string" } },
                  "ddl": { "type": "string", "description": "CREATE INDEX text. Never executed by Oranda." },
                  "rationale": { "type": "string" },
                  "risk": { "type": "string", "description": "Write overhead, plan changes of other statements." }
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

public sealed record TuningIndexSuggestion(string Table, IReadOnlyList<string> Columns, string Ddl, string Rationale, string? Risk = null);

public sealed record TuningEvidence(string Tool, string Finding);

/// <summary>튜닝 결과의 직렬화 문맥. 스키마와 같은 snake_case 이름을 쓴다.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TuningResult))]
public sealed partial class AgentSchemaJsonContext : JsonSerializerContext
{
}
