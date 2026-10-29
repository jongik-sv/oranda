using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;
using Oranda.Language.Ai.References;
using Oranda.Language.Ai.Tools;

namespace Oranda.Language.Ai.Handlers;

/// <summary>
/// 에이전트 실행(<see cref="AgentSession"/>)마다 <see cref="TuningToolbox"/> 하나를 만들어 준다. 실행 한 번 캐시(<see cref="ToolRunCache"/>)가
/// 실행 안에서만 살도록 도구 상자를 세션에 묶는다(세션이 사라지면 함께 사라진다).
/// </summary>
[OrandaService(typeof(AgentToolboxProvider))]
internal sealed class AgentToolboxProvider(
    ITuningService tuning, IMetadataProvider metadata, ITableKeyDetailProvider keys, IExecutionPlanService plans, ILanguageService language)
{
    private readonly ConditionalWeakTable<AgentSession, TuningToolbox> _boxes = [];

    public TuningToolbox For(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _boxes.GetValue(session, _ => new TuningToolbox(tuning, metadata, keys, plans, language, new ToolRunCache()));
    }
}

/// <summary>
/// 도구 처리자 공통: 인자를 읽고 도구 상자를 불러 결과 JSON 을 만든다. 허용 수준·범위·감사·가림은 AgentGate 몫이라 여기서 하지 않는다.
/// 실패는 예외 대신 <see cref="AgentToolOutput.Error"/> 로 돌려준다(서버 오류 원문은 ORA 번호만).
/// </summary>
internal abstract partial class ToolHandlerBase(AgentToolboxProvider boxes) : IAgentToolHandler
{
    public abstract string ToolName { get; }

    public async ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        try
        {
            var result = await RunAsync(boxes.For(invocation.Session), invocation, ct).ConfigureAwait(false);
            return new AgentToolOutput { Result = result, SqlIds = SqlIdsOf(result) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 취소 중에 드라이버가 낸 오류(ORA-01013 등)는 취소로 다룬다.
            throw new OperationCanceledException(ct);
        }
        catch (AiToolException ex)
        {
            return AgentToolOutput.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return AgentToolOutput.Error("인자가 올바르지 않습니다: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            return AgentToolOutput.Error("조회 시간이 초과되었습니다."); // 호출자의 취소가 아닌 취소(명령 시간 초과 등)
        }
        catch (Exception ex)
        {
            var code = OraCode().Match(ex.Message).Value;
            return AgentToolOutput.Error(code.Length > 0 ? $"조회에 실패했습니다 ({code})." : "조회에 실패했습니다.");
        }
    }

    /// <summary>
    /// 게이트는 문장의 객체 이름을 문맥의 현재 스키마로 풀어 범위를 검사하지만 문장을 실행하는 도구는 접속 사용자 스키마에서 실행한다.
    /// 두 풀이가 다른 객체를 가리키면 검사한 것과 실행할 것이 달라지므로 소유자를 적도록 돌려보낸다.
    /// </summary>
    protected static async Task EnsureSameResolutionAsync(IStatementReferenceExtractor extractor, AgentToolInvocation inv, string sql, CancellationToken ct)
    {
        var current = inv.Session.Context.CurrentSchema;
        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, inv.Session.Profile.User, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var viaCurrent = await extractor.AnalyzeAsync(inv.Db, sql, current, ct).ConfigureAwait(false);
        var viaUser = await extractor.AnalyzeAsync(inv.Db, sql, null, ct).ConfigureAwait(false);
        var a = viaCurrent.Objects.Select(o => o.Key).Order(StringComparer.Ordinal);
        var b = viaUser.Objects.Select(o => o.Key).Order(StringComparer.Ordinal);
        if (!a.SequenceEqual(b, StringComparer.Ordinal))
        {
            throw new AiToolException("문장의 객체 이름이 편집기의 현재 스키마와 접속 사용자에서 다른 객체로 풀립니다. OWNER.NAME 으로 소유자를 적어 주십시오.");
        }
    }

    protected abstract Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct);

    /// <summary>
    /// 이 결과로 이 요청에서 조회해도 되게 된 sql_id(게이트는 결과 본문을 훑지 않으므로 여기서만 알린다).
    /// 문장을 실행했거나 문장의 sql_id 를 알아낸 도구가 덮어쓴다.
    /// </summary>
    protected virtual IReadOnlyList<string> SqlIdsOf(JsonNode result) => [];

    /// <summary>sql_id 도구가 볼 수 있는 커서의 파싱 스키마: 실행 범위의 현재 스키마, 없으면 접속 사용자.</summary>
    protected static string ScopeSchema(AgentToolInvocation inv) =>
        (string.IsNullOrWhiteSpace(inv.Session.Context.CurrentSchema) ? inv.Session.Profile.User : inv.Session.Context.CurrentSchema).ToUpperInvariant();

    [GeneratedRegex(@"ORA-\d{5}")]
    private static partial Regex OraCode();

    protected static JsonNode Node<T>(T value) => ToolJson.ToNode(value);

    protected static DbObjectRef Table(AgentToolInvocation inv, string argument)
    {
        if (!inv.Resolved.TryGetValue(argument, out var o))
        {
            throw new AiToolException($"'{argument}' 인자를 객체로 해석하지 못했습니다.");
        }

        return new DbObjectRef(o.Owner, o.Name, string.IsNullOrEmpty(o.Kind) ? "TABLE" : o.Kind);
    }

    protected static string? Text(AgentToolInvocation inv, string name) =>
        inv.Arguments.TryGetPropertyValue(name, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    protected static string RequiredText(AgentToolInvocation inv, string name) =>
        Text(inv, name) ?? throw new AiToolException($"'{name}' 인자가 필요합니다.");

    /// <summary>정수 인자. 없으면 null, 정수가 아니거나 <paramref name="min"/>~<paramref name="max"/> 밖이면 오류.</summary>
    protected static int? Number(AgentToolInvocation inv, string name, int min = 0, int max = int.MaxValue)
    {
        if (!inv.Arguments.TryGetPropertyValue(name, out var n) || n is null)
        {
            return null;
        }

        if (n is not JsonValue v || !v.TryGetValue<int>(out var i) || i < min || i > max)
        {
            throw new AiToolException($"'{name}' 는 {min}~{max} 사이의 정수여야 합니다.");
        }

        return i;
    }

    /// <summary>문자열 배열 인자. 없으면 null, 문자열이 아닌 원소가 있으면 오류.</summary>
    protected static IReadOnlyList<string>? TextList(AgentToolInvocation inv, string name)
    {
        if (!inv.Arguments.TryGetPropertyValue(name, out var n) || n is null)
        {
            return null;
        }

        if (n is not JsonArray arr || arr.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _)))
        {
            throw new AiToolException($"'{name}' 는 문자열 배열이어야 합니다.");
        }

        return [.. arr.Select(x => x!.GetValue<string>())];
    }
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class DescribeObjectHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.DescribeObject;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct) =>
        Node(await box.DescribeObjectAsync(inv.Db, Table(inv, AgentFields.ObjectName), ct).ConfigureAwait(false));
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetIndexInfoHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.GetIndexInfo;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct) =>
        Node(await box.GetIndexInfoAsync(inv.Db, Table(inv, AgentFields.Table), ct).ConfigureAwait(false));
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetTableStatsHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.GetTableStats;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct)
    {
        var table = Table(inv, AgentFields.Table);
        var columns = TextList(inv, AgentFields.Columns);
        if (columns is { Count: > 0 })
        {
            // 호출이 열을 지정했다.
        }
        else if (inv.Session.Context.StatementText is { Length: > 0 } text)
        {
            // 열을 생략하면 대상 문장이 쓰는 열만(문장이 이 표를 쓰지 않으면 전체).
            var refs = StatementReferenceExtractor.Extract(text);
            var used = refs.Objects
                .Where(o => string.Equals(o.Name, table.Name, StringComparison.OrdinalIgnoreCase)
                    && (o.Owner is null || string.Equals(o.Owner, table.Owner, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(refs.ColumnsOf)
                .Distinct()
                .ToList();
            columns = used.Count > 0 ? used : null;
        }

        return Node(await box.GetTableStatsAsync(inv.Db, table, columns, ct).ConfigureAwait(false));
    }
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetHistogramHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    /// <summary>한 응답에 싣는 버킷 수 상한(개수가 큰 순).</summary>
    private const int MaxBuckets = 50;

    public override string ToolName => AgentToolNames.GetHistogram;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct)
    {
        // 끝값은 행 값 수준에서만 싣는다. 그 아래에서는 AgentGate 도 같은 필드를 지우므로 이중 방어다.
        var endpoints = inv.Level >= AiAccessLevel.QueryRows;
        var result = await box.GetHistogramAsync(inv.Db, Table(inv, AgentFields.Table), RequiredText(inv, AgentFields.Column), MaxBuckets, endpoints, ct).ConfigureAwait(false);
        return Node(result);
    }
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetCursorPlanHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.GetCursorPlan;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct) =>
        Node(await box.GetCursorPlanAsync(inv.Db, new CursorRef(RequiredText(inv, AgentFields.SqlId), Number(inv, "child", 0, 65535)), ScopeSchema(inv), ct).ConfigureAwait(false));
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class ExplainPlanHandler(AgentToolboxProvider boxes, IStatementReferenceExtractor extractor) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.ExplainPlan;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct)
    {
        var sql = RequiredText(inv, AgentFields.Sql);
        await EnsureSameResolutionAsync(extractor, inv, sql, ct).ConfigureAwait(false);
        return Node(await box.ExplainPlanAsync(inv.Db, sql, inv.ExplainPlanAllowed, ct).ConfigureAwait(false));
    }
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetSqlStatsHandler(AgentToolboxProvider boxes) : ToolHandlerBase(boxes)
{
    public override string ToolName => AgentToolNames.GetSqlStats;

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct) =>
        Node(await box.GetSqlStatsAsync(inv.Db, RequiredText(inv, AgentFields.SqlId), ScopeSchema(inv), ct).ConfigureAwait(false));
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetTuningContextHandler(AgentToolboxProvider boxes, IStatementReferenceExtractor extractor) : ToolHandlerBase(boxes)
{
    private const int DefaultBudgetTokens = 6000;

    public override string ToolName => AgentToolNames.GetTuningContext;

    protected override IReadOnlyList<string> SqlIdsOf(JsonNode result) =>
        result["statement"]?["sql_id"]?.GetValue<string>() is { Length: > 0 } id ? [id] : [];

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct)
    {
        var ctx = inv.Session.Context;
        var sqlIdArg = Text(inv, AgentFields.SqlId);
        var sql = Text(inv, AgentFields.Sql);

        // sql_id 만 주고 문장을 안 줬을 때는 그 sql_id 가 요청의 대상 문장일 때만 대상 문장 글을 쓴다(다른 문장과 커서를 짝짓지 않는다).
        if (sql is null && (sqlIdArg is null || string.Equals(sqlIdArg, ctx.SqlId, StringComparison.OrdinalIgnoreCase)))
        {
            sql = ctx.StatementText;
        }

        var sqlId = sqlIdArg ?? (sql is null || string.Equals(sql, ctx.StatementText, StringComparison.Ordinal) ? ctx.SqlId : null);
        var allowExplain = inv.ExplainPlanAllowed;
        if (allowExplain && sql is not null && sqlId is null)
        {
            // 문장을 EXPLAIN 으로 실행하게 되므로 검사한 이름 풀이와 실행 풀이가 같을 때만 쓴다.
            try
            {
                await EnsureSameResolutionAsync(extractor, inv, sql, ct).ConfigureAwait(false);
            }
            catch (AiToolException)
            {
                allowExplain = false;
            }
        }

        var owner = ctx.CurrentSchema ?? inv.Session.Profile.User.ToUpperInvariant();
        var request = new TuningContextRequest(sql, sqlId, Number(inv, "budget_tokens", 500, 20000) ?? DefaultBudgetTokens, allowExplain, owner, ScopeSchema(inv));
        return Node(await box.GetTuningContextAsync(inv.Db, request, ct).ConfigureAwait(false));
    }
}

[OrandaService(typeof(IAgentToolHandler))]
internal sealed class BenchmarkQueriesHandler(AgentToolboxProvider boxes, IStatementReferenceExtractor extractor) : ToolHandlerBase(boxes)
{
    private const int DefaultRepeat = 3;

    public override string ToolName => AgentToolNames.BenchmarkQueries;

    protected override IReadOnlyList<string> SqlIdsOf(JsonNode result) =>
        [.. result.AsArray().Select(x => x?["sql_id"]?.GetValue<string>()).OfType<string>().Where(id => id.Length > 0)];

    protected override async Task<JsonNode> RunAsync(TuningToolbox box, AgentToolInvocation inv, CancellationToken ct)
    {
        if (!inv.Arguments.TryGetPropertyValue(AgentFields.Variants, out var node) || node is not JsonArray arr)
        {
            throw new AiToolException("'variants' 인자가 필요합니다.");
        }

        var variants = new List<BenchmarkVariant>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject o
                || o["label"] is not JsonValue label || !label.TryGetValue<string>(out var l)
                || o["sql"] is not JsonValue sql || !sql.TryGetValue<string>(out var s))
            {
                throw new AiToolException("'variants' 의 각 원소는 문자열 label 과 sql 을 가진 객체여야 합니다.");
            }

            variants.Add(new BenchmarkVariant(l, s));
        }

        foreach (var v in variants)
        {
            await EnsureSameResolutionAsync(extractor, inv, v.Sql, ct).ConfigureAwait(false);
        }

        // 실행 허용은 게이트가 문장을 검사한 뒤 채운다(Query 이상·검사 완료). 운영 접속은 문장마다 시간 상한을 둔다.
        // 게이트가 대체하라고 한 변형(위험 함수·DB 링크·시퀀스 등)은 실행하지 않고 공유 풀 커서 실측으로 바꾼다. 결과 순서는 변형 순서 그대로다.
        var production = inv.Session.Profile.Environment == ConnectionEnvironment.Production;
        var substitutions = inv.Substitutions ?? [];
        if (substitutions.Count > 0 && substitutions.Count != variants.Count)
        {
            throw new AiToolException("변형 검사 결과의 개수가 맞지 않습니다.");
        }

        if (!inv.MayExecuteSql)
        {
            throw new AiToolException("이 접속의 허용 수준으로는 benchmark_queries 를 실행할 수 없습니다(Query 이상이 필요합니다).");
        }

        var run = variants.Where((_, i) => substitutions.Count == 0 || substitutions[i] is null).ToList();
        box.CheckSubstitutes(variants.Where((_, i) => substitutions.Count > 0 && substitutions[i] is not null));
        var measured = run.Count == 0 ? [] : await box.BenchmarkAsync(
            inv.Db, run, Number(inv, "repeat", 1, 5) ?? DefaultRepeat, inv.MayExecuteSql, ScopeSchema(inv), ct,
            production ? TuningToolbox.ProductionStatementTimeout : null, inv.ReadOnlyExecution).ConfigureAwait(false);
        if (substitutions.Count == 0)
        {
            return Node(measured);
        }

        var result = new JsonArray();
        var next = 0;
        for (var i = 0; i < variants.Count; i++)
        {
            result.Add(substitutions[i] is { } reason
                ? Node(await box.CursorStatsInsteadAsync(inv.Db, variants[i], reason, ScopeSchema(inv), ct).ConfigureAwait(false))
                : Node(measured[next++]));
        }

        return result;
    }
}
