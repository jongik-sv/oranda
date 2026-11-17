using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Schema;

// 계획 T17(설계 18.9·시안 ⑧): 열 형식 변경 자동 변환을 실제 Oracle 에서 본다 — 바꿔치기 단계 실행 뒤 정의·데이터·뷰, 「변환 안 되는 행」(26ai = VALIDATE_CONVERSION,
// 11g = PL/SQL 행 시험, 같은 건수), 참조 FK, 다시 만들기 스크립트. 상주 컨테이너 환경 변수는 TableEditIntegrationTests 와 같다(26ai 1521/FREEPDB1, 11g 1522/XE).
// 시험 계정은 실행마다 TE_xxxxxxxx 로 만들고 끝나면 지운다.
[Trait("Category", "Integration")]
public sealed class ColumnConversionIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _owner = "TE_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _password = "Te" + Guid.NewGuid().ToString("N")[..16] + "x1";
    private readonly List<ISession> _sessions = [];
    private readonly OracleColumnConversionChecks _conversions = new();
    private readonly OracleTableEditChecks _checks = new();
    private readonly OracleTableEditDialect _dialect = new();
    private bool _enabled;
    private int _major;

    public ColumnConversionIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    private DbObjectRef Ref(string name) => new(_owner, name, "TABLE");

    public async Task InitializeAsync()
    {
        _enabled = Env("HOST") is not null && Env("SERVICE") is not null && Env("ADMIN_USER") is not null && Env("ADMIN_PASSWORD") is not null;
        if (!_enabled)
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            }

            return;
        }

        await using (var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!))
        {
            await admin.OpenAsync();
            _major = int.Parse(admin.ServerVersion.Split('.')[0], CultureInfo.InvariantCulture);
            await ExecAsync(admin, $"CREATE USER {_owner} IDENTIFIED BY \"{_password}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users");
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO {_owner}");
        }

        try
        {
            await using var owner = await OwnerAsync();
            foreach (var sql in Setup())
            {
                await ExecAsync(owner, sql);
            }
        }
        catch
        {
            await DropOwnerAsync(); // InitializeAsync 가 실패하면 xUnit 은 DisposeAsync 를 부르지 않는다
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        if (_enabled)
        {
            await DropOwnerAsync();
        }
    }

    private static IEnumerable<string> Setup()
    {
        foreach (var table in new[] { "conv", "conv2" })
        {
            yield return $"""
                CREATE TABLE {table} (
                  id NUMBER CONSTRAINT pk_{table} PRIMARY KEY,
                  order_no VARCHAR2(20 CHAR) DEFAULT '0' NOT NULL,
                  code VARCHAR2(10),
                  amount NUMBER(10,2),
                  CONSTRAINT uk_{table}_01 UNIQUE (order_no, code),
                  CONSTRAINT ck_{table}_01 CHECK (id > 0 OR order_no IS NOT NULL)
                )
                """;
            yield return $"COMMENT ON COLUMN {table}.order_no IS '주문 번호'";
            yield return $"INSERT INTO {table} VALUES (1, '10', 'a', 1.5)";
            yield return $"INSERT INTO {table} VALUES (2, ' 20 ', 'b', 12345678.25)";
            yield return $"INSERT INTO {table} VALUES (3, '30', NULL, NULL)";
        }

        yield return "CREATE INDEX ix_conv_01 ON conv (order_no, id)";
        yield return "CREATE INDEX ix_conv_02 ON conv (UPPER(order_no))";
        yield return "CREATE VIEW v_conv AS SELECT id, order_no FROM conv";
        yield return "CREATE TABLE bad (id NUMBER PRIMARY KEY, v VARCHAR2(20))";
        yield return "INSERT INTO bad VALUES (1, '1')";
        yield return "INSERT INTO bad VALUES (2, 'x2')";
        yield return "INSERT INTO bad VALUES (3, '3')";
        yield return "INSERT INTO bad VALUES (4, '4.5x')";
        yield return "INSERT INTO bad VALUES (5, '12')";
        yield return "INSERT INTO bad VALUES (6, NULL)";
        yield return "CREATE TABLE codes (id NUMBER PRIMARY KEY, code VARCHAR2(10) CONSTRAINT uk_codes UNIQUE, CONSTRAINT ck_codes CHECK (code <> 'x'))";
        yield return "INSERT INTO codes VALUES (1, '1')";
        yield return "INSERT INTO codes VALUES (2, '01')";
        yield return "INSERT INTO codes VALUES (3, '2')";
        yield return "CREATE VIEW v_slow AS SELECT TO_CHAR(a.object_id) AS v FROM all_objects a, all_objects b, all_objects c";
        yield return "CREATE TABLE parent (id NUMBER PRIMARY KEY, code VARCHAR2(10) CONSTRAINT uk_parent UNIQUE)";
        yield return "CREATE TABLE child (id NUMBER, pcode VARCHAR2(10) CONSTRAINT fk_child_01 REFERENCES parent (code))";
        yield return "COMMIT";
    }

    [Fact]
    public async Task Swap_converts_text_to_number_and_restores_definition_constraints_indexes_and_view()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var original = TableDraftFactory.FromTable(await NewProvider().GetTableAsync(s, Ref("CONV"), default), _dialect);
        Assert.Contains(original.Constraints, k => k.Name == "CK_CONV_01"); // 끝이 IS NOT NULL 인 사용자 CHECK 도 읽는다(NOT NULL 제약으로 보지 않음)
        var source = original.Columns.Single(c => c.Name == "ORDER_NO");
        var target = source with { DataType = "NUMBER", Length = null, CharSemantics = null, Precision = 12, Scale = null, DefaultValue = "0" };
        var request = new ColumnConversionRequest(original, source.RowKey, target, ColumnConversionDefaults.Expression(source, target))
        {
            Dependents = await _checks.DependentsAsync(s, Ref("CONV"), default),
        };

        var failures = await _conversions.FailingRowsAsync(s, request.Table, source, target, request.Expression, default);
        Assert.Equal(0, failures.Count); // ' 20 ' 도 TO_NUMBER 로 바뀐다
        Assert.Empty(await _conversions.ReferencingForeignKeysAsync(s, request.Table, "ORDER_NO", default));

        var plan = ColumnConversionBuilder.BuildSwap(request, _dialect, s.Server);
        _output.WriteLine(plan.RemainingScript(0));
        await using (var owner = await OwnerAsync())
        {
            foreach (var statement in plan.Statements)
            {
                await ExecAsync(owner, statement.Text);
            }
        }

        var after = await NewProvider().GetTableAsync(s, Ref("CONV"), default);
        var column = after.Columns.Single(c => c.Name == "ORDER_NO");
        Assert.Equal(("NUMBER", 12, false, "0", "주문 번호"), (column.DataType, column.Precision, column.IsNullable, column.DefaultValue?.Trim(), column.Comment));
        Assert.Equal(after.Columns.Count, column.Position); // 바꿔치기는 열이 맨 뒤로 간다
        Assert.Contains(after.Constraints, c => c.Name == "UK_CONV_01" && c.Columns.SequenceEqual(["ORDER_NO", "CODE"]));
        Assert.Contains(after.Constraints, c => c.Name == "CK_CONV_01");
        Assert.Contains(after.Indexes, i => i.Name == "IX_CONV_01");
        Assert.Contains(after.Indexes, i => i.Name == "IX_CONV_02");
        Assert.Equal("VALID", await ScalarAsync($"SELECT status FROM user_objects WHERE object_name = 'V_CONV'"));
        Assert.Equal("10,20,30", await ScalarAsync("SELECT LISTAGG(TO_CHAR(order_no), ',') WITHIN GROUP (ORDER BY id) FROM conv"));

    }

    [Theory]
    [InlineData(null, 3)] // 'x2'·'4.5x' 는 숫자가 아니고 '12' 는 NUMBER(1) 를 넘친다
    [InlineData("TO_NUMBER(v) * 1", 3)] // 사용자 식 = 행 단위 시험(12.2+ 도)
    public async Task Failing_rows_are_counted_the_same_on_every_version_with_view_rows_sql(string? expression, long expected)
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var original = TableDraftFactory.FromTable(await NewProvider().GetTableAsync(s, Ref("BAD"), default), _dialect);
        var source = original.Columns.Single(c => c.Name == "V");
        var target = source with { DataType = "NUMBER", Length = null, CharSemantics = null, Precision = 1, Scale = null };
        expression ??= ColumnConversionDefaults.Expression(source, target);

        var failures = await _conversions.FailingRowsAsync(s, Ref("BAD"), source, target, expression, default);
        var notNull = await _conversions.FailingRowsAsync(s, Ref("BAD"), source, target with { IsNullable = false }, expression, default);

        Assert.Equal((expected, false), (failures.Count, failures.IsLowerBound));
        Assert.Equal(expected + 1, notNull.Count); // NULL 행도 NOT NULL 에 못 들어간다
        Assert.Equal("2,4,5", await ScalarAsync($"SELECT LISTAGG(id, ',') WITHIN GROUP (ORDER BY id) FROM ({failures.ViewRowsSql})"));
        _output.WriteLine($"{(_major >= 12 ? "12+" : "11g")} {expression}: {failures.Count}건, {failures.FirstError}\n{failures.ViewRowsSql}");
    }

    [Fact]
    public async Task Wrong_expression_fails_every_row_with_the_server_error()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var source = new ColumnDraft("c2", "V") { DataType = "VARCHAR2", Length = 20 };

        var failures = await _conversions.FailingRowsAsync(s, Ref("BAD"), source, new ColumnDraft("c2", "V") { DataType = "NUMBER" }, "TO_NUMBER(v", default);

        Assert.Equal((6L, false), (failures.Count, failures.IsLowerBound)); // 식이 틀리면 동적 SQL 이 행마다 실패 — 모든 행
        Assert.Contains("ORA-", failures.FirstError, StringComparison.Ordinal);
        Assert.Equal(6, await _checks.CountRowsAsync(s, Ref("BAD"), default)); // 세션은 그대로 쓸 수 있다
    }

    [Fact]
    public async Task Restored_constraints_are_checked_with_the_converted_values()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        string[] others = ["ID"];

        // '1' 과 '01' 은 숫자로 바꾸면 같은 값 → UK_CODES 중복 2행. CHECK (code <> 'x') 는 숫자 열에서 계산 오류(ORA-01722) → 「1건 이상」
        var duplicates = await _conversions.ConvertedDuplicateKeysAsync(s, Ref("CODES"), others, "CODE", "TO_NUMBER(code)", ["CODE"], default);
        var noDuplicates = await _conversions.ConvertedDuplicateKeysAsync(s, Ref("CODES"), others, "CODE", "code", ["CODE"], default);
        var check = await _conversions.ConvertedCheckViolationsAsync(s, Ref("CODES"), others, "CODE", "TO_NUMBER(code)", "code <> 'x'", default);
        var checkOk = await _conversions.ConvertedCheckViolationsAsync(s, Ref("CODES"), others, "CODE", "TO_NUMBER(code)", "code > 0 AND id > 0", default);

        Assert.Equal(2, duplicates.Count);
        Assert.Equal(0, noDuplicates.Count);
        Assert.True(check.IsLowerBound);
        Assert.Contains("ORA-01722", check.FirstError, StringComparison.Ordinal);
        Assert.Equal(0, checkOk.Count);
    }

    [Fact]
    public async Task Cancelling_the_row_test_cancels_the_server_command()
    {
        if (Skip())
        {
            return;
        }

        if (_major < 12)
        {
            _output.WriteLine("건너뜀: 11g 실행 중 취소는 podman 중계에서 확인할 수 없다(TableEditIntegrationTests 와 같음)");
            return;
        }

        var s = await OpenAsync();
        var source = new ColumnDraft("c1", "V") { DataType = "VARCHAR2", Length = 40 };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // 사용자 식이라 PL/SQL 행 시험(1차)이 돈다 — 블록이 ORA-01013 을 삼키면 끝까지 돌거나 「1건 이상」이 된다
        var run = _conversions.FailingRowsAsync(s, new DbObjectRef(_owner, "V_SLOW", "VIEW"), source, source with { DataType = "NUMBER", Length = null }, "TO_NUMBER(v) + 0", cts.Token);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(run, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(6, await _checks.CountRowsAsync(s, Ref("BAD"), default)); // 세션은 그대로 쓸 수 있다
    }

    [Fact]
    public async Task Referencing_foreign_keys_block_only_the_referenced_column()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();

        Assert.Equal([new ForeignKeyRef(_owner, "CHILD", "FK_CHILD_01")], await _conversions.ReferencingForeignKeysAsync(s, Ref("PARENT"), "CODE", default));
        Assert.Empty(await _conversions.ReferencingForeignKeysAsync(s, Ref("PARENT"), "ID", default));
    }

    [Fact]
    public async Task Rebuild_script_keeps_the_column_order_and_converts_the_values()
    {
        if (Skip())
        {
            return;
        }

        var s = await OpenAsync();
        var info = await NewProvider().GetTableAsync(s, Ref("CONV2"), default);
        var original = TableDraftFactory.FromTable(info, _dialect);
        var source = original.Columns.Single(c => c.Name == "ORDER_NO");
        var target = source with { DataType = "NUMBER", Length = null, CharSemantics = null, Precision = 12, Scale = null, DefaultValue = "0" };
        var request = new ColumnConversionRequest(original, source.RowKey, target, ColumnConversionDefaults.Expression(source, target));

        var script = ColumnConversionBuilder.BuildRebuild(request, info.Columns, enterprise: false, usePrimaryKey: true, dependentDdl: "");
        _output.WriteLine(script);
        await using (var owner = await OwnerAsync())
        {
            foreach (var statement in Statements(script))
            {
                await ExecAsync(owner, statement);
            }
        }

        var after = await NewProvider().GetTableAsync(s, Ref("CONV2"), default);
        Assert.Equal(["ID", "ORDER_NO", "CODE", "AMOUNT"], after.Columns.Select(c => c.Name));
        var column = after.Columns.Single(c => c.Name == "ORDER_NO");
        Assert.Equal(("NUMBER", 12, false, "0"), (column.DataType, column.Precision, column.IsNullable, column.DefaultValue?.Trim()));
        Assert.Equal("10,20,30", await ScalarAsync("SELECT LISTAGG(TO_CHAR(order_no), ',') WITHIN GROUP (ORDER BY id) FROM conv2"));
    }

    /// <summary>CTAS 다시 만들기 스크립트(PL/SQL 블록 없음)를 문장으로: 주석 줄을 빼고 <c>;</c> 줄 끝에서 자른다.</summary>
    private static IEnumerable<string> Statements(string script)
    {
        var lines = script.ReplaceLineEndings("\n").Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        foreach (var part in string.Join('\n', lines).Split(";\n"))
        {
            var text = part.Trim().TrimEnd(';');
            if (text.Length > 0)
            {
                yield return text;
            }
        }
    }

    private bool Skip()
    {
        if (_enabled)
        {
            return false;
        }

        _output.WriteLine("건너뜀: ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
        return true;
    }

    private static OracleMetadataProvider NewProvider() => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(_password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-column-conversion", _owner, DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private async Task<OracleConnection> OwnerAsync()
    {
        var conn = Connect(_owner, _password);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var conn = await OwnerAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    private async Task DropOwnerAsync()
    {
        await using var admin = Connect(Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
        await admin.OpenAsync();
        await ExecAsync(admin, $"DROP USER {_owner} CASCADE");
    }

    private static OracleConnection Connect(string user, string password) =>
        new(new ConnectionProfile("t-column-conversion", user, DataSource).BuildConnectionString(password));

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
