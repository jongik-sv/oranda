using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Formatting;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Tuning;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers.External;

/// <summary>외부 연결 처리자 시험의 가짜 허가·호출·저장소.</summary>
internal static class ExternalFakes
{
    public static readonly Guid DevId = new("b1f6c2d4-0000-4000-8000-000000000001");
    public static readonly Guid ProdId = new("b1f6c2d4-0000-4000-8000-000000000002");
    public static readonly Guid OffId = new("b1f6c2d4-0000-4000-8000-000000000003");

    public static AgentGrantConnection Dev(AiAccessLevel level = AiAccessLevel.QueryRows) =>
        new(DevId, "dev-erp", ConnectionEnvironment.Development, Dbms.Oracle, level);

    public static AgentGrantConnection Prod() =>
        new(ProdId, "prd-erp", ConnectionEnvironment.Production, Dbms.Tibero, AiAccessLevel.Metadata);

    public static AgentGrantConnection Off() =>
        new(OffId, "off-erp", ConnectionEnvironment.Test, Dbms.Oracle, AiAccessLevel.Off);

    public static AgentGrantView Grant(params AgentGrantConnection[] connections) =>
        new("g1", connections, DateTimeOffset.UnixEpoch, null, ["Claude Code"]);

    public static AppToolInvocation App(JsonObject args, AgentGrantView? grant = null, AgentGrantConnection? connection = null) =>
        new(grant ?? Grant(Dev()), "Claude Code", args, connection);

    /// <summary>게이트가 Host Db 처리자에 넘기는 호출(문장은 검사를 마친 것으로 본다).</summary>
    public static AgentToolInvocation Db(
        JsonObject args, AiAccessLevel level = AiAccessLevel.QueryRows, ConnectionEnvironment env = ConnectionEnvironment.Development,
        ISession? db = null, string? currentSchema = null, bool readOnlyExecution = false, params (string Arg, AgentObjectRef Obj)[] resolved)
    {
        var profile = TestData.Profile(env);
        var run = new AgentRunScope("g1:dev-erp", profile, new AiContext("dev-erp", env, level, CurrentSchema: currentSchema));
        return new AgentToolInvocation(
            new AgentSession(run), db ?? new FakeSession(profile), level, args, resolved.ToDictionary(r => r.Arg, r => r.Obj),
            MayExecuteSql: level >= AiAccessLevel.Query && AgentGrantPolicy.ExecutesIn(env),
            ReadOnlyExecution: readOnlyExecution);
    }

    public static IServiceProvider Services(params IAppToolHandler[] handlers) => new HandlerServices(handlers);

    private sealed class HandlerServices(IAppToolHandler[] handlers) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<IAppToolHandler>) ? handlers : null;
    }
}

/// <summary>편집기 레인 get_tab 의 가짜: 받은 인자를 남기고 정한 결과를 돌려준다.</summary>
internal sealed class FakeGetTab(Func<JsonObject, AgentToolOutput> respond) : IAppToolHandler
{
    public List<AppToolInvocation> Calls { get; } = [];

    public string ToolName => "get_tab";

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct)
    {
        Calls.Add(invocation);
        return ValueTask.FromResult(respond(invocation.Arguments));
    }

    public static FakeGetTab Returning(string text, bool truncated = false) =>
        new(_ => AgentToolOutput.Ok(new JsonObject { ["tab_id"] = "t1", ["version"] = 3, ["text"] = text, ["truncated"] = truncated }));
}

internal sealed class FakeFormatterProfiles(params FormatterProfile[] profiles) : IFormatterProfileStore
{
    public Task<IReadOnlyList<FormatterProfile>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FormatterProfile>>(profiles);

    public Task SaveAsync(FormatterProfile profile, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();

    public Task ExportAsync(string id, string path, CancellationToken ct) => throw new NotSupportedException();

    public Task<FormatterProfile> ImportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeHistory : IHistoryStore
{
    public List<HistoryEntry> Entries { get; } = [];

    public List<HistoryQuery> Queries { get; } = [];

    public Task<long> AddAsync(HistoryEntry entry, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query, CancellationToken ct)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<HistoryEntry>>(
        [
            .. Entries
                .Where(e => query.ProfileId is null || e.ProfileId == query.ProfileId)
                .Where(e => query.From is null || e.ExecutedAt >= query.From)
                .OrderByDescending(e => e.ExecutedAt)
                .Take(query.Limit),
        ]);
    }

    public Task DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct) => throw new NotSupportedException();

    public Task ClearAsync(Guid? profileId, CancellationToken ct) => throw new NotSupportedException();

    public Task PruneAsync(HistorySettings settings, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>sql_id 가시성 조회만 답하는 튜닝 서비스(파싱 스키마 SCOTT).</summary>
internal sealed class VisibleCursors : ITuningService
{
    public bool Visible { get; set; } = true;

    public Task<TuningCandidateSet> GetTuningCandidatesAsync(ISession s, TuningCandidateQuery query, CancellationToken ct) =>
        Task.FromResult(new TuningCandidateSet(Visible ? [.. (query.SqlIds ?? []).Select(id => new SqlStatsSample(id) { ParsingSchema = "SCOTT" })] : [], 0));

    public Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct) =>
        throw new NotSupportedException();

    public static AgentToolboxProvider Boxes(IMetadataProvider metadata, VisibleCursors? tuning = null) =>
        new(tuning ?? new VisibleCursors(), metadata, new NoKeys(), new NoPlans(), new LanguageService());

    private sealed class NoKeys : ITableKeyDetailProvider
    {
        public Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoPlans : IExecutionPlanService
    {
        public Task<ExecutionPlan> ExplainAsync(ISession s, Oranda.Core.Execution.SqlStatement stmt, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) => throw new NotSupportedException();
    }
}
