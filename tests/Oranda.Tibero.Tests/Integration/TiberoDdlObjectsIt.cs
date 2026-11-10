using System.Runtime.ExceptionServices;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Language.Lexing;
using Oranda.Oracle.Schema;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 사전 뷰로 만드는 DDL(T3 D2b: 뷰·시퀀스·시노님·인덱스·트리거 상태·표 종속 객체)을 실제 서버로 확인한다.
/// 읽기: 새 카탈로그 <c>meta.ddl_*</c> 가 이 서버에서 파싱되는지(없는 이름으로 0행), 질의 글이 있는 SYS 뷰 하나·PUBLIC 시노님 하나의 DDL.
/// 쓰기(<c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만, ORT_ 이름만). 객체마다 시험을 나눈다(시험 계정에 CREATE VIEW 등이 없을 수 있다).
/// <list type="bullet">
/// <item>표: 기본키·유일 키·이름 있는/없는 검사 제약·NOT NULL 열·자기 참조 외래키(ON DELETE CASCADE)·표/열 주석·일반 인덱스·내림차순 인덱스·PUBLIC 권한·
/// 사용 안 함 트리거. 열 순서 바꾸기 CTAS 길과 같게 ORT_ 백업으로 복사 → 원본 지움 → <c>CREATE TABLE … AS SELECT * FROM 백업</c>(제약 없음) →
/// <c>DependentPart(plain, full)</c> 를 문장으로 나눠 실행 → 제약·인덱스·주석·권한·트리거가 정확히 한 번씩 있는지 센다. 따로 종속 객체 없는 표 DDL 만으로 표를 다시 만든다.</item>
/// <item>뷰·시퀀스(START WITH 5)·시노님: 각자 제 객체만 만들고(뷰·시노님은 제 ORT_ 표 위에) DDL 을 뽑아 지운 뒤 다시 만든다.
/// 만드는 권한이 없으면(TBR-17004, <see cref="DbErrorKind.InsufficientPrivileges"/>) 시험 출력에 「권한 없음」 만 쓰고 끝낸다.</item>
/// </list>
/// 재지 않은 사실(Tibero GET_DDL 표 글이 제약·인덱스·주석을 담는지, 제약 인덱스 이름, 시퀀스 LAST_NUMBER 뜻)은 단언 메시지의 참·거짓으로만 남긴다.
/// 접속 값(계정·DB·호스트)은 단언 메시지·출력에 넣지 않는다: 글 비교는 참·거짓으로 하고, 예외는 형식 이름·오류 번호만 남긴다(<see cref="QuietAsync"/>).
/// 시험마다 시작할 때 남은 제 객체를 지우고, 끝나면 제 객체만 정리한다(정리 실패가 본문 실패를 가리지 않는다).
/// </summary>
[Trait("Category", "Integration")]
[Collection("TiberoPublicGrant")] // #117 TBR-12033
public class TiberoDdlObjectsIt(ITestOutputHelper output)
{
    private static readonly string Table = TiberoItSessions.ObjectName("T3D2BT");
    private static readonly string Backup = TiberoItSessions.ObjectName("T3D2BT_BK");
    private static readonly string Pk = TiberoItSessions.ObjectName("T3D2BT_PK");
    private static readonly string Uk = TiberoItSessions.ObjectName("T3D2BT_UK");
    private static readonly string Ck = TiberoItSessions.ObjectName("T3D2BT_CK");
    private static readonly string Fk = TiberoItSessions.ObjectName("T3D2BT_FK");
    private static readonly string Index = TiberoItSessions.ObjectName("T3D2BT_IX");
    private static readonly string DescIndex = TiberoItSessions.ObjectName("T3D2BT_IXD");
    private static readonly string Trigger = TiberoItSessions.ObjectName("T3D2BT_TRG");
    private static readonly string View = TiberoItSessions.ObjectName("T3D2BV");
    private static readonly string ViewBase = TiberoItSessions.ObjectName("T3D2BV_T");
    private static readonly string Sequence = TiberoItSessions.ObjectName("T3D2BSQ");
    private static readonly string Synonym = TiberoItSessions.ObjectName("T3D2BSY");
    private static readonly string SynonymBase = TiberoItSessions.ObjectName("T3D2BSY_T");

    /// <summary>시험마다 지우는 제 객체(뒤 의존 순서, 표는 PURGE).</summary>
    private static readonly (string Name, string Type, string Sql)[] TableObjects =
    [
        (Trigger, "TRIGGER", $"DROP TRIGGER {Trigger}"),
        (Table, "TABLE", $"DROP TABLE {Table} PURGE"),
        (Backup, "TABLE", $"DROP TABLE {Backup} PURGE"),
    ];

    private static readonly (string Name, string Type, string Sql)[] ViewObjects =
        [(View, "VIEW", $"DROP VIEW {View}"), (ViewBase, "TABLE", $"DROP TABLE {ViewBase} PURGE")];

    private static readonly (string Name, string Type, string Sql)[] SequenceObjects = [(Sequence, "SEQUENCE", $"DROP SEQUENCE {Sequence}")];

    private static readonly (string Name, string Type, string Sql)[] SynonymObjects =
        [(Synonym, "SYNONYM", $"DROP SYNONYM {Synonym}"), (SynonymBase, "TABLE", $"DROP TABLE {SynonymBase} PURGE")];

    private static readonly string[] NewCatalogIds =
        ["meta.ddl_view", "meta.ddl_sequence", "meta.ddl_index", "meta.ddl_table_indexes", "meta.ddl_constraints", "meta.ddl_trigger"];

    private static TiberoMetadataProvider NewProvider() => new(TiberoCatalogQuery.RunAsync);

    [TiberoItFact]
    public async Task New_ddl_catalog_queries_parse_on_this_server()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var binds = new Dictionary<string, object?>(StringComparer.Ordinal) { ["owner"] = "SYS", ["name"] = "ORT_NO_SUCH_OBJECT" };
        foreach (var id in NewCatalogIds)
        {
            var rows = await QuietAsync(id, () => TiberoCatalogQuery.RunAsync(session, id, binds, CancellationToken.None));
            Assert.Empty(rows.Rows);
        }
    }

    [TiberoItFact]
    public async Task A_visible_sys_view_and_a_public_synonym_come_back_in_oracle_shape()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var provider = NewProvider();
        // 질의 글(TEXT)이 있는 뷰만 고른다. *_VIEWS 열은 실측표에 없어 이 질의가 TEXT 열을 함께 드러낸다.
        var view = await QuietAsync("pick SYS view", async () => TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT view_name FROM (SELECT view_name FROM all_views WHERE owner = 'SYS' AND text IS NOT NULL ORDER BY view_name) WHERE ROWNUM = 1")));
        if (view is not null)
        {
            var ddl = await QuietAsync("GetDdlAsync(VIEW)", () => provider.GetDdlAsync(session, new DbObjectRef("SYS", view, "VIEW"), new DdlOptions(), CancellationToken.None));
            Assert.StartsWith("CREATE OR REPLACE FORCE VIEW " + TiberoDdlText.Qualified("SYS", view), ddl, StringComparison.Ordinal);
            Assert.EndsWith(";", ddl, StringComparison.Ordinal);
        }

        var synonym = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT synonym_name FROM (SELECT synonym_name FROM all_synonyms WHERE owner = 'PUBLIC' ORDER BY synonym_name) WHERE ROWNUM = 1"));
        if (synonym is not null)
        {
            var ddl = await QuietAsync("GetDdlAsync(SYNONYM)", () => provider.GetDdlAsync(session, new DbObjectRef("PUBLIC", synonym, "SYNONYM"), new DdlOptions(), CancellationToken.None));
            Assert.StartsWith("CREATE OR REPLACE PUBLIC SYNONYM " + TiberoDdlAssembler.Quote(synonym) + " FOR ", ddl, StringComparison.Ordinal);
        }
    }

    [TiberoWriteFact]
    public Task Table_dependent_ddl_rebuilds_a_ctas_copy_and_the_plain_table_ddl_rebuilds_the_table() =>
        WriteAsync(TableObjects, async (session, owner) => { await TableAsync(session, owner); return true; });

    [TiberoWriteFact]
    public Task View_ddl_rebuilds_the_view() => WriteAsync(ViewObjects, ViewAsync);

    [TiberoWriteFact]
    public Task Sequence_ddl_rebuilds_the_sequence_starting_where_it_was() => WriteAsync(SequenceObjects, SequenceAsync);

    [TiberoWriteFact]
    public Task Synonym_ddl_rebuilds_the_synonym() => WriteAsync(SynonymObjects, SynonymAsync);

    /// <summary>
    /// 쓰기 시험 틀: 남은 제 객체를 지우고 본문을 돌린 뒤 제 객체만 정리한다. 본문 실패는 잡아 두었다가 정리 후 그대로 다시 던진다(정리 실패가 가리지 않게).
    /// 본문이 false 면 만드는 권한이 없다는 뜻이라 「권한 없음」 만 쓰고 끝낸다.
    /// </summary>
    private async Task WriteAsync((string Name, string Type, string Sql)[] objects, Func<TiberoSession, string, Task<bool>> body)
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
        var leftovers = await DropAsync(session, objects); // 지난 실행이 끊겨 남은 것
        Assert.True(leftovers.Count == 0, "지난 실행이 남긴 ORT_ 객체를 지우지 못했다: " + string.Join(", ", leftovers));

        Exception? failure = null;
        var ran = true;
        try
        {
            ran = await body(session, it.Env.User.ToUpperInvariant());
        }
#pragma warning disable CA1031 // 본문 실패를 잡아 두고 정리한 뒤 그대로 다시 던진다(정리 실패가 본문 실패를 가리지 않게).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        var cleanup = await DropAsync(session, objects);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (!ran)
        {
            output.WriteLine("권한 없음");
        }

        Assert.True(cleanup.Count == 0, "ORT_ 객체 정리 실패: " + string.Join(", ", cleanup));
    }

    /// <summary>객체를 만든다. 권한이 없으면(TBR-17004·InsufficientPrivileges) false, 다른 오류는 시험 실패(형식 이름·오류 번호만).</summary>
    private static async Task<bool> CreateOrNoPrivilegeAsync(TiberoSession session, string what, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
            return true;
        }
#pragma warning disable CA1031 // 권한 없음만 걸러 내고 나머지는 접속 값 없는 시험 실패로 바꿔 던진다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var error = Oranda.Tibero.Execution.TiberoErrorClassifier.ToDbError(ex, null);
            if (error?.Kind == DbErrorKind.InsufficientPrivileges || error?.Code == "TBR-17004")
            {
                return false;
            }

            throw new Xunit.Sdk.XunitException(what + " failed: " + Describe(ex));
        }
    }

    private static async Task<bool> ViewAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "view base table", $"CREATE TABLE {ViewBase} (ID NUMBER, NAME VARCHAR2(20))");
        if (!await CreateOrNoPrivilegeAsync(session, "create view", $"CREATE VIEW {View} AS SELECT ID, NAME FROM {ViewBase}"))
        {
            return false;
        }

        var ddl = await DdlAsync(NewProvider(), session, owner, View, "VIEW");
        Assert.True(ddl.StartsWith("CREATE OR REPLACE FORCE VIEW " + Q(owner, View) + " (\"ID\", \"NAME\") AS", StringComparison.Ordinal), "뷰 DDL 의 머리가 Oracle 모양이 아니다");
        await ExecQuietAsync(session, "drop view", $"DROP VIEW {View}");
        await RunScriptAsync(session, "VIEW", ddl);
        await AssertValidAsync(session, (ok, message) => Assert.True(ok, message), (View, "VIEW"));
        return true;
    }

    private static async Task<bool> SequenceAsync(TiberoSession session, string owner)
    {
        if (!await CreateOrNoPrivilegeAsync(session, "create sequence", $"CREATE SEQUENCE {Sequence} START WITH 5 INCREMENT BY 2 CACHE 10"))
        {
            return false;
        }

        var ddl = await DdlAsync(NewProvider(), session, owner, Sequence, "SEQUENCE");
        var startWith5 = ddl.Contains(" START WITH 5 ", StringComparison.Ordinal);
        Assert.True(ddl.StartsWith("CREATE SEQUENCE " + Q(owner, Sequence) + " ", StringComparison.Ordinal) && ddl.Contains(" INCREMENT BY 2 ", StringComparison.Ordinal),
            $"시퀀스 DDL 이 Oracle 모양이 아니다 | start_with_5={startWith5}");
        Assert.True(startWith5, "만든 뒤 쓰지 않은 시퀀스(START WITH 5)의 DDL 이 START WITH 5 가 아니다(LAST_NUMBER 뜻) | start_with_5=False");
        await ExecQuietAsync(session, "drop sequence", $"DROP SEQUENCE {Sequence}");
        await RunScriptAsync(session, "SEQUENCE", ddl);
        await AssertValidAsync(session, (ok, message) => Assert.True(ok, message), (Sequence, "SEQUENCE"));
        return true;
    }

    private static async Task<bool> SynonymAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "synonym base table", $"CREATE TABLE {SynonymBase} (ID NUMBER)");
        if (!await CreateOrNoPrivilegeAsync(session, "create synonym", $"CREATE SYNONYM {Synonym} FOR {SynonymBase}"))
        {
            return false;
        }

        var ddl = await DdlAsync(NewProvider(), session, owner, Synonym, "SYNONYM");
        Assert.True(string.Equals(ddl, $"CREATE OR REPLACE SYNONYM {Q(owner, Synonym)} FOR {Q(owner, SynonymBase)};", StringComparison.Ordinal), "시노님 DDL 이 Oracle 모양이 아니다");
        await ExecQuietAsync(session, "drop synonym", $"DROP SYNONYM {Synonym}");
        await RunScriptAsync(session, "SYNONYM", ddl);
        await AssertValidAsync(session, (ok, message) => Assert.True(ok, message), (Synonym, "SYNONYM"));
        return true;
    }

    private static async Task TableAsync(TiberoSession session, string owner)
    {
        var provider = NewProvider();
        await CreateTableObjectsAsync(session);

        var tableRef = new DbObjectRef(owner, Table, "TABLE");
        var plain = await QuietAsync("GetDdlAsync(TABLE)", () => provider.GetDdlAsync(session, tableRef, new DdlOptions(), CancellationToken.None));
        var full = await QuietAsync("GetDdlAsync(TABLE, dependents)",
            () => provider.GetDdlAsync(session, tableRef, new DdlOptions { IncludeDependentObjects = true }, CancellationToken.None));
        var indexDdl = await DdlAsync(provider, session, owner, Index, "INDEX");
        var descIndexDdl = await DdlAsync(provider, session, owner, DescIndex, "INDEX");
        var triggerDdl = await DdlAsync(provider, session, owner, Trigger, "TRIGGER");
        var dependents = ColumnReorderScripts.DependentPart(plain, full);

        // 재지 않은 사실(참·거짓만, 단언 메시지에 붙인다).
        var facts = string.Join(' ',
            $"pk_inline={plain.Contains(Pk, StringComparison.Ordinal)}",
            $"uk_inline={plain.Contains(Uk, StringComparison.Ordinal)}",
            $"ck_inline={plain.Contains(Ck, StringComparison.Ordinal)}",
            $"fk_inline={plain.Contains(Fk, StringComparison.Ordinal)}",
            $"index_inline={plain.Contains(Index + "\"", StringComparison.Ordinal)}",
            $"comment_inline={plain.Contains("COMMENT ON", StringComparison.OrdinalIgnoreCase)}",
            $"pk_index_named_like_constraint={await CountAsync(session, $"SELECT COUNT(*) FROM user_indexes WHERE table_name = '{Table}' AND index_name = '{Pk}'") > 0}",
            $"desc_index_has_desc={descIndexDdl.Contains(" DESC)", StringComparison.Ordinal)}",
            $"refused={dependents.Contains("-- ", StringComparison.Ordinal)}");
        void Expect(bool ok, string message) => Assert.True(ok, message + " | " + facts);

        Expect(full.StartsWith(plain, StringComparison.Ordinal) && dependents.Length > 0, "종속 DDL 이 테이블 DDL 뒤에 붙지 않았다");
        Expect(!dependents.Contains("-- ", StringComparison.Ordinal), "종속 DDL 에 만들지 못한 객체의 주석 줄이 있다");
        Expect(!dependents.Contains("IS NOT NULL", StringComparison.OrdinalIgnoreCase), "NOT NULL 검사가 종속 DDL 에 들어갔다");
        Expect(Occurrences(dependents, " ADD CONSTRAINT ") == 5, "종속 DDL 의 제약이 기본키·유일 키·검사 둘·외래키 다섯 개가 아니다");
        Expect(dependents.Contains("ADD CONSTRAINT " + Q(Pk) + " PRIMARY KEY (\"ID\")", StringComparison.Ordinal), "기본키 문장이 없다");
        Expect(dependents.Contains("ADD CONSTRAINT " + Q(Uk) + " UNIQUE (\"CODE\")", StringComparison.Ordinal), "유일 키 문장이 없다");
        Expect(dependents.Contains("ADD CONSTRAINT " + Q(Ck) + " CHECK (", StringComparison.Ordinal), "검사 제약 문장이 없다");
        Expect(dependents.Contains("ADD CONSTRAINT " + Q(Fk) + " FOREIGN KEY (\"PARENT_ID\") REFERENCES " + Q(owner, Table) + " (\"ID\") ON DELETE CASCADE",
            StringComparison.Ordinal), "자기 참조 외래키(ON DELETE CASCADE) 문장이 없다");
        Expect(string.Equals(indexDdl, $"CREATE INDEX {Q(owner, Index)} ON {Q(owner, Table)} (\"NAME\");", StringComparison.Ordinal), "인덱스 DDL 이 Oracle 모양이 아니다");
        Expect(dependents.Contains(indexDdl, StringComparison.Ordinal) && dependents.Contains(descIndexDdl, StringComparison.Ordinal), "인덱스가 종속 DDL 에 없다");
        Expect(descIndexDdl.Contains(" DESC)", StringComparison.Ordinal), "내림차순 인덱스 DDL 에 DESC 가 없다");
        Expect(triggerDdl.EndsWith("\n/\nALTER TRIGGER " + Q(owner, Trigger) + " DISABLE;", StringComparison.Ordinal), "트리거 DDL 이 ALTER TRIGGER … DISABLE; 로 끝나지 않는다");
        Expect(dependents.Contains(triggerDdl, StringComparison.Ordinal), "트리거가 종속 DDL 에 없다");
        Expect(dependents.Contains("COMMENT ON TABLE " + Q(owner, Table), StringComparison.Ordinal)
            && dependents.Contains("COMMENT ON COLUMN " + Q(owner, Table) + ".\"NAME\"", StringComparison.Ordinal), "테이블·열 주석이 종속 DDL 에 없다");
        Expect(dependents.Contains("GRANT SELECT ON " + Q(owner, Table) + " TO PUBLIC;", StringComparison.Ordinal), "PUBLIC 권한이 종속 DDL 에 없다");

        // 1) 열 순서 바꾸기 CTAS 길: 제약 없는 복사본에 종속 부분만 다시 실행한다.
        var indexCount = await CountAsync(session, $"SELECT COUNT(*) FROM user_indexes WHERE table_name = '{Table}'");
        await ExecQuietAsync(session, "backup", $"CREATE TABLE {Backup} AS SELECT * FROM {Table}");
        await ExecQuietAsync(session, "drop original", $"DROP TABLE {Table} PURGE");
        await ExecQuietAsync(session, "ctas copy", $"CREATE TABLE {Table} AS SELECT * FROM {Backup}");
        await RunScriptAsync(session, "dependents", dependents);

        Assert.Equal(2, await CountAsync(session, $"SELECT COUNT(*) FROM {Table}"));
        foreach (var (type, name) in new[] { ("P", Pk), ("U", Uk), ("R", Fk), ("C", Ck) })
        {
            Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{Table}' AND constraint_name = '{name}' AND constraint_type = '{type}'") == 1,
                $"다시 만든 제약({type})이 정확히 하나가 아니다");
        }

        Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{Table}' AND constraint_type IN ('P', 'U', 'R')") == 3,
            "기본키·유일 키·외래키가 정확히 하나씩이 아니다");
        Expect(await CountAsync(session,
                $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{Table}' AND constraint_type = 'C' AND search_condition NOT LIKE '%IS NOT NULL'") == 2,
            "NOT NULL 이 아닌 검사 제약(이름 있는 것·없는 것)이 정확히 둘이 아니다");
        Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE constraint_name = '{Fk}' AND delete_rule = 'CASCADE'") == 1, "외래키의 ON DELETE CASCADE 가 없다");
        Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_indexes WHERE table_name = '{Table}'") == indexCount, "다시 만든 테이블의 인덱스 수가 원래와 다르다");
        foreach (var name in new[] { Index, DescIndex })
        {
            Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_indexes WHERE table_name = '{Table}' AND index_name = '{name}'") == 1, "다시 만든 인덱스가 정확히 하나가 아니다");
        }

        Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_tab_comments WHERE table_name = '{Table}' AND comments = '시험 테이블'") == 1, "테이블 주석이 없다");
        Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_col_comments WHERE table_name = '{Table}' AND column_name = 'NAME' AND comments = '이름'") == 1, "열 주석이 없다");
        Expect(await CountAsync(session, $"SELECT COUNT(*) FROM all_tab_privs WHERE owner = USER AND table_name = '{Table}' AND grantee = 'PUBLIC' AND privilege = 'SELECT'") == 1,
            "PUBLIC SELECT 권한이 정확히 하나가 아니다");
        Expect(await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE table_name = '{Table}'") == 1
            && await CountAsync(session, $"SELECT COUNT(*) FROM user_triggers WHERE trigger_name = '{Trigger}' AND status = 'DISABLED'") == 1,
            "트리거가 하나가 아니거나 DISABLED 가 아니다");
        await AssertValidAsync(session, Expect, (Table, "TABLE"), (Index, "INDEX"), (DescIndex, "INDEX"), (Trigger, "TRIGGER"));

        // 2) 종속 객체 없는 표 DDL 만으로 표를 다시 만든다.
        var dropped = await DropAsync(session, TableObjects);
        Expect(dropped.Count == 0, "다시 만들기 전에 지우지 못했다: " + string.Join(", ", dropped));
        await RunScriptAsync(session, "plain table", plain);
        await AssertValidAsync(session, Expect, (Table, "TABLE"));
    }

    private static async Task CreateTableObjectsAsync(TiberoSession session)
    {
        string[] statements =
        [
            $"CREATE TABLE {Table} (ID NUMBER NOT NULL, NAME VARCHAR2(20) NOT NULL, CODE VARCHAR2(10), PARENT_ID NUMBER, " +
            $"CONSTRAINT {Pk} PRIMARY KEY (ID), CONSTRAINT {Uk} UNIQUE (CODE), CONSTRAINT {Ck} CHECK (ID > 0), CHECK (PARENT_ID <> 0), " +
            $"CONSTRAINT {Fk} FOREIGN KEY (PARENT_ID) REFERENCES {Table} (ID) ON DELETE CASCADE)",
            $"COMMENT ON TABLE {Table} IS '시험 테이블'",
            $"COMMENT ON COLUMN {Table}.NAME IS '이름'",
            $"CREATE INDEX {Index} ON {Table} (NAME)",
            $"CREATE INDEX {DescIndex} ON {Table} (PARENT_ID DESC)",
            $"GRANT SELECT ON {Table} TO PUBLIC",
            $"INSERT INTO {Table} (ID, NAME, CODE, PARENT_ID) VALUES (1, 'a', 'c1', NULL)",
            $"INSERT INTO {Table} (ID, NAME, CODE, PARENT_ID) VALUES (2, 'b', 'c2', 1)",
            "COMMIT",
            $"CREATE TRIGGER {Trigger} BEFORE INSERT ON {Table} FOR EACH ROW\nBEGIN\n  NULL;\nEND;",
            $"ALTER TRIGGER {Trigger} DISABLE",
        ];
        for (var i = 0; i < statements.Length; i++)
        {
            await ExecQuietAsync(session, "setup statement " + i, statements[i]);
        }
    }

    private static Task<string> DdlAsync(TiberoMetadataProvider provider, TiberoSession session, string owner, string name, string type) =>
        QuietAsync($"GetDdlAsync({type})", () => provider.GetDdlAsync(session, new DbObjectRef(owner, name, type), new DdlOptions(), CancellationToken.None));

    private static async Task AssertValidAsync(TiberoSession session, Action<bool, string> check, params (string Name, string Type)[] objects)
    {
        foreach (var (name, type) in objects)
        {
            var valid = await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{name}' AND object_type = '{type}' AND status = 'VALID'");
            check(valid == 1, $"다시 만든 {type} 이 없거나 VALID 가 아니다");
        }
    }

    /// <summary>DDL 글을 편집기와 같은 분리기로 문장으로 나눠 차례로 보낸다(SQL 은 끝의 <c>;</c> 없이, PL/SQL 은 <c>END;</c> 까지).</summary>
    private static async Task RunScriptAsync(TiberoSession session, string what, string ddl)
    {
        var statements = StatementSplitter.Split(ddl, SplitOptions.Default)
            .Where(s => s.Kind != StatementKind.SqlPlus && !string.IsNullOrWhiteSpace(s.Text))
            .ToList();
        Assert.NotEmpty(statements);
        for (var i = 0; i < statements.Count; i++)
        {
            await ExecQuietAsync(session, $"rebuild {what} statement {i}", statements[i].Text);
        }
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Q(string name) => TiberoDdlAssembler.Quote(name);

    private static string Q(string owner, string name) => TiberoDdlText.Qualified(owner, name);

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
            throw new Xunit.Sdk.XunitException(what + " failed: " + Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var code = Oranda.Tibero.Execution.TiberoErrorClassifier.ToDbError(ex, null)?.Code;
        return ex.GetType().Name + (code is null ? string.Empty : " " + code);
    }

    private static async Task ExecQuietAsync(TiberoSession session, string what, string sql) =>
        await QuietAsync(what, async () => { await ExecAsync(session, sql); return true; });

    /// <summary>
    /// 시험 하나의 ORT_ 객체(<paramref name="objects"/>, 뒤 의존 순서, 표는 PURGE)가 남아 있으면 지운다. 하나가 실패해도 나머지를 지우고, 실패는
    /// (유형·오류 번호만) 모아 돌려준다 — 부르는 쪽이 본문 실패를 가리지 않게 판단한다.
    /// </summary>
    private static async Task<List<string>> DropAsync(TiberoSession session, (string Name, string Type, string Sql)[] objects)
    {
        var failures = new List<string>();
        foreach (var (name, type, sql) in objects)
        {
            try
            {
                if (await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{name}' AND object_type = '{type}'") > 0)
                {
                    await ExecAsync(session, sql);
                }
            }
#pragma warning disable CA1031 // 정리 단계: 나머지도 지우고 실패는 모아서 알린다.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add($"drop {type} {Describe(ex)}");
            }
        }

        return failures;
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
