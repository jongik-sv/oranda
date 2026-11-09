using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Schema;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 메타데이터 제공자를 실제 서버로 읽는다. 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 접속 계정 이름이 든 값은 <see cref="TiberoItSessions.Same"/> 로 참·거짓만 본다(Assert.Equal·Contains·All 은 실패 때 값을 찍는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoMetadataIt
{
    /// <summary>메모리 캐시·기록 버스를 붙이고 백그라운드 갱신은 끈 제공자(시험이 배경 작업에 흔들리지 않게).</summary>
    private static TiberoMetadataProvider NewProvider()
        => new(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync);

    [Fact]
    public async Task Schemas_include_the_login_schema_and_its_tables_come_back_filtered_by_type()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var user = it.Env.User.ToUpperInvariant();
        var provider = NewProvider();

        var schemas = await provider.ListSchemasAsync(session, CancellationToken.None);
        var tables = await provider.ListObjectsAsync(session, new ObjectQuery { Owner = user, ObjectTypes = ["TABLE"], Limit = 50 }, CancellationToken.None);

        var hasLogin = schemas.Any(x => TiberoItSessions.Same(user, x));
        Assert.True(hasLogin, "스키마 목록에 접속 스키마가 없다");
        Assert.True(tables.Count <= 50, "max_rows 를 넘었다");
        var onlyOwnTables = tables.All(o => TiberoItSessions.Same(user, o.Owner) && o.ObjectType == "TABLE");
        Assert.True(onlyOwnTables, "소유자·유형 거르기 밖의 객체가 왔다");
    }

    /// <summary>
    /// DUAL 은 모든 계정이 읽을 수 있는 사전 표다(PUBLIC 시노님 → SYS 의 표). 시노님 풀이·객체 목록·표 정보·상세를 한 번에 본다.
    /// 시노님 사슬은 3 단계까지 따라가고, 시노님이 없는 서버면 SYS.DUAL 을 쓴다. 목록에 없으면(이 계정에 안 보이면) 아무것도 하지 않는다.
    /// </summary>
    [Fact]
    public async Task Dual_resolves_lists_and_reads_as_a_table_with_the_general_and_column_sections()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var provider = NewProvider();

        var (owner, name) = ("PUBLIC", "DUAL");
        var resolved = false;
        for (var i = 0; i < 3; i++)
        {
            if (await provider.GetNameStepAsync(session, owner, name, CancellationToken.None) is not { IsSynonym: true, TargetName: { } next } step)
            {
                break;
            }

            (owner, name, resolved) = (step.TargetOwner ?? owner, next, true);
        }

        if (!resolved)
        {
            (owner, name) = ("SYS", "DUAL");
        }

        var listed = await provider.ListObjectsAsync(session, new ObjectQuery { Owner = owner, NamePattern = name, ObjectTypes = ["TABLE", "VIEW"] },
            CancellationToken.None);
        if (listed.Count == 0)
        {
            return;
        }

        var dual = listed[0];
        var table = await QuietAsync("GetTableAsync", () => provider.GetTableAsync(session, dual, CancellationToken.None));
        var detail = await QuietAsync("GetObjectDetailAsync", () => provider.GetObjectDetailAsync(session, dual, CancellationToken.None));

        Assert.NotEmpty(table.Columns);
        Assert.Equal("일반", detail.Sections[0].Title);
        Assert.Equal(table.Columns.Count, detail.Sections.Single(x => x.Title == "열").Rows.Count); // 표 주석이 있으면 「설명」 이 「열」 앞에 온다
        Assert.Equal(dual.ObjectType, detail.Sections[0].Rows[0][1]);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetDdlAsync(session, dual, new DdlOptions(), CancellationToken.None));
    }

    /// <summary>DBMS_OUTPUT 은 모든 계정이 쓰는 내장 패키지다: 멤버·인자 매핑(오버로드 번호 없는 Tibero 멤버 행)을 본다.</summary>
    [Fact]
    public async Task Dbms_output_members_include_put_line_with_an_in_argument()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var provider = NewProvider();
        var step = await provider.GetNameStepAsync(session, "PUBLIC", "DBMS_OUTPUT", CancellationToken.None);
        var owner = step is { IsSynonym: true, TargetOwner: { } o } ? o : "SYS";

        var members = await provider.GetProceduresAsync(session, new DbObjectRef(owner, "DBMS_OUTPUT", "PACKAGE"), CancellationToken.None);

        var putLine = members.Where(m => m.Name == "PUT_LINE").ToList();
        Assert.NotEmpty(putLine);
        Assert.DoesNotContain(putLine, m => m.IsFunction);
        if (putLine.Exists(m => m.Arguments.Count > 0))
        {
            Assert.Contains(putLine, m => m.Arguments.Any(a => a.Direction == ArgumentDirection.In));
        }
    }

    /// <summary>SYS.DBMS_SQL.PARSE 는 오버로드가 여럿인 내장 프로시저다: 멤버 행 수만큼 나오고, 인자를 오버로드별로 나눴다면 한 항목 안에 같은 위치가 두 번 없다.</summary>
    [Fact]
    public async Task Dbms_sql_parse_comes_back_as_several_overloads_without_repeated_positions()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var members = await NewProvider().GetProceduresAsync(session, new DbObjectRef("SYS", "DBMS_SQL", "PACKAGE"), CancellationToken.None);

        var parse = members.Where(m => m.Name == "PARSE").ToList();
        Assert.True(parse.Count > 1, $"PARSE 항목 수 {parse.Count}");
        if (parse.Exists(m => m.Arguments.Count > 0))
        {
            var repeated = parse.Where(m => m.Arguments.Select(a => a.Position).Distinct().Count() != m.Arguments.Count).Select(m => m.Overload).ToList();
            Assert.Empty(repeated);
        }
    }

    [Fact]
    public async Task A_test_table_reads_back_its_columns_keys_and_detail_sections()
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

        var name = TiberoItSessions.ObjectName("T3A_META");
        var session = await it.OpenAsync();
        await DropIfLeftAsync(session, name); // 지난 실행이 CREATE 와 정리 사이에 끊겼으면 남은 표
        await ExecAsync(session,
            $"CREATE TABLE {name} (ID NUMBER(9) NOT NULL, NAME VARCHAR2(20) DEFAULT 'X', CONSTRAINT {name}_PK PRIMARY KEY (ID), CONSTRAINT {name}_CK CHECK (ID > 0))");
        try
        {
            await ExecAsync(session, $"INSERT INTO {name} (ID, NAME) VALUES (1, 'A')");
            var user = it.Env.User.ToUpperInvariant();
            var provider = NewProvider();

            var listed = await provider.ListObjectsAsync(session, new ObjectQuery { Owner = user, NamePattern = name, ObjectTypes = ["TABLE"] }, CancellationToken.None);
            var found = await provider.FindObjectsByPrefixAsync(session, name, 10, CancellationToken.None);
            var table = await QuietAsync("GetTableAsync", () => provider.GetTableAsync(session, new DbObjectRef(user, name, "TABLE"), CancellationToken.None));
            var detail = await QuietAsync("GetObjectDetailAsync", () => provider.GetObjectDetailAsync(session, new DbObjectRef(user, name, "TABLE"), CancellationToken.None));
            var step = await provider.GetNameStepAsync(session, user, name, CancellationToken.None);

            var listedOwn = listed.Count == 1 && TiberoItSessions.Same(user, listed[0].Owner) && listed[0].Name == name;
            Assert.True(listedOwn, "목록에 시험 표 하나만 와야 한다");
            var foundOwn = found.Any(o => TiberoItSessions.Same(user, o.Owner) && o.Name == name);
            Assert.True(foundOwn, "접두어 찾기에 시험 표가 없다");
            Assert.Equal([("ID", 1, false), ("NAME", 2, true)], table.Columns.Select(c => (c.Name, c.Position, c.IsNullable)));
            Assert.Equal("NUMBER", table.Columns[0].DataType);
            Assert.StartsWith("VARCHAR", table.Columns[1].DataType, StringComparison.Ordinal); // Tibero 는 VARCHAR2 를 VARCHAR 로 적을 수 있다
            Assert.Equal((9, 0), (table.Columns[0].Precision, table.Columns[0].Scale));
            Assert.Equal((20, "'X'"), (table.Columns[1].Length, table.Columns[1].DefaultValue));
            Assert.Equal([(name + "_CK", ConstraintKind.Check), (name + "_PK", ConstraintKind.PrimaryKey)],
                table.Constraints.Where(c => c.Name.StartsWith(name + "_", StringComparison.Ordinal)).Select(c => (c.Name, c.Kind))
                    .OrderBy(c => c.Name, StringComparer.Ordinal));
            Assert.Equal(["ID"], Assert.Single(table.Indexes).Columns);
            Assert.Equal(["일반", "열", "제약", "인덱스"], detail.Sections.Select(x => x.Title).Take(4));
            Assert.Equal(NameStep.TableOrView, step);
        }
        finally
        {
            await ExecAsync(session, $"DROP TABLE {name}");
        }
    }

    /// <summary>
    /// 캐시한 표 상세(payload)는 객체 목록 갱신이 LAST_DDL_TIME 변화를 볼 때만 버려진다. Tibero 가 인덱스·주석·제약 DDL 에서
    /// 표의 LAST_DDL_TIME 을 올리는지 실측한다(DATE 라 초 단위 → DDL 앞에 1초 넘게 쉰다). 어느 DDL 이 안 올리는지 메시지에 참·거짓으로 찍는다.
    /// </summary>
    [Fact]
    public async Task Index_comment_and_constraint_ddl_refresh_a_cached_table()
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

        var name = TiberoItSessions.ObjectName("T3E_DDL");
        var session = await it.OpenAsync();
        await DropIfLeftAsync(session, name);
        await ExecAsync(session, $"CREATE TABLE {name} (ID NUMBER(9), V NUMBER(9))");
        try
        {
            var user = it.Env.User.ToUpperInvariant();
            var table = new DbObjectRef(user, name, "TABLE");
            var provider = NewProvider();
            await provider.ListObjectsAsync(session, new ObjectQuery(), CancellationToken.None); // 객체 목록 캐시를 채운다
            var before = await QuietAsync("GetTableAsync", () => provider.GetTableAsync(session, table, CancellationToken.None));

            async Task<TableInfo> AfterAsync(string ddl)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.2));
                await ExecAsync(session, ddl);
                await provider.RefreshAsync(session, CancellationToken.None);
                return await QuietAsync("GetTableAsync", () => provider.GetTableAsync(session, table, CancellationToken.None));
            }

            var index = (await AfterAsync($"CREATE INDEX {name}_IX ON {name} (V)")).Indexes.Any(i => i.Name == name + "_IX");
            var comment = (await AfterAsync($"COMMENT ON TABLE {name} IS 'T3E'")).Comment == "T3E";
            var constraint = (await AfterAsync($"ALTER TABLE {name} ADD CONSTRAINT {name}_CK CHECK (ID > 0)")).Constraints.Any(c => c.Name == name + "_CK");

            Assert.True(before.Indexes.Count == 0 && before.Comment is null, "처음 읽은 표에 인덱스·주석이 이미 있다");
            Assert.True(index && comment && constraint,
                $"캐시한 표가 DDL 뒤에도 그대로다(LAST_DDL_TIME 안 오름): index={index} comment={comment} constraint={constraint}");
        }
        finally
        {
            await ExecAsync(session, $"DROP TABLE {name}");
        }
    }

    /// <summary>
    /// 표를 못 찾은 예외 메시지에는 <c>소유자.이름</c>(접속 계정)이 실린다. 시험 로그에 남지 않게 형식 이름만 담아 다시 던진다.
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
            throw new Xunit.Sdk.XunitException(what + " failed: " + ex.GetType().Name);
        }
    }

    private static async Task DropIfLeftAsync(TiberoSession session, string name)
    {
        var left = await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{name}'");
        if (TiberoSessionQuery.Long(left) > 0)
        {
            await ExecAsync(session, $"DROP TABLE {name}");
        }
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
