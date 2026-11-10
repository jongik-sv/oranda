using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.ExternalLoad;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 외부 적재 도구 tbLoader(XL2, 조정 결정 88·89): Oracle → Tibero(첫 계정만 — 두 번째 계정에는 테이블을 만들지 않는다, 결정 59).
/// 도구가 깔린 PC 에서만: 환경 변수 <c>ORANDA_IT_TBLOADER</c> = tbloader 경로(또는 폴더) 또는 <c>auto</c>(TB_HOME·PATH 에서 찾기). 없으면 건너뛴다.
/// 실측 목적: tbLoader 가 표준 입력으로 비밀번호를 받는지, TBDSN_FILE 로 만든 별칭을 쓰는지, 로그 형식·종료 코드, 행/초.
/// 결과·알림·도구 로그를 출력에 남긴다(비밀은 <see cref="CrossDbmsItEnvironment.Scrub"/>).
/// </summary>
public class CrossDbmsExternalLoadIt(ITestOutputHelper output)
{
    private static readonly ResolvedPeriod Period = new(null, null);

    private static string? ToolSetting => Environment.GetEnvironmentVariable("ORANDA_IT_TBLOADER") is { Length: > 0 } v ? v : null;

    [CrossDbmsFact]
    public async Task Oracle_rows_are_loaded_into_an_existing_tibero_table_by_tbloader()
    {
        if (ToolSetting is null)
        {
            output.WriteLine("ORANDA_IT_TBLOADER 가 없어 건너뜀");
            return;
        }

        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var t = CrossDbmsItEnvironment.Name("XL2_EX");
        const string ddl = "ID NUMBER(10) PRIMARY KEY, V VARCHAR2(60), N NUMBER(12,2), D DATE, TS TIMESTAMP(6), R RAW(8), C CLOB";
        await env.CreateTableAsync(Dbms.Oracle, t, ddl);
        await env.CreateTableAsync(Dbms.Tibero, t, ddl);
        await env.ExecAsync(Dbms.Oracle, $"INSERT INTO {t} (ID, V, N, D, TS, R, C) SELECT LEVEL, 'row, \"' || LEVEL || '\"', LEVEL * 1.25, DATE '2026-01-01' + LEVEL,"
                                         + " TIMESTAMP '2026-01-01 00:00:00.5' + NUMTODSINTERVAL(LEVEL, 'SECOND'), HEXTORAW('0A0B'), 'clob ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 300");
        await env.ExecAsync(Dbms.Oracle, $"INSERT INTO {t} (ID) VALUES (301)");
        await env.ExecAsync(Dbms.Tibero, $"INSERT INTO {t} (ID, V) VALUES (999, 'old')");

        var result = await RunAsync(env, new TableRule(t, RuleKind.All, TargetAction.DeleteAllInsert) { Load = LoadMethod.ExternalTool }, "existing");

        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(301, await env.CountAsync(Dbms.Tibero, t));
        Assert.Equal(0, await env.CountAsync(Dbms.Tibero, t, "ID = 999"));
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, t, "ID = 301 AND N IS NULL AND V IS NULL")); // 빈 숫자 필드가 0 이 아니라 NULL
        Assert.Equal(1, await env.CountAsync(Dbms.Tibero, t, "ID = 7 AND V = 'row, \"7\"' AND N = 8.75"));
        Assert.Contains("외부 도구", table.Message ?? string.Empty, StringComparison.Ordinal); // 기본 방식으로 다시 넣었으면 그 이유가 알림에 있다
    }

    [CrossDbmsFact]
    public async Task Measure_standard_and_tbloader_rows_per_second_on_the_same_table()
    {
        if (ToolSetting is null || Environment.GetEnvironmentVariable("ORANDA_IT_XL_MEASURE") != "1")
        {
            output.WriteLine("ORANDA_IT_TBLOADER·ORANDA_IT_XL_MEASURE=1 이 없어 건너뜀");
            return;
        }

        await using var env = await CrossDbmsItEnvironment.CreateAsync();
        if (env is null)
        {
            return;
        }

        var rows = int.TryParse(Environment.GetEnvironmentVariable("ORANDA_IT_XL_ROWS"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 200_000;
        var t = CrossDbmsItEnvironment.Name("XL2_BIG");
        const string ddl = "ID NUMBER(10) PRIMARY KEY, NAME VARCHAR2(60), AMT NUMBER(12,2), D DATE, NOTE VARCHAR2(200)";
        await env.CreateTableAsync(Dbms.Oracle, t, ddl);
        await env.CreateTableAsync(Dbms.Tibero, t, ddl);
        await env.ExecAsync(Dbms.Oracle, $"INSERT /*+ APPEND */ INTO {t} SELECT LEVEL, 'name ' || LEVEL, LEVEL / 7, DATE '2026-01-01' + MOD(LEVEL, 365), RPAD('note', 120, 'x')"
                                         + $" FROM DUAL CONNECT BY LEVEL <= {rows.ToString(CultureInfo.InvariantCulture)}");
        foreach (var load in new[] { LoadMethod.Standard, LoadMethod.ExternalTool, LoadMethod.Standard, LoadMethod.ExternalTool })
        {
            var sw = Stopwatch.StartNew();
            var result = await RunAsync(env, new TableRule(t, RuleKind.All, TargetAction.DeleteAllInsert) { Load = load }, "measure." + load);
            sw.Stop();
            Assert.Equal(CopyOutcome.Succeeded, Assert.Single(result.Tables).Outcome);
            output.WriteLine($"XL2 measure {load}: {rows.ToString("N0", CultureInfo.InvariantCulture)}행 · {sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}초"
                             + $" · {(rows / sw.Elapsed.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture)}행/초");
        }
    }

    private async Task<CopyRunResult> RunAsync(CrossDbmsItEnvironment env, TableRule rule, string item)
    {
        var sessions = await env.OpenCopySessionsAsync(Dbms.Oracle, Dbms.Tibero);
        var runner = new TestProcessRunner();
        var setting = ToolSetting == "auto" ? null : ToolSetting;
        var target = sessions.Target.Profile;
        var external = new CopyExternalLoad(new ExternalToolProbe(runner, new SystemExternalToolEnvironment()), runner, new ExternalToolPaths(null, setting),
                                            _ => Task.FromResult<ExternalLoadLogin?>(new ExternalLoadLogin(target.User, target.DataSource, env.Tibero.Env.Password, null)));
        sessions = sessions with { ExternalLoad = external };
        var job = CrossDbmsItEnvironment.Job(sessions, [rule], new CopyOptions { Commit = CommitMode.PerTable });
        var report = await env.Precheck(Dbms.Oracle, Dbms.Tibero).RunAsync(sessions, job, Period, null, CancellationToken.None);
        foreach (var i in report.Items)
        {
            output.WriteLine($"XL2 {item} precheck[{i.Code}/{i.Level}]: {env.Scrub(i.Message)}");
        }

        foreach (var p in report.Preview.Where(p => p.Sql.StartsWith("LOAD DATA", StringComparison.Ordinal)))
        {
            output.WriteLine($"XL2 {item} control:\n{env.Scrub(p.Sql)}");
        }

        Assert.False(report.HasErrors);
        var result = await env.Runner(Dbms.Oracle, Dbms.Tibero, new CrossMemoryRunLog())
            .RunAsync(sessions, job, report, CrossDbmsItEnvironment.NoDecisions(), Period, null, new CopyRunControl(), CancellationToken.None);
        foreach (var table in result.Tables)
        {
            output.WriteLine($"XL2 {item} table: {table.Table} {table.Outcome} 원본 {table.SourceRows} 대상 {table.TargetRowsText} {env.Scrub(table.Message ?? string.Empty)}");
        }

        foreach (var notice in result.Notices)
        {
            output.WriteLine($"XL2 {item} notice: {env.Scrub(notice)}");
        }

        foreach (var (start, outcome) in runner.Runs)
        {
            output.WriteLine($"XL2 {item} tool: {Path.GetFileName(start.FileName)} {env.Scrub(string.Join(' ', start.Arguments))} → 종료 {outcome.ExitCode}");
            output.WriteLine($"XL2 {item} tool stdout: {env.Scrub(Tail(outcome.StandardOutput))}");
            output.WriteLine($"XL2 {item} tool stderr: {env.Scrub(Tail(outcome.StandardError))}");
        }

        return result;
    }

    private static string Tail(string text) => text.Length <= 3000 ? text : text[^3000..];

    /// <summary>시험용 프로세스 실행기(앱은 Platform 의 실행기를 쓴다). 실행마다 시작 정보와 결과를 남긴다(출력 관찰용).</summary>
    private sealed class TestProcessRunner : IExternalProcessRunner
    {
        public List<(ExternalProcessStart Start, ExternalProcessResult Result)> Runs { get; } = [];

        public async Task<ExternalProcessResult> RunAsync(ExternalProcessStart start, CancellationToken ct)
        {
            var info = new ProcessStartInfo(start.FileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = start.WorkingDirectory,
            };
            foreach (var arg in start.Arguments)
            {
                info.ArgumentList.Add(arg);
            }

            foreach (var (key, value) in start.Environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(key);
                }
                else
                {
                    info.Environment[key] = value;
                }
            }

            Process process;
            try
            {
                process = Process.Start(info) ?? throw new ExternalProcessException("실행하지 못했습니다.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new ExternalProcessException("실행하지 못했습니다: " + ex.Message, ex);
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
                if (start.StandardInput is { } input)
                {
                    try
                    {
                        await process.StandardInput.WriteAsync(input);
                    }
                    catch (IOException)
                    {
                        // 도구가 표준 입력을 읽지 않고 끝났다.
                    }
                }

                process.StandardInput.Close();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(start.Timeout ?? TimeSpan.FromMinutes(20));
                try
                {
                    await process.WaitForExitAsync(limit.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                    var timedOut = new ExternalProcessResult(-1, await stdout, await stderr, TimedOut: true);
                    Runs.Add((start, timedOut));
                    return timedOut;
                }

                var result = new ExternalProcessResult(process.ExitCode, await stdout, await stderr);
                Runs.Add((start, result));
                return result;
            }
        }
    }
}
