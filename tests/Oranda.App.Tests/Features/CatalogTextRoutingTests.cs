using Oranda.App.Features;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.Flashback;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.Tuning.Trace;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features;

/// <summary>
/// U5: App 의 카탈로그 접근이 <c>CatalogRouting</c>(→ <c>DbmsServices.CatalogText</c>)을 거친다.
/// Oracle 세션 + Oracle 카탈로그 선택기는 예전 Oracle 고정 호환 멤버(<see cref="EmbeddedCatalogCharacterizationTests"/> 가 글자를 고정)와 같은 글을 낸다.
/// 선택기·구현·세션이 없거나 비 Oracle 이면 null 을 돌려주고 던지지 않는다(부르는 쪽이 기능을 끄고 이유를 보인다).
/// </summary>
public sealed class CatalogTextRoutingTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static readonly ServerInfo Server = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, false, false, false, false));

    /// <summary>Oracle 카탈로그(<c>OracleSqlCatalog</c>, internal)를 특성으로 찾아 넣은 선택기를 든 서비스 공급자.</summary>
    private static Provider OracleCatalogServices()
    {
        var type = typeof(Oranda.Oracle.Catalog.EmbeddedCatalog).Assembly.GetTypes()
            .Single(t => t.GetCustomAttributes(typeof(OrandaServiceAttribute), inherit: false).Cast<OrandaServiceAttribute>()
                .Any(a => a.ServiceType == typeof(ISqlCatalog) && a.ForDbms == Dbms.Oracle));
        var table = new DbmsServiceTable().Add(typeof(ISqlCatalog), Dbms.Oracle, () => Activator.CreateInstance(type, nonPublic: true)!);
        return new Provider(table);
    }

    private sealed class Provider(IDbmsServiceSelector? selector) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IDbmsServiceSelector) ? selector : null;
    }

    private sealed class FakeSession(Dbms dbms) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev", "scott", "h:1521/s", Dbms: dbms);

        public ServerInfo Server { get; } = CatalogTextRoutingTests.Server with { Dbms = dbms };

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---------- (a) Oracle 세션 + Oracle 카탈로그 선택기 = 예전과 같은 글 ----------

    [Fact]
    public void Oracle_session_gets_the_same_text_as_the_oracle_fixed_members()
    {
        var services = OracleCatalogServices();
        var session = new FakeSession(Dbms.Oracle);
        var key = new SessionKey(2, 12, 345);

        Assert.Equal(PlanStatsRerunner.StatisticsLevelSql, PlanStatsRerunner.StatisticsLevelStatement(services, session)!.Text);
        Assert.Equal("ALTER SESSION", PlanStatsRerunner.StatisticsLevelStatement(services, session)!.Verb);

        Assert.Equal(OptimizerTraceRunner.EventOnSql, OptimizerTraceRunner.EventOn(services, session));
        Assert.Equal(OptimizerTraceRunner.EventOffSql, OptimizerTraceRunner.EventOff(services, session));
        Assert.Equal(OptimizerTraceRunner.ParseBlock, OptimizerTraceRunner.ParseStatement(services, session)!.Text);

        Assert.Equal(SessionMonitorCommands.DisconnectStatement(key, true, true).Text, SessionMonitorCommands.DisconnectStatement(services, session, key, true, true)!.Text);
        Assert.Equal("ALTER SYSTEM DISCONNECT SESSION '12,345,@2' IMMEDIATE", SessionMonitorCommands.DisconnectStatement(services, session, key, true, true)!.Text);
        Assert.Equal("12,345,@2", SessionMonitorCommands.DisconnectStatement(services, session, key, true, true)!.DangerousTarget);

        Assert.Equal(LockText.KillStatement(key, rac: true), LockText.KillStatement(services, session, key, rac: true));
        Assert.Equal("ALTER SYSTEM KILL SESSION '12,345,@2' IMMEDIATE", LockText.KillStatement(services, session, key, rac: true));
    }

    [Fact]
    public void Oracle_session_fk_index_check_and_flashback_versions_match_the_fixed_members()
    {
        var services = OracleCatalogServices();
        var session = new FakeSession(Dbms.Oracle);

        Assert.Equal(LockHints.ForeignKeyIndexCheck("HR.EMP"), LockHints.ForeignKeyIndexCheck(services, session, "HR.EMP"));
        Assert.Equal(LockHints.ForeignKeyIndexCheck("O'N"), LockHints.ForeignKeyIndexCheck(services, session, "O'N"));

        var fixedSql = FlashbackTrackSql.Build(Emp, ["EMPNO", "ENAME"], ["EMPNO"], [7369m], "HR", 0.25m, new DateTime(2026, 10, 27, 9, 30, 15));
        var routed = FlashbackTrackSql.BuildFor(services, session, Emp, ["EMPNO", "ENAME"], ["EMPNO"], [7369m], "HR", 0.25m, new DateTime(2026, 10, 27, 9, 30, 15));
        Assert.Equal(fixedSql, routed);
    }

    [Fact]
    public void Without_a_service_provider_the_text_comes_from_the_oracle_fixed_path_for_oracle_or_no_session_only()
    {
        Assert.Equal("ALTER SESSION SET STATISTICS_LEVEL = ALL", CatalogRouting.Text(null, null, "app.statistics_level"));
        Assert.Equal("ALTER SESSION SET STATISTICS_LEVEL = ALL", CatalogRouting.Text(null, new FakeSession(Dbms.Oracle), "app.statistics_level"));
        Assert.Null(CatalogRouting.Text(null, new FakeSession(Dbms.Tibero), "app.statistics_level"));
    }

    // ---------- (b) 구현·세션 없음 = null (던지지 않는다) ----------

    [Fact]
    public void No_selector_no_catalog_or_no_session_gives_null_everywhere()
    {
        var key = new SessionKey(1, 7, 8);
        var oracle = new FakeSession(Dbms.Oracle);
        var tibero = new FakeSession(Dbms.Tibero);
        var noSelector = new Provider(null);
        var emptySelector = new Provider(new DbmsServiceTable());
        var withCatalog = OracleCatalogServices();

        foreach (var (services, session) in new (IServiceProvider, ISession?)[]
                 {
                     (noSelector, oracle), (emptySelector, oracle), (emptySelector, tibero), (withCatalog, tibero), (withCatalog, null),
                 })
        {
            Assert.Null(PlanStatsRerunner.StatisticsLevelStatement(services, session));
            Assert.Null(OptimizerTraceRunner.EventOn(services, session));
            Assert.Null(OptimizerTraceRunner.EventOff(services, session));
            Assert.Null(OptimizerTraceRunner.ParseStatement(services, session));
            Assert.Null(SessionMonitorCommands.DisconnectStatement(services, session, key, true, false));
            Assert.Null(LockText.KillStatement(services, session, key, rac: false));
            Assert.Null(LockHints.ForeignKeyIndexCheck(services, session, "HR.EMP"));
            Assert.Null(FlashbackTrackSql.BuildFor(services, session, Emp, ["EMPNO"], ["EMPNO"], [1m]));
        }
    }

    [Fact]
    public void Unknown_catalog_id_is_null_not_an_exception_and_the_reason_names_the_dbms()
    {
        Assert.Null(CatalogRouting.Text(OracleCatalogServices(), new FakeSession(Dbms.Oracle), "app.no_such_statement"));
        Assert.Contains("Tibero", CatalogRouting.Unavailable(new FakeSession(Dbms.Tibero)), StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_hints_without_a_catalog_drop_only_the_copy_sql()
    {
        var withoutCatalog = LockHints.Build([], new Provider(new DbmsServiceTable()), new FakeSession(Dbms.Tibero));
        var compat = LockHints.Build([]);
        Assert.Equal(compat.Count, withoutCatalog.Count); // 대기자가 없으면 힌트 모양은 같다(복사 SQL 이 필요한 힌트가 없을 뿐)
    }

    // ---------- 소스 스캔: 호환 멤버가 부르는 CatalogRouting 한 곳 말고 EmbeddedCatalog 직접 호출이 없다 ----------

    [Fact]
    public void App_sources_reach_embedded_catalog_only_through_catalog_routing()
    {
        var root = FindRepoRoot();
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Oranda.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFileName(file) == "CatalogRouting.cs")
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (line.Contains("EmbeddedCatalog", StringComparison.Ordinal) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    hits.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + ":" + lineNumber);
                }
            }
        }

        Assert.Empty(hits);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.GetFiles(dir.FullName, "Oranda.sln*").Length > 0)
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("저장소 루트를 찾지 못했다.");
    }
}
