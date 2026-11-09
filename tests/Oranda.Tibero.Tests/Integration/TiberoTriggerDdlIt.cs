using System.Globalization;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 트리거 DDL 조립기(<see cref="TiberoTriggerDdl"/>)를 실제 서버로 확인한다: ORT_ 표 1개 + 그 표의 ORT_ 트리거 1개를 시험 계정 스키마에 만들고
/// <c>user_source</c> 글로 조립한 DDL 로 지웠다 다시 만들어 트리거가 그대로 동작하는지 본다. <c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돈다.
/// 시작 때 지난 실행이 남긴 것을 지우고, 정리는 finally 에서 트리거 → 표 차례(조정자 결정).
/// <c>user_triggers</c> 로 조립하는 길은 없다(DESCRIPTION·TRIGGER_BODY 열 없음, win144 #30 실측).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTriggerDdlIt
{
    private static readonly string Table = TiberoItSessions.ObjectName("T3DDL");
    private static readonly string Trigger = TiberoItSessions.ObjectName("T3DDL_TRG");

    [TiberoWriteFact]
    public async Task Source_text_rebuilds_a_working_trigger()
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

        var session = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();
        await CreateAsync(session);
        try
        {
            var lines = await LinesAsync(session, $"SELECT text FROM user_source WHERE name = '{Trigger}' AND type = 'TRIGGER' ORDER BY line");
            var ddl = TiberoTriggerDdl.FromSource(owner, Trigger, lines);

            Assert.True(ddl is not null, "user_source 의 트리거 글을 조립하지 못했다(줄 수 " + lines.Count.ToString(CultureInfo.InvariantCulture) + ")");
            // 머리에 계정 이름이 있으므로 참·거짓만 본다(Assert.StartsWith 는 실패 때 두 값을 찍는다).
            var qualifiedHead = ddl.StartsWith("CREATE OR REPLACE TRIGGER " + TiberoTriggerDdl.Qualified(owner, Trigger), StringComparison.Ordinal);
            Assert.True(qualifiedHead, "조립한 DDL 의 머리가 CREATE OR REPLACE TRIGGER \"계정\".\"트리거\" 가 아니다");
            await RebuildAndCheckAsync(session, ddl);
        }
        finally
        {
            await DropAsync(session);
        }
    }

    private static async Task CreateAsync(TiberoSession session)
    {
        await DropAsync(session); // 지난 실행이 끊겨 남은 것
        await ExecAsync(session, $"CREATE TABLE {Table} (A NUMBER, B NUMBER)");
        await ExecAsync(session,
            $"CREATE TRIGGER {Trigger}\nBEFORE INSERT ON {Table}\nFOR EACH ROW\nWHEN (new.a > 0)\nBEGIN\n  :new.b := :new.a * 2;\nEND;");
    }

    /// <summary>원래 트리거를 지우고 조립한 DDL 로 다시 만든 뒤, 넣은 행에 트리거가 값을 채우는지(WHEN 조건 포함) 보고 되돌린다.</summary>
    private static async Task RebuildAndCheckAsync(TiberoSession session, string ddl)
    {
        await ExecAsync(session, $"DROP TRIGGER {Trigger}");
        await ExecAsync(session, ddl);

        var status = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session, $"SELECT status FROM user_objects WHERE object_name = '{Trigger}' AND object_type = 'TRIGGER'"));
        Assert.Equal("VALID", status);

        await ExecAsync(session, $"INSERT INTO {Table} (A) VALUES (3)");
        await ExecAsync(session, $"INSERT INTO {Table} (A) VALUES (-1)");
        var fired = TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, $"SELECT B FROM {Table} WHERE A = 3"));
        var skipped = await TiberoSessionQuery.ScalarAsync(session, $"SELECT B FROM {Table} WHERE A = -1");
        await ExecAsync(session, TiberoSession.RollbackSql);

        Assert.Equal(6, fired);
        Assert.Null(skipped); // WHEN (new.a > 0) 이 그대로 옮겨졌다
    }

    private static async Task DropAsync(TiberoSession session)
    {
        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE trigger_name = '{Trigger}'") > 0)
        {
            await ExecAsync(session, $"DROP TRIGGER {Trigger}");
        }

        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'") > 0)
        {
            await ExecAsync(session, $"DROP TABLE {Table}");
        }
    }

    private static async Task<long> CountAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0;

    private static async Task<List<string?>> LinesAsync(TiberoSession session, string sql)
    {
        var table = await TiberoSessionQuery.RunAsync(session, sql);
        return [.. table.Rows.Select(r => TiberoSessionQuery.Str(r[0]))];
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
}
