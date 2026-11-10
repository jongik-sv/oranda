using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Connection;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Transfer.Copy;

/// <summary>
/// Tibero 복사 실행기·사전 점검 하위 형식(T5 TI1b, 서버 없음). <c>Dbms.Tibero</c> 키로 등록한다(xdb-copy XC0, Tibero→Tibero 복사 켬).
/// 같은 계정·같은 표 복사는 Core 판정(<see cref="CopySameDatabase"/>)이 Tibero 의 DbKey 모양(<c>DB이름/-/계정</c>)에서 막는지 가짜 세션으로 본다.
/// 서버로 도는 확인은 <c>TiberoCopySameAccountIt</c>.
/// </summary>
public sealed class TiberoCopyRunnerTests
{
    private const string Owner = "SCOTT";
    private const string Table = "EMP";

    [Fact]
    public void Subtypes_construct_with_fakes_and_are_registered_under_the_Tibero_key()
    {
        var bulk = new TiberoBulkExecutionService(new AllowAll(), new NoQuestions(), new EventBus());
        var execution = new TiberoExecutionService(new AllowAll(), new NoQuestions(), new EventBus(), null, dbmsOutput: false);

        var runner = new TiberoCopyRunner(bulk, execution, new SentinelRunLog());
        var precheck = new TiberoCopyPrecheckService(bulk, new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync), new AllowAll());

        Assert.IsAssignableFrom<ICopyRunner>(runner);
        Assert.IsAssignableFrom<ICopyPrecheckService>(precheck);
        AssertRegistered(typeof(TiberoCopyRunner), typeof(ICopyRunner));
        AssertRegistered(typeof(TiberoCopyPrecheckService), typeof(ICopyPrecheckService));
        AssertRegistered(typeof(TiberoBulkExecutionService), typeof(IBulkExecutionService));
    }

    private static void AssertRegistered(Type implementation, Type service)
    {
        var attribute = Assert.Single(implementation.GetCustomAttributes<OrandaServiceAttribute>(inherit: false));
        Assert.Equal(service, attribute.ServiceType);
        Assert.Equal(Dbms.Tibero, attribute.Dbms);
    }

    [Fact]
    public void Subtypes_pass_the_Tibero_parts_to_the_bases()
    {
        var bulk = new TiberoBulkExecutionService(new AllowAll(), new NoQuestions(), new EventBus());
        var execution = new TiberoExecutionService(new AllowAll(), new NoQuestions(), new EventBus(), null, dbmsOutput: false);
        var runner = new TiberoCopyRunner(bulk, execution, new SentinelRunLog());
        var precheck = new TiberoCopyPrecheckService(bulk, new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync), new AllowAll());

        Assert.IsType<TiberoSqlCatalog>(BaseField(runner, typeof(CopyRunner), "_catalog"));
        Assert.IsType<TiberoErrorClassifier>(BaseField(runner, typeof(CopyRunner), "_errors"));
        Assert.IsType<TiberoCopyDialect>(BaseField(runner, typeof(CopyRunner), "_dialect"));
        Assert.IsType<TiberoBulkValueCodec>(BaseField(runner, typeof(CopyRunner), "_codec"));
        Assert.IsType<TiberoRecreateStep>(BaseField(runner, typeof(CopyRunner), "_recreate"));

        // 주 생성자 매개변수는 컴파일러가 만든 필드(<이름>P)로 잡힌다.
        Assert.IsType<TiberoSqlCatalog>(BaseField(precheck, typeof(CopyPrecheckService), "<catalog>P"));
        Assert.IsType<TiberoErrorClassifier>(BaseField(precheck, typeof(CopyPrecheckService), "<errors>P"));
        Assert.IsType<TiberoCopyPrecheckDialect>(BaseField(precheck, typeof(CopyPrecheckService), "<dialect>P"));
        Assert.IsType<TiberoCopyDialect>(BaseField(precheck, typeof(CopyPrecheckService), "<statements>P"));
        Assert.IsType<TiberoBulkValueCodec>(BaseField(precheck, typeof(CopyPrecheckService), "<codec>P"));
    }

    private static object? BaseField(object instance, Type baseType, string name)
    {
        var field = baseType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field is not null, "base field not found: " + name);
        return field!.GetValue(instance);
    }

    [Fact]
    public void Tibero_db_key_is_same_database_for_another_user_and_different_for_another_db_name()
    {
        var scott = Server("TIBERO", "SCOTT");
        var other = Server("TIBERO", "HR");
        var elsewhere = Server("TIBERO2", "SCOTT");

        Assert.Equal("TIBERO/-/SCOTT", TiberoServerProbe.BuildDbKey(scott));
        Assert.True(CopySameDatabase.IsSameDatabase(TiberoServerProbe.BuildDbKey(scott), scott, TiberoServerProbe.BuildDbKey(other), other));
        Assert.True(CopySameDatabase.IsSameDatabase(TiberoServerProbe.BuildDbKey(scott), scott, TiberoServerProbe.BuildDbKey(scott), scott));
        Assert.False(CopySameDatabase.IsSameDatabase(TiberoServerProbe.BuildDbKey(scott), scott, TiberoServerProbe.BuildDbKey(elsewhere), elsewhere));
    }

    [Fact]
    public async Task Precheck_blocks_the_same_table_of_the_same_database_before_reading_anything()
    {
        var sessions = Sessions(Server("TIBERO", "SCOTT"), Server("TIBERO", "SCOTT"));
        var precheck = NewPrecheck();

        var report = await precheck.RunAsync(sessions, Job(), new ResolvedPeriod(null, null), null, CancellationToken.None);

        var item = Assert.Single(report.Items);
        Assert.Equal("SameSourceTarget", item.Code);
        Assert.Equal(CheckLevel.Error, item.Level);
        Assert.Contains("같은 DB", item.Message, StringComparison.Ordinal);
        Assert.Equal(Table, item.Table);
        Assert.Empty(report.Order);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public async Task Runner_refuses_the_same_table_of_the_same_database_before_the_run_log_or_anything_is_sent()
    {
        var sessions = Sessions(Server("TIBERO", "SCOTT"), Server("TIBERO", "SCOTT"));
        var job = Job();
        var report = Report(job);
        var log = new SentinelRunLog();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewRunner(log).RunAsync(
            sessions, job, report, Decisions(), new ResolvedPeriod(null, null), null, new CopyRunControl(), CancellationToken.None));

        Assert.Contains("원본과 대상이 같은 DB", ex.Message, StringComparison.Ordinal);
        Assert.False(log.Begun);
    }

    [Fact]
    public async Task Runner_passes_the_same_database_guard_when_the_db_name_differs()
    {
        // DB 이름이 다르면 막히지 않고 실행 기록을 시작한다(여기서 가짜 기록이 멈춘다 = 가드를 지났다는 표시).
        var sessions = Sessions(Server("TIBERO", "SCOTT"), Server("TIBERO2", "SCOTT"));
        var job = Job();
        var log = new SentinelRunLog();

        await Assert.ThrowsAsync<SentinelRunLog.Reached>(() => NewRunner(log).RunAsync(
            sessions, job, Report(job), Decisions(), new ResolvedPeriod(null, null), null, new CopyRunControl(), CancellationToken.None));

        Assert.True(log.Begun);
    }

    private static TiberoCopyRunner NewRunner(ICopyRunLog log) =>
        new(new TiberoBulkExecutionService(new AllowAll(), new NoQuestions(), new EventBus()),
            new TiberoExecutionService(new AllowAll(), new NoQuestions(), new EventBus(), null, dbmsOutput: false), log);

    private static TiberoCopyPrecheckService NewPrecheck() =>
        new(new TiberoBulkExecutionService(new AllowAll(), new NoQuestions(), new EventBus()),
            new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync), new AllowAll());

    /// <summary>Tibero 모양 서버 정보: DB_UNIQUE_NAME 없이 DB 이름만, 컨테이너 없음(DbKey = <c>DB이름/-/계정</c>).</summary>
    private static ServerInfo Server(string dbName, string user) =>
        TestData.Server("6") with { DbUniqueName = null, DatabaseName = dbName, ContainerName = null, SessionUser = user, Dbms = Dbms.Tibero };

    private static CopySessions Sessions(ServerInfo source, ServerInfo target) =>
        new(new FakeSession(TestData.Profile(readOnly: true) with { Dbms = Dbms.Tibero }, source) { DbKey = TiberoServerProbe.BuildDbKey(source) },
            new FakeSession(TestData.Profile() with { Dbms = Dbms.Tibero }, target) { DbKey = TiberoServerProbe.BuildDbKey(target) });

    /// <summary>대상 지우기가 없는 처리(AppendOnly)로 같은 계정의 같은 표를 복사하는 작업.</summary>
    private static CopyJob Job() => new()
    {
        Id = "ti1b", Name = "ti1b", Source = new ConnectionRef("a", Owner), Target = new ConnectionRef("b", Owner),
        SourceOwner = Owner, TargetOwner = Owner, MaskSalt = "salt",
        Tables = [new TableRule(Table, RuleKind.All, TargetAction.AppendOnly)],
    };

    /// <summary>사전 점검이 막은 작업은 계획이 없으므로, 실행기의 같은 DB 가드까지 닿도록 손으로 만든 계획이 든 보고서.</summary>
    private static PrecheckReport Report(CopyJob job)
    {
        var info = new TableInfo(new DbObjectRef(Owner, Table, "TABLE"), [new ColumnInfo("ID", 1, "NUMBER")]);
        return new PrecheckReport([], [new CopyTablePlan(job.Tables[0], info, info, ["ID"], ["ID"])], [], new Dictionary<string, long>());
    }

    private static CopyDecisions Decisions() =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(), new HashSet<string>(StringComparer.Ordinal), false);

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }

    /// <summary>실행 기록을 시작하면 표시하고 멈추는 가짜: 실행기가 가드를 지나 실제로 일을 시작했는지를 본다.</summary>
    private sealed class SentinelRunLog : ICopyRunLog
    {
        public bool Begun { get; private set; }

        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct)
        {
            Begun = true;
            throw new Reached();
        }

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

        public sealed class Reached : Exception;
    }
}
