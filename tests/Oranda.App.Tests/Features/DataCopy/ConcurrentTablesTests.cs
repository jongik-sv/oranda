using System.ComponentModel;
using Oranda.App.Features.DataCopy;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.DataCopy;

/// <summary>
/// PF2 화면 쪽: 작업 옵션 「동시 테이블」 저장·이유 글, 레인 세션 쌍 열기·닫기(<see cref="DataCopySessions.OpenWorkersAsync(Func{ConnectionProfile, int, CancellationToken, Task{DedicatedSessionBatch}}, ConnectionProfile, ConnectionProfile, int, CancellationToken)"/>),
/// 실행이 레인 세션을 실행기에 넘기고 끝나면 닫는 것, 단계 알림의 상태 글.
/// </summary>
public sealed class ConcurrentTablesTests
{
    private static readonly CopyDecisions NoDecisions = new(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false);

    [Fact]
    public void Concurrent_tables_option_loads_and_saves_and_one_is_not_written()
    {
        var h = new DataCopyHarness();
        var editor = new RuleEditorViewModel(h.Services());
        Assert.Equal(1, editor.ConcurrentTables); // 새 작업은 한 테이블씩

        editor.SetConnections([h.Source, h.Target]);
        editor.Load(TestJobs.Job("동시") with { Options = new CopyOptions { ConcurrentTables = 3 } }, h.Source, h.Target);
        Assert.Equal(3, editor.ConcurrentTables);
        Assert.Equal(3, editor.BuildJob().Options.ConcurrentTables);

        editor.ConcurrentTables = 1;
        Assert.Null(editor.BuildJob().Options.ConcurrentTables); // 1 이면 작업 파일에 쓰지 않는다

        editor.ConcurrentTables = 9;
        Assert.Equal(CopyConcurrency.Max, editor.BuildJob().Options.ConcurrentTables);
    }

    [Fact]
    public void Reason_text_follows_concurrent_tables_point_in_time_read_and_commit_mode()
    {
        var h = new DataCopyHarness();
        var editor = new RuleEditorViewModel(h.Services());
        editor.SetConnections([h.Source, h.Target]);
        editor.Load(TestJobs.Job("이유"), h.Source, h.Target);
        Assert.True(editor.ConsistentRead);
        Assert.Equal(string.Empty, editor.ConcurrencyReason); // 1 이면 이유 없음
        Assert.False(editor.HasConcurrencyReason);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)editor).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        editor.ConcurrentTables = 2;
        Assert.Contains(nameof(RuleEditorViewModel.ConcurrencyReason), changed);
        Assert.Equal(CopyConcurrency.ReadPointNote, editor.ConcurrencyReason); // PF2b: Oracle 원본은 같은 시점 읽기를 켠 채 동시(시작 SCN 으로 읽기)
        Assert.True(editor.HasConcurrencyReason);

        changed.Clear();
        editor.ConsistentRead = false; // 커밋은 테이블마다(기본)
        Assert.Contains(nameof(RuleEditorViewModel.ConcurrencyReason), changed);
        Assert.Equal(string.Empty, editor.ConcurrencyReason);

        changed.Clear();
        editor.Commit = CommitMode.AtEnd;
        Assert.Contains(nameof(RuleEditorViewModel.ConcurrencyReason), changed);
        Assert.Equal(CopyConcurrency.AtEndReason, editor.ConcurrencyReason);

        editor.ConcurrentTables = 1;
        Assert.Equal(string.Empty, editor.ConcurrencyReason);
    }

    [Fact]
    public void Tibero_source_keeps_the_point_in_time_read_reason_and_switching_the_source_updates_it()
    {
        // PF2b: Tibero 원본은 시점 지정 읽기를 아직 쓰지 않는다 — 같은 시점 읽기를 켜면 예전처럼 한 테이블씩(이유 글).
        var h = new DataCopyHarness();
        var tibero = TestJobs.Profile("TBSRC", ConnectionEnvironment.Development, 9) with { Dbms = Dbms.Tibero };
        var editor = new RuleEditorViewModel(h.Services());
        editor.SetConnections([h.Source, tibero, h.Target]);
        editor.Load(TestJobs.Job("티베로", source: "TBSRC") with { Options = new CopyOptions { ConcurrentTables = 2 } }, tibero, h.Target);
        Assert.Equal(CopyConcurrency.ConsistentReadReason, editor.ConcurrencyReason);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)editor).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        editor.Source = editor.SourceOptions.Single(o => o.Profile.Name == "SRC"); // Oracle 원본으로 바꾸면 시작 SCN 안내
        Assert.Contains(nameof(RuleEditorViewModel.ConcurrencyReason), changed);
        Assert.Equal(CopyConcurrency.ReadPointNote, editor.ConcurrencyReason);
    }

    [Fact]
    public async Task Open_workers_opens_read_only_sources_and_targets_once_per_profile()
    {
        var h = new DataCopyHarness();
        var calls = new List<(ConnectionProfile Profile, int Count)>();

        var opened = await DataCopySessions.OpenWorkersAsync((profile, count, _) =>
        {
            calls.Add((profile, count));
            return Task.FromResult(new DedicatedSessionBatch(
                [.. Enumerable.Range(0, count).Select(_ => (ISession)new FakeSession(profile, purpose: SessionPurpose.Dedicated))], null));
        }, h.Source, h.Target, 2, CancellationToken.None);

        Assert.Equal(2, opened.Pairs.Count);
        Assert.Null(opened.Note);
        Assert.Equal(2, calls.Count); // 접속마다 한 번(비밀번호 한 번)
        Assert.True(calls[0].Profile.ReadOnly);
        Assert.Equal("SRC", calls[0].Profile.Name);
        Assert.Equal("TGT", calls[1].Profile.Name);
        Assert.False(calls[1].Profile.ReadOnly);
        Assert.All(opened.Pairs, p => Assert.True(p.Source.Profile.ReadOnly));
        Assert.All(opened.Pairs, p => Assert.False(p.Target.Profile.ReadOnly));

        await DataCopySessions.CloseWorkersAsync(opened.Pairs);
        Assert.All(opened.Pairs, p =>
        {
            Assert.Equal(SessionState.Closed, p.Source.State);
            Assert.Equal(SessionState.Closed, p.Target.State);
        });
    }

    [Fact]
    public async Task Fewer_targets_than_sources_pairs_what_opened_closes_the_extra_source_and_notes_it()
    {
        var h = new DataCopyHarness();
        var sources = new List<FakeSession>();

        var opened = await DataCopySessions.OpenWorkersAsync((profile, count, _) =>
        {
            if (profile.Name == "SRC")
            {
                var made = Enumerable.Range(0, count).Select(_ => new FakeSession(profile, purpose: SessionPurpose.Dedicated)).ToList();
                sources.AddRange(made);
                return Task.FromResult(new DedicatedSessionBatch([.. made], null));
            }

            return Task.FromResult(new DedicatedSessionBatch([new FakeSession(profile, purpose: SessionPurpose.Dedicated)], "ORA-02391: exceeded simultaneous SESSIONS_PER_USER limit"));
        }, h.Source, h.Target, 3, CancellationToken.None);

        var pair = Assert.Single(opened.Pairs);
        Assert.Same(sources[0], pair.Source);
        Assert.Equal(3, sources.Count);
        Assert.Equal(SessionState.Idle, sources[0].State);
        Assert.Equal(SessionState.Closed, sources[1].State); // 짝이 없는 원본은 닫는다
        Assert.Equal(SessionState.Closed, sources[2].State);
        Assert.Equal("레인 세션을 3쌍 가운데 1쌍만 열어 동시 2개로 줄여 복사합니다: ORA-02391: exceeded simultaneous SESSIONS_PER_USER limit", opened.Note);
    }

    [Fact]
    public async Task Production_target_opens_no_worker_session()
    {
        var h = new DataCopyHarness();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => DataCopySessions.OpenWorkersAsync((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new DedicatedSessionBatch([], null));
        }, h.Source, h.Production, 1, CancellationToken.None));

        Assert.Equal(0, calls);
    }

    [Fact]
    public Task Run_hands_worker_pairs_to_the_runner_shows_the_stage_and_closes_them_after() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var runner = new SessionsRunner();
        runner.Reports.Add(new CopyProgress(string.Empty, 0, null, string.Empty) { Stage = new CopyStage(2, 1, 3) });
        var release = new TaskCompletionSource();
        runner.Behavior = async () => { await release.Task; return TestJobs.Result(); };
        var vm = new RunViewModel(Services(h, runner));
        var main =new CopySessions(new FakeSession(h.Source with { ReadOnly = true }), new FakeSession(h.Target));
        var job = TestJobs.Job("동시", rules: [TestJobs.Rule("A"), TestJobs.Rule("B")]) with
        {
            ConsistentRead = false,
            Options = new CopyOptions { ConcurrentTables = 2 },
        };

        var run = vm.StartAsync(main, job, ConcurrentReport(), NoDecisions, new ResolvedPeriod(null, null));
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var sessions = Assert.IsType<CopySessions>(runner.Sessions);
        Assert.Same(main.Source, sessions.Source);
        Assert.Same(main.Target, sessions.Target);
        var worker = Assert.Single(sessions.Workers);
        Assert.True(worker.Source.Profile.ReadOnly);
        Assert.Equal("SRC", worker.Source.Profile.Name);
        Assert.Equal("TGT", worker.Target.Profile.Name);
        Assert.Equal("동시 2개 · 단계 1/3", vm.StatusText);

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Closed, worker.Source.State); // 실행이 끝나면 레인 세션을 닫는다
        Assert.Equal(SessionState.Closed, worker.Target.State);
        Assert.NotEqual(SessionState.Closed, main.Source.State); // 주 쌍은 창이 닫는다(다시 넣기에 쓴다)
    });

    [Fact]
    public Task Point_in_time_read_without_the_read_point_check_opens_no_worker_session() => HeadlessHost.Run(async () =>
    {
        // 점검 결과에 시점 읽기 점검(FLASHBACK 권한을 본 것)이 없다: 실행기가 한 테이블씩 돌므로(CopyConcurrency.ForRun) 레인 세션을 열지 않는다.
        var h = new DataCopyHarness();
        var runner = new SessionsRunner();
        var vm = new RunViewModel(Services(h, runner));
        var job = TestJobs.Job("같은 시점") with { Options = new CopyOptions { ConcurrentTables = 2 } }; // 같은 시점 읽기 켬

        await vm.StartAsync(new CopySessions(new FakeSession(h.Source), new FakeSession(h.Target)), job, ConcurrentReport(), NoDecisions, new ResolvedPeriod(null, null));

        Assert.Empty(h.Opened);
        Assert.Empty(Assert.IsType<CopySessions>(runner.Sessions).Workers);
    });

    [Fact]
    public Task Point_in_time_read_with_the_read_point_check_opens_workers_and_shows_the_scn() => HeadlessHost.Run(async () =>
    {
        // PF2b: Oracle 원본은 같은 시점 읽기를 켠 채 동시에 복사한다(시작 SCN). 실행 결과의 SCN 이 「읽기 시작」 옆에 보인다.
        var h = new DataCopyHarness();
        var runner = new SessionsRunner { Behavior = () => Task.FromResult(TestJobs.Result() with { ReadPoint = "SCN 4242" }) };
        var vm = new RunViewModel(Services(h, runner));
        var job = TestJobs.Job("같은 시점") with { Options = new CopyOptions { ConcurrentTables = 2 } };
        var report = ConcurrentReport() with
        {
            Items = [.. ConcurrentReport().Items, new CheckItem(null, CopyCheckCodes.ReadPoint, CheckLevel.Pass, "시작 SCN 으로 읽습니다.")],
        };

        await vm.StartAsync(new CopySessions(new FakeSession(h.Source with { ReadOnly = true }), new FakeSession(h.Target)), job, report, NoDecisions,
            new ResolvedPeriod(null, null));

        Assert.Single(Assert.IsType<CopySessions>(runner.Sessions).Workers);
        Assert.EndsWith(" · SCN 4242", vm.ReadStartedText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Tibero_source_with_point_in_time_read_opens_no_worker_session() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var runner = new SessionsRunner();
        var vm = new RunViewModel(Services(h, runner));
        var tibero = h.Source with { Dbms = Dbms.Tibero };
        var job = TestJobs.Job("티베로") with { Options = new CopyOptions { ConcurrentTables = 2 } };
        var report = ConcurrentReport() with
        {
            Items = [.. ConcurrentReport().Items, new CheckItem(null, CopyCheckCodes.ReadPoint, CheckLevel.Pass, "시작 SCN 으로 읽습니다.")],
        };

        await vm.StartAsync(new CopySessions(new FakeSession(tibero), new FakeSession(h.Target)), job, report, NoDecisions, new ResolvedPeriod(null, null));

        Assert.Empty(h.Opened);
        Assert.Empty(Assert.IsType<CopySessions>(runner.Sessions).Workers);
        Assert.DoesNotContain("SCN", vm.ReadStartedText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Fewer_worker_pairs_than_asked_are_noted_in_the_work_log() => HeadlessHost.Run(async () =>
    {
        var h = new DataCopyHarness();
        var runner = new SessionsRunner();
        var vm = new RunViewModel(new DataCopyServices
        {
            Runner = runner,
            Time = new FixedTime(),
            SessionsOpener = (_, _, _) => Task.FromResult(new DedicatedSessionBatch([], "ORA-00020: maximum number of processes exceeded")),
        });
        var job = TestJobs.Job("모자람") with { ConsistentRead = false, Options = new CopyOptions { ConcurrentTables = 2 } };

        await vm.StartAsync(new CopySessions(new FakeSession(h.Source), new FakeSession(h.Target)), job, ConcurrentReport(), NoDecisions, new ResolvedPeriod(null, null));

        Assert.Empty(Assert.IsType<CopySessions>(runner.Sessions).Workers);
        Assert.Contains(vm.Log.Lines, l => l.Text.Contains("ORA-00020", StringComparison.Ordinal));
    });

    // 실행기만 바꾼 서비스. 세션 하나 열기는 하네스처럼 받은 프로필을 남기고 가짜 세션을 연다(여러 개 열기는 이것을 여러 번 부른다).
    private static DataCopyServices Services(DataCopyHarness h, ICopyRunner runner) => new()
    {
        Runner = runner,
        Time = new FixedTime(),
        SessionOpener = (profile, _) =>
        {
            h.Opened.Add(profile);
            return Task.FromResult<ISession?>(new FakeSession(profile, purpose: SessionPurpose.Dedicated));
        },
    };

    // 2 테이블 단계가 있고 동시 복사 확인 항목이 든 점검 결과.
    private static PrecheckReport ConcurrentReport() =>
        TestJobs.Report(
            items: [new CheckItem(null, CopyCheckCodes.ConcurrentCopy, CheckLevel.Confirm, "테이블을 2개까지 동시에 복사합니다.")],
            order: [TestJobs.Plan("A"), TestJobs.Plan("B")]) with
        {
            Stages = [["APP.A", "APP.B"]],
        };

    /// <summary>받은 세션 쌍을 남기는 실행기.</summary>
    private sealed class SessionsRunner : ICopyRunner
    {
        public CopySessions? Sessions { get; private set; }

        public List<CopyProgress> Reports { get; } = [];

        public Func<Task<CopyRunResult>> Behavior { get; set; } = () => Task.FromResult(TestJobs.Result());

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CopyRunResult> RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions, ResolvedPeriod period,
            IProgress<CopyProgress>? progress, CopyRunControl control, CancellationToken ct)
        {
            Sessions = sessions;
            foreach (var p in Reports)
            {
                progress?.Report(p);
            }

            Started.TrySetResult();
            return Behavior();
        }

        public Task<CopyRunResult> RetryFailedRowsAsync(CopySessions sessions, CopyJob job, PrecheckReport report, IReadOnlyList<FailedRow> rows,
            IProgress<CopyProgress>? progress, CancellationToken ct) => Task.FromResult(TestJobs.Result());
    }
}
