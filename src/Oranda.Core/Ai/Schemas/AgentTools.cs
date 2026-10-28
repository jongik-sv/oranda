using Oranda.Core.Ai.Access;

namespace Oranda.Core.Ai.Schemas;

/// <summary>도구 이름 9개(설계 §5.2). MCP 도구 이름과 파이프 <c>tools/call</c> 의 <c>name</c> 이 같은 값이다.</summary>
public static class AgentToolNames
{
    public const string DescribeObject = "describe_object";
    public const string GetIndexInfo = "get_index_info";
    public const string GetTableStats = "get_table_stats";
    public const string GetHistogram = "get_histogram";
    public const string GetCursorPlan = "get_cursor_plan";
    public const string ExplainPlan = "explain_plan";
    public const string GetSqlStats = "get_sql_stats";
    public const string GetTuningContext = "get_tuning_context";
    public const string BenchmarkQueries = "benchmark_queries";
}

/// <summary>
/// 도구 인자·결과에서 <c>AgentGate</c> 가 이름으로 찾는 필드(설계 §5.1 범위 검사, §5.4 행 값).
/// 모든 도구 스키마가 같은 인자 이름을 쓰므로 게이트는 도구별 분기 없이 대상 객체를 뽑는다.
/// </summary>
public static class AgentFields
{
    /// <summary>접속 별칭(인자). 없으면 실행 중인 요청의 접속.</summary>
    public const string Connection = "connection";

    /// <summary>객체 이름(<c>OWNER.NAME</c> 또는 <c>NAME</c>) 인자: <c>describe_object</c>.</summary>
    public const string ObjectName = "object";

    /// <summary>표 이름 인자: <c>get_index_info</c>·<c>get_table_stats</c>·<c>get_histogram</c>.</summary>
    public const string Table = "table";

    /// <summary>열 이름 인자(<c>get_histogram</c>) 또는 열 이름 배열(<c>get_table_stats</c> 의 <c>columns</c>).</summary>
    public const string Column = "column";

    public const string Columns = "columns";

    public const string SqlId = "sql_id";

    /// <summary>
    /// 문장 글 인자: <c>explain_plan</c>·<c>get_tuning_context</c> 의 <c>sql</c>, <c>benchmark_queries</c> 의 <c>variants[].sql</c>.
    /// 게이트는 이 글을 모두 <c>IStatementReferenceExtractor.AnalyzeAsync</c> 로 분석해 문장 1개·Query·참조 객체가 범위 안(실행 범위의 기준 객체 ∪ 그 상대 ∪ 보낼 범위)일 때만 통과시킨다.
    /// </summary>
    public const string Sql = "sql";

    /// <summary><c>benchmark_queries</c> 의 변형 배열. 원소 <c>{ "label": …, "sql": … }</c>.</summary>
    public const string Variants = "variants";

    /// <summary>
    /// 결과에서 히스토그램 끝값(<c>endpoint_actual_value</c> 에서 온 실제 열 값)이 들어가는 필드 이름.
    /// </summary>
    public const string HistogramEndpoint = "endpoint";

    /// <summary>열 통계의 최솟값(<c>low_value</c>)이 들어가는 필드 이름. 실제 열 값이다(연구서 §5.4).</summary>
    public const string ColumnLowValue = "low_value";

    /// <summary>열 통계의 최댓값(<c>high_value</c>)이 들어가는 필드 이름. 실제 열 값이다.</summary>
    public const string ColumnHighValue = "high_value";

    /// <summary>
    /// 결과에서 실제 열 값이 들어가는 필드 이름 전부. 게이트는 허용 수준이 <see cref="AiAccessLevel.QueryRows"/> 미만이면
    /// 결과의 어느 깊이에서든 이 이름의 속성을 지운다. 처리자가 행 값을 결과에 넣어야 하면 반드시 이 이름 중 하나로만 넣는다(다른 이름은 가려지지 않는다).
    /// </summary>
    public static IReadOnlyList<string> RowValueFields { get; } =
        [HistogramEndpoint, HistogramEndpointValue, HistogramEndpointActualValue, ColumnLowValue, ColumnHighValue];

    /// <summary>
    /// 결과에서 SQL 글·술어 글이 들어가는 필드 이름 전부(계획 단계의 <c>access</c>·<c>filter</c>, 술어 칸, 문장 글). 리터럴 가림이 필요한 실행에서
    /// 게이트는 이 이름의 문자열만 <c>ISqlLiteralMasker</c> 로 가린다. 모든 문자열을 가리면 <c>note</c>·숫자가 망가지므로 이름으로 정한다.
    /// 처리자가 결과에 SQL 글을 넣어야 하면 이 이름 중 하나로만 넣는다(다른 이름은 가려지지 않는다).
    /// </summary>
    public static IReadOnlyList<string> SqlTextFields { get; } =
        ["access", "filter", "access_predicates", "filter_predicates", "sql_text"];

    /// <summary>
    /// 결과에서 열을 식별하는 필드 이름 전부(비교는 소문자로 바꾸고 <c>_</c>·<c>-</c> 를 뺀 뒤 한다). <b>처리자는 결과에서 열을 식별할 때 이 이름만 쓴다.</b>
    /// 게이트는 이 이름의 값(또는 값이 객체면 그 안의 같은 이름)이 민감 컬럼이면 그 객체의 행 값 필드를 지운다.
    /// 다른 이름으로 열 이름을 내면 민감 컬럼 가림이 적용되지 않는다.
    /// </summary>
    public static IReadOnlyList<string> ColumnIdentityFields { get; } =
        ["name", "column", "column_name", "col_name", "column_nm", "colname", "col"];

    /// <summary>기존 <c>HistogramBucket</c> 의 snake_case 직렬화 이름(<c>endpoint_value</c>). 처리자가 모델을 그대로 내보내도 가려지도록 목록에 둔다.</summary>
    public const string HistogramEndpointValue = "endpoint_value";

    /// <summary>카탈로그 열 이름(<c>endpoint_actual_value</c>).</summary>
    public const string HistogramEndpointActualValue = "endpoint_actual_value";
}

/// <summary>
/// 제공자 중립 도구 정의 한 개. <paramref name="InputSchemaJson"/> 은 JSON Schema(draft 2020-12 부분집합) 문자열이라
/// MCP <c>inputSchema</c> 와 API 제공자의 tool 정의 양쪽에 그대로 쓴다.
/// </summary>
/// <param name="Name"><see cref="AgentToolNames"/> 의 값.</param>
/// <param name="Description">LLM 이 읽는 영어 설명(도구 선택 정확도를 위해 영어).</param>
/// <param name="MinimumLevel">이 도구를 쓰려면 접속의 실효 허용 수준이 이 값 이상이어야 한다.</param>
/// <param name="InputSchemaJson">입력 JSON Schema.</param>
/// <param name="RequiresExplainPlanSetting">접속의 <c>allowExplainPlan</c> 이 켜져 있어야 한다(<c>EXPLAIN PLAN</c> 이 PLAN_TABLE 에 쓰기 때문).</param>
/// <param name="DevTestOnly">운영 라벨 접속에서는 쓸 수 없다.</param>
public sealed record AgentToolDefinition(
    string Name,
    string Description,
    AiAccessLevel MinimumLevel,
    string InputSchemaJson,
    bool RequiresExplainPlanSetting = false,
    bool DevTestOnly = false);

/// <summary>튜닝 도구 9개의 정의(설계 §5.2 표). 목록 순서가 LLM 에 보이는 순서다.</summary>
public static class AgentTools
{
    private const string Conn = """
        "connection": { "type": "string", "description": "Connection alias. Omit to use the connection of the current request." }
        """;

    public static IReadOnlyList<AgentToolDefinition> All { get; } =
    [
        new(AgentToolNames.DescribeObject,
            "Columns (name, type, nullability, default, comment), PK/UK/FK constraints and index names of a table or view.",
            AiAccessLevel.Metadata,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["object"], "properties": {
              {{Conn}},
              "object": { "type": "string", "description": "OWNER.NAME or NAME." }
            } }
            """),
        new(AgentToolNames.GetIndexInfo,
            "Every index of a table: columns in order (descending flag, function expression), uniqueness, kind, status, visibility, partitioning, blevel, leaf_blocks, distinct_keys, clustering_factor, last analyzed and stale flag.",
            AiAccessLevel.Plan,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["table"], "properties": {
              {{Conn}},
              "table": { "type": "string", "description": "OWNER.NAME or NAME." }
            } }
            """),
        new(AgentToolNames.GetTableStats,
            "Optimizer statistics of a table (num_rows, blocks, avg_row_len, last analyzed, stale) and of its columns (num_distinct, num_nulls, density, histogram kind, bucket count).",
            AiAccessLevel.Plan,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["table"], "properties": {
              {{Conn}},
              "table": { "type": "string", "description": "OWNER.NAME or NAME." },
              "columns": { "type": "array", "items": { "type": "string" }, "description": "Columns to include. Omit for the columns the statement uses." }
            } }
            """),
        new(AgentToolNames.GetHistogram,
            "Histogram of a column: kind, bucket count and the row count of each bucket. Endpoint values are included only when the connection allows row values.",
            AiAccessLevel.Plan,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["table", "column"], "properties": {
              {{Conn}},
              "table": { "type": "string", "description": "OWNER.NAME or NAME." },
              "column": { "type": "string" }
            } }
            """),
        new(AgentToolNames.GetCursorPlan,
            "Actual execution plan of a cursor in the shared pool with A-Rows, Starts and elapsed time per step, and the plan hash.",
            AiAccessLevel.Plan,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["sql_id"], "properties": {
              {{Conn}},
              "sql_id": { "type": "string" },
              "child": { "type": "integer", "minimum": 0, "description": "Child cursor number. Omit for the latest." }
            } }
            """),
        new(AgentToolNames.ExplainPlan,
            "Estimated execution plan of a SELECT statement. Only available when the connection allows EXPLAIN PLAN.",
            AiAccessLevel.Plan,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["sql"], "properties": {
              {{Conn}},
              "sql": { "type": "string", "description": "A single SELECT statement." }
            } }
            """,
            RequiresExplainPlanSetting: true),
        new(AgentToolNames.GetSqlStats,
            "Execution statistics of a cursor: executions, elapsed time per execution, buffer gets and rows processed.",
            AiAccessLevel.Plan,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["sql_id"], "properties": {
              {{Conn}},
              "sql_id": { "type": "string" }
            } }
            """),
        new(AgentToolNames.GetTuningContext,
            "One bundle for the statement being tuned: statistics of the referenced objects, the columns the statement uses, their indexes and plan findings. Prefer this first, then ask for details with the other tools.",
            AiAccessLevel.Plan,
            $$"""
            { "type": "object", "additionalProperties": false, "properties": {
              {{Conn}},
              "sql": { "type": "string", "description": "Statement text. Omit to use the statement of the current request." },
              "sql_id": { "type": "string" },
              "budget_tokens": { "type": "integer", "minimum": 500, "maximum": 20000, "description": "Approximate size limit of the result." }
            } }
            """),
        new(AgentToolNames.BenchmarkQueries,
            "Runs SELECT variants repeatedly and reports elapsed time, logical and physical reads, row count and plan hash of each. Development and test connections only.",
            AiAccessLevel.Query,
            $$"""
            { "type": "object", "additionalProperties": false, "required": ["variants"], "properties": {
              {{Conn}},
              "variants": { "type": "array", "minItems": 1, "maxItems": 5, "items": {
                "type": "object", "additionalProperties": false, "required": ["label", "sql"], "properties": {
                  "label": { "type": "string" },
                  "sql": { "type": "string", "description": "A single SELECT statement." }
                } } },
              "repeat": { "type": "integer", "minimum": 1, "maximum": 5, "default": 3 }
            } }
            """,
            DevTestOnly: true),
    ];

    /// <summary>이름으로 정의를 찾는다. 없으면 null.</summary>
    public static AgentToolDefinition? Find(string name)
    {
        foreach (var tool in All)
        {
            if (string.Equals(tool.Name, name, StringComparison.Ordinal))
            {
                return tool;
            }
        }

        return null;
    }
}
