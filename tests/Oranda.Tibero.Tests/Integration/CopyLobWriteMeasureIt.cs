using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 측정 시험(PF5): LOB 열이 있는 테이블 둘을 Oracle→Tibero 로 긴 CLOB 행 처리 옛 동작(<see cref="TiberoLobWrite.WholeBatchRowByRow"/>)·새 동작
/// (<see cref="TiberoLobWrite.SplitLongRows"/>)을 번갈아 3번씩 복사해, 실행마다 시간 나눔과 처리별 중앙값(쓰기 초·행/초)을 <c>PF5 measure O2T …</c> 줄로 출력한다.
/// 단언은 결과 건수만.
/// <list type="bullet">
/// <item><c>PF5_CLOB</c>(ID, CL CLOB, V VARCHAR2): CLOB 은 PF3 측정과 같은 값(1~3000자, 1000행마다 104000자) — 새 동작이 달라지는 모양.</item>
/// <item><c>PF5_BLOB</c>(ID, CL CLOB, BL BLOB, BLOB 1~2000바이트): 옛 동작은 묶음 전체를 한 행씩(드라이버가 이진 배열을 받지 않는다, TB1 3.1),
/// 새 동작은 PF6 부터 작은 BLOB 을 16진 글자 배열 + HEXTORAW 로 배열 구간에 넣는다(긴 CLOB 행만 한 행씩) — PF6 전후 비교.</item>
/// <item>교차 복사 관문(<see cref="CrossDbmsItEnvironment.SkipReason"/>)에 더해 <c>ORANDA_IT_COPY_MEASURE=1</c> 일 때만 돈다(행 수 <c>ORANDA_IT_COPY_MEASURE_ROWS</c>, 기본 5000).</item>
/// <item>실행: <c>ORANDA_IT_COPY_MEASURE=1 ORANDA_IT_TIBERO_WRITE=1 dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~CopyLobWriteMeasureIt" --logger "console;verbosity=detailed"</c></item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class CopyLobWriteMeasureIt(ITestOutputHelper output)
{
    private static readonly ResolvedPeriod Period = new(null, null);

    [CopyMeasureFact]
    public async Task Measure_oracle_to_tibero_lob_write_with_whole_batch_and_split_long_rows()
    {
        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var rows = CopyMeasureFactAttribute.Rows();
        var n = rows.ToString(CultureInfo.InvariantCulture);
        var clob = CrossDbmsItEnvironment.Name("PF5_CLOB");
        var blob = CrossDbmsItEnvironment.Name("PF5_BLOB");
        const string ClobColumns = "ID NUMBER(10) PRIMARY KEY, CL CLOB, V VARCHAR2(100)";
        const string BlobColumns = "ID NUMBER(10) PRIMARY KEY, CL CLOB, BL BLOB";
        foreach (var dbms in new[] { Dbms.Oracle, Dbms.Tibero })
        {
            await env.CreateTableAsync(dbms, clob, ClobColumns);
            await env.CreateTableAsync(dbms, blob, BlobColumns);
        }

        await env.ExecAsync(Dbms.Oracle,
            $"INSERT INTO {clob} SELECT LEVEL, TO_CLOB(RPAD('c', MOD(LEVEL, 3000) + 1, 'c')), 'v ' || LEVEL FROM DUAL CONNECT BY LEVEL <= {n}");
        await env.ExecAsync(Dbms.Oracle,
            $"INSERT INTO {blob} SELECT LEVEL, TO_CLOB(RPAD('c', MOD(LEVEL, 3000) + 1, 'c')), "
            + $"TO_BLOB(UTL_RAW.CAST_TO_RAW(RPAD('b', MOD(LEVEL, 2000) + 1, 'b'))) FROM DUAL CONNECT BY LEVEL <= {n}");
        await env.ExecAsync(Dbms.Oracle,
            "DECLARE c CLOB; BEGIN c := RPAD('y', 4000, 'y'); FOR i IN 1..25 LOOP c := c || RPAD('y', 4000, 'y'); END LOOP; "
            + $"UPDATE {clob} SET CL = c WHERE MOD(ID, 1000) = 0; UPDATE {blob} SET CL = c WHERE MOD(ID, 1000) = 0; COMMIT; END;");

        var runs = new Dictionary<(string Table, TiberoLobWrite Mode), List<CopyTableTimes>>();
        for (var round = 1; round <= 3; round++)
        {
            foreach (var mode in new[] { TiberoLobWrite.WholeBatchRowByRow, TiberoLobWrite.SplitLongRows })
            {
                env.TiberoBulk.LobWrite = mode;
                var sessions = await env.OpenCopySessionsAsync(Dbms.Oracle, Dbms.Tibero);
                var job = CrossDbmsItEnvironment.Job(sessions,
                    [new TableRule(clob, RuleKind.All, TargetAction.DeleteAllInsert), new TableRule(blob, RuleKind.All, TargetAction.DeleteAllInsert)]);
                var report = await env.Precheck(Dbms.Oracle, Dbms.Tibero).RunAsync(sessions, job, Period, null, CancellationToken.None);
                Assert.True(!report.HasErrors, env.Scrub(string.Join(" | ", report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + " " + i.Message))));

                var sw = Stopwatch.StartNew();
                var result = await env.Runner(Dbms.Oracle, Dbms.Tibero, new CrossMemoryRunLog())
                    .RunAsync(sessions, job, report, CrossDbmsItEnvironment.NoDecisions(), Period, null, new CopyRunControl(), CancellationToken.None);
                sw.Stop();

                foreach (var (name, label) in new[] { (clob, "PF5_CLOB"), (blob, "PF5_BLOB") })
                {
                    var table = Assert.Single(result.Tables, t => string.Equals(t.Table, name, StringComparison.OrdinalIgnoreCase));
                    Assert.True(table.TargetRows == rows, env.Scrub(result.Outcome + " " + table.Outcome + " " + table.Message + " | " + string.Join(" / ", result.Notices)));
                    Assert.NotNull(table.Times);
                    var key = (label, mode);
                    if (!runs.TryGetValue(key, out var list))
                    {
                        runs[key] = list = [];
                    }

                    list.Add(table.Times);
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"PF5 measure O2T {label} lob write={mode} round={round} rows={rows}: run {sw.Elapsed.TotalSeconds:F1}s · {CopyLogText.Times(table.Times)}"));
                }
            }
        }

        foreach (var ((label, mode), times) in runs)
        {
            var write = times.Select(t => t.Write.TotalSeconds).Order().ToList();
            var speed = times.Select(t => t.RowsPerSecond).Order().ToList();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"PF5 measure O2T {label} lob write={mode} median rows={rows}: 쓰기 {write[write.Count / 2]:F2}초 · {speed[speed.Count / 2]:N0}행/초 ({times.Count}회)"));
        }
    }
}
