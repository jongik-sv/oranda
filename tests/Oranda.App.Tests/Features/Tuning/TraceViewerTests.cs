using System.Runtime.CompilerServices;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Tuning.Trace;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using TestSession = Oranda.Testing.FakeSession;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>TSK-06-04 트레이스 보기: 상태(11g·권한·빈 파일·오류), TKPROF 식 요약 정렬, idle 대기, 본문 거름, 5 MB 자름, 10053 실행 순서.</summary>
public class TraceViewerTests
{
    internal static readonly string[] SampleLines =
    [
        "Trace file /u01/app/oracle/diag/rdbms/mesqa/MESQA1/trace/MESQA1_ora_183274.trc",
        "Oracle Database 19c Enterprise Edition Release 19.0.0.0.0",
        "PARSING IN CURSOR #140211 len=212 dep=0 uid=104 oct=3 lid=104 tim=8812004 hv=2215 ad='7f1a' sqlid='7h35uxf5uhmm1'",
        "SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh",
        "END OF STMT",
        "PARSE #140211:c=812,e=1022,p=0,cr=0,cu=0,mis=1,r=0,dep=0,og=1,plh=1838229974,tim=8812010",
        "EXEC #140211:c=44,e=44,p=0,cr=0,cu=0,mis=0,r=0,dep=0,og=1,plh=1838229974,tim=8812101",
        "WAIT #140211: nam='SQL*Net message to client' ela= 2 driver id=1413697536 #bytes=1 p3=0",
        "WAIT #140211: nam='db file scattered read' ela= 4122 file#=7 block#=2488 blocks=128",
        "WAIT #140211: nam='SQL*Net message from client' ela= 9800 driver id=1413697536 #bytes=1 p3=0",
        "FETCH #140211:c=39512,e=141880,p=2386,cr=3244,cu=0,mis=0,r=93,dep=0,og=1,plh=1838229974",
        "STAT #140211 id=1 cnt=93 pid=0 pos=1 obj=0 op='SORT ORDER BY'",
    ];

    /// <summary>요약 시험용 고정 결과(파서는 TSK-06-02 몫이라 여기서는 결과만 준다).</summary>
    internal static TraceSummary Summary() => new(
    [
        new TraceStatement("UPDATE MES.LOT_HIST SET EVT_STAT = :1", "4w1xq9n2b7c0d") { ExecuteCount = 1208, CpuMicros = 1_020_000, ElapsedMicros = 3_410_000, Disk = 2880, Query = 4830, Current = 6204, Rows = 1208 },
        new TraceStatement("SELECT lh.lot_id, lh.evt_time FROM mes.lot_hist lh", "7h35uxf5uhmm1")
            { ParseCount = 12, ExecuteCount = 12, FetchCount = 156, CpuMicros = 6_180_000, ElapsedMicros = 22_100_000, Disk = 372_264, Query = 506_160, Rows = 14_496 },
        new TraceStatement("COMMIT", "0k8s2p1d4c6vb") { ExecuteCount = 1208, CpuMicros = 100_000, ElapsedMicros = 660_000 },
    ],
    [
        new TraceWaitTotal("SQL*Net message from client", 156, 9_800_000),
        new TraceWaitTotal("db file scattered read", 2912, 14_200_000),
        new TraceWaitTotal("gc cr multi block request", 611, 1_380_000),
    ]);

    internal sealed class FakeTuning : ITuningService
    {
        public Func<SessionKey, TraceFileInfo> File { get; set; } = _ => new TraceFileInfo("/u01/trace/MESQA1_ora_183274.trc", true, null);

        public Func<TraceFileInfo, IReadOnlyList<string>> Lines { get; set; } = _ => SampleLines;

        public List<SessionKey> Asked { get; } = [];

        public Task<TraceFileInfo> GetTraceFileAsync(ISession s, SessionKey target, CancellationToken ct)
        {
            Asked.Add(target);
            return Task.FromResult(File(target));
        }

        public Task<IReadOnlyList<string>> ReadTraceLinesAsync(ISession s, TraceFileInfo file, CancellationToken ct) => Task.FromResult(Lines(file));
    }

    private static TraceViewerViewModel NewVm(FakeTuning tuning)
    {
        return new TraceViewerViewModel(tuning) { Parser = _ => Summary() };
    }

    [Fact]
    public async Task Default_parser_is_the_core_tkprof_summary()
    {
        var vm = new TraceViewerViewModel(new FakeTuning()); // 파서를 바꾸지 않음 = Core TraceParser
        await vm.LoadAsync(new TestSession());
        var s = Assert.Single(vm.Statements);
        Assert.Equal("7h35uxf5uhmm1", s.SqlIdText);
        Assert.Equal("93", s.RowsText);
        Assert.Equal("0.14", s.ElapsedText); // e= 1022 + 44 + 141880 μs
        Assert.Contains(vm.Waits, w => w.Event == "db file scattered read");
        Assert.True(vm.Waits.Single(w => w.Event == "SQL*Net message from client").IsIdle);
    }

    [Fact]
    public void First_screen_asks_for_a_connection()
    {
        var vm = new TraceViewerViewModel();
        Assert.True(vm.IsNoSession);
        Assert.False(vm.CanRunOptimizerTrace);
    }

    [Fact]
    public async Task Summary_sorts_statements_marks_the_top_one_and_leaves_idle_waits_out_of_the_total()
    {
        var tuning = new FakeTuning();
        var vm = NewVm(tuning);
        await vm.LoadAsync(new TestSession());

        Assert.Equal([default], tuning.Asked); // 내 세션
        Assert.True(vm.IsReady);
        Assert.True(vm.ShowSummary);
        Assert.Equal("내 세션", vm.TargetText);
        Assert.Equal(["7h35uxf5uhmm1", "4w1xq9n2b7c0d", "0k8s2p1d4c6vb"], vm.Statements.Select(s => s.SqlIdText)); // elapsed 순
        Assert.True(vm.Statements[0].IsTop);
        Assert.Equal("84%", vm.Statements[0].ShareText);
        Assert.Equal("22.10", vm.Statements[0].ElapsedText);
        Assert.Equal("372,264", vm.Statements[0].DiskText);
        Assert.Same(vm.Statements[0], vm.SelectedStatement);

        // 호출 표: 호출별 횟수 + 합계 줄
        Assert.Equal(4, vm.CallRows.Count);
        Assert.Equal(["Fetch", "156"], vm.CallRows[2].Take(2));
        Assert.Equal(["합계", "180", "6.18", "22.10", "372,264", "506,160", "0", "14,496"], vm.CallRows[3]);
        Assert.Equal("7h35uxf5uhmm1", vm.CallCaption);

        // 대기: 합계 큰 순, idle 은 표시하고 합계에서 뺀다
        Assert.Equal("db file scattered read", vm.Waits[0].Event);
        Assert.True(vm.Waits.Single(w => w.Event.StartsWith("SQL*Net", StringComparison.Ordinal)).IsIdle);
        Assert.Equal("15.58", vm.WaitTotalText);
        Assert.Contains("문장 3 · 경과 합계 26.17 s · 대기 합계 15.58 s (idle 제외)", vm.StatusText, StringComparison.Ordinal);

        vm.SortIndex = (int)TraceSortKey.Executions;
        Assert.Equal("4w1xq9n2b7c0d", vm.Statements[0].SqlIdText);
        Assert.Same(vm.Statements.Single(s => s.SqlIdText == "7h35uxf5uhmm1"), vm.SelectedStatement); // 고른 문장은 남는다
        vm.SortIndex = (int)TraceSortKey.Current;
        Assert.Equal("4w1xq9n2b7c0d", vm.Statements[0].SqlIdText);

        var text = vm.ExportText();
        Assert.Contains("7h35uxf5uhmm1", text, StringComparison.Ordinal);
        Assert.Contains("SQL*Net message from client (idle)", text, StringComparison.Ordinal);
        Assert.Single(vm.FileRows);
        Assert.Equal("MESQA1_ora_183274.trc", vm.FileRows[0][0]);
    }

    [Fact]
    public async Task Body_highlights_lines_and_filters_wait_stat_and_find()
    {
        var vm = NewVm(new FakeTuning());
        await vm.LoadAsync(new TestSession());
        vm.ViewIndex = 1;
        Assert.True(vm.ShowBody);
        Assert.False(vm.ShowSummary);
        Assert.Equal(SampleLines.Length - 1, vm.VisibleLines.Count); // STAT 줄은 처음엔 숨김
        Assert.True(vm.VisibleLines.Single(l => l.Number == 3).IsParsing);
        Assert.True(vm.VisibleLines.Single(l => l.Number == 9).IsWait);
        Assert.True(vm.VisibleLines.Single(l => l.Number == 10).IsIdleWait);

        vm.ShowStatLines = true;
        Assert.Equal(SampleLines.Length, vm.VisibleLines.Count);
        vm.ShowWaitLines = false;
        Assert.DoesNotContain(vm.VisibleLines, l => l.Text.StartsWith("WAIT", StringComparison.Ordinal));
        vm.ShowWaitLines = true;
        vm.FindText = "scattered";
        Assert.Equal([9], vm.VisibleLines.Select(l => l.Number));
        Assert.Contains("보이는 줄 1", vm.BodyInfoText, StringComparison.Ordinal);
        Assert.Equal(vm.VisibleLines[0].Text, vm.ExportText());
        vm.FindText = string.Empty;
        Assert.Equal(SampleLines.Length, vm.VisibleLines.Count);
    }

    [Fact]
    public async Task Big_files_show_the_first_five_megabytes_until_read_all()
    {
        var line = new string('x', 1023);
        var tuning = new FakeTuning { Lines = _ => [.. Enumerable.Repeat(line, 6 * 1024)] }; // 6 MB
        var vm = NewVm(tuning);
        await vm.LoadAsync(new TestSession());
        Assert.True(vm.IsTruncated);
        Assert.Equal(5 * 1024, vm.SelectedFile!.Lines.Count);
        Assert.Contains("앞 5.0 MB 만", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("6.0 MB", vm.BodyInfoText, StringComparison.Ordinal);

        vm.ReadAll();
        Assert.False(vm.IsTruncated);
        Assert.Equal(6 * 1024, vm.SelectedFile!.Lines.Count);
        Assert.Equal(6 * 1024, vm.VisibleLines.Count);

        Assert.Single(TraceViewerViewModel.Head(["only one huge line " + new string('y', 100)], 10)); // 한 줄은 늘 보인다
    }

    [Fact]
    public async Task Eleven_g_shows_the_server_path_and_reason_only()
    {
        var tuning = new FakeTuning { File = _ => new TraceFileInfo("/u01/app/oracle/diag/rdbms/mesdev/MESDEV/trace/MESDEV_ora_40112.trc", false, "v$diag_trace_file_contents 는 12.2 부터 있습니다.") };
        var vm = NewVm(tuning);
        await vm.LoadAsync(new TestSession(), new SessionKey(1, 133, 7), "세션 1·133");
        Assert.True(vm.IsUnreadable);
        Assert.Equal("/u01/app/oracle/diag/rdbms/mesdev/MESDEV/trace/MESDEV_ora_40112.trc", vm.ServerPath);
        Assert.Contains("12.2", vm.Message, StringComparison.Ordinal);
        Assert.Equal("세션 1·133", vm.TargetText);
        Assert.Equal(new SessionKey(1, 133, 7), Assert.Single(tuning.Asked));
        Assert.Empty(vm.Files);
    }

    [Fact]
    public async Task Empty_trace_privilege_errors_and_other_errors_get_their_own_screens()
    {
        var tuning = new FakeTuning { Lines = _ => [] };
        var vm = NewVm(tuning);
        await vm.LoadAsync(new TestSession());
        Assert.True(vm.IsNoTrace);
        Assert.Contains("트레이스 켜기", vm.Message, StringComparison.Ordinal);

        tuning.File = _ => throw new InvalidOperationException("ORA-00942: table or view does not exist");
        await vm.LoadAsync(new TestSession());
        Assert.True(vm.IsNoPrivilege);
        Assert.Contains("GRANT SELECT ON v_$diag_info", TraceViewerViewModel.GrantSql, StringComparison.Ordinal);

        tuning.File = _ => throw new InvalidOperationException("ORA-03113: end-of-file on communication channel\n자세히");
        await vm.LoadAsync(new TestSession());
        Assert.True(vm.IsError);
        Assert.EndsWith("ORA-03113: end-of-file on communication channel", vm.Message, StringComparison.Ordinal);

        var none = new TraceViewerViewModel(new EmptyTuning());
        await none.LoadAsync(new TestSession());
        Assert.True(none.IsError);
    }

    private sealed class EmptyTuning : ITuningService;

    [Fact]
    public async Task Optimizer_trace_opens_the_file_in_the_body_at_the_marker_line()
    {
        string[] lines = [.. SampleLines, "Registered qb: SEL$1 0x7f (PARSER)", "/* ORANDA_10053_20261024 */ SELECT 1 FROM dual", "***************************************"];
        var tuning = new FakeTuning { Lines = _ => lines };
        var vm = NewVm(tuning);
        var session = new TestSession();
        vm.OptimizerTrace = _ =>
        {
            Assert.True(vm.IsTracing);
            Assert.False(vm.CanRunOptimizerTrace);
            return Task.FromResult(new OptimizerTraceOutcome(session, new TraceFileInfo("/u01/trace/x.trc", true, null), "ORANDA_10053_20261024"));
        };
        Assert.True(vm.CanRunOptimizerTrace);

        await vm.RunOptimizerTraceAsync();

        Assert.False(vm.IsTracing);
        Assert.Equal(1, vm.ViewIndex);
        Assert.True(vm.SelectedFile!.Is10053);
        Assert.Contains("ORANDA_10053_20261024", vm.ScrollTarget!.Text, StringComparison.Ordinal);
        Assert.Same(session, vm.Session);

        vm.OptimizerTrace = _ => throw new InvalidOperationException("ORA-01031: insufficient privileges");
        await vm.RunOptimizerTraceAsync();
        Assert.Equal("10053 실패: ORA-01031: insufficient privileges", vm.StatusText);
        Assert.False(vm.IsTracing);
    }

    [Fact]
    public void Statement_items_flag_recursive_rows_through_the_depth_contract_when_present()
    {
        var item = new TraceStatementItem(1, new TraceStatement("select obj# from obj$", null) { ElapsedMicros = 10 }, 100, false);
        Assert.Equal("10%", item.ShareText);
        Assert.False(item.IsRecursive);
        var recursive = new TraceStatementItem(3, new TraceStatement("select obj# from obj$", null) { Depth = 1 }, 100, false);
        Assert.True(recursive.IsRecursive); // dep>0 = 회색 막대
        Assert.False(recursive.IsNormal);
        Assert.Equal("<1%", new TraceStatementItem(2, new TraceStatement("x", null) { ElapsedMicros = 1 }, 1000, false).ShareText);
        Assert.Equal(TraceLineKind.Stat, TraceLineItem.Classify("STAT #1 id=1"));
        Assert.Equal(TraceLineKind.Normal, TraceLineItem.Classify("WAITING"));
    }

    // ---- 10053 실행 쪽 ----

    private sealed class Engine : IExecutionService
    {
        public List<(SqlStatement Stmt, IReadOnlyDictionary<string, BindValue> Binds)> Calls { get; } = [];

        public Func<SqlStatement, ExecutionResult> Result { get; set; } = s => new ExecutionResult(s, ExecutionStatus.Succeeded);

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            Calls.Add((stmt, binds));
            return Task.FromResult(Result(stmt));
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static readonly SqlStatement Select = new("SELECT * FROM emp WHERE deptno = :d", default, StatementKind.Query) { Verb = "SELECT", BindNames = ["d"] };

    [Fact]
    public async Task Runner_turns_10053_on_parses_a_marked_copy_without_running_it_then_turns_it_off()
    {
        var session = new TestSession();
        var engine = new Engine();
        var tuning = new FakeTuning();
        var runner = new OptimizerTraceRunner(new ExecutionState(), () => session, () => engine, () => tuning);

        var outcome = await runner.RunAsync(Select, default);

        Assert.Equal(
            [OptimizerTraceRunner.EventOnSql, OptimizerTraceRunner.ParseBlock, OptimizerTraceRunner.EventOffSql],
            engine.Calls.Select(c => c.Stmt.Text));
        Assert.Equal(["ALTER SESSION", "DECLARE", "ALTER SESSION"], engine.Calls.Select(c => c.Stmt.Verb));
        Assert.Contains("DBMS_SQL.PARSE", OptimizerTraceRunner.ParseBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("EXPLAIN", OptimizerTraceRunner.ParseBlock, StringComparison.Ordinal); // PLAN_TABLE 에 쓰지 않는다
        Assert.DoesNotContain("EXECUTE", OptimizerTraceRunner.ParseBlock, StringComparison.Ordinal); // 실행하지 않는다
        var copy = (string)engine.Calls[1].Binds[OptimizerTraceRunner.SqlBindName].Value!;
        Assert.Equal($"/* {outcome.Marker} */ {Select.Text}", copy);
        Assert.StartsWith("ORANDA_10053_", outcome.Marker, StringComparison.Ordinal);
        Assert.Equal([default], tuning.Asked); // 내 세션의 파일
        Assert.Same(session, outcome.Session);
    }

    [Fact]
    public async Task Runner_turns_the_event_off_even_when_the_parse_fails_and_refuses_ddl()
    {
        var engine = new Engine
        {
            Result = s => s.Kind == StatementKind.PlSql
                ? new ExecutionResult(s, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") { Number = 942 } }
                : new ExecutionResult(s, ExecutionStatus.Succeeded),
        };
        var runner = new OptimizerTraceRunner(new ExecutionState(), () => new TestSession(), () => engine, () => new FakeTuning());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Select, default));
        Assert.Equal(OptimizerTraceRunner.EventOffSql, engine.Calls[^1].Stmt.Text);

        var ddl = new SqlStatement("DROP TABLE t", default, StatementKind.Ddl) { Verb = "DROP" };
        Assert.Throws<InvalidOperationException>(() => OptimizerTraceRunner.BuildCopy(ddl, "M"));
        var sub = Select with { SubstitutionNames = ["owner"] };
        Assert.Throws<InvalidOperationException>(() => OptimizerTraceRunner.BuildCopy(sub, "M"));
        var dml = new SqlStatement("UPDATE t SET a = 1", default, StatementKind.Dml) { Verb = "UPDATE" };
        Assert.Equal("/* M */ UPDATE t SET a = 1", OptimizerTraceRunner.BuildCopy(dml, "M")); // DML 은 파싱만이라 실행되지 않는다

        engine.Calls.Clear();
        engine.Result = s => new ExecutionResult(s, ExecutionStatus.Denied) { Policy = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile, "읽기 전용") };
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Select, default));
        Assert.Equal("읽기 전용", denied.Message);
        Assert.Single(engine.Calls); // 켜기가 막히면 더 보내지 않는다

        var none = new OptimizerTraceRunner(new ExecutionState(), () => null, () => engine, () => new FakeTuning());
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.RunAsync(Select, default));
    }

    [Fact]
    public async Task Command_handler_opens_the_viewer_for_my_session_or_the_given_one()
    {
        var tuning = new FakeTuning();
        TraceViewerViewModel? opened = null;
        var handler = new TraceCommandHandler
        {
            SessionProvider = () => new TestSession(),
            OpenDocument = (id, vm, title) =>
            {
                Assert.Equal("oranda.panel.traceViewer", id);
                Assert.Equal("트레이스 보기", title);
                opened ??= new TraceViewerViewModel(tuning) { Parser = _ => Summary() };
                return opened;
            },
            StatementProvider = () => Select,
            OptimizerTraceOverride = (stmt, _) => Task.FromResult(new OptimizerTraceOutcome(new TestSession(), new TraceFileInfo("/t.trc", true, null), "M")),
        };

        await handler.ExecuteAsync(new Oranda.Core.Shell.CommandContext(TraceCommandHandler.TraceViewerId, null!), default);
        Assert.True(opened!.IsReady);
        Assert.Equal([default], tuning.Asked);

        await handler.ExecuteAsync(new Oranda.Core.Shell.CommandContext(TraceCommandHandler.TraceViewerId, null!, new SessionKey(2, 55, 9)), default);
        Assert.Equal(new SessionKey(2, 55, 9), tuning.Asked[^1]);

        await handler.ExecuteAsync(new Oranda.Core.Shell.CommandContext(TraceCommandHandler.OptimizerTraceId, null!), default);
        Assert.Equal(1, opened.ViewIndex);
        Assert.True(opened.CanRunOptimizerTrace);
    }
}
