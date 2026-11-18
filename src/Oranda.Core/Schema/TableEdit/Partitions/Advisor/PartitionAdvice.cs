using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// AI 파티션 추천의 구조 결과(<c>AiRequest.OutputSchema</c> = <see cref="Schema"/>, 설계 PA-6). 이름은 snake_case.
/// 방식·간격·배치 같은 고르는 값은 AI 가 낸 글 그대로 둔다(모르는 값이 와도 읽기는 되고, 앱 확인이 ✖ 로 거른다). 쓰는 값 = <see cref="AdviceVerdict"/>·<see cref="AdviceMethod"/>·<see cref="AdviceInterval"/>·<see cref="AdviceLayoutKind"/>.
/// </summary>
/// <param name="Verdict"><see cref="AdviceVerdict.Partition"/> | <see cref="AdviceVerdict.NoPartition"/></param>
/// <param name="Summary">한두 줄 요약(화면 언어)</param>
public sealed record PartitionAdvice(
    string Verdict,
    string Summary,
    IReadOnlyList<PartitionCandidate> Candidates,
    IReadOnlyList<NotChosen> NotChosen)
{
    /// <summary>JSON 스키마 글(<c>--json-schema</c> 인자와 API 제공자의 구조 출력에 그대로 쓴다).</summary>
    public const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["verdict", "summary", "candidates", "not_chosen"],
          "properties": {
            "verdict": { "type": "string", "enum": ["partition", "no_partition"], "description": "no_partition when partitioning would not pay off; then candidates may be empty." },
            "summary": { "type": "string", "description": "One or two short sentences in the prompt language." },
            "candidates": {
              "type": "array",
              "description": "At most 3, best first.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["rank", "title", "method", "key_columns", "layout", "local_indexes", "reasons", "cautions"],
                "properties": {
                  "rank": { "type": "integer", "description": "1 = best." },
                  "title": { "type": "string", "description": "Short name, about 30 characters." },
                  "method": { "type": "string", "enum": ["range", "list", "hash"] },
                  "key_columns": { "type": "array", "items": { "type": "string" }, "description": "Partition key columns of the table, in order." },
                  "interval": { "type": "string", "description": "For range: month, day, year, none, or number:<n> for a numeric step. Use none for string or number date keys (no INTERVAL possible)." },
                  "layout": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["kind"],
                    "properties": {
                      "kind": { "type": "string", "enum": ["monthly", "yearly", "daily", "values", "hash", "number_ranges"] },
                      "start": { "type": "string", "description": "First period as YYYY-MM (monthly), YYYY (yearly) or YYYY-MM-DD (daily)." },
                      "periods_ahead": { "type": "integer", "description": "Periods to create after today when there is no INTERVAL (default 12)." },
                      "groups": {
                        "type": "array",
                        "description": "For values: one LIST partition per group.",
                        "items": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["name", "values", "is_default"],
                          "properties": {
                            "name": { "type": "string" },
                            "values": { "type": "array", "items": { "type": "string" }, "description": "Values exactly as given in the evidence (placeholders such as V1 stay as they are)." },
                            "is_default": { "type": "boolean", "description": "true = the DEFAULT partition; values is then empty." }
                          }
                        }
                      },
                      "hash_count": { "type": "integer", "description": "For hash: number of partitions, a power of two." },
                      "bounds": { "type": "array", "items": { "type": "string" }, "description": "For number_ranges: upper bounds in ascending order." }
                    }
                  },
                  "subpartition": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["method", "key_columns"],
                    "description": "Optional subpartition template.",
                    "properties": {
                      "method": { "type": "string", "enum": ["list", "hash"] },
                      "key_columns": { "type": "array", "items": { "type": "string" } },
                      "groups": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["name", "values", "is_default"],
                          "properties": {
                            "name": { "type": "string" },
                            "values": { "type": "array", "items": { "type": "string" } },
                            "is_default": { "type": "boolean" }
                          }
                        }
                      },
                      "hash_count": { "type": "integer" }
                    }
                  },
                  "local_indexes": { "type": "array", "items": { "type": "string" }, "description": "Existing index names to make LOCAL." },
                  "reasons": { "type": "array", "items": { "type": "string" }, "description": "Short reasons citing the evidence numbers." },
                  "cautions": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "not_chosen": {
              "type": "array",
              "description": "Columns that look like keys but were not chosen, and why.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["column", "reason"],
                "properties": {
                  "column": { "type": "string" },
                  "reason": { "type": "string" }
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
}

/// <summary>후보 하나.</summary>
/// <param name="Method"><see cref="AdviceMethod"/> 값</param>
/// <param name="Interval"><see cref="AdviceInterval"/> 값 또는 <c>number:&lt;n&gt;</c>. 범위가 아니면 null</param>
/// <param name="LocalIndexes">LOCAL 로 만들 기존 인덱스 이름(PA-12)</param>
public sealed record PartitionCandidate(
    int Rank,
    string Title,
    string Method,
    IReadOnlyList<string> KeyColumns,
    string? Interval,
    PartitionLayout Layout,
    SubpartitionAdvice? Subpartition,
    IReadOnlyList<string> LocalIndexes,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Cautions);

/// <summary>파티션 줄을 어떻게 만들지.</summary>
/// <param name="Kind"><see cref="AdviceLayoutKind"/> 값</param>
/// <param name="Start">첫 기간(<c>YYYY-MM</c>·<c>YYYY</c>·<c>YYYY-MM-DD</c>)</param>
/// <param name="PeriodsAhead">INTERVAL 없을 때 오늘 뒤로 만들 기간 수(null = 12)</param>
public sealed record PartitionLayout(
    string Kind,
    string? Start = null,
    int? PeriodsAhead = null,
    IReadOnlyList<ListGroup>? Groups = null,
    int? HashCount = null,
    IReadOnlyList<string>? Bounds = null);

/// <summary>LIST 묶음 하나. <see cref="Values"/> 는 가린 자리표시자(<c>V1</c>)일 수 있다 — 채우기가 금고로 되돌린다.</summary>
public sealed record ListGroup(string Name, IReadOnlyList<string> Values, bool IsDefault);

/// <summary>하위 파티션 템플릿(PA-11, 바꾸기 때만).</summary>
/// <param name="Method"><see cref="AdviceMethod.List"/> | <see cref="AdviceMethod.Hash"/></param>
public sealed record SubpartitionAdvice(string Method, IReadOnlyList<string> KeyColumns, IReadOnlyList<ListGroup>? Groups = null, int? HashCount = null);

/// <summary>키로 안 고른 열과 이유.</summary>
public sealed record NotChosen(string Column, string Reason);

/// <summary><see cref="PartitionAdvice.Verdict"/> 값.</summary>
public static class AdviceVerdict
{
    public const string Partition = "partition";
    public const string NoPartition = "no_partition";
}

/// <summary><see cref="PartitionCandidate.Method"/>·<see cref="SubpartitionAdvice.Method"/> 값.</summary>
public static class AdviceMethod
{
    public const string Range = "range";
    public const string List = "list";
    public const string Hash = "hash";

    /// <summary>글 → 방식. 모르는 글이면 null.</summary>
    public static PartitionMethod? ToMethod(string? method) => method?.Trim().ToLowerInvariant() switch
    {
        Range => PartitionMethod.Range,
        List => PartitionMethod.List,
        Hash => PartitionMethod.Hash,
        _ => null,
    };
}

/// <summary><see cref="PartitionCandidate.Interval"/> 값.</summary>
public static class AdviceInterval
{
    public const string Month = "month";
    public const string Day = "day";
    public const string Year = "year";
    public const string None = "none";

    /// <summary>숫자 간격 머리(<c>number:1000</c>).</summary>
    public const string NumberPrefix = "number:";
}

/// <summary><see cref="PartitionLayout.Kind"/> 값.</summary>
public static class AdviceLayoutKind
{
    public const string Monthly = "monthly";
    public const string Yearly = "yearly";
    public const string Daily = "daily";
    public const string Values = "values";
    public const string Hash = "hash";
    public const string NumberRanges = "number_ranges";
}

/// <summary>추천 결과의 직렬화 문맥. 스키마와 같은 snake_case 이름을 쓴다.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PartitionAdvice))]
internal sealed partial class PartitionAdviceJsonContext : JsonSerializerContext
{
}
