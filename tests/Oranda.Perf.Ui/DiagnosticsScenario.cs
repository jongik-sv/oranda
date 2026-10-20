using System.Diagnostics;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.SqlEditor;
using Oranda.Perf.Common;

namespace Oranda.Perf.Ui;

/// <summary>
/// PRD 7.2 「문법 검사 반영 (1만 줄 파일, 입력 멈춘 뒤) 300 ms 이하」.
/// 편집기는 입력이 EditorDiagnostics.Debounce(지금 100 ms, D-020, TRD 4.5) 만큼 멈추면 분석을 시작한다. 그래서 두 값을 따로 잰다.
///  - 분석 반영: 디바운스가 끝난 시점부터 밑줄이 문서에 반영될 때까지(= 분석 + 반영). 판정 값.
///  - 마지막 키 → 반영: 마지막 키 입력부터 반영까지(디바운스 포함). PRD 문구를 「마지막 키 기준」 으로 읽으면 이 값이 기준이다.
/// 진단은 문서 맨 끝 한 줄에 오류(닫히지 않은 괄호)를 넣었다 빼는 편집으로 만든다.
/// </summary>
internal static class DiagnosticsScenario
{
    public static async Task RunAsync(UiContext ctx)
    {
        var lines = ctx.Options.LinesOr(10_000);
        var samples = ctx.Options.SamplesOr(15);
        var warmup = ctx.Options.WarmupOr(3);
        var host = SqlEditorHost.Current;
        var tab = host.NewDocument(SampleSql.EditorDocument(lines));
        await UiContext.WaitUntilAsync(() => tab.Surface is SqlEditorView, TimeSpan.FromSeconds(10), "편집기 탭이 화면에 붙음");
        var view = (SqlEditorView)tab.Surface!;
        var diag = EditorDiagnostics.Of(view) ?? EditorDiagnostics.Attach(view);

        // 처음 분석(전체 문서). 디바운스가 끝나고 분석이 돌아서 반영될 때까지.
        var coldStart = Stopwatch.StartNew();
        await UiContext.WaitUntilAsync(() => diag.AppliedAnalyses >= 1 && !diag.IsPending, TimeSpan.FromSeconds(120), "첫 분석 반영");
        var cold = coldStart.Elapsed.TotalMilliseconds;

        var fromLastKey = new List<double>();
        var analyzeNow = new List<double>();
        var toggle = false;
        for (var i = 0; i < warmup + samples; i++)
        {
            await Task.Delay(400);
            toggle = !toggle;
            var end = tab.Document.TextLength;
            var applied = diag.AppliedAnalyses;
            var tKey = Stopwatch.GetTimestamp();
            if (toggle)
            {
                tab.Document.Insert(end, "select (1 from dual;\n"); // 닫히지 않은 괄호 → 오류 하나
            }
            else
            {
                tab.Document.Remove(end - "select (1 from dual;\n".Length, "select (1 from dual;\n".Length);
            }

            while (diag.AppliedAnalyses == applied)
            {
                await Task.Delay(1);
                if (Stopwatch.GetElapsedTime(tKey).TotalSeconds > 60)
                {
                    throw new TimeoutException("분석이 반영되지 않았다.");
                }
            }

            var total = Stopwatch.GetElapsedTime(tKey).TotalMilliseconds;
            if (i >= warmup)
            {
                fromLastKey.Add(total);
            }
        }

        // 디바운스를 건너뛰고 지금 분석(AnalyzeNowAsync)한 시간: 분석 + 반영만.
        for (var i = 0; i < warmup + samples; i++)
        {
            await Task.Delay(400);
            toggle = !toggle;
            var end = tab.Document.TextLength;
            if (toggle)
            {
                tab.Document.Insert(end, "select (1 from dual;\n");
            }
            else
            {
                tab.Document.Remove(end - "select (1 from dual;\n".Length, "select (1 from dual;\n".Length);
            }

            var t0 = Stopwatch.GetTimestamp();
            await diag.AnalyzeNowAsync();
            if (i >= warmup)
            {
                analyzeNow.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
        }

        var debounce = EditorDiagnostics.Debounce.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        ctx.Report.Rows.Add(ReportRow.Create($"문법 검사 {lines}줄: 디바운스 뒤 → 반영(분석+반영)", "ms", 300, analyzeNow, "p95", $"AnalyzeNowAsync(디바운스 건너뜀). 판정 값(TRD 4.5 의 {debounce} 멈춤 이후 기준)"));
        ctx.Report.Rows.Add(ReportRow.Create($"문법 검사 {lines}줄: 마지막 키 → 반영(디바운스 {debounce} 포함)", "ms", 300, fromLastKey, "p95", "PRD 문구를 마지막 키 기준으로 읽으면 이 값. 디바운스가 한도 300 ms 의 대부분(지금은 전부)을 쓴다"));
        ctx.Report.Rows.Add(ReportRow.Create($"문법 검사 {lines}줄: 첫 전체 분석", "ms", null, [cold], "max", "문서를 연 뒤 첫 반영까지(디바운스 포함). 표본 1개, 참고"));
        host.Close(tab);
    }
}
