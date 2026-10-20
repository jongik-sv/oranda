using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language.PlSql;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Execution;

// 소유: TSK-08-02. PL/SQL 실행 대화상자 — 메타데이터(all_arguments) → 호출 블록 생성 → 실행 엔진 → OUT 값·커서·DBMS_OUTPUT.

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class PlsqlCallDialogTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly string _owner = "PRN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly ExecBus _bus = new();
    private OracleSessionManager _manager = null!;

    public PlsqlCallDialogTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        var u = _owner;
        await using var conn = await OpenSystemAsync();
        string[] ddl =
        [
            $"CREATE USER {u} NO AUTHENTICATION QUOTA UNLIMITED ON USERS",
            $"CREATE TABLE {u}.emp (id NUMBER PRIMARY KEY, name VARCHAR2(30), sal NUMBER)",
            $"INSERT INTO {u}.emp VALUES (1, 'KING', 1000)",
            $"INSERT INTO {u}.emp VALUES (2, 'BLAKE', 2000)",
            $"INSERT INTO {u}.emp VALUES (3, 'CLARK', 3000)",
            "COMMIT",
            $@"CREATE PACKAGE {u}.emp_pkg AS
  PROCEDURE raise_sal(p_id IN NUMBER, p_pct IN NUMBER DEFAULT 10, p_msg OUT VARCHAR2, p_new_sal IN OUT NUMBER, p_rc OUT SYS_REFCURSOR);
  FUNCTION is_rich(p_sal IN NUMBER, p_flag IN BOOLEAN DEFAULT FALSE) RETURN BOOLEAN;
  PROCEDURE flip(p_flag IN OUT BOOLEAN);
  PROCEDURE hire(p_name IN VARCHAR2);
  PROCEDURE hire(p_id IN NUMBER, p_name IN VARCHAR2 DEFAULT 'NONAME');
  PROCEDURE say(p_text IN VARCHAR2, p_when IN DATE DEFAULT NULL, p_out OUT DATE);
END emp_pkg;",
            $@"CREATE PACKAGE BODY {u}.emp_pkg AS
  PROCEDURE raise_sal(p_id IN NUMBER, p_pct IN NUMBER DEFAULT 10, p_msg OUT VARCHAR2, p_new_sal IN OUT NUMBER, p_rc OUT SYS_REFCURSOR) IS
  BEGIN
    UPDATE {u}.emp SET sal = sal * (1 + p_pct / 100) WHERE id = p_id RETURNING sal INTO p_new_sal;
    p_msg := 'raised ' || p_id || ' by ' || p_pct;
    DBMS_OUTPUT.PUT_LINE('raise_sal ' || p_id);
    OPEN p_rc FOR SELECT id, name, sal FROM {u}.emp WHERE id <= p_id ORDER BY id;
  END;
  FUNCTION is_rich(p_sal IN NUMBER, p_flag IN BOOLEAN DEFAULT FALSE) RETURN BOOLEAN IS
  BEGIN
    RETURN p_sal > 1500 OR p_flag;
  END;
  PROCEDURE flip(p_flag IN OUT BOOLEAN) IS
  BEGIN
    p_flag := NOT p_flag;
  END;
  PROCEDURE hire(p_name IN VARCHAR2) IS
  BEGIN
    DBMS_OUTPUT.PUT_LINE('hire by name ' || p_name);
  END;
  PROCEDURE hire(p_id IN NUMBER, p_name IN VARCHAR2 DEFAULT 'NONAME') IS
  BEGIN
    DBMS_OUTPUT.PUT_LINE('hire by id ' || p_id || ' ' || p_name);
  END;
  PROCEDURE say(p_text IN VARCHAR2, p_when IN DATE DEFAULT NULL, p_out OUT DATE) IS
  BEGIN
    p_out := NVL(p_when, DATE '2000-01-01');
    DBMS_OUTPUT.PUT_LINE(p_text);
  END;
END emp_pkg;",
            $"CREATE FUNCTION {u}.double_it(p_n IN NUMBER) RETURN NUMBER IS BEGIN RETURN p_n * 2; END;",
        ];
        foreach (var sql in ddl)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        await using var conn = await OpenSystemAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP USER {_owner} CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task In_out_inout_and_ref_cursor_procedure_returns_all_results()
    {
        var s = await OpenAsync();
        var (target, proc) = await FindAsync(s, "RAISE_SAL");

        // P_PCT 는 기본값(10)을 쓰도록 뺀다.
        var block = CallBlockBuilder.Build(target, proc,
        [
            new CallInput("P_ID", "2"),
            new CallInput("P_PCT", "", UseDefault: true),
            new CallInput("P_NEW_SAL", "0"),
        ]);
        Assert.True(block.CanRun, string.Join("; ", block.Problems));
        Assert.DoesNotContain("P_PCT =>", block.Text, StringComparison.Ordinal);

        var r = await Service().ExecuteAsync(s, block.Statement, block.Binds, null, default);

        Assert.True(r.Status == ExecutionStatus.Succeeded, r.Error?.Message);
        Assert.Equal("raised 2 by 10", r.OutValues["P_P_MSG"]);
        Assert.Equal(2200m, r.OutValues["P_P_NEW_SAL"]);
        Assert.Contains("raise_sal 2", r.DbmsOutput);
        await using var cursor = Assert.IsAssignableFrom<IResultSet>(r.OutValues["P_P_RC"]);
        var page = await cursor.GetPageAsync(0, default);
        Assert.Equal(2, page.RowCount);
        Assert.Equal(["ID", "NAME", "SAL"], cursor.Columns.Select(c => c.Name));
        Assert.Equal("KING", page.Columns[1][0]);
        await s.RollbackAsync(default);
    }

    [Fact]
    public async Task Explicit_value_overrides_default()
    {
        var s = await OpenAsync();
        var (target, proc) = await FindAsync(s, "RAISE_SAL");
        var block = CallBlockBuilder.Build(target, proc,
            [new CallInput("P_ID", "1"), new CallInput("P_PCT", "50"), new CallInput("P_NEW_SAL", "")]);

        var r = await Service().ExecuteAsync(s, block.Statement, block.Binds, null, default);

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal("raised 1 by 50", r.OutValues["P_P_MSG"]);
        Assert.Equal(1500m, r.OutValues["P_P_NEW_SAL"]);
        await ((IResultSet)r.OutValues["P_P_RC"]!).DisposeAsync();
        await s.RollbackAsync(default);
    }

    [Fact]
    public async Task Boolean_function_and_in_out_boolean_work_through_number_binds()
    {
        var s = await OpenAsync();
        var (rich, richProc) = await FindAsync(s, "IS_RICH");
        var block = CallBlockBuilder.Build(rich, richProc, [new CallInput("P_SAL", "2000"), new CallInput("P_FLAG", "", UseDefault: true)]);
        var r = await Service().ExecuteAsync(s, block.Statement, block.Binds, null, default);

        Assert.True(r.Status == ExecutionStatus.Succeeded, r.Error?.Message);
        Assert.Equal("TRUE", CallBlock.FormatOut(block.Outputs.Single(), r.OutValues["RET"]));

        var poor = CallBlockBuilder.Build(rich, richProc, [new CallInput("P_SAL", "10"), new CallInput("P_FLAG", "false")]);
        var r2 = await Service().ExecuteAsync(s, poor.Statement, poor.Binds, null, default);
        Assert.Equal("FALSE", CallBlock.FormatOut(poor.Outputs.Single(), r2.OutValues["RET"]));

        var (flip, flipProc) = await FindAsync(s, "FLIP");
        var f = CallBlockBuilder.Build(flip, flipProc, [new CallInput("P_FLAG", "true")]);
        var r3 = await Service().ExecuteAsync(s, f.Statement, f.Binds, null, default);
        Assert.True(r3.Status == ExecutionStatus.Succeeded, r3.Error?.Message);
        Assert.Equal("FALSE", CallBlock.FormatOut(f.Outputs.Single(), r3.OutValues["P_P_FLAG"]));
    }

    [Fact]
    public async Task Overloads_are_chosen_by_argument_names_and_types()
    {
        var s = await OpenAsync();
        var procs = await Metadata().GetProceduresAsync(s, new DbObjectRef(_owner, "EMP_PKG", "PACKAGE"), default);
        var hires = procs.Where(p => p.Name == "HIRE").ToList();
        Assert.Equal(2, hires.Count);
        var target = new CallTarget(_owner, "EMP_PKG", "HIRE");

        var byName = hires.Single(p => p.Arguments.Any(a => a.Name == "P_NAME" && a.Position == 1));
        var byId = hires.Single(p => p.Arguments.Any(a => a.Name == "P_ID"));

        var a = CallBlockBuilder.Build(target, byName, [new CallInput("P_NAME", "Ann")]);
        var ra = await Service().ExecuteAsync(s, a.Statement, a.Binds, null, default);
        var b = CallBlockBuilder.Build(target, byId, [new CallInput("P_ID", "7"), new CallInput("P_NAME", "", UseDefault: true)]);
        var rb = await Service().ExecuteAsync(s, b.Statement, b.Binds, null, default);

        Assert.True(ra.Status == ExecutionStatus.Succeeded, ra.Error?.Message);
        Assert.Contains("hire by name Ann", ra.DbmsOutput);
        Assert.True(rb.Status == ExecutionStatus.Succeeded, rb.Error?.Message);
        Assert.Contains("hire by id 7 NONAME", rb.DbmsOutput);
    }

    [Fact]
    public async Task Date_argument_and_standalone_function_work()
    {
        var s = await OpenAsync();
        var (say, sayProc) = await FindAsync(s, "SAY");
        var block = CallBlockBuilder.Build(say, sayProc, [new CallInput("P_TEXT", "hi"), new CallInput("P_WHEN", "2026-10-20 13:45:00")]);
        var r = await Service().ExecuteAsync(s, block.Statement, block.Binds, null, default);

        Assert.True(r.Status == ExecutionStatus.Succeeded, r.Error?.Message);
        Assert.Equal(new DateTime(2026, 10, 20, 13, 45, 0), r.OutValues["P_P_OUT"]);
        Assert.Equal("2026-10-20 13:45:00", CallBlock.FormatOut(block.Outputs.Single(), r.OutValues["P_P_OUT"]));

        var procs = await Metadata().GetProceduresAsync(s, new DbObjectRef(_owner, "DOUBLE_IT", "FUNCTION"), default);
        var fn = Assert.Single(procs);
        Assert.True(fn.IsFunction);
        var call = CallBlockBuilder.Build(new CallTarget(_owner, null, "DOUBLE_IT"), fn, [new CallInput("P_N", "21")]);
        var r2 = await Service().ExecuteAsync(s, call.Statement, call.Binds, null, default);
        Assert.Equal(42m, r2.OutValues["RET"]);
    }

    [Fact]
    public async Task Server_error_is_reported_as_failed_result()
    {
        var s = await OpenAsync();
        var (target, proc) = await FindAsync(s, "RAISE_SAL");
        var block = CallBlockBuilder.Build(target, proc, [new CallInput("P_ID", "1"), new CallInput("P_PCT", "x'"), new CallInput("P_NEW_SAL", "")]);

        Assert.False(block.CanRun); // 숫자가 아닌 값은 서버에 보내지 않는다
        var bad = CallBlockBuilder.Build(new CallTarget(_owner, "EMP_PKG", "NO_SUCH"), proc, [new CallInput("P_ID", "1"), new CallInput("P_NEW_SAL", "")]);
        var r = await Service().ExecuteAsync(s, bad.Statement, bad.Binds, null, default);

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.NotNull(r.Error);
    }

    private OracleExecutionService Service() => new(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);

    private static OracleMetadataProvider Metadata() => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<(CallTarget Target, ProcedureInfo Procedure)> FindAsync(ISession s, string member)
    {
        var procs = await Metadata().GetProceduresAsync(s, new DbObjectRef(_owner, "EMP_PKG", "PACKAGE"), default);
        return (new CallTarget(_owner, "EMP_PKG", member), procs.Single(p => p.Name == member));
    }

    private async Task<ISession> OpenAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);
        return await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private async Task<OracleConnection> OpenSystemAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }
}
