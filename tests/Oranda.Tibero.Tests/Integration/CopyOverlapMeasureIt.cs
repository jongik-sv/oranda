using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 측정 시험(PF1, 결정 69·74): 수십만 행 테이블을 Oracle→Tibero 로 읽기·쓰기 겹치기 켬·끔을 번갈아 3회씩 복사해 걸린 시간과 시간 나눔
/// (적재 구간·읽기·첫 행·변환·쓰기·행/초·테이블 전체·기타)을 <c>PF1 measure …</c> 줄로, 끝에 켬·끔 각각 중앙값 줄을 출력한다. 단언은 결과 건수만.
/// <list type="bullet">
/// <item>교차 복사 관문(<see cref="CrossDbmsItEnvironment.SkipReason"/>)에 더해 <c>ORANDA_IT_COPY_MEASURE=1</c> 일 때만 돈다(행 수 <c>ORANDA_IT_COPY_MEASURE_ROWS</c>, 기본 5000).</item>
/// <item>실행: <c>ORANDA_IT_COPY_MEASURE=1 ORANDA_IT_TIBERO_WRITE=1 dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~CopyOverlapMeasureIt" --logger "console;verbosity=detailed"</c></item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class CopyOverlapMeasureIt(ITestOutputHelper output)
{
    private static readonly ResolvedPeriod Period = new(null, null);

    [CopyMeasureFact]
    public async Task Measure_oracle_to_tibero_copy_with_and_without_overlap()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var rows = CopyMeasureFactAttribute.Rows();
        var t = CrossDbmsItEnvironment.Name("PF1_MEAS");
        const string Columns = "ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(40), AMT NUMBER(12,2), DT DATE";
        await env.CreateTableAsync(Dbms.Oracle, t, Columns);
        await env.CreateTableAsync(Dbms.Tibero, t, Columns);
        await env.ExecAsync(Dbms.Oracle,
            $"INSERT INTO {t} SELECT LEVEL, 'name ' || LEVEL, MOD(LEVEL, 100000) / 7, DATE '2026-01-01' + MOD(LEVEL, 365) FROM DUAL CONNECT BY LEVEL <= "
            + rows.ToString(CultureInfo.InvariantCulture));

        // 켬·끔을 번갈아 3회씩(첫 회 예열 영향을 줄인다, 원격 #155). 회마다 줄 하나, 끝에 켬·끔 각각 값마다 중앙값.
        var runs = new List<(bool Overlap, TimeSpan Wall, CopyTableTimes Times)>();
        foreach (var overlap in new[] { true, false, true, false, true, false })
        {
            var sessions = await env.OpenCopySessionsAsync(Dbms.Oracle, Dbms.Tibero);
            var job = CrossDbmsItEnvironment.Job(sessions, [new TableRule(t, RuleKind.All, TargetAction.DeleteAllInsert)], new CopyOptions { SerialReadWrite = !overlap });
            var report = await env.Precheck(Dbms.Oracle, Dbms.Tibero).RunAsync(sessions, job, Period, null, CancellationToken.None);
            Assert.True(!report.HasErrors, env.Scrub(string.Join(" | ", report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + " " + i.Message))));

            var sw = Stopwatch.StartNew();
            var result = await env.Runner(Dbms.Oracle, Dbms.Tibero, new CrossMemoryRunLog())
                .RunAsync(sessions, job, report, CrossDbmsItEnvironment.NoDecisions(), Period, null, new CopyRunControl(), CancellationToken.None);
            sw.Stop();

            var table = Assert.Single(result.Tables);
            Assert.True(table.TargetRows == rows, env.Scrub(result.Outcome + " " + table.Outcome + " " + table.Message + " | " + string.Join(" / ", result.Notices)));
            var times = Assert.IsType<CopyTableTimes>(table.Times);
            runs.Add((overlap, sw.Elapsed, times));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PF1 measure O2T overlap={overlap} rows={rows}: 실행 전체 {sw.Elapsed.TotalSeconds:F2}s · ")
                             + CopyLogText.Times(times));
        }

        foreach (var overlap in new[] { true, false })
        {
            output.WriteLine($"PF1 measure O2T overlap={overlap} rows={rows.ToString(CultureInfo.InvariantCulture)} 중앙값(3회): "
                             + MeasureMedian.Text(runs.Where(r => r.Overlap == overlap).Select(r => (r.Wall, r.Times)).ToList()));
        }
    }
}

/// <summary>복사 측정 시험: 교차 복사 관문 + <c>ORANDA_IT_COPY_MEASURE=1</c>. 행 수는 <c>ORANDA_IT_COPY_MEASURE_ROWS</c>(기본 5000).</summary>
public sealed class CopyMeasureFactAttribute : FactAttribute
{
    public CopyMeasureFactAttribute() =>
        Skip = Environment.GetEnvironmentVariable("ORANDA_IT_COPY_MEASURE") != "1"
            ? "측정 시험: ORANDA_IT_COPY_MEASURE=1 일 때만 돈다."
            : CrossDbmsItEnvironment.SkipReason();

    public static int Rows() =>
        int.TryParse(Environment.GetEnvironmentVariable("ORANDA_IT_COPY_MEASURE_ROWS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 5_000;
}

/// <summary>측정 시험의 중앙값 줄(원격 #155): 실행 전체·적재 구간·읽기·첫 행·변환·쓰기·행/초·테이블 전체·기타를 값마다 따로 중앙값으로.</summary>
internal static class MeasureMedian
{
    public static string Text(IReadOnlyList<(TimeSpan Wall, CopyTableTimes Times)> runs)
    {
        static string S(IEnumerable<TimeSpan> values) => Median(values.Select(v => v.TotalSeconds)).ToString("F2", CultureInfo.InvariantCulture) + "s";
        static IEnumerable<TimeSpan> Known(IEnumerable<TimeSpan?> values) => values.Where(v => v is not null).Select(v => v!.Value);
        return $"실행 전체 {S(runs.Select(r => r.Wall))} · 적재 구간 {S(Known(runs.Select(r => r.Times.Load)))} · 읽기 {S(runs.Select(r => r.Times.Read))}"
               + $"(첫 행까지 {S(Known(runs.Select(r => r.Times.FirstRow)))}) · 변환 {S(runs.Select(r => r.Times.Convert))} · 쓰기 {S(runs.Select(r => r.Times.Write))}"
               + $" · {Median(runs.Select(r => r.Times.RowsPerSecond)).ToString("N0", CultureInfo.InvariantCulture)}행/초(넣은 행 / 적재 구간)"
               + $" · 테이블 전체 {S(Known(runs.Select(r => r.Times.Total)))} · 기타 {S(Known(runs.Select(r => r.Times.Other)))}";
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
