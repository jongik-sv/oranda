using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.Metadata;

// 소유: TSK-08-01. 소스·컴파일 오류·충돌 확인(last_ddl_time)·실행 엔진 경유 컴파일.

[Trait("Category", "Integration")]
public sealed class PlSqlSourceTests : IClassFixture<OracleFreeFixture>, IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly string _owner = "PLS_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<ISession> _sessions = [];

    public PlSqlSourceTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        var u = _owner;
        await using var conn = await OpenSystemAsync();
        string[] ddl =
        [
            $"CREATE USER {u} NO AUTHENTICATION QUOTA UNLIMITED ON USERS",
            $"CREATE PACKAGE {u}.pk AS\n  PROCEDURE a;\nEND pk;",
            $"CREATE PACKAGE BODY {u}.pk AS\n  PROCEDURE a IS\n  BEGIN\n    NULL;\n  END a;\nEND pk;",
            // 일부러 틀린 프로시저: 4번째 줄 안의 없는 테이블·열 참조(컴파일은 되지만 INVALID)
            $"CREATE PROCEDURE {u}.bad AS\n  v NUMBER;\nBEGIN\n  SELECT x INTO v FROM no_such_table_here;\nEND;",
            $"CREATE PROCEDURE {u}.good AS BEGIN NULL; END;",
        ];
        foreach (var sql in ddl)
        {
            await ExecAsync(conn, sql);
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        await using var conn = await OpenSystemAsync();
        await ExecAsync(conn, $"DROP USER {_owner} CASCADE");
    }

    [Fact]
    public async Task Source_of_spec_and_body_are_separate_and_start_after_create_or_replace()
    {
        var provider = NewProvider();
        var s = await OpenAsync();

        var spec = await provider.GetSourceAsync(s, new DbObjectRef(_owner, "PK", "PACKAGE"), default);
        var body = await provider.GetSourceAsync(s, new DbObjectRef(_owner, "PK", "PACKAGE BODY"), default);

        Assert.Matches(@"^PACKAGE\s+pk AS", spec); // 서버는 소유자 이름을 공백으로 바꿔 저장한다
        Assert.Contains("PROCEDURE a;", spec);
        Assert.DoesNotContain("BEGIN", spec);
        Assert.Matches(@"^PACKAGE BODY\s+pk AS", body);
        Assert.Contains("NULL;", body);
        Assert.Equal(6, body.TrimEnd().Split('\n').Length);
    }

    [Fact]
    public async Task Missing_source_raises_error_instead_of_empty_text()
    {
        var provider = NewProvider();
        var s = await OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetSourceAsync(s, new DbObjectRef(_owner, "NO_SUCH", "PACKAGE BODY"), default));
    }

    [Fact]
    public async Task Compile_errors_give_line_column_and_message_and_valid_object_has_none()
    {
        var provider = NewProvider();
        var s = await OpenAsync();

        var errors = await provider.GetCompileErrorsAsync(s, new DbObjectRef(_owner, "BAD", "PROCEDURE"), default);
        var none = await provider.GetCompileErrorsAsync(s, new DbObjectRef(_owner, "GOOD", "PROCEDURE"), default);

        Assert.NotEmpty(errors);
        var first = errors[0];
        Assert.Equal(4, errors.First(e => e.Message.Contains("no_such_table_here", StringComparison.OrdinalIgnoreCase) || e.Line == 4).Line);
        Assert.True(first.Column >= 1);
        Assert.Contains("PL", first.Message, StringComparison.Ordinal);
        Assert.False(first.IsWarning);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Body_edited_through_execution_service_compiles_and_error_line_matches_the_source_line()
    {
        var provider = NewProvider();
        var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500);
        var s = await OpenAsync();
        var body = new DbObjectRef(_owner, "PK", "PACKAGE BODY");

        // 편집기가 하듯: 서버 소스 → 소유자를 다시 적은 CREATE OR REPLACE 글자.
        var source = await provider.GetSourceAsync(s, body, default);
        var original = "CREATE OR REPLACE " + System.Text.RegularExpressions.Regex.Replace(source.TrimEnd(), @"^PACKAGE BODY\s+", $"PACKAGE BODY \"{_owner}\".");
        var text = original.Replace("NULL;", "NULL NULL;", StringComparison.Ordinal);
        var result = await exec.ExecuteAsync(s, new SqlStatement(text, default, StatementKind.Ddl) { Verb = "CREATE" },
            new Dictionary<string, BindValue>(), null, default);

        // 컴파일 오류가 있어도 문장은 서버에 받아들여지고, 오류는 all_errors 로 읽는다.
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var errors = await provider.GetCompileErrorsAsync(s, body, default);
        Assert.Contains(errors, e => e.Line == 4 && !e.IsWarning);

        var ok = await exec.ExecuteAsync(s, new SqlStatement(original, default, StatementKind.Ddl) { Verb = "CREATE" },
            new Dictionary<string, BindValue>(), null, default);
        Assert.Equal(ExecutionStatus.Succeeded, ok.Status);
        Assert.Empty(await provider.GetCompileErrorsAsync(s, body, default));
    }

    [Fact]
    public async Task Last_ddl_time_changes_when_another_session_replaces_the_source()
    {
        var provider = NewProvider();
        var s = await OpenAsync();
        var target = new DbObjectRef(_owner, "GOOD", "PROCEDURE");

        var before = await LastDdlAsync(provider, s, target);
        await Task.Delay(1500); // last_ddl_time 은 초 단위
        await using (var other = await OpenSystemAsync())
        {
            await ExecAsync(other, $"CREATE OR REPLACE PROCEDURE {_owner}.good AS BEGIN NULL; /* 다른 사람이 고침 */ END;");
        }

        var after = await LastDdlAsync(provider, s, target);
        var source = await provider.GetSourceAsync(s, target, default);

        Assert.NotEqual(before, after);
        Assert.True(after > before);
        Assert.Contains("다른 사람이 고침", source);
    }

    private static async Task<DateTime> LastDdlAsync(OracleMetadataProvider provider, ISession s, DbObjectRef target)
    {
        var detail = await provider.GetObjectDetailAsync(s, target, default);
        var row = detail.Sections.Single(x => x.Title == "일반").Rows.Single(r => (string?)r[0] == "마지막 DDL");
        return Assert.IsType<DateTime>(row[1]);
    }

    private static OracleMetadataProvider NewProvider()
        => new(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan);

    private async Task<ISession> OpenAsync()
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(_db.SystemPassword);
        var session = await manager.OpenAsync(new ConnectionProfile("t-pls", "system", _db.DataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private async Task<OracleConnection> OpenSystemAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
