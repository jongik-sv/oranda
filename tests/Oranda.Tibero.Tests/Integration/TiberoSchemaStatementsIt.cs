using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 객체 관리 문장(<see cref="TiberoSchemaStatements"/>)을 실제 서버로 확인한다: ORT_ 표 1개 + ORT_ 프로시저 1개를 시험 계정 스키마에 만들고
/// 컴파일 문장(→ VALID), 표를 지운 뒤 휴지통 항목의 Flashback 문장(→ 표가 돌아옴), 다시 지운 뒤 Purge 문장(→ 휴지통에서 사라짐)을 실행해 본다.
/// <c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돈다. PurgeAll 은 계정의 휴지통 전체를 비우므로 실행하지 않는다(문장 글만 단위 시험이 본다).
/// ALTER SYSTEM 류 파라미터 변경도 실행하지 않는다. 시작 때 지난 실행이 남긴 것을 지우고, 정리는 finally 에서 한다.
/// 접속 정보(계정·DB 이름)는 단정 메시지에 넣지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoSchemaStatementsIt
{
    private static readonly string Table = TiberoItSessions.ObjectName("T3STMT");
    private static readonly string Procedure = TiberoItSessions.ObjectName("T3STMT_P");

    private readonly TiberoSchemaStatements _statements = new();

    [Fact]
    public async Task Compile_flashback_and_purge_statements_work_on_the_server()
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
        await CleanupAsync(session);
        // 시험 서버는 휴지통이 기본 꺼짐이다(#28·#47 실측: DROP 뒤 항목 없음, ALTER SESSION SET RECYCLEBIN = ODP-7001, USE_RECYCLEBIN 은 됨).
        var enabled = await EnableRecycleBinAsync(session);
        Assert.True(enabled is not null, "ALTER SESSION SET USE_RECYCLEBIN 을 Y·YES·TRUE·ON 어느 값으로도 켜지 못했다");
        try
        {
            await ExecAsync(session, $"CREATE TABLE {Table} (A NUMBER)");
            await ExecAsync(session, $"INSERT INTO {Table} (A) VALUES (1)");
            await ExecAsync(session, $"CREATE OR REPLACE PROCEDURE {Procedure} IS\nBEGIN\n  NULL;\nEND;");

            await CompileProcedureAsync(session, owner);

            // 1차: 지우고 Flashback 문장으로 되살린다.
            await ExecAsync(session, $"DROP TABLE {Table}");
            var dropped = await FindRecycledAsync(session);
            Assert.True(dropped is not null, "DROP TABLE 뒤 휴지통에 항목이 없다(USE_RECYCLEBIN 을 켰는데도)");
            Assert.Equal(0, await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'"));

            await ExecAsync(session, _statements.Flashback(dropped).Text);
            Assert.Equal(1, await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'"));
            Assert.Equal(1, await CountAsync(session, $"SELECT COUNT(*) FROM {Table} WHERE A = 1"));

            // 2차: 다시 지우고 Purge 문장으로 휴지통에서 없앤다.
            await ExecAsync(session, $"DROP TABLE {Table}");
            var again = await FindRecycledAsync(session);
            Assert.True(again is not null, "두 번째 DROP TABLE 뒤 휴지통에 항목이 없다(USE_RECYCLEBIN 을 켰는데도)");

            await ExecAsync(session, _statements.PurgeItem(again).Text);
            var left = await FindRecycledAsync(session);
            Assert.True(left is null, "Purge 문장을 실행했는데도 휴지통에 표 항목이 남아 있다");
        }
        finally
        {
            await CleanupAsync(session);
        }
    }

    private async Task CompileProcedureAsync(TiberoSession session, string owner)
    {
        var statement = _statements.Compile(new DbObjectRef(owner, Procedure, "PROCEDURE"));
        Assert.True(statement is not null, "프로시저 컴파일 문장이 만들어지지 않았다");
        await ExecAsync(session, statement.Text);

        var status = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            $"SELECT status FROM user_objects WHERE object_name = '{Procedure}' AND object_type = 'PROCEDURE'"));
        Assert.Equal("VALID", status);
    }

    /// <summary>이 세션의 휴지통을 켠다. 받아들인 값(Y·YES·TRUE·ON 중 처음 된 것), 다 안 되면 null.</summary>
    private static async Task<string?> EnableRecycleBinAsync(TiberoSession session)
    {
        foreach (var value in new[] { "Y", "YES", "TRUE", "ON" })
        {
            try
            {
                await ExecAsync(session, "ALTER SESSION SET USE_RECYCLEBIN = " + value);
                return value;
            }
#pragma warning disable CA1031 // 다음 값으로 해 본다(서버가 받는 값 꼴을 실측한다).
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return null;
    }

    /// <summary>휴지통에서 이 시험 표의 항목(가장 최근 것)을 찾는다. 없으면 null.</summary>
    private static async Task<RecycleBinItem?> FindRecycledAsync(TiberoSession session)
    {
        var items = await new TiberoSchemaAdminProvider().ListRecycleBinAsync(session, CancellationToken.None);
        return items
            .Where(i => TiberoItSessions.Same(Table, i.OriginalName, ignoreCase: true) && !string.Equals(i.ObjectType, "INDEX", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.DropTime ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    /// <summary>이 시험이 만든 것만 지운다: 프로시저, 표(휴지통 거치지 않고), 지난 실행이 휴지통에 남긴 이 표의 항목.</summary>
    private async Task CleanupAsync(TiberoSession session)
    {
        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{Procedure}' AND object_type = 'PROCEDURE'") > 0)
        {
            await ExecAsync(session, $"DROP PROCEDURE {Procedure}");
        }

        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'") > 0)
        {
            await ExecAsync(session, $"DROP TABLE {Table} PURGE");
        }

        var items = await new TiberoSchemaAdminProvider().ListRecycleBinAsync(session, CancellationToken.None);
        foreach (var item in items.Where(i => TiberoItSessions.Same(Table, i.OriginalName, ignoreCase: true)))
        {
            await ExecAsync(session, _statements.PurgeItem(item).Text);
        }
    }

    private static async Task<long> CountAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0;

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
