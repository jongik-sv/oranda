using System.Diagnostics;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Perf.Common;

namespace Oranda.Perf.Ui;

/// <summary>PRD 7.2 「포맷 (2,000줄) 500 ms 이하」: 앱이 쓰는 포맷 서비스와 편집기 탭에 적용하는 경로를 둘 다 잰다.</summary>
internal static class FormatScenario
{
    public static async Task RunAsync(UiContext ctx)
    {
        var lines = ctx.Options.LinesOr(2000);
        var samples = ctx.Options.SamplesOr(25);
        var warmup = ctx.Options.WarmupOr(3);
        var (sql, actualLines) = SampleSql.FormatInput(lines);
        var formatter = ctx.Services.GetRequiredService<ISqlFormatter>();
        var extended = new FormatterOptions { Align = new AlignOptions { EqualsSign = true }, CommaPosition = CommaPosition.Leading };

        // 첫 호출(예열 전) 값은 따로 남긴다: 사용자가 앱을 켜고 처음 포맷할 때의 값에 가깝다.
        var first = Stopwatch.StartNew();
        Ensure(formatter.Format(sql, new FormatterOptions()));
        first.Stop();

        ctx.Report.Rows.Add(ReportRow.Create(
            $"포맷 {actualLines}줄: 서비스 호출, 기본 옵션", "ms", 500, Measure(formatter, sql, new FormatterOptions(), samples, warmup), "p95",
            $"예열 {warmup}회 뒤 {samples}회. 앱의 ISqlFormatter 서비스"));
        ctx.Report.Rows.Add(ReportRow.Create(
            $"포맷 {actualLines}줄: 서비스 호출, 확장 옵션(= 정렬·앞쪽 쉼표)", "ms", 500, Measure(formatter, sql, extended, samples, warmup), "p95",
            "같은 방식"));
        ctx.Report.Rows.Add(ReportRow.Create(
            "포맷: 맨 처음 한 번(예열 전)", "ms", 500, [first.Elapsed.TotalMilliseconds], "max", "앱을 켜고 첫 포맷. 표본 1개"));

        // 편집기 탭에 적용하는 전체 경로(문서 교체·실행 취소 1단계 포함). 매번 원문으로 되돌린 뒤 잰다.
        var tab = SqlEditorHost.Current.NewDocument(sql);
        var apply = new List<double>();
        for (var i = 0; i < warmup + samples; i++)
        {
            tab.Document.Text = sql;
            var sw = Stopwatch.StartNew();
            FormatterProfilesCommands.Apply(tab, formatter, new FormatterOptions(), null, out var message);
            sw.Stop();
            if (tab.Text == sql)
            {
                throw new InvalidOperationException("포맷이 문서를 바꾸지 않았다: " + message);
            }

            if (i >= warmup)
            {
                apply.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        ctx.Report.Rows.Add(ReportRow.Create($"포맷 {actualLines}줄: 편집기 탭에 적용(문서 교체 포함)", "ms", 500, apply, "p95", "FormatterProfilesCommands.Apply"));
        SqlEditorHost.Current.Close(tab);
        await Task.CompletedTask;
    }

    private static List<double> Measure(ISqlFormatter formatter, string sql, FormatterOptions options, int samples, int warmup)
    {
        for (var i = 0; i < warmup; i++)
        {
            Ensure(formatter.Format(sql, options));
        }

        var values = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            var sw = Stopwatch.StartNew();
            var result = formatter.Format(sql, options);
            sw.Stop();
            Ensure(result);
            values.Add(sw.Elapsed.TotalMilliseconds);
        }

        return values;
    }

    private static void Ensure(FormatResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"포맷 실패 {result.Failure}: {result.Message}");
        }
    }
}
