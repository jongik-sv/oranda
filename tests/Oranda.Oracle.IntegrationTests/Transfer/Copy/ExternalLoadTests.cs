using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.ExternalLoad;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 외부 적재 도구(SQL*Loader, 조정 결정 88·89, XL1). CopyDbScope 사용.
// 도구가 깔린 PC 에서만 돈다: 환경 변수 ORANDA_IT_SQLLDR = sqlldr 경로(또는 폴더) 또는 "auto"(PATH·ORACLE_HOME·Instant Client 에서 찾기).
// 없으면 시험 본문을 건너뛰고 출력에 그렇게 적는다(이 PC 에서는 컴파일까지만 확인한다).

/// <summary>
/// 외부 도구로 넣기: 이미 있는 테이블(일반 경로, 정리 확정 뒤 적재)·새 테이블(direct path, 키는 적재 뒤)·거절 행(실패 행 + 키)·비밀번호 두 길(표준 입력·비밀 parfile).
/// 측정(같은 테이블 기본 방식 대 외부 도구 행/초)은 ORANDA_IT_XL_MEASURE=1 일 때만.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class ExternalLoadTests
{
    private static readonly ResolvedPeriod NoPeriod = new(null, null);

    private readonly OracleFreeFixture _db;
    private readonly ITestOutputHelper _out;

    public ExternalLoadTests(OracleFreeFixture db, ITestOutputHelper output)
    {
        _db = db;
        _out = output;
    }

    private static string? ToolSetting => Environment.GetEnvironmentVariable("ORANDA_IT_SQLLDR") is { Length: > 0 } v ? v : null;

    [Theory]
    [InlineData(LoaderPasswordVia.StandardInput)]
    [InlineData(LoaderPasswordVia.PrivateParfile)]
    public async Task Existing_table_is_cleaned_committed_then_loaded_by_the_tool_with_every_value_kind(LoaderPasswordVia via)
    {
        if (ToolSetting is null)
        {
            _out.WriteLine("ORANDA_IT_SQLLDR 가 없어 건너뜀");
            return;
        }

        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        using var work = new FolderCleanup(WorkRoot(scope)); // 빈 폴더까지 지운다(#181)
        const string columns = "ID NUMBER PRIMARY KEY, NAME VARCHAR2(40), AMT NUMBER(12,3), D DATE, TS TIMESTAMP(6), TZ TIMESTAMP(6) WITH TIME ZONE,"
                               + " YM INTERVAL YEAR(2) TO MONTH, DS INTERVAL DAY(2) TO SECOND(6), R RAW(8), BD BINARY_DOUBLE, C CLOB, B BLOB";
        await scope.CreateSourceTableAsync("XL_ALL", columns);
        await scope.CreateTargetTableAsync("XL_ALL", columns);
        await scope.ExecSourceAsync(Block(
            "INSERT INTO XL_ALL VALUES (1, 'plain', 1.5, DATE '2026-01-02', TIMESTAMP '2026-01-02 03:04:05.123456', TIMESTAMP '2026-01-02 03:04:05.5 +09:00',"
            + " INTERVAL '1-2' YEAR TO MONTH, INTERVAL '3 04:05:06.7' DAY TO SECOND, HEXTORAW('0A0B'), 2.5, 'clob one', HEXTORAW('DEADBEEF'))",
            "INSERT INTO XL_ALL VALUES (2, 'comma, \"quote\" and' || CHR(10) || 'newline', -0.001, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)",
            "INSERT INTO XL_ALL (ID, NAME, C, B) VALUES (3, '한글 이름', RPAD('x', 40000, 'y'), EMPTY_BLOB())",
            "INSERT INTO XL_ALL (ID, NAME, C, B) VALUES (4, 'empty clob, null blob', EMPTY_CLOB(), NULL)",
            "INSERT INTO XL_ALL (ID, NAME, C, B) VALUES (5, 'zero bytes', 'a' || CHR(0) || 'b', HEXTORAW('00FF00'))",
            "INSERT INTO XL_ALL (ID, NAME, C, B) VALUES (6, 'null clob, empty blob', NULL, EMPTY_BLOB())"));
        await scope.ExecTargetAsync(Block("INSERT INTO XL_ALL (ID, NAME) VALUES (99, 'old')"));

        var job = scope.Job(new TableRule("XL_ALL", RuleKind.All, TargetAction.DeleteAllInsert) { Load = LoadMethod.ExternalTool });
        var sessions = (await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development)) with { ExternalLoad = External(scope, via) };
        var report = await PrecheckAsync(scope, sessions, job);

        Assert.Contains(report.Items, i => i.Code == CopyCheckCodes.ExternalLoad && i.Level == CheckLevel.Confirm);
        Assert.Equal(LoadMethod.ExternalTool, Assert.Single(report.Order).Load);
        Assert.Contains(report.Preview, p => p.Sql.StartsWith("OPTIONS (DIRECT=TRUE", StringComparison.Ordinal)); // 이미 있는 테이블이지만 트리거·FK·CHECK 없음 + 전체 정리 = direct(결정 96)
        _out.WriteLine("도구: " + report.ExternalTool);

        var logs = new LogLines();
        var result = await RunAsync(scope, sessions, job, report, logs);

        Assert.True(result.Outcome == CopyOutcome.Succeeded && result.FailedRows.Count == 0, Describe(result));
        Assert.Contains("외부 도구(", result.Tables[0].Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(Diff(scope, "XL_ALL", "ID, NAME, AMT, D, TS, TZ, YM, DS, R, BD")));
        var lobDiff = await AdminColumnAsync(
            "SELECT S.ID || ': C src=' || NVL2(S.C, TO_CHAR(DBMS_LOB.GETLENGTH(S.C)), 'NULL') || ' dst=' || NVL2(T.C, TO_CHAR(DBMS_LOB.GETLENGTH(T.C)), 'NULL')"
            + " || ' · B src=' || NVL2(S.B, TO_CHAR(DBMS_LOB.GETLENGTH(S.B)), 'NULL') || ' dst=' || NVL2(T.B, TO_CHAR(DBMS_LOB.GETLENGTH(T.B)), 'NULL')"
            + $" FROM {scope.SrcUser}.XL_ALL S JOIN {scope.DstUser}.XL_ALL T ON T.ID = S.ID"
            + " WHERE NVL(DBMS_LOB.COMPARE(S.C, T.C), CASE WHEN S.C IS NULL AND T.C IS NULL THEN 0 ELSE 1 END) <> 0"
            + " OR NVL(DBMS_LOB.COMPARE(S.B, T.B), CASE WHEN S.B IS NULL AND T.B IS NULL THEN 0 ELSE 1 END) <> 0 ORDER BY S.ID");
        Assert.True(lobDiff.Count == 0, "LOB 이 다른 행(ID: 길이, NULL): " + string.Join(" / ", lobDiff) + " | " + string.Join(" / ", logs.Matching("LOB NULL", "SQL*Loader 끝")));
        Assert.Equal("1,2,3,4,5,6", await ListAsync($"SELECT ID FROM {scope.DstUser}.XL_ALL ORDER BY ID")); // 99 는 정리됨
        Assert.Empty(LeftoverFolders(scope));
    }

    [Fact]
    public async Task New_table_goes_direct_path_and_gets_its_key_after_the_load()
    {
        if (ToolSetting is null)
        {
            _out.WriteLine("ORANDA_IT_SQLLDR 가 없어 건너뜀");
            return;
        }

        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        using var work = new FolderCleanup(WorkRoot(scope)); // 빈 폴더까지 지운다(#181)
        await scope.ExecSourceAsync("CREATE TABLE XL_NEW (ID NUMBER CONSTRAINT XL_NEW_PK PRIMARY KEY, NAME VARCHAR2(20))");
        await scope.ExecSourceAsync("BEGIN FOR I IN 1 .. 5000 LOOP INSERT INTO XL_NEW VALUES (I, 'n' || I); END LOOP; COMMIT; END;");

        var job = scope.Job(new TableRule("XL_NEW", RuleKind.All, TargetAction.DeleteAllInsert) { CreateIfMissing = true, Load = LoadMethod.ExternalTool });
        var sessions = (await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development)) with { ExternalLoad = External(scope) };
        var report = await PrecheckAsync(scope, sessions, job);
        Assert.Contains(report.Preview, p => p.Sql.StartsWith("OPTIONS (DIRECT=TRUE", StringComparison.Ordinal));

        var result = await RunAsync(scope, sessions, job, report);

        Assert.True(result.Outcome == CopyOutcome.Succeeded, Describe(result));
        Assert.Equal(5000, await CountAsync($"SELECT COUNT(*) FROM {scope.DstUser}.XL_NEW"));
        Assert.Equal("XL_NEW_PK:ENABLED:VALID",
            await ListAsync($"SELECT C.CONSTRAINT_NAME || ':' || C.STATUS || ':' || I.STATUS FROM DBA_CONSTRAINTS C JOIN DBA_INDEXES I"
                            + $" ON I.OWNER = C.OWNER AND I.INDEX_NAME = C.INDEX_NAME WHERE C.OWNER = '{scope.DstUser}' AND C.TABLE_NAME = 'XL_NEW' AND C.CONSTRAINT_TYPE = 'P'"));
    }

    [Fact]
    public async Task Rows_the_target_refuses_become_failed_rows_with_their_keys()
    {
        if (ToolSetting is null)
        {
            _out.WriteLine("ORANDA_IT_SQLLDR 가 없어 건너뜀");
            return;
        }

        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        using var work = new FolderCleanup(WorkRoot(scope)); // 빈 폴더까지 지운다(#181)
        await scope.CreateSourceTableAsync("XL_REJ", "ID NUMBER PRIMARY KEY, AMT NUMBER");
        await scope.CreateTargetTableAsync("XL_REJ", "ID NUMBER PRIMARY KEY, AMT NUMBER CONSTRAINT XL_REJ_CK CHECK (AMT > 0)");
        await scope.ExecSourceAsync(Block("INSERT INTO XL_REJ VALUES (1, 5)", "INSERT INTO XL_REJ VALUES (2, -1)", "INSERT INTO XL_REJ VALUES (3, 7)"));

        var job = scope.Job(new TableRule("XL_REJ", RuleKind.All, TargetAction.DeleteAllInsert) { Load = LoadMethod.ExternalTool });
        var sessions = (await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development)) with { ExternalLoad = External(scope) };
        var report = await PrecheckAsync(scope, sessions, job);
        Assert.Contains(report.Preview, p => p.Sql.StartsWith("OPTIONS (DIRECT=FALSE", StringComparison.Ordinal)); // CHECK 가 있어 일반 경로(어긋난 행을 거절)
        var result = await RunAsync(scope, sessions, job, report);

        Assert.Contains("외부 도구(", result.Tables[0].Message ?? string.Empty, StringComparison.Ordinal); // 기본 방식으로 다시 넣은 게 아니라 도구가 거절했다
        Assert.Equal("1,3", await ListAsync($"SELECT ID FROM {scope.DstUser}.XL_REJ ORDER BY ID"));
        var failed = Assert.Single(result.FailedRows);
        Assert.Equal("2", failed.Key["ID"]);
        Assert.Contains("ORA-02290", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Measure_standard_and_external_rows_per_second_on_the_same_table()
    {
        if (ToolSetting is null || Environment.GetEnvironmentVariable("ORANDA_IT_XL_MEASURE") != "1")
        {
            _out.WriteLine("ORANDA_IT_SQLLDR·ORANDA_IT_XL_MEASURE=1 이 없어 건너뜀");
            return;
        }

        var rows = int.TryParse(Environment.GetEnvironmentVariable("ORANDA_IT_XL_ROWS"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 500_000;
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        using var work = new FolderCleanup(WorkRoot(scope)); // 빈 폴더까지 지운다(#181)
        const string columns = "ID NUMBER PRIMARY KEY, NAME VARCHAR2(60), AMT NUMBER(12,2), D DATE, NOTE VARCHAR2(200)";
        await scope.CreateSourceTableAsync("XL_BIG", columns);
        await scope.CreateTargetTableAsync("XL_BIG", columns);
        await scope.ExecSourceAsync($"INSERT /*+ APPEND */ INTO XL_BIG SELECT LEVEL, 'name ' || LEVEL, LEVEL / 7, DATE '2026-01-01' + MOD(LEVEL, 365),"
                                    + $" RPAD('note', 120, 'x') FROM DUAL CONNECT BY LEVEL <= {rows.ToString(CultureInfo.InvariantCulture)}");
        await scope.ExecSourceAsync("COMMIT");

        // 세 갈래(결정 96): 기본 방식 · 외부 도구로 이미 있는 테이블(트리거·FK·CHECK 없음 → direct) · 외부 도구로 새 테이블(direct, 키는 적재 뒤). 두 번씩.
        (string Label, LoadMethod Load, bool Fresh)[] rounds =
        [
            ("기본 방식", LoadMethod.Standard, false), ("외부·기존 테이블", LoadMethod.ExternalTool, false), ("외부·새 테이블", LoadMethod.ExternalTool, true),
            ("기본 방식", LoadMethod.Standard, false), ("외부·기존 테이블", LoadMethod.ExternalTool, false), ("외부·새 테이블", LoadMethod.ExternalTool, true),
        ];
        foreach (var (label, load, fresh) in rounds)
        {
            if (fresh)
            {
                await scope.ExecTargetAsync("DROP TABLE XL_BIG PURGE"); // 없으면 만들기로 새로 만든다
            }

            var job = scope.Job(new TableRule("XL_BIG", RuleKind.All, TargetAction.DeleteAllInsert) { Load = load, CreateIfMissing = fresh });
            var sessions = (await scope.OpenSessionsAsync(env: ConnectionEnvironment.Development)) with { ExternalLoad = External(scope) };
            var report = await PrecheckAsync(scope, sessions, job);
            if (load == LoadMethod.ExternalTool)
            {
                Assert.Contains(report.Preview, p => p.Sql.StartsWith("OPTIONS (DIRECT=TRUE", StringComparison.Ordinal)); // 두 갈래 모두 direct
            }

            if (!fresh)
            {
                await scope.ExecTargetAsync("TRUNCATE TABLE XL_BIG"); // 앞 회차의 50만 행 DELETE 시간을 빼고 적재만 잰다(#181)
            }

            var logs = new LogLines();
            var sw = Stopwatch.StartNew();
            var result = await RunAsync(scope, sessions, job, report, logs);
            sw.Stop();
            Assert.True(result.Outcome == CopyOutcome.Succeeded, Describe(result));
            if (load == LoadMethod.ExternalTool)
            {
                Assert.Contains(logs.Matching("SQL*Loader 로 넣습니다"), l => l.Contains("direct path", StringComparison.Ordinal));
                // 도구가 실패해 기본 방식으로 다시 넣었으면 측정이 아니다(#177 은 이걸 못 잡았다).
                var message = result.Tables[0].Message ?? string.Empty;
                Assert.True(message.Contains("외부 도구(", StringComparison.Ordinal) && !message.Contains("기본 방식으로 다시", StringComparison.Ordinal), Describe(result));
            }

            _out.WriteLine($"{label}: {rows.ToString("N0", CultureInfo.InvariantCulture)}행 · {sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}초"
                           + $" · {(rows / sw.Elapsed.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture)}행/초"
                           + (result.Tables[0].Times is { } t
                               ? $" · 적재 {Seconds(t.Load)} (읽기 {Seconds(t.Read)} · 변환 {Seconds(t.Convert)} · 쓰기 {Seconds(t.Write)}) · {t.RowsPerSecond.ToString("N0", CultureInfo.InvariantCulture)}행/초(적재 구간)"
                               : string.Empty));
            foreach (var line in logs.Matching("데이터 파일", "SQL*Loader 끝", "SQL*Loader 로그 요약", "LOB NULL"))
            {
                _out.WriteLine("  " + line);
            }
        }
    }

    /// <summary>
    /// 진단(#177 SQL*Loader-128): 접속 문자열·비밀번호 넘기는 길을 여러 모양으로 sqlldr 에 줘 보고 도구 출력을 그대로 적는다(비밀번호는 가림).
    /// 없는 테이블로 적재하게 해 두어, 로그인에 성공하면 SQL*Loader-941(테이블 설명 실패)로 끝나고 실패하면 SQL*Loader-128 + 원인 ORA- 가 나온다.
    /// 하나도 로그인하지 못하면 실패하고 메시지에 전부 싣는다.
    /// </summary>
    [Fact]
    public async Task Sqlldr_login_forms_report_what_the_tool_says()
    {
        if (ToolSetting is null)
        {
            _out.WriteLine("ORANDA_IT_SQLLDR 가 없어 건너뜀");
            return;
        }

        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        using var work = new FolderCleanup(WorkRoot(scope)); // 빈 폴더까지 지운다(#181)
        var runner = new TestProcessRunner();
        var tool = new ExternalToolProbe(runner, new SystemExternalToolEnvironment()).Locate(ExternalLoaderKind.SqlLoader, ToolSetting == "auto" ? null : ToolSetting).Path;
        Assert.NotNull(tool);

        // '#' 없는 비밀번호의 사용자(parfile 의 '#' 주석 여부를 가린다). 끝나면 지운다.
        var plainUser = "XLP_" + scope.Tag.ToUpperInvariant();
        var plainPassword = "Xl" + Guid.NewGuid().ToString("N")[..16];
        await AdminExecAsync($"CREATE USER {plainUser} IDENTIFIED BY \"{plainPassword}\"");
        var report = new StringBuilder();
        var ok = new List<string>();
        var dir = Path.Combine(WorkRoot(scope), "login-probe");
        try
        {
            await AdminExecAsync($"GRANT CREATE SESSION TO {plainUser}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "probe.ctl"), "LOAD DATA\nINFILE *\nAPPEND INTO TABLE XL_NO_SUCH_TABLE\nFIELDS TERMINATED BY ','\n(A)\nBEGINDATA\n1\n");
            var ds = _db.DataSource;
            var user = scope.DstUser;
            var pw = scope.Password;
            var tns = TnsAlias(dir, ds);
            var cases = new List<(string Name, string? UserId, string? Stdin, string[] Extra, string? TnsAdmin, string Secret)>
            {
                ("stdin pw · user@ds", $"{user}@{ds}", pw + "\n", [], null, pw),
                ("stdin pw · user@//ds", $"{user}@//{ds}", pw + "\n", [], null, pw),
                ("stdin pw · user@\"ds\"", $"{user}@\"{ds}\"", pw + "\n", [], null, pw),
                ("stdin all · (no userid) user/\"pw\"@ds", null, $"{user}/\"{pw}\"@{ds}\n", [], null, pw),
                ("parfile · user/\"pw\"@ds", $"{user}/\"{pw}\"@{ds}", null, [], null, pw),
                ("parfile · user/\"pw\"@//ds", $"{user}/\"{pw}\"@//{ds}", null, [], null, pw),
                ("parfile · plain pw user/pw@ds", $"{plainUser}/{plainPassword}@{ds}", null, [], null, plainPassword),
                ("parfile · plain pw user/\"pw\"@ds", $"{plainUser}/\"{plainPassword}\"@{ds}", null, [], null, plainPassword),
                ("stdin pw · plain pw user@ds", $"{plainUser}@{ds}", plainPassword + "\n", [], null, plainPassword),
                ("cmdline · user/\"pw\"@ds", null, null, [$"userid={user}/\"{pw}\"@{ds}"], null, pw),
            };
            if (tns is not null)
            {
                cases.Add(("stdin pw · TNS alias user@ORANDA_XL", $"{user}@ORANDA_XL", pw + "\n", [], dir, pw));
                cases.Add(("parfile · TNS alias user/\"pw\"@ORANDA_XL", $"{user}/\"{pw}\"@ORANDA_XL", null, [], dir, pw));
            }

            var n = 0;
            foreach (var c in cases)
            {
                n++;
                var par = Path.Combine(dir, $"p{n}.par");
                var log = Path.Combine(dir, $"p{n}.log");
                File.WriteAllText(par, (c.UserId is null ? string.Empty : "userid=" + c.UserId + "\n")
                                       + $"control={Path.Combine(dir, "probe.ctl")}\nlog={log}\nbad={Path.Combine(dir, $"p{n}.bad")}\nerrors=0\n");
                var env = new Dictionary<string, string?>(SqlLoaderCommand.BaseEnvironment, StringComparer.Ordinal);
                if (c.TnsAdmin is not null)
                {
                    env["TNS_ADMIN"] = c.TnsAdmin;
                }

                var result = await runner.RunAsync(new ExternalProcessStart(tool!, [.. c.Extra, "parfile=" + par], dir, env, c.Stdin, TimeSpan.FromSeconds(60)), CancellationToken.None);
                var text = $"stdout: {result.StandardOutput.Trim()}\nstderr: {result.StandardError.Trim()}\nlog: {(File.Exists(log) ? File.ReadAllText(log).Trim() : "(없음)")}";
                text = text.Replace(c.Secret, "********", StringComparison.Ordinal);
                var loggedIn = !text.Contains("SQL*Loader-128", StringComparison.Ordinal) && !text.Contains("ORA-01017", StringComparison.Ordinal)
                               && (text.Contains("SQL*Loader-941", StringComparison.Ordinal) || text.Contains("ORA-00942", StringComparison.Ordinal));
                if (loggedIn)
                {
                    ok.Add(c.Name);
                }

                report.Append(CultureInfo.InvariantCulture, $"===== {c.Name} · exit {result.ExitCode} · {(loggedIn ? "LOGIN OK" : "LOGIN FAILED")}\n");
                report.Append(text.Length > 2500 ? text[..2500] + " …" : text).Append('\n');
            }
        }
        finally
        {
            await AdminExecAsync($"DROP USER {plainUser} CASCADE");
            try
            {
                Directory.Delete(WorkRoot(scope), recursive: true); // parfile 에 비밀번호가 있다
            }
            catch (IOException)
            {
            }
        }

        _out.WriteLine("data source: " + _db.DataSource);
        _out.WriteLine("login ok: " + string.Join(" | ", ok));
        _out.WriteLine(report.ToString());
        Assert.True(ok.Count > 0, "sqlldr 가 어느 모양으로도 로그인하지 못했습니다.\n" + report);
    }

    /// <summary>host[:port]/service 모양이면 그 폴더에 tnsnames.ora(별칭 ORANDA_XL)를 쓰고 폴더를 돌려준다. 다른 모양이면 null.</summary>
    private static string? TnsAlias(string dir, string dataSource)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dataSource.Trim(), @"^(?://)?(?<host>[^:/]+)(?::(?<port>\d+))?/(?<svc>[^:/?]+)$");
        if (!m.Success)
        {
            return null;
        }

        var port = m.Groups["port"].Success ? m.Groups["port"].Value : "1521";
        File.WriteAllText(Path.Combine(dir, "tnsnames.ora"),
            $"ORANDA_XL = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = {m.Groups["host"].Value})(PORT = {port})) (CONNECT_DATA = (SERVICE_NAME = {m.Groups["svc"].Value})))\n");
        return dir;
    }

    private async Task AdminExecAsync(string sql)
    {
        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- 준비물 ----

    private CopyExternalLoad External(CopyDbScope scope, LoaderPasswordVia via = LoaderPasswordVia.StandardInput)
    {
        var runner = new TestProcessRunner();
        var setting = ToolSetting == "auto" ? null : ToolSetting;
        return new CopyExternalLoad(new ExternalToolProbe(runner, new SystemExternalToolEnvironment()), runner, new ExternalToolPaths(setting, null),
                                    _ => Task.FromResult<ExternalLoadLogin?>(new ExternalLoadLogin(scope.DstUser, _db.DataSource, scope.Password, null)))
        {
            WorkRoot = WorkRoot(scope),
            PasswordVia = via,
        };
    }

    private static string Seconds(TimeSpan? span) => span is { } s ? s.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "초" : "?";

    /// <summary>시험의 작업 뿌리 폴더(oranda-it-xl-태그)를 시험 끝에 통째로 지운다.</summary>
    private sealed class FolderCleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string WorkRoot(CopyDbScope scope) => Path.Combine(Path.GetTempPath(), "oranda-it-xl-" + scope.Tag);

    private static IEnumerable<string> LeftoverFolders(CopyDbScope scope)
    {
        var root = Path.Combine(WorkRoot(scope), LoaderWorkspace.FolderName);
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : [];
    }

    private static string Diff(CopyDbScope scope, string table, string columns) =>
        $"SELECT COUNT(*) FROM ((SELECT {columns} FROM {scope.SrcUser}.{table} MINUS SELECT {columns} FROM {scope.DstUser}.{table})"
        + $" UNION ALL (SELECT {columns} FROM {scope.DstUser}.{table} MINUS SELECT {columns} FROM {scope.SrcUser}.{table}))";

    private static async Task<PrecheckReport> PrecheckAsync(CopyDbScope scope, CopySessions sessions, CopyJob job)
    {
        var report = await scope.Precheck().RunAsync(sessions, job, NoPeriod, null, CancellationToken.None);
        Assert.False(report.HasErrors, string.Join(Environment.NewLine, report.Items.Where(i => i.Level == CheckLevel.Error).Select(i => i.Code + ": " + i.Message)));
        return report;
    }

    private static Task<CopyRunResult> RunAsync(CopyDbScope scope, CopySessions sessions, CopyJob job, PrecheckReport report, IProgress<CopyProgress>? progress = null)
    {
        scope.Ui.ConfirmAnswer = false;
        var decisions = new CopyDecisions(
            new HashSet<string>(StringComparer.Ordinal), new HashSet<ForeignKeyRef>(),
            report.Preview.Where(p => p.Policy == PolicyDecisionKind.Confirm).Select(p => p.Sql).ToHashSet(StringComparer.Ordinal),
            ProceedSequenceGap: false);
        var runner = new OracleCopyRunner(scope.Bulk(), new OracleExecutionService(new ExecutionPolicy(), scope.Ui, scope.Bus, null, pageSize: 500), new NoRunLog());
        return runner.RunAsync(sessions, job, report, decisions, NoPeriod, progress, new CopyRunControl(), CancellationToken.None);
    }

    private static string Describe(CopyRunResult result)
        => result.Outcome + ": " + string.Join(" / ", result.Tables.Select(t => t.Table + "=" + t.Outcome + " " + t.Message))
           + " | notices: " + string.Join(" / ", result.Notices);

    private static string Block(params string[] statements)
        => "BEGIN " + string.Join(' ', statements.Select(s => s.TrimEnd(';') + ";")) + " COMMIT; END;";

    private string AdminConnectionString => new ConnectionProfile("admin", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword);

    private async Task<List<string?>> AdminColumnAsync(string sql)
    {
        await using var conn = new OracleConnection(AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var values = new List<string?>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return values;
    }

    private async Task<long> CountAsync(string sql) => long.Parse((await AdminColumnAsync(sql)).FirstOrDefault() ?? "0", CultureInfo.InvariantCulture);

    private async Task<string> ListAsync(string sql) => string.Join(',', (await AdminColumnAsync(sql)).Select(v => v ?? "<null>"));

    /// <summary>
    /// 시험용 프로세스 실행기(앱은 Platform 의 실행기를 쓴다 — 이 시험 프로젝트는 Platform 을 참조하지 않는다). 표준 입력을 쓰고 닫고, 취소하면 프로세스 트리를 끝낸다.
    /// </summary>
    private sealed class TestProcessRunner : IExternalProcessRunner
    {
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
                    await process.StandardInput.WriteAsync(input);
                }

                process.StandardInput.Close();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (start.Timeout is { } timeout)
                {
                    limit.CancelAfter(timeout);
                }

                try
                {
                    await process.WaitForExitAsync(limit.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                    return new ExternalProcessResult(-1, await stdout, await stderr, TimedOut: true);
                }

                return new ExternalProcessResult(process.ExitCode, await stdout, await stderr);
            }
        }
    }

    /// <summary>실행의 작업 로그 줄을 모은다(측정·진단 출력용).</summary>
    private sealed class LogLines : IProgress<CopyProgress>
    {
        private readonly List<string> _lines = [];

        public void Report(CopyProgress value)
        {
            if (value.Log is { } entry)
            {
                lock (_lines)
                {
                    _lines.Add(entry.Text);
                }
            }
        }

        public List<string> Matching(params string[] starts)
        {
            lock (_lines)
            {
                return [.. _lines.Where(l => starts.Any(s => l.StartsWith(s, StringComparison.Ordinal)))];
            }
        }
    }

    private sealed class NoRunLog : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(new Record());

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;

        private sealed class Record : ICopyRunRecord
        {
            public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

            public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

            public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

            public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct) => Task.CompletedTask;

            public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
