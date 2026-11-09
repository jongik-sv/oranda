using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>세션 관리자·세션을 실제 서버로 확인한다(읽기 문장과 세션 설정만). 서버·드라이버가 없으면 아무것도 하지 않는다.</summary>
[Trait("Category", "Integration")]
public class TiberoSessionIt
{
    [TiberoItFact]
    public async Task Open_fills_server_info_identity_and_publishes()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var opened = new List<SessionOpened>();
        it.Events.Subscribe<SessionOpened>(opened, opened.Add);

        var session = await it.OpenAsync(SessionPurpose.Monitor);

        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal(Dbms.Tibero, session.Server.Dbms);
        Assert.True(session.Server.SessionId > 0);
        Assert.True(TiberoItSessions.Same(TiberoServerProbeKey(it), session.DbKey));
        Assert.Contains(session, it.Manager.Sessions);
        Assert.Equal(session.Id, Assert.Single(opened).SessionId);

        // V$SESSION 의 MODULE·ACTION·CLIENT_IDENTIFIER(실측: SET_MODULE + SET_IDENTIFIER 블록이 반영된다).
        var row = await QueryRowAsync(session, "SELECT MODULE, ACTION, CLIENT_IDENTIFIER FROM V$SESSION WHERE SID = " + session.Server.SessionId.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(TiberoSessionManager.ModuleName, row[0]);
        Assert.Equal("Monitor", row[1]);
        Assert.Equal("oranda:" + session.Id.ToString("N")[..8], row[2]);
    }

    [TiberoItFact]
    public async Task Session_is_in_manual_commit_mode_and_sql_commit_keeps_it()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();

        // 쓰기 없이 트랜잭션을 연다(tb-exec 실측 방법): PSM 블록 안 LOCAL_TRANSACTION_ID(TRUE). 자동 커밋이면 다음 문장에서 NULL.
        Assert.NotNull(await OpenTransactionAndReadIdAsync(session));

        await session.CommitAsync(CancellationToken.None); // SQL COMMIT → 트랜잭션 끝, 수동 모드는 그대로
        Assert.Null((await QueryRowAsync(session, "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL"))[0]);
        Assert.NotNull(await OpenTransactionAndReadIdAsync(session));

        await session.RollbackAsync(CancellationToken.None);
        Assert.NotNull(await OpenTransactionAndReadIdAsync(session));
    }

    [TiberoItFact]
    public async Task Nls_is_applied_and_unsupported_items_are_skipped()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var profile = it.Profile() with { Nls = new NlsSettings { DateFormat = "YYYY-MM-DD", Language = "AMERICAN", Territory = "AMERICA" } };
        var session = await it.OpenAsync(profile: profile);

        var row = await QueryRowAsync(session, "SELECT VALUE FROM NLS_SESSION_PARAMETERS WHERE PARAMETER = 'NLS_DATE_FORMAT'");
        Assert.Equal("YYYY-MM-DD", row[0]);
    }

    [TiberoItFact]
    public async Task Close_ends_the_server_session_and_keep_alive_runs()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var watcher = await it.OpenAsync(SessionPurpose.Monitor);
        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var sid = session.Server.SessionId.ToString(CultureInfo.InvariantCulture);

        await session.KeepAliveTickAsync();
        Assert.Equal(SessionState.Idle, session.State);

        await session.DisposeAsync();
        Assert.Equal(SessionState.Closed, session.State);
        Assert.DoesNotContain(session, it.Manager.Sessions);

        // Pooling=false: 닫으면 서버 세션이 끝난다(실측 1.5초 안). 잠깐 기다리며 확인한다.
        string? count = null;
        for (var i = 0; i < 20 && count != "0"; i++)
        {
            await Task.Delay(250);
            count = (await QueryRowAsync(watcher, "SELECT COUNT(*) FROM V$SESSION WHERE SID = " + sid + " AND CLIENT_IDENTIFIER = 'oranda:" + session.Id.ToString("N")[..8] + "'"))[0];
        }

        Assert.Equal("0", count);
    }

    /// <summary>쓰기 IT(D3: ORANDA_IT_TIBERO_WRITE=1, ORT_ 표 하나): DML 은 커밋 전에는 다른 세션에 안 보이고, 커밋 없이 닫으면 롤백된다.</summary>
    [TiberoWriteFact]
    public async Task Write_dml_stays_uncommitted_and_close_rolls_it_back()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var table = TiberoItSessions.ObjectName("TBS_CLOSE");
        var owner = await it.OpenAsync();
        var other = await it.OpenAsync(SessionPurpose.Monitor);
        await DropIfExistsAsync(owner, table);
        await ExecAsync(owner, $"CREATE TABLE {table} (ID NUMBER)");
        try
        {
            var writer = await it.OpenAsync(SessionPurpose.Dedicated);
            await ExecAsync(writer, $"INSERT INTO {table} (ID) VALUES (1)");
            Assert.Equal("0", (await QueryRowAsync(other, $"SELECT COUNT(*) FROM {table}"))[0]); // 자동 커밋 아님

            await ExecAsync(writer, $"UPDATE {table} SET ID = 2");
            await writer.DisposeAsync(); // 커밋 없이 닫기
            Assert.Equal("0", (await QueryRowAsync(other, $"SELECT COUNT(*) FROM {table}"))[0]); // 롤백됨

            await ExecAsync(owner, $"INSERT INTO {table} (ID) VALUES (3)");
            await owner.CommitAsync(CancellationToken.None);
            Assert.Equal("1", (await QueryRowAsync(other, $"SELECT COUNT(*) FROM {table}"))[0]);

            await ExecAsync(owner, $"INSERT INTO {table} (ID) VALUES (4)");
            await owner.RollbackAsync(CancellationToken.None);
            Assert.Equal("1", (await QueryRowAsync(other, $"SELECT COUNT(*) FROM {table}"))[0]);
        }
        finally
        {
            await DropIfExistsAsync(owner, table);
        }
    }

    private static async Task DropIfExistsAsync(TiberoSession session, string table)
    {
        var exists = (await QueryRowAsync(session, $"SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = '{table}'"))[0];
        if (exists != "0")
        {
            await ExecAsync(session, $"DROP TABLE {table}");
        }
    }

    private static string TiberoServerProbeKey(TiberoItSessions it) => $"{it.Env.DbName}/-/{it.Env.User}".ToUpperInvariant();

    private static async Task<string?> OpenTransactionAndReadIdAsync(TiberoSession session)
    {
        await ExecAsync(session, "DECLARE t VARCHAR2(100); BEGIN t := DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE); END;");
        return (await QueryRowAsync(session, "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL"))[0];
    }

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private static async Task<string?[]> QueryRowAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            return row;
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
