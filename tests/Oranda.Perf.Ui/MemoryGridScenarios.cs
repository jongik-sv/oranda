using System.Diagnostics;
using Avalonia.Controls;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Composition;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Perf.Common;

namespace Oranda.Perf.Ui;

/// <summary>
/// PRD 7.2 메모리 두 항목: 「유휴 메모리(접속 1개, 탭 1개) 전용 150 MB 이하」, 「접속 3개 + 10만 행 결과 3개 400 MB 이하」.
/// 이 프로세스가 곧 앱이다(진짜 창·진짜 서비스). 판정 값은 전용 메모리(Windows 전용 작업 집합, macOS phys_footprint)의 중앙값이고
/// Windows 는 커밋(전용 바이트)도 함께 적는다. 앱은 작업 접속을 한 번에 하나만 가지므로(IConnectionService.ActiveSession)
/// 접속 3개는 활성 접속 1개 + 세션 관리자로 연 작업 세션 2개로 만든다.
/// </summary>
internal static class MemoryScenario
{
    public static async Task RunAsync(UiContext ctx)
    {
        var rows = ctx.Options.RowsOr(100_000);
        var idle = ctx.Options.SecondsOr(10);
        var pid = Environment.ProcessId;

        var empty = await SampleAsync(pid, idle);
        AddRows(ctx, "유휴 메모리: 접속 0, 탭 0(기동 직후)", null, empty, "참고. 기동 직후 " + idle + "초 유휴");

        using var flow = await Flow.StartAsync(ctx);
        flow.Db.Exec($"create table pf_rows as select level id, 'name ' || level name, mod(level, 100) grp, date '2026-01-01' + mod(level, 365) d, level / 8 ratio from dual connect by level <= {rows}");
        var tab1 = await flow.OpenTabAsync("select 1 from dual");
        var one = await SampleAsync(pid, idle);
        AddRows(ctx, "유휴 메모리: 접속 1, 탭 1", 150, one, $"접속 직후 {idle}초 유휴(질의 실행 없음)");

        // 접속 3: 활성 1 + 작업 세션 2 (연결을 열어 둔다)
        var manager = ctx.Services.GetRequiredService<ISessionManager>();
        var extra = new List<ISession>();
        for (var i = 0; i < 2; i++)
        {
            using var credential = new SecureCredential(flow.Db.Password);
            extra.Add(await manager.OpenAsync(flow.Db.Profile("perf-extra" + i), credential, SessionPurpose.Work, default));
        }

        // 결과 3: 세 탭에서 각각 rows 행 질의를 모두 가져온다.
        var models = new List<ResultGridModel>();
        var tabs = new List<Oranda.App.Features.SqlEditor.SqlEditorViewModel> { tab1 };
        for (var i = 0; i < 3; i++)
        {
            var tab = i == 0 ? tab1 : await flow.OpenTabAsync("select * from pf_rows");
            if (i == 0)
            {
                tab.Document.Text = "select * from pf_rows";
            }
            else
            {
                tabs.Add(tab);
            }

            var model = await flow.RunAsync(tab) ?? throw new InvalidOperationException("결과 모델이 없다.");
            await model.FetchAllAsync();
            models.Add(model);
        }

        var resident = models.Sum(m => m.Cache.ResidentBytes);
        var loaded = models.Sum(m => m.LoadedRowCount);
        if (loaded < rows * 3L)
        {
            throw new InvalidOperationException($"기대한 행 수를 못 가져왔다: {loaded} < {rows * 3L}");
        }

        var three = await SampleAsync(pid, idle);
        AddRows(ctx, $"메모리: 접속 3 + {rows} 행 결과 3", 400, three, $"세션 {1 + extra.Count}개, 결과 3개 모두 가져온 뒤 {idle}초 유휴. 열 5개(NUMBER·VARCHAR2·NUMBER·DATE·NUMBER)");

        var perRowCache = resident / (double)loaded;
        var perRowPrivate = (three.Private - one.Private) * 1048576.0 / loaded;
        ctx.Report.Rows.Add(ReportRow.Create("결과 행당 추가 메모리(페이지 캐시 추정)", "bytes", 200, [perRowCache], "max", "페이지 캐시의 ResidentBytes / 가져온 행. 데이터 자체 포함, 표본 1개"));
        ctx.Report.Rows.Add(ReportRow.Create("결과 행당 추가 메모리(전용 메모리 증가분 / 행)", "bytes", 200, [perRowPrivate], "max", "접속 1·탭 1 대비 증가분(접속 2개 추가분 포함). 표본 1개"));

        foreach (var s in extra)
        {
            await s.DisposeAsync();
        }

        foreach (var t in tabs)
        {
            flow.Host.Close(t);
        }
    }

    private sealed record Stage(double Private, double Commit, double Managed, IReadOnlyList<double> PrivateSamples, IReadOnlyList<double> CommitSamples);

    private static async Task<Stage> SampleAsync(int pid, int idleSeconds)
    {
        await Task.Delay(idleSeconds * 1000);
        var priv = new List<double>();
        var commit = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var m = MemoryProbe.Read(pid);
            priv.Add(m.PrivateMb ?? m.WorkingSetMb ?? 0);
            commit.Add(m.CommitMb ?? 0);
            await Task.Delay(1000);
        }

        return new Stage(Stats.Median(priv), Stats.Median(commit), GC.GetTotalMemory(false) / 1048576.0, priv, commit);
    }

    private static void AddRows(UiContext ctx, string item, double? limit, Stage s, string note)
    {
        ctx.Report.Rows.Add(ReportRow.Create(item + ": 전용 메모리", "MB", limit, s.PrivateSamples, "median", note));
        if (s.Commit > 0)
        {
            ctx.Report.Rows.Add(ReportRow.Create(item + ": 커밋(전용 바이트)", "MB", null, s.CommitSamples, "median", "Windows 참고 값"));
        }

        ctx.Report.Rows.Add(ReportRow.Create(item + ": 관리 힙", "MB", null, [s.Managed], "max", "GC.GetTotalMemory(false)"));
    }
}

/// <summary>PRD 7.2 「100만 행 그리드 스크롤 60 fps 유지, 추가 메모리 행당 200 bytes 이하」: 진짜 앱의 결과 그리드를 100만 행으로 채워 자동 스크롤한다.</summary>
internal static class GridScenario
{
    private static readonly (string Name, ScrollKind Kind)[] Plans =
    [
        ("세로 천천히(프레임당 3행)", ScrollKind.VerticalSmooth),
        ("세로 빠르게(프레임당 60행)", ScrollKind.VerticalFast),
        ("가로 왕복(프레임당 40px)", ScrollKind.Horizontal),
        ("무작위 점프(프레임마다)", ScrollKind.Jump),
    ];

    public static async Task RunAsync(UiContext ctx)
    {
        var rows = ctx.Options.RowsOr(1_000_000);
        var seconds = ctx.Options.SecondsOr(8);
        var pid = Environment.ProcessId;
        using var flow = await Flow.StartAsync(ctx);
        flow.Db.Exec($"create table pf_big as select level id, 'name ' || level name, mod(level, 100) grp, date '2026-01-01' + mod(level, 365) d, level / 8 ratio, 'code' || mod(level, 1000) code from dual connect by level <= {rows}");
        var tab = await flow.OpenTabAsync("select * from pf_big");
        await Task.Delay(500);
        var before = MemoryProbe.Read(pid);
        var managedBefore = GC.GetTotalMemory(true);
        var model = await flow.RunAsync(tab) ?? throw new InvalidOperationException("결과 모델이 없다.");
        var fetch = Stopwatch.StartNew();
        await model.FetchAllAsync();
        fetch.Stop();
        if (model.LoadedRowCount < rows)
        {
            throw new InvalidOperationException($"행을 다 못 가져왔다: {model.LoadedRowCount} < {rows}");
        }

        await Task.Delay(1000);
        var managedAfter = GC.GetTotalMemory(true);
        var after = MemoryProbe.Read(pid);
        var grid = flow.FindGrid(model) ?? throw new InvalidOperationException("결과 그리드 컨트롤이 화면에 없다.");
        ctx.Report.Rows.Add(ReportRow.Create($"{rows} 행 모두 가져오기(서버 → 그리드)", "ms", null, [fetch.Elapsed.TotalMilliseconds], "max", "참고. 표본 1개"));
        ctx.Report.Rows.Add(ReportRow.Create("그리드 행당 추가 메모리: 관리 힙(GC 뒤)", "bytes", 200, [(managedAfter - managedBefore) / (double)rows], "max", $"가져온 {rows} 행, 열 6개. 데이터 포함. 표본 1개"));
        ctx.Report.Rows.Add(ReportRow.Create("그리드 행당 추가 메모리: 페이지 캐시 추정", "bytes", 200, [model.Cache.ResidentBytes / (double)model.LoadedRowCount], "max", $"메모리에 둔 페이지 {model.Cache.ResidentPageCount}개, 임시 파일로 내린 페이지 {model.Cache.SpilledPageCount}개(캐시 상한 200 MB)"));
        if (before.PrivateMb is { } b && after.PrivateMb is { } a)
        {
            ctx.Report.Rows.Add(ReportRow.Create("그리드 행당 추가 메모리: 전용 메모리 증가분 / 행", "bytes", 200, [(a - b) * 1048576.0 / rows], "max", "표본 1개"));
        }

        var rng = new Random(20261020);
        // 대조: 스크롤 없이 가만히 둔 창의 프레임 간격. 화면·드라이버 한계(예: 30~40 fps 로 묶임)와 그리드 몫을 가른다.
        await Task.Delay(500);
        var idle = FrameSummary.From(await RunPlanAsync(ctx, grid, ScrollKind.Idle, 4, rng, warmupSeconds: 1));
        ctx.Report.Rows.Add(ReportRow.Create("대조: 스크롤 없는 창의 프레임 간격", "ms", 1000.0 / 58, [idle.P95Ms], "max", $"fps {idle.Fps:F1}, 평균 {idle.MeanMs:F1} ms, 최대 {idle.MaxMs:F1} ms. 이 값이 이미 17 ms 넘으면 화면·드라이버 한계"));
        foreach (var (name, kind) in Plans)
        {
            await Task.Delay(500);
            var times = await RunPlanAsync(ctx, grid, kind, seconds, rng, warmupSeconds: 2);
            var f = FrameSummary.From(times);
            ctx.Report.Rows.Add(ReportRow.Create($"100만 행 스크롤 {name}: fps", "fps", null, [f.Fps], "max", $"프레임 {f.Frames}, 평균 간격 {f.MeanMs:F1} ms, p95 {f.P95Ms:F1} ms, 최대 {f.MaxMs:F1} ms, 33 ms 넘는 프레임 {f.SlowFrames}"));
            ctx.Report.Rows.Add(ReportRow.Create($"100만 행 스크롤 {name}: 프레임 간격", "ms", 1000.0 / 58, [f.P95Ms], "max", "p95 프레임 간격이 17.2 ms(58 fps 상당) 이하여야 60 fps 유지로 본다"));
        }

        flow.Host.Close(tab);
    }

    /// <summary>창의 애니메이션 프레임마다 스크롤 위치를 옮기고 프레임 시각(ms)을 모은다. 앞 warmupSeconds 초는 버린다.</summary>
    private static async Task<List<double>> RunPlanAsync(UiContext ctx, ResultGridControl grid, ScrollKind kind, int seconds, Random rng, int warmupSeconds)
    {
        var times = new List<double>(seconds * 70);
        var clock = Stopwatch.StartNew();
        var done = new TaskCompletionSource();
        var frame = 0;
        void Tick(TimeSpan _)
        {
            var now = clock.Elapsed.TotalSeconds;
            var (x, y) = ScrollPlan.Offset(kind, frame++, grid.RowHeight, grid.MaxOffsetX, grid.MaxOffsetY, rng);
            grid.ScrollTo(x, y);
            if (now >= warmupSeconds)
            {
                times.Add(clock.Elapsed.TotalMilliseconds);
            }

            if (now >= warmupSeconds + seconds)
            {
                done.TrySetResult();
                return;
            }

            ((TopLevel)ctx.Window).RequestAnimationFrame(Tick);
        }

        ((TopLevel)ctx.Window).RequestAnimationFrame(Tick);
        await done.Task;
        return times;
    }
}
