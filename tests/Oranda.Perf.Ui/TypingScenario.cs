using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;
using Oranda.App.Features.SqlEditor;
using Oranda.Perf.Common;

namespace Oranda.Perf.Ui;

/// <summary>
/// PRD 7.2 「키 입력 → 화면 반영 (1만 줄 파일) 16 ms 이하 (p95)」. 진짜 앱의 SQL 편집기 탭(L1 강조·접기·진단 포함)에
/// 입력 이벤트를 넣고 실제 렌더 스레드가 그릴 때까지 잰다(spikes/TSK-01-05 typing-window 와 같은 방식).
/// 판정(D-007)은 렌더 틱 대기를 뺀 처리 시간 = 입력 처리 + 레이아웃 + 렌더. 틱 대기를 넣은 「반영」 값도 함께 적는다.
/// OS 의 WM_CHAR 경로와 모니터 스캔아웃은 빠진다.
/// </summary>
internal static class TypingScenario
{
    public static async Task RunAsync(UiContext ctx)
    {
        var lines = ctx.Options.LinesOr(10_000);
        var samples = ctx.Options.SamplesOr(500);
        var warmup = ctx.Options.WarmupOr(50);
        var host = SqlEditorHost.Current;
        var tab = host.NewDocument(SampleSql.EditorDocument(lines));
        await UiContext.WaitUntilAsync(() => tab.Surface is SqlEditorView, TimeSpan.FromSeconds(10), "편집기 탭이 화면에 붙음");
        var view = (SqlEditorView)tab.Surface!;
        var area = view.TextEditorControl.TextArea;
        var middle = Math.Min(5000, lines);
        view.TextEditorControl.ScrollToLine(middle);
        area.Focus();
        var line = tab.Document.GetLineByNumber(middle);
        tab.Select(line.Offset + 3, 0);
        await Task.Delay(500);

        foreach (var (name, kind) in new[] { ("문자 입력/Backspace", "char"), ("Enter/Backspace", "enter") })
        {
            var reflect = new List<double>();
            var work = new List<double>();
            var input = new List<double>();
            var toggle = false;
            for (var s = 0; s < warmup + samples; s++)
            {
                await Task.Delay(25); // 사람 타자 간격 흉내(이전 프레임이 끝나게)
                toggle = !toggle;
                var t0 = Stopwatch.GetTimestamp();
                if (toggle && kind == "char")
                {
                    area.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "x" });
                }
                else if (toggle)
                {
                    Key(area, Avalonia.Input.Key.Enter);
                }
                else
                {
                    Key(area, Avalonia.Input.Key.Back);
                }

                var ti = Stopwatch.GetTimestamp();
                ctx.Window.UpdateLayout();
                var tl = Stopwatch.GetTimestamp();
                var batch = ctx.Compositor.RequestCompositionBatchCommitAsync();
                await batch.Processed;
                var t1 = Stopwatch.GetTimestamp();
                await batch.Rendered;
                var t2 = Stopwatch.GetTimestamp();
                if (s < warmup)
                {
                    continue;
                }

                reflect.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
                input.Add(Stopwatch.GetElapsedTime(t0, ti).TotalMilliseconds);
                // 처리 = 입력 처리 + 레이아웃 + 렌더(Processed → Rendered). Processed 까지의 틱 대기는 빼고 레이아웃 뒤 구간만 더한다.
                work.Add(Stopwatch.GetElapsedTime(t0, tl).TotalMilliseconds + Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            }

            ctx.Report.Rows.Add(ReportRow.Create($"{lines}줄 {name}: 처리 시간(D-007, 틱 대기 제외)", "ms", 16, work, "p95", "판정 값. 입력 처리+레이아웃+렌더"));
            ctx.Report.Rows.Add(ReportRow.Create($"{lines}줄 {name}: 반영 시간(틱 대기 포함)", "ms", 16, reflect, "p95", "참고. 주입 → 렌더 스레드가 그림"));
            ctx.Report.Rows.Add(ReportRow.Create($"{lines}줄 {name}: 입력 처리만(문서 변경·렉싱·ViewModel)", "ms", null, input, "p95", "참고"));
        }

        host.Close(tab);
    }

    private static void Key(Interactive target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = target });
    }
}
