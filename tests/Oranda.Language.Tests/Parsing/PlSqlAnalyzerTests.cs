using System.Diagnostics;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Language.Parsing;
using Xunit.Abstractions;

namespace Oranda.Language.Tests.Parsing;

// 소유: TSK-04-03. L2 분석·문법 오류.
// 성능은 단정하지 않는다(2026-10-20 조정자 결정: 이번 회차 성능 시험 보류). 측정 시험은 값만 출력한다.
public class PlSqlAnalyzerTests(ITestOutputHelper output)
{
    /// <summary>정상 회귀 예제: 오류(Error)가 하나도 나오면 안 된다.</summary>
    public static TheoryData<string> ValidStatements => new()
    {
        "SELECT * FROM emp",
        "SELECT e.empno, e.ename, d.dname FROM emp e JOIN dept d ON d.deptno = e.deptno WHERE e.sal > :min_sal ORDER BY 2",
        "SELECT /*+ INDEX(e emp_pk) */ COUNT(*) FROM emp e WHERE e.hiredate >= DATE '2020-01-01'",
        "WITH t AS (SELECT level AS n FROM dual CONNECT BY level <= 10) SELECT SUM(n) OVER (ORDER BY n) FROM t",
        "SELECT deptno, LISTAGG(ename, ',') WITHIN GROUP (ORDER BY ename) FROM emp GROUP BY deptno HAVING COUNT(*) > 1",
        "SELECT q'[it's]' AS s, 'a''b' AS t FROM dual",
        "SELECT * FROM emp FETCH FIRST 10 ROWS ONLY",
        "SELECT * FROM emp WHERE ename = '&name' AND deptno = &dept",
        "INSERT INTO emp (empno, ename) VALUES (1, 'A')",
        "INSERT ALL INTO t1 (a) VALUES (x) INTO t2 (b) VALUES (x) SELECT 1 AS x FROM dual",
        "UPDATE emp SET sal = sal * 1.1 WHERE deptno = 10",
        "DELETE FROM emp WHERE empno = :id",
        "MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = s.v WHEN NOT MATCHED THEN INSERT (id, v) VALUES (s.id, s.v)",
        "CREATE TABLE t (id NUMBER(10) PRIMARY KEY, name VARCHAR2(100 CHAR) NOT NULL, created DATE DEFAULT SYSDATE)",
        "ALTER TABLE t ADD (memo CLOB)",
        "CREATE INDEX t_ix ON t (name, created)",
        "CREATE OR REPLACE VIEW v_emp AS SELECT empno, ename FROM emp",
        "DROP TABLE t PURGE",
        "TRUNCATE TABLE t",
        "COMMENT ON TABLE t IS '설명'",
        "GRANT SELECT ON t TO scott",
        "COMMIT",
        "ROLLBACK",
        "CREATE SEQUENCE s START WITH 1 INCREMENT BY 1",
        "BEGIN\n  NULL;\nEND;",
        "DECLARE\n  v NUMBER := 0;\nBEGIN\n  FOR r IN (SELECT empno FROM emp) LOOP\n    v := v + 1;\n  END LOOP;\n  DBMS_OUTPUT.PUT_LINE(v);\nEXCEPTION\n  WHEN OTHERS THEN\n    RAISE;\nEND;",
        "CREATE OR REPLACE PROCEDURE p_hello(p_name IN VARCHAR2) IS\nBEGIN\n  DBMS_OUTPUT.PUT_LINE('hi ' || p_name);\nEND p_hello;",
        "CREATE OR REPLACE FUNCTION f_add(a NUMBER, b NUMBER) RETURN NUMBER IS\nBEGIN\n  RETURN a + b;\nEND;",
        "CREATE OR REPLACE PACKAGE pkg AS\n  PROCEDURE p;\n  FUNCTION f RETURN NUMBER;\nEND pkg;",
        "CREATE OR REPLACE PACKAGE BODY pkg AS\n  PROCEDURE p IS BEGIN NULL; END;\n  FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;\nEND pkg;",
        "CREATE OR REPLACE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW\nBEGIN\n  :NEW.created := SYSDATE;\nEND;",
        "DECLARE\n  b BOOLEAN := TRUE;\nBEGIN\n  IF b THEN\n    NULL;\n  END IF;\nEND;",
    };

    [Theory]
    [MemberData(nameof(ValidStatements))]
    public async Task Valid_statement_has_no_errors(string sql)
    {
        var diagnostics = await new LanguageService().AnalyzeAsync(sql, CancellationToken.None);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Catalog_queries_are_regression_examples_with_no_errors()
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src", "Oranda.Oracle", "Catalog"), "*.sql", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var service = new LanguageService();
        var failures = new List<string>();
        foreach (var file in files)
        {
            // 카탈로그 자리 표시자: @PFX@ 는 뷰 접두사, @SEGMENTS@ 는 세그먼트 뷰(표 자리), 그 밖의 @이름@ 은 실행 때 식으로 바뀐다.
            var sql = System.Text.RegularExpressions.Regex.Replace(
                (await File.ReadAllTextAsync(file)).Replace("@PFX@", "dba", StringComparison.Ordinal)
                    .Replace("@SEGMENTS@", "dba_segments", StringComparison.Ordinal),
                "@[A-Z_]+@",
                "NULL");
            var diagnostics = await service.AnalyzeAsync(sql, CancellationToken.None);
            failures.AddRange(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{Path.GetFileName(file)}@{d.Range.Start}: {d.Message}"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("SELECT * FROM WHERE x = 1", "WHERE")]
    [InlineData("SELECT a, FROM t", "FROM")]
    [InlineData("UPDATE t SET WHERE id = 1", "WHERE")]
    [InlineData("SELEC * FROM t", "SELEC")]
    [InlineData("BEGIN\n  x := ;\nEND;", ";")]
    public async Task Syntax_error_is_reported_at_the_offending_token(string sql, string offending)
    {
        var diagnostics = await new LanguageService().AnalyzeAsync(sql, CancellationToken.None);

        var error = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(PlSqlAnalyzer.ErrorCode, error.Code);
        Assert.Equal(sql.IndexOf(offending, StringComparison.Ordinal), error.Range.Start);
        Assert.Equal(offending.Length, error.Range.Length);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Unfinished_statement_is_reported_at_its_last_character()
    {
        const string sql = "SELECT * FROM t WHERE   ";

        var diagnostics = await new LanguageService().AnalyzeAsync(sql, CancellationToken.None);

        var error = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(sql.TrimEnd().Length - 1, error.Range.Start);
    }

    [Fact]
    public async Task Diagnostics_use_document_offsets_across_statements()
    {
        const string doc = "SELECT 1 FROM dual;\n\nSELECT * FROM WHERE x = 1;\n\nBEGIN NULL; END;\n/\n";

        var diagnostics = await new LanguageService().AnalyzeAsync(doc, CancellationToken.None);

        var error = Assert.Single(diagnostics);
        Assert.Equal(doc.IndexOf("WHERE", StringComparison.Ordinal), error.Range.Start);
    }

    [Theory]
    [InlineData("CREATE TABLE IF NOT EXISTS t (id NUMBER)")]
    [InlineData("CREATE TABLE emb (id NUMBER, v VECTOR(3, FLOAT32))")]
    [InlineData("CREATE DOMAIN email_d AS VARCHAR2(100) CONSTRAINT CHECK (REGEXP_LIKE(email_d, '@'))")]
    [InlineData("SELECT id FROM emb ORDER BY VECTOR_DISTANCE(v, :q, COSINE) FETCH FIRST 5 ROWS ONLY")]
    [InlineData("CREATE JSON RELATIONAL DUALITY VIEW dv AS SELECT JSON {'id' : e.empno} FROM emp e")]
    [InlineData("DROP TABLE IF EXISTS t")]
    [InlineData("SELECT TRUE AND FALSE")]
    [InlineData("SELECT 1 + 1")]
    public async Task New_23ai_syntax_is_not_an_error(string sql)
    {
        var diagnostics = await new LanguageService().AnalyzeAsync(sql, CancellationToken.None);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        if (diagnostics.Count > 0)
        {
            var info = Assert.Single(diagnostics);
            Assert.Equal(DiagnosticSeverity.Info, info.Severity);
            Assert.Equal(PlSqlAnalyzer.UnsupportedCode, info.Code);
            Assert.Contains("분석 불가", info.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Unknown_form_of_a_known_command_is_unsupported_not_an_error()
    {
        var result = PlSqlAnalyzer.Analyze("ALTER FROBNICATE everything", StatementKind.Ddl);

        Assert.Equal(AnalysisOutcome.Unsupported, result.Outcome);
        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(result.Diagnostics).Severity);
    }

    [Fact]
    public async Task Sql_plus_commands_are_not_analyzed()
    {
        const string doc = "SET SERVEROUTPUT ON\nPROMPT hello there\nSELECT 1 FROM dual;\n";

        var diagnostics = await new LanguageService().AnalyzeAsync(doc, CancellationToken.None);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Only_changed_statements_are_parsed_again()
    {
        var service = new LanguageService();
        const string first = "SELECT 1 FROM dual;\nSELECT 2 FROM dual;\nSELECT 3 FROM dual;\n";
        await service.AnalyzeAsync(first, CancellationToken.None);
        Assert.Equal(3, service.ParsedStatementCount);

        await service.AnalyzeAsync(first.Replace("SELECT 2", "SELECT 22", StringComparison.Ordinal), CancellationToken.None);

        Assert.Equal(4, service.ParsedStatementCount);
    }

    [Fact]
    public async Task Cached_result_is_moved_to_the_new_statement_position()
    {
        var service = new LanguageService();
        const string bad = "SELECT * FROM WHERE x = 1;\n";
        await service.AnalyzeAsync(bad, CancellationToken.None);
        var doc = "SELECT 1 FROM dual;\n" + bad;

        var diagnostics = await service.AnalyzeAsync(doc, CancellationToken.None);

        Assert.Equal(doc.IndexOf("WHERE", StringComparison.Ordinal), Assert.Single(diagnostics).Range.Start);
    }

    [Fact]
    public async Task Analysis_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new LanguageService().AnalyzeAsync("SELECT 1 FROM dual", cts.Token));
    }

    [Fact]
    public async Task Analysis_runs_on_a_background_thread()
    {
        int caller = Environment.CurrentManagedThreadId;
        int? worker = null;
        var service = new LanguageService();

        // 시험용 동기화 문맥이 없어도 Task.Run 이 스레드 풀로 보낸다: 결과 Task 가 호출 스레드에서 끝나지 않았는지 본다.
        var task = service.AnalyzeAsync("SELECT 1 FROM dual", CancellationToken.None);
        await task.ContinueWith(_ => worker = Environment.CurrentManagedThreadId, TaskScheduler.Default);

        Assert.NotNull(worker);
        Assert.True(Thread.CurrentThread.IsThreadPoolThread || caller != worker);
    }

    [Fact]
    public void Server_compile_errors_map_to_document_ranges()
    {
        const string doc = "-- header\nCREATE OR REPLACE PROCEDURE p IS\nBEGIN\n  x := 1;\nEND;\n";
        int start = doc.IndexOf("CREATE", StringComparison.Ordinal);

        var diagnostics = PlSqlAnalyzer.FromServerErrors(doc, start,
            [new CompileError(3, 3, "PLS-00201: identifier 'X' must be declared", false), new CompileError(1, 1, "w", true)]);

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(new Oranda.Core.Text.TextRange(doc.IndexOf("x :=", StringComparison.Ordinal), 1), diagnostics[0].Range);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
        Assert.Equal(new Oranda.Core.Text.TextRange(start, "CREATE".Length), diagnostics[1].Range);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostics[1].Severity);
    }

    [Fact]
    public async Task Warm_up_task_runs_in_idle_phase_off_the_start_path()
    {
        var task = new PlSqlWarmUpTask();

        await task.RunAsync(CancellationToken.None);

        Assert.Equal(StartupPhase.Idle, task.Phase);
        Assert.True(PlSqlAnalyzer.IsWarm);
        Assert.NotNull(typeof(PlSqlWarmUpTask).GetCustomAttributes(typeof(OrandaServiceAttribute), false).SingleOrDefault());
    }

    [Trait("Category", "Perf")]
    [Fact]
    public async Task Measure_parse_times_without_asserting()
    {
        // 값만 출력한다(성능 시험 보류). Release 기준 목표: 워밍업 뒤 바뀐 문장 1개 재분석이 300 ms 디바운스 뒤 수 ms.
        var sw = Stopwatch.StartNew();
        PlSqlAnalyzer.WarmUp();
        output.WriteLine($"warm-up(이미 데워졌으면 0에 가깝다): {sw.Elapsed.TotalMilliseconds:0.0} ms");

        var service = new LanguageService();
        var doc = string.Join("\n", Enumerable.Range(0, 200).Select(i =>
            $"SELECT e.empno, e.ename FROM emp e WHERE e.deptno = {i} AND e.sal > :min ORDER BY 1;"));
        sw.Restart();
        await service.AnalyzeAsync(doc, CancellationToken.None);
        output.WriteLine($"200 문장 첫 분석: {sw.Elapsed.TotalMilliseconds:0.0} ms");

        sw.Restart();
        await service.AnalyzeAsync(doc.Replace("= 100 ", "= 1000 ", StringComparison.Ordinal), CancellationToken.None);
        output.WriteLine($"문장 1개 바꾼 뒤 재분석: {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
    }
}
