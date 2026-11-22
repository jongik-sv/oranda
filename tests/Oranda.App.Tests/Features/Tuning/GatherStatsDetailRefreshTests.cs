using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>
/// 통계 수집 뒤 테이블 창(객체 상세)에 바로 보이기(결함 2026-11-22): 그 테이블의 메타데이터 상세 캐시를 비우고, 열린 테이블 창의 통계 탭을 다시 읽는다.
/// 닫고 다시 열어도 새 값. 다른 테이블·다른 접속 창은 건드리지 않는다. 편집기에서 DBMS_STATS 를 직접 돌려도 같다.
/// </summary>
public class GatherStatsDetailRefreshTests
{
    private static readonly DbObjectRef EmpRef = new("SCOTT", "EMP", "TABLE");
    private static readonly TableStatsTarget Emp = new("SCOTT", "EMP");

    [Fact]
    public async Task Open_table_window_on_the_stats_tab_shows_the_new_statistics_at_once()
    {
        var meta = new StatsMeta { Analyzed = "2026-01-01" };
        var session = new FakeSession();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(session, EmpRef, showDdl: false);
        var stats = vm.Sections.Single(s => s.Title == "통계");
        vm.SelectedSection = stats;
        await vm.SectionLoad!;
        Assert.Equal("2026-01-01", stats.Rows[0][1]);

        meta.Analyzed = "2026-11-22"; // 통계 수집이 사전을 바꿨다
        await GatherStatsRefresh.ApplyAsync([Emp], new GatherStatsViews { Session = session, Meta = meta, Details = [vm] }, CancellationToken.None);

        Assert.Equal("2026-11-22", stats.Rows[0][1]);
        Assert.Equal(["SCOTT.EMP."], meta.Forgotten); // 그 테이블 상세 캐시만 비운다(유형은 모른다)
    }

    [Fact]
    public async Task Stats_tab_not_in_view_is_read_again_only_when_chosen()
    {
        var meta = new StatsMeta { Analyzed = "old" };
        var session = new FakeSession();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(session, EmpRef, showDdl: false);
        var stats = vm.Sections.Single(s => s.Title == "통계");
        vm.SelectedSection = stats;
        await vm.SectionLoad!;
        vm.SelectedSection = vm.Sections.Single(s => s.Title == "열");
        var reads = meta.StatsReads;

        meta.Analyzed = "new";
        await GatherStatsRefresh.ApplyAsync([Emp], new GatherStatsViews { Session = session, Meta = meta, Details = [vm] }, CancellationToken.None);
        Assert.Equal(reads, meta.StatsReads); // 보고 있지 않은 탭은 지금 읽지 않는다
        Assert.False(stats.IsLoaded);

        vm.SelectedSection = stats;
        await vm.SectionLoad!;
        Assert.Equal("new", stats.Rows[0][1]);
    }

    [Fact]
    public async Task Closed_and_reopened_window_gets_the_new_value_from_a_forgotten_cache()
    {
        var meta = new StatsMeta { Analyzed = "old", CacheOutline = true };
        var session = new FakeSession();
        var first = new ObjectDetailViewModel(meta);
        await first.OpenAsync(session, EmpRef, showDdl: false);
        Assert.Equal("old", first.Sections.Single(s => s.Title == "열").Rows[0][1]); // 캐시된 표 구역(통계가 든 값)

        meta.Analyzed = "new";
        await GatherStatsRefresh.ApplyAsync([Emp], new GatherStatsViews { Session = session, Meta = meta }, CancellationToken.None); // 창은 닫혀 있다

        var reopened = new ObjectDetailViewModel(meta);
        await reopened.OpenAsync(session, EmpRef, showDdl: false);
        Assert.Equal("new", reopened.Sections.Single(s => s.Title == "열").Rows[0][1]);
    }

    [Fact]
    public async Task Windows_of_another_table_or_another_connection_are_left_alone()
    {
        var meta = new StatsMeta { Analyzed = "old" };
        var session = new FakeSession();
        var other = new FakeSession { DbKey = "OTHER/DB/SCOTT" };
        var dept = new ObjectDetailViewModel(meta);
        await dept.OpenAsync(session, new DbObjectRef("SCOTT", "DEPT", "TABLE"), showDdl: false);
        dept.SelectedSection = dept.Sections.Single(s => s.Title == "통계");
        await dept.SectionLoad!;
        var elsewhere = new ObjectDetailViewModel(meta);
        await elsewhere.OpenAsync(other, EmpRef, showDdl: false);
        elsewhere.SelectedSection = elsewhere.Sections.Single(s => s.Title == "통계");
        await elsewhere.SectionLoad!;
        var reads = meta.StatsReads;

        meta.Analyzed = "new";
        await GatherStatsRefresh.ApplyAsync([Emp], new GatherStatsViews { Session = session, Meta = meta, Details = [dept, elsewhere] }, CancellationToken.None);

        Assert.Equal(reads, meta.StatsReads);
        Assert.Equal("old", dept.Sections.Single(s => s.Title == "통계").Rows[0][1]);
        Assert.Equal("old", elsewhere.Sections.Single(s => s.Title == "통계").Rows[0][1]);
    }

    [Fact]
    public async Task Schema_browser_properties_showing_the_table_read_it_again()
    {
        var meta = new StatsMeta { Analyzed = "old" };
        ISession session = new FakeSession();
        using var props = new SchemaPropertiesViewModel(() => meta, () => session) { Delay = TimeSpan.Zero };
        await props.ShowAsync(EmpRef);
        Assert.Equal("old", props.Tabs.Single(t => t.Title == "통계").Rows[0][1]);

        meta.Analyzed = "new";
        await GatherStatsRefresh.ApplyAsync([Emp], new GatherStatsViews { Session = session, Meta = meta, Properties = props }, CancellationToken.None);

        Assert.Equal("new", props.Tabs.Single(t => t.Title == "통계").Rows[0][1]);
    }

    [Fact]
    public Task Stats_tab_in_the_view_shows_the_new_value_after_gathering() => HeadlessHost.Run(async () =>
    {
        var meta = new StatsMeta { Analyzed = "2026-01-01" };
        var session = new FakeSession();
        var vm = new ObjectDetailViewModel(meta);
        await vm.OpenAsync(session, EmpRef, showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 700 };
        w.Show();
        try
        {
            vm.SelectedSection = vm.Sections.Single(s => s.Title == "통계");
            await vm.SectionLoad!;
            Dispatcher.UIThread.RunJobs();
            var table = view.FindControl<DetailTable>("SectionTable")!;
            Assert.Contains("2026-01-01", vm.SelectedSection!.Text, StringComparison.Ordinal);

            meta.Analyzed = "2026-11-22";
            await GatherStatsRefresh.ApplyAsync([Emp], new GatherStatsViews { Session = session, Meta = meta, Details = [vm] }, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(vm.Sections.Single(s => s.Title == "통계"), vm.SelectedSection); // 탭은 그대로
            Assert.Contains("2026-11-22", vm.SelectedSection!.Text, StringComparison.Ordinal);
            Assert.Equal(1, table.List.ItemCount);
        }
        finally
        {
            w.Close();
        }
    });

    // ---- 편집기에서 DBMS_STATS 직접 실행 ------------------------------------------------------

    [Theory]
    [InlineData("BEGIN DBMS_STATS.GATHER_TABLE_STATS('SCOTT', 'EMP'); END;", "SCOTT.EMP")]
    [InlineData("begin dbms_stats.gather_table_stats(ownname => 'scott', tabname => 'emp', cascade => true); end;", "SCOTT.EMP")]
    [InlineData("BEGIN DBMS_STATS.GATHER_TABLE_STATS(tabname => 'EMP', ownname => 'SCOTT'); END;", "SCOTT.EMP")]
    [InlineData("EXEC DBMS_STATS.GATHER_TABLE_STATS(USER, 'EMP')", "MES.EMP")]
    [InlineData("BEGIN SYS.DBMS_STATS.GATHER_TABLE_STATS('SCOTT', '\"Mixed\"'); END;", "SCOTT.\"Mixed\"")]
    [InlineData("BEGIN DBMS_STATS.GATHER_TABLE_STATS('A','T1'); DBMS_STATS.GATHER_TABLE_STATS('A','T2'); END;", "A.T1,A.T2")]
    [InlineData("BEGIN DBMS_STATS.GATHER_TABLE_STATS(v_owner, 'EMP'); END;", "")] // 변수 = 모른다
    [InlineData("BEGIN DBMS_STATS.GATHER_SCHEMA_STATS('SCOTT'); END;", "")]
    [InlineData("SELECT 'DBMS_STATS.GATHER_TABLE_STATS(''A'',''B'')' FROM dual", "")] // 문자열 안
    [InlineData("-- DBMS_STATS.GATHER_TABLE_STATS('A','B')\nSELECT 1 FROM dual", "")] // 주석 안
    public void Editor_statement_names_the_gathered_tables(string sql, string expected)
    {
        var found = GatherStatsSqlTargets.Find(sql, "mes");
        Assert.Equal(expected, string.Join(",", found.Select(t => t.Display)));
    }

    [Fact]
    public void Only_successful_editor_runs_trigger_a_refresh_and_the_dialog_path_is_not_counted_twice()
    {
        var session = new FakeSession();
        var stmt = new SqlStatement("BEGIN DBMS_STATS.GATHER_TABLE_STATS('SCOTT', 'EMP'); END;", default, StatementKind.PlSql);
        StatementExecuted Run(ExecutionStatus status, HistorySource source) =>
            new(session.Id, stmt, new ExecutionResult(stmt, status), new ExecutionOptions { Source = source });

        Assert.Equal([Emp], GatherStatsWatch.TargetsOf(Run(ExecutionStatus.Succeeded, HistorySource.Editor), session));
        Assert.Empty(GatherStatsWatch.TargetsOf(Run(ExecutionStatus.Failed, HistorySource.Editor), session));
        Assert.Empty(GatherStatsWatch.TargetsOf(Run(ExecutionStatus.Succeeded, GatherStatsRunner.Source), session)); // 확인 창이 스스로 새로 읽는다
    }

    /// <summary>통계 탭(「마지막 분석」)을 늘 서버에서 읽는 가짜 공급자. <see cref="CacheOutline"/> 이면 표 구역을 상세 캐시처럼 잊을 때까지 둔다.</summary>
    private sealed class StatsMeta : IMetadataProvider
    {
        private readonly Dictionary<string, string> _cached = [];

        public string Analyzed { get; set; } = string.Empty;

        public bool CacheOutline { get; init; }

        public int StatsReads { get; private set; }

        public List<string> Forgotten { get; } = [];

        public Task ForgetObjectDetailsAsync(ISession s, IReadOnlyList<DbObjectRef> objects, CancellationToken ct)
        {
            foreach (var o in objects)
            {
                Forgotten.Add($"{o.Owner}.{o.Name}.{o.ObjectType}");
                foreach (var key in _cached.Keys.Where(k => k.StartsWith($"{s.DbKey}|{o.Owner}.{o.Name}|", StringComparison.Ordinal)).ToList())
                {
                    _cached.Remove(key);
                }
            }

            return Task.CompletedTask;
        }

        public Task<ObjectDetailOutline> GetDetailOutlineAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            var key = $"{s.DbKey}|{obj.Owner}.{obj.Name}|{obj.ObjectType}";
            if (!CacheOutline || !_cached.TryGetValue(key, out var value))
            {
                value = Analyzed;
                _cached[key] = value;
            }

            return Task.FromResult(new ObjectDetailOutline(obj,
            [
                new DetailTab("일반", DetailPart.General),
                new DetailTab("열", DetailPart.Table, new DetailSection("열", ["이름", "분석"], [["ID", value]])),
                new DetailTab("통계", DetailPart.Stats),
                new DetailTab("권한", DetailPart.Privileges),
            ]));
        }

        public Task<IReadOnlyList<DetailSection>> GetDetailPartAsync(ISession s, DbObjectRef obj, DetailPart part, CancellationToken ct)
        {
            if (part == DetailPart.Stats)
            {
                StatsReads++;
            }

            return Task.FromResult<IReadOnlyList<DetailSection>>(part switch
            {
                DetailPart.Stats => [Stats()],
                DetailPart.General => [new DetailSection("일반", ["항목", "값"], [["유형", obj.ObjectType]])],
                _ => [],
            });
        }

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj, [Stats()]));

        private DetailSection Stats() => new("통계", ["항목", "값"], [["마지막 분석", Analyzed]]);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
