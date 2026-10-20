using System.Diagnostics;
using Oranda.App.Features.Completion;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.Perf.Common;

namespace Oranda.Perf.Ui;

/// <summary>PRD 7.2 「자동완성 목록 표시 (딕셔너리 캐시 후) 150 ms 이하」. 진짜 접속·진짜 편집기에서 별칭 뒤 「.」 로 열 목록을 잰다.</summary>
internal static class CompletionScenario
{
    public static async Task RunAsync(UiContext ctx)
    {
        var samples = ctx.Options.SamplesOr(30);
        var warmup = ctx.Options.WarmupOr(3);
        using var flow = await Flow.StartAsync(ctx);
        flow.Db.Exec("create table pf_emp (empno number primary key, ename varchar2(30), job varchar2(20), mgr number, hiredate date, sal number(10,2), comm number(10,2), deptno number)");
        flow.Db.Exec("create table pf_dept (deptno number primary key, dname varchar2(30), loc varchar2(30))");
        for (var i = 0; i < 40; i++)
        {
            flow.Db.Exec($"create table pf_t{i:D2} (id number primary key, c1 varchar2(20), c2 varchar2(20), c3 number, c4 date)");
        }

        // 편집기 안에서 「별칭.」 바로 뒤에 커서가 있다. 문장은 완성된 SELECT 라 열 후보가 나온다.
        const string Sql = "select e. from pf_emp e join pf_dept d on d.deptno = e.deptno";
        var tab = await flow.OpenTabAsync(Sql);
        var view = (SqlEditorView)tab.Surface!;
        var area = view.TextEditorControl.TextArea;
        area.Focus();
        view.TextEditorControl.CaretOffset = Sql.IndexOf("e. ", StringComparison.Ordinal) + 2;
        var controller = CompletionController.Shared;

        // 딕셔너리 캐시가 데워질 때까지 다시 시도한다(처음에는 일부만 읽혀 목록이 안 열릴 수 있다).
        var warmStart = Stopwatch.StartNew();
        var opened = false;
        for (var attempt = 0; attempt < 40 && !opened; attempt++)
        {
            opened = await controller.ShowCompletionAsync(area, tab, automatic: true);
            if (!opened)
            {
                await Task.Delay(500);
            }
        }

        if (!opened)
        {
            throw new InvalidOperationException($"자동완성 목록이 열리지 않았다. 후보 {controller.LastResult?.Items.Count}, partial={controller.LastResult?.IsPartial}");
        }

        var firstOpenedAfter = warmStart.Elapsed.TotalMilliseconds;
        controller.CloseCompletion();
        await ctx.RenderBatchAsync();

        var call = new List<double>();
        var shown = new List<double>();
        var candidates = new List<double>();
        for (var i = 0; i < warmup + samples; i++)
        {
            await Task.Delay(100);
            var t0 = Stopwatch.GetTimestamp();
            var ok = await controller.ShowCompletionAsync(area, tab, automatic: true);
            var t1 = Stopwatch.GetTimestamp();
            if (!ok)
            {
                throw new InvalidOperationException("캐시가 데워진 뒤 목록이 열리지 않았다.");
            }

            var (_, rendered) = await ctx.RenderBatchAsync();
            if (i >= warmup)
            {
                call.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                shown.Add(Stopwatch.GetElapsedTime(t0, rendered).TotalMilliseconds);
                candidates.Add(controller.LastResult!.Items.Count);
            }

            controller.CloseCompletion();
        }

        ctx.Report.Rows.Add(ReportRow.Create("자동완성 목록 표시: 요청 → 창이 그려짐(캐시 후)", "ms", 150, shown, "p95", $"별칭 뒤 「.」, 후보 {candidates.Max():F0}개, 판정 값"));
        ctx.Report.Rows.Add(ReportRow.Create("자동완성: 요청 → 목록 창 열림(그리기 전)", "ms", 150, call, "p95", "ShowCompletionAsync 가 돌아온 시각. 참고"));
        ctx.Report.Rows.Add(ReportRow.Create("자동완성: 접속 뒤 첫 목록이 열릴 때까지", "ms", null, [firstOpenedAfter], "max", "딕셔너리 캐시가 데워지는 시간(재시도 포함). 표본 1개, 참고"));
        Host(flow).Close(tab);
    }

    private static SqlEditorHost Host(Flow flow) => flow.Host;
}

/// <summary>PRD 7.2 「첫 행 표시 (서버가 첫 묶음을 돌려준 뒤) 100 ms 이하」.</summary>
internal static class FirstRowScenario
{
    public static async Task RunAsync(UiContext ctx)
    {
        var rows = ctx.Options.RowsOr(100_000);
        var samples = ctx.Options.SamplesOr(15);
        var warmup = ctx.Options.WarmupOr(2);
        using var flow = await Flow.StartAsync(ctx);
        flow.Db.Exec($"create table pf_rows as select level id, 'name ' || level name, mod(level, 100) grp, date '2026-01-01' + mod(level, 365) d, level / 8 ratio from dual connect by level <= {rows}");

        var afterPage = new List<double>();
        var wholeRun = new List<double>();
        var missed = 0;
        for (var i = 0; i < warmup + samples; i++)
        {
            var tab = await flow.OpenTabAsync("select * from pf_rows");
            tab.Select(0, 0);
            var t0 = Stopwatch.GetTimestamp();
            await flow.Controller.RunAsync(RunMode.Statement, tab: tab);
            var model = Flow.ModelOf(tab) ?? throw new InvalidOperationException("결과 그리드 모델이 없다.");
            var arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (model.LoadedRowCount > 0)
            {
                missed++; // 모델을 붙잡기 전에 첫 묶음이 이미 왔다 → 이 표본은 버린다
            }
            else
            {
                model.Changed += (_, _) =>
                {
                    if (model.LoadedRowCount > 0)
                    {
                        arrived.TrySetResult(Stopwatch.GetTimestamp());
                    }
                };
                var tArrived = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var (_, rendered) = await ctx.RenderBatchAsync();
                if (i >= warmup)
                {
                    afterPage.Add(Stopwatch.GetElapsedTime(tArrived, rendered).TotalMilliseconds);
                    wholeRun.Add(Stopwatch.GetElapsedTime(t0, rendered).TotalMilliseconds);
                }
            }

            flow.Host.Close(tab);
            await Task.Delay(200);
        }

        ctx.Report.Rows.Add(ReportRow.Create("첫 행 표시: 첫 묶음 수신 → 화면에 그려짐", "ms", 100, afterPage, "p95", $"{rows} 행 표 전체 조회, 묶음 500행. 모델 Changed → 렌더 스레드가 그림. 놓친 표본 {missed}"));
        ctx.Report.Rows.Add(ReportRow.Create("첫 행 표시: 실행 시작 → 화면에 그려짐(서버 시간 포함)", "ms", null, wholeRun, "p95", "참고"));
    }
}

/// <summary>PRD 7.2 「실행 취소 반응 1 s 이하」: 긴 질의를 실행 중에 취소 명령을 내려 실행 상태가 끝날 때까지.</summary>
internal static class CancelScenario
{
    public static async Task RunAsync(UiContext ctx)
    {
        var samples = ctx.Options.SamplesOr(10);
        using var flow = await Flow.StartAsync(ctx);
        var sleep = new List<double>();
        var heavy = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            sleep.Add(await OneAsync(flow, "begin dbms_session.sleep(60); end;"));
            heavy.Add(await OneAsync(flow, "declare x number := 0; begin for i in 1..2000000000 loop x := x + sqrt(i); end loop; end;"));
        }

        ctx.Report.Rows.Add(ReportRow.Create("실행 취소: 대기(PL/SQL sleep) 중 취소 → 실행 끝", "ms", 1000, sleep, "p95", "취소 명령 → 컨트롤러가 유휴로 돌아옴"));
        ctx.Report.Rows.Add(ReportRow.Create("실행 취소: CPU 계산 중인 PL/SQL 루프 취소 → 실행 끝", "ms", 1000, heavy, "p95", "서버가 CPU 를 쓰는 질의"));
    }

    private static async Task<double> OneAsync(Flow flow, string sql)
    {
        var tab = await flow.OpenTabAsync(sql);
        tab.Select(0, 0);
        var run = flow.Controller.RunAsync(RunMode.Statement, tab: tab);
        await UiContext.WaitUntilAsync(() => flow.Controller.State.Busy, TimeSpan.FromSeconds(10), "실행 중 상태");
        await Task.Delay(700); // 서버가 일을 시작하게 한다
        var t0 = Stopwatch.GetTimestamp();
        flow.Controller.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(60));
        var elapsed = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (flow.Controller.State.Busy)
        {
            throw new InvalidOperationException("취소 뒤에도 실행 중 상태다.");
        }

        var tail = string.Join(" | ", flow.Controller.Log.Messages.TakeLast(3).Select(m => m.Text.Split((char)10)[0]));
        Console.WriteLine($"[cancel] {sql[..Math.Min(30, sql.Length)]} -> {elapsed:F0} ms; 로그: {tail}");
        flow.Host.Close(tab);
        return elapsed;
    }
}
