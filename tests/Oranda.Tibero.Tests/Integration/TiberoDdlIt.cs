using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// DDL 추출(<c>GetDdlAsync</c>)을 실제 서버로 확인한다.
/// 읽기: 접속 계정이 볼 수 있는 SYS 패키지 하나의 명세 DDL(<c>meta.source</c> 길), 접속 계정의 표 하나의 DDL(<c>meta.ddl</c> PSM 블록·OUT CLOB 길, 끝 표시 켬·끔).
/// 쓰기(<c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만): 마지막 열 이름이 한글인 ORT_ 표 1개 + ORT_ 프로시저 1개를 만들고 DDL 을 뽑아 지운 뒤 그 글로 다시 만든다.
/// 접속 값(계정·DB·호스트)은 단언 메시지·시험 출력에 넣지 않는다: 참·거짓만 보고, 계정 이름이 실릴 수 있는 예외는 형식 이름만 남겨 다시 던진다(<see cref="QuietAsync"/>).
/// 제공자는 내부 (CatalogRunner) 생성자로 만든다(객체 목록 캐시와 상관없는 시험).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoDdlIt
{
    private static readonly string Table = TiberoItSessions.ObjectName("T3DDLT");
    private static readonly string Procedure = TiberoItSessions.ObjectName("T3DDLP");

    private static TiberoMetadataProvider NewProvider() => new(TiberoCatalogQuery.RunAsync);

    [TiberoItFact]
    public async Task A_visible_sys_package_spec_comes_back_as_a_qualified_create_or_replace()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var name = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT name FROM (SELECT DISTINCT name FROM all_source WHERE owner = 'SYS' AND type = 'PACKAGE' ORDER BY name) WHERE ROWNUM = 1"));
        if (name is null)
        {
            return; // 이 계정에 보이는 SYS 패키지 소스가 없다
        }

        var provider = NewProvider();
        var obj = new DbObjectRef("SYS", name, "PACKAGE");

        var withSlash = await provider.GetDdlAsync(session, obj, new DdlOptions(), CancellationToken.None);
        var bare = await provider.GetDdlAsync(session, obj, new DdlOptions { SqlTerminator = false }, CancellationToken.None);

        var head = "CREATE OR REPLACE PACKAGE " + TiberoDdlText.Qualified("SYS", name);
        var qualified = withSlash.StartsWith(head, StringComparison.Ordinal) && bare.StartsWith(head, StringComparison.Ordinal);
        Assert.True(qualified, "DDL 의 머리가 CREATE OR REPLACE PACKAGE \"SYS\".\"이름\" 이 아니다");
        Assert.True(withSlash.EndsWith("\n/", StringComparison.Ordinal), "끝 표시를 켠 DDL 이 새 줄 / 로 끝나지 않는다");
        Assert.False(bare.EndsWith('/'), "끝 표시를 끈 DDL 이 / 로 끝난다");
    }

    /// <summary>쓰기 없이 meta.ddl(PSM 블록, OUT CLOB, :terminator 두 번)을 서버에 보낸다. 접속 계정에 표가 없으면 아무것도 하지 않는다.</summary>
    [TiberoItFact]
    public async Task A_table_of_the_login_schema_comes_back_from_the_metadata_block_with_and_without_the_terminator()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var name = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT table_name FROM (SELECT table_name FROM user_tables WHERE table_name NOT LIKE 'BIN$%' ORDER BY table_name) WHERE ROWNUM = 1"));
        if (name is null)
        {
            return; // 접속 계정에 표가 없다
        }

        var provider = NewProvider();
        var obj = new DbObjectRef(it.Env.User.ToUpperInvariant(), name, "TABLE");

        var withSemicolon = await QuietAsync("GetDdlAsync(TABLE)", () => provider.GetDdlAsync(session, obj, new DdlOptions(), CancellationToken.None));
        var bare = await QuietAsync("GetDdlAsync(TABLE, no terminator)", () => provider.GetDdlAsync(session, obj, new DdlOptions { SqlTerminator = false }, CancellationToken.None));

        Assert.True(bare.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase), "테이블 DDL 이 CREATE TABLE 로 시작하지 않는다");
        Assert.True(withSemicolon.EndsWith(';') && !bare.EndsWith(';'), "끝 표시 ; 가 켬·끔을 따르지 않는다");
    }

    [TiberoWriteFact]
    public async Task A_table_and_a_procedure_rebuild_from_their_ddl()
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
        var provider = NewProvider();
        await DropAsync(session); // 지난 실행이 끊겨 남은 것
        // 마지막 열 이름이 한글이다: meta.ddl 이 DDL 끝 글자를 잘라 보는 자리(바이트·글자 길이 차이)를 지난다.
        await ExecAsync(session, $"CREATE TABLE {Table} (A NUMBER NOT NULL, B VARCHAR2(20), \"비고\" VARCHAR2(100))");
        await ExecAsync(session, $"CREATE PROCEDURE {Procedure} (P IN NUMBER) IS\nBEGIN\n  NULL;\nEND;");
        try
        {
            var plain = new DdlOptions { SqlTerminator = false };
            var tableRef = new DbObjectRef(owner, Table, "TABLE");
            var procRef = new DbObjectRef(owner, Procedure, "PROCEDURE");
            var tableDdl = await QuietAsync("GetDdlAsync(TABLE)", () => provider.GetDdlAsync(session, tableRef, plain, CancellationToken.None));
            var procDdl = await QuietAsync("GetDdlAsync(PROCEDURE)", () => provider.GetDdlAsync(session, procRef, plain, CancellationToken.None));
            var tableWithTerminator = await QuietAsync("GetDdlAsync(TABLE, terminator)", () => provider.GetDdlAsync(session, tableRef, new DdlOptions(), CancellationToken.None));
            var procWithTerminator = await QuietAsync("GetDdlAsync(PROCEDURE, terminator)", () => provider.GetDdlAsync(session, procRef, new DdlOptions(), CancellationToken.None));

            var tableHead = tableDdl.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) && !tableDdl.EndsWith(';');
            Assert.True(tableHead, "테이블 DDL 이 CREATE TABLE 로 시작하지 않거나 ; 로 끝난다");
            Assert.True(tableDdl.Contains("\"비고\"", StringComparison.Ordinal), "테이블 DDL 에 한글 열 이름이 없다");
            Assert.True(tableWithTerminator.EndsWith(';'), "끝 표시를 켠 테이블 DDL 이 ; 로 끝나지 않는다");
            var procHead = procDdl.StartsWith("CREATE OR REPLACE PROCEDURE " + TiberoDdlText.Qualified(owner, Procedure), StringComparison.Ordinal);
            Assert.True(procHead, "프로시저 DDL 의 머리가 CREATE OR REPLACE PROCEDURE \"계정\".\"이름\" 이 아니다");
            Assert.True(procWithTerminator.EndsWith("\n/", StringComparison.Ordinal), "끝 표시를 켠 프로시저 DDL 이 새 줄 / 로 끝나지 않는다");

            await ExecAsync(session, $"DROP TABLE {Table}");
            await ExecAsync(session, $"DROP PROCEDURE {Procedure}");
            await QuietAsync("CREATE TABLE from DDL", async () => { await ExecAsync(session, tableDdl); return true; });
            await QuietAsync("CREATE PROCEDURE from DDL", async () => { await ExecAsync(session, procDdl); return true; });

            Assert.Equal(1, await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'"));
            var status = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
                $"SELECT status FROM user_objects WHERE object_name = '{Procedure}' AND object_type = 'PROCEDURE'"));
            Assert.Equal("VALID", status);
        }
        finally
        {
            await DropAsync(session);
        }
    }

    /// <summary>
    /// 예외 메시지에는 <c>소유자.이름</c>(접속 계정)·DDL 글이 실릴 수 있다. 시험 로그에 남지 않게 형식 이름과 서버 오류 번호만 담아 다시 던진다.
    /// </summary>
    private static async Task<T> QuietAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
#pragma warning disable CA1031 // 시험 실패로 바꿔 던진다(메시지에서 접속 계정을 뺀다).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var code = Oranda.Tibero.Execution.TiberoErrorClassifier.ToDbError(ex, null)?.Code;
            throw new Xunit.Sdk.XunitException(what + " failed: " + ex.GetType().Name + (code is null ? string.Empty : " " + code));
        }
    }

    private static async Task DropAsync(TiberoSession session)
    {
        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{Procedure}' AND object_type = 'PROCEDURE'") > 0)
        {
            await ExecAsync(session, $"DROP PROCEDURE {Procedure}");
        }

        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{Table}'") > 0)
        {
            await ExecAsync(session, $"DROP TABLE {Table}");
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
