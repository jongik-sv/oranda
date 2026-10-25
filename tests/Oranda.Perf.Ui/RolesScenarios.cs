using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;
using Oranda.App.Shell;
using Oranda.App.ViewModels;
using Oranda.Perf.Common;

namespace Oranda.Perf.Ui;

/// <summary>
/// TSK-07-07 의 앱 안쪽 측정(TRD 10.10). 진짜 앱(Oranda.App)을 <c>--features</c> 구성으로 띄워 같은 프로세스 안에서만 읽을 수 있는 값을 잰다.
/// 게시본 프로세스 바깥 값(시작 표식·전용 메모리·스레드·핸들)은 <c>Oranda.Perf --features</c> 가 잰다.
/// </summary>
internal static class RolesScenarios
{
    /// <summary>
    /// 접속 0·탭 0 유휴 상태의 앱 안 값: 상태 칸 수, 스레드 풀 스레드·타이머 수, 관리 힙, 전용 메모리. DB 는 쓰지 않는다.
    /// 이 프로세스는 하네스(Oranda.Perf.Ui) 이므로 메모리 절댓값은 게시본보다 크다 — 구성끼리의 차이만 본다.
    /// </summary>
    public static async Task RunAsync(UiContext ctx)
    {
        var idle = ctx.Options.SecondsOr(30);
        var label = ctx.Options.Features ?? "all";
        var entries = (ctx.Window.DataContext as MainViewModel)?.StatusItems;
        var status = entries?.Count;
        var hasBlocker = entries?.Any(e => e.Item is Oranda.App.Features.BlockerAlert.BlockerStatusViewModel) == true;
        var logCounts = LogCounts.Parse(ReadTraceLog());
        await Task.Delay(idle * 1000);

        var pid = Environment.ProcessId;
        var priv = new List<double>();
        var commit = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var m = MemoryProbe.Read(pid);
            priv.Add(m.PrivateMb ?? m.WorkingSetMb ?? 0);
            commit.Add(m.CommitMb ?? 0);
            await Task.Delay(1000);
        }

        using var proc = Process.GetCurrentProcess();
        var heap = GC.GetTotalMemory(false) / 1048576.0;
        var gcInfo = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out var availWorker, out _);
        ThreadPool.GetMaxThreads(out var maxWorker, out _);
        var note = $"구성 {label}, 접속 0·탭 0, 기동 뒤 {idle}초 유휴, 하네스 프로세스 안";

        ctx.Report.Rows.Add(ReportRow.Create("상태 칸 수(MainViewModel.StatusItems)", "개", null, [status ?? -1], "max", note));
        ctx.Report.Rows.Add(ReportRow.Create("블로커 칸(= 감시기·15초 폴링·모니터 세션의 주인) 있음(1 = 있음)", "개", null, [hasBlocker ? 1 : 0], "max", "BlockerWatcher 는 이 칸 안에서만 만들어진다. " + note));
        ctx.Report.Rows.Add(ReportRow.Create("상태 칸 건너뜀(Trace)", "개", null, [logCounts.StatusSkipped], "max", "Trace 「상태 칸 건너뜀」 줄 수"));
        ctx.Report.Rows.Add(ReportRow.Create("시작 작업 돈(Trace)", "개", null, [logCounts.TasksRan ?? -1], "max", "-1 = 줄 없음. " + note));
        ctx.Report.Rows.Add(ReportRow.Create("시작 작업 건너뜀(Trace)", "개", null, [logCounts.TasksSkipped ?? -1], "max", "-1 = 줄 없음"));
        ctx.Report.Rows.Add(ReportRow.Create("전용 메모리(전용 작업 집합)", "MB", null, priv, "median", note + ", 1초 간격 5회"));
        ctx.Report.Rows.Add(ReportRow.Create("커밋(전용 바이트)", "MB", null, commit, "median", "Windows 참고 값"));
        ctx.Report.Rows.Add(ReportRow.Create("관리 힙(GC.GetTotalMemory(false))", "MB", null, [heap], "max", "표본 1개"));
        ctx.Report.Rows.Add(ReportRow.Create("관리 힙 크기(GC 마지막 정보)", "MB", null, [gcInfo.HeapSizeBytes / 1048576.0], "max", "표본 1개"));
        ctx.Report.Rows.Add(ReportRow.Create("프로세스 스레드 수", "개", null, [proc.Threads.Count], "max", "표본 1개"));
        ctx.Report.Rows.Add(ReportRow.Create("스레드 풀 스레드 수", "개", null, [ThreadPool.ThreadCount], "max", "ThreadPool.ThreadCount"));
        ctx.Report.Rows.Add(ReportRow.Create("스레드 풀 사용 중 작업자", "개", null, [maxWorker - availWorker], "max", "최대 - 남은 수"));
        ctx.Report.Rows.Add(ReportRow.Create("System.Threading.Timer 활성 수", "개", null, [Timer.ActiveCount], "max", "Timer.ActiveCount(DispatcherTimer 는 UI 스레드 타이머라 포함 안 됨)"));
        ctx.Report.Rows.Add(ReportRow.Create("핸들 수", "개", null, [proc.HandleCount], "max", "표본 1개"));
        ctx.Report.Rows.Add(ReportRow.Create("로드된 어셈블리 수", "개", null, [AppDomain.CurrentDomain.GetAssemblies().Length], "max", "지연 로드 확인용 참고"));
    }

    /// <summary>
    /// 미커밋 DML 뒤 1분 동안 앱이 연 DB 세션 수(TRD 10.10). 앱의 접속 서비스로 접속하고 앱의 실행 컨트롤러로 INSERT 를 실행해(자동 커밋 꺼짐)
    /// 트랜잭션을 연 채 두고, 그 사용자의 V$SESSION 수를 5초·30초·60초에 센다. 블로커 감시가 켜져 있으면 모니터 세션이 하나 더 열려 2개가 된다.
    /// 조회 전용 Role(viewer)은 쓰기 문이 막히므로 DML 이 실행됐는지 함께 적는다. 레인 전용 사용자는 끝나면 지운다.
    /// </summary>
    public static async Task SessionsAsync(UiContext ctx)
    {
        var seconds = ctx.Options.SecondsOr(60);
        var label = ctx.Options.Features ?? "all";
        using var flow = await Flow.StartAsync(ctx);
        flow.Db.Exec("create table pf_blk (id number)");
        var tab = await flow.OpenTabAsync("insert into pf_blk values (1)");
        string outcome;
        try
        {
            await flow.RunAsync(tab);
            await Task.Delay(1500);
            outcome = flow.Controller.State.TxnPending ? "실행됨(미커밋 있음)" : "미커밋 없음(쓰기 문이 막혔거나 실행되지 않음)";
        }
        catch (Exception ex) when (ex is InvalidOperationException or OracleException)
        {
            outcome = "예외: " + ex.GetType().Name;
        }

        // 5초·30초·끝 시점에 센다(시점 사이에는 쉰다).
        var counts = new List<double>();
        var elapsed = 0;
        foreach (var at in new[] { 5, 30, seconds }.Distinct().Order())
        {
            await Task.Delay((at - elapsed) * 1000);
            elapsed = at;
            counts.Add(flow.Db.CountSessions());
        }

        var note = $"구성 {label}. DML: {outcome}";
        ctx.Report.Rows.Add(ReportRow.Create("앱이 연 DB 세션 수(미커밋 DML 뒤 5초·30초·끝 시점)", "개", null, counts, "max", note));
        ctx.Report.Rows.Add(ReportRow.Create($"앱이 연 DB 세션 수({seconds}초 시점)", "개", null, [counts[^1]], "max", note));
        ctx.Report.Rows.Add(ReportRow.Create("미커밋 트랜잭션(1 = 열림)", "개", null, [flow.Controller.State.TxnPending ? 1 : 0], "max", note));
        // 되돌림: 미커밋 INSERT 는 Flow.Dispose 가 접속을 끊고 레인 사용자를 지우면서 사라진다.
    }

    private static string? ReadTraceLog()
    {
        var path = TraceLog.FilePath;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }
}
