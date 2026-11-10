using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Sessions;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 복사 카탈로그(<c>Catalog/copy/*.sql</c>, TB3a)의 SELECT 를 실제 서버로 읽는다(읽기 전용). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 바인드는 접속 계정 스키마, 그 스키마에서 동적으로 찾은 표·시퀀스·제약, 보이는 SYS 표, 없는 이름이다. 목적은 문장이 파싱되고
/// Oracle 판과 같은 열 수를 돌려주는지 보는 것이다. 접속 값(계정·DB·호스트)은 단정 메시지에 싣지 않고 참·거짓과 id, 숫자만 싣는다.
/// </summary>
[Trait("Category", "Integration")]
public partial class TiberoCopyCatalogIt
{
    private const string NoSuchName = "ORT_NO_SUCH_COPY_OBJECT";

    /// <summary>id → 결과 열 수(Oracle 판과 같다).</summary>
    private static readonly (string Id, int Columns)[] Shapes =
    [
        ("copy.session_user", 1),
        ("copy.session_roles", 1),
        ("copy.session_privs", 1),
        ("copy.table_privs", 2),
        ("copy.enabled_triggers", 2),
        ("copy.referencing_fks", 3),
        ("copy.identity_cols", 2),
        ("copy.sequence_info", 2),
        ("copy.sequence_names", 1),
        ("copy.parameter_value", 1),
        ("copy.tablespace", 1),
        ("copy.constraint_owner", 1),
        ("copy.index_owner", 1),
        ("copy.object_exists", 1),
        ("copy.trigger_count_all", 1),
        ("copy.target_is_table", 1),
        ("copy.table_exists", 1),
        ("copy.sequence_increment", 3),
        ("copy.fk_columns", 4),
        ("copy.col_priv_count", 1),
        ("copy.other_grantor_count", 1),
        ("copy.mview_log_count", 1),
        ("copy.vpd_policy_count", 1),
    ];

    [TiberoItFact]
    public async Task Every_copy_select_parses_and_returns_the_oracle_column_count_for_non_existent_names()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var failures = new List<string>();
        foreach (var (id, columns) in Shapes)
        {
            try
            {
                var table = await RunAsync(session, id, Binds(id, NoSuchName, NoSuchName));
                if (table.Columns.Count != columns)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture, $"{id}: 열 {table.Columns.Count}개(기대 {columns})"));
                }
            }
            catch (Exception ex)
            {
                // 오류 번호만. 메시지 본문에는 접속 값(호스트·포트·계정)이 들어갈 수 있다.
                failures.Add(id + ": " + ErrorCode(ex));
            }
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    [TiberoItFact]
    public async Task Session_selects_see_the_login_user_and_roles_and_privileges()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var user = (await RunAsync(session, "copy.session_user")).Rows;
        Assert.True(user.Count == 1, "copy.session_user 가 한 행이 아니다");
        Assert.True(TiberoItSessions.Same(session.Profile.User, TiberoSessionQuery.Str(user[0][0]), ignoreCase: true), "copy.session_user 가 접속 계정과 다르다");

        var privileges = (await RunAsync(session, "copy.session_privs")).Rows;
        var roles = (await RunAsync(session, "copy.session_roles")).Rows;
        Assert.True(privileges.Count + roles.Count > 0, "시스템 권한도 롤도 없다(접속만 되는 계정이 아니다)");
    }

    [TiberoItFact]
    public async Task Own_table_is_found_by_the_existence_and_count_selects_and_the_counts_agree()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = TiberoSessionQuery.Str((await RunAsync(session, "copy.session_user")).Rows[0][0])!;
        var tableName = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT table_name FROM (SELECT table_name FROM user_tables WHERE table_name NOT LIKE 'BIN$%' ORDER BY table_name) WHERE ROWNUM = 1"));
        if (tableName is null)
        {
            return; // 이 계정에 표가 없다: 위 없는 이름 시험으로 끝낸다
        }

        Assert.Equal(1L, await CountAsync(session, "copy.table_exists", ("OWNER", owner), ("NAME", tableName)));
        Assert.Equal(0L, await CountAsync(session, "copy.table_exists", ("OWNER", owner), ("NAME", NoSuchName)));
        var isTable = await CountAsync(session, "copy.target_is_table", ("OWN", owner), ("TBL", tableName));
        Assert.True(isTable is 0 or 1, "copy.target_is_table 이 0 이나 1 이 아니다");
        // 분할 표의 TABLE PARTITION 행·같은 이름의 인덱스도 세므로 1 이상이면 된다(Core 는 > 0 만 본다).
        Assert.True(await CountAsync(session, "copy.object_exists", ("OWN", owner), ("NAME", tableName)) >= 1, "copy.object_exists 가 자기 테이블을 못 찾는다");

        var tablespace = (await RunAsync(session, "copy.tablespace", Binds("copy.tablespace", owner, tableName))).Rows;
        Assert.Single(tablespace);

        var all = await CountAsync(session, "copy.trigger_count_all", ("OWN", owner), ("TBL", tableName));
        var enabled = (await RunAsync(session, "copy.enabled_triggers", Binds("copy.enabled_triggers", owner, tableName))).Rows.Count;
        Assert.True(all >= enabled, string.Create(CultureInfo.InvariantCulture, $"켜진 트리거 {enabled}개가 전체 {all}개보다 많다"));

        foreach (var id in new[] { "copy.col_priv_count", "copy.other_grantor_count", "copy.mview_log_count", "copy.vpd_policy_count" })
        {
            var n = await CountAsync(session, id, ("OWN", owner), ("TBL", tableName));
            Assert.True(n >= 0, id + " 건수가 음수다");
        }

        // 소유자 본인이 준 권한뿐이면 다른 계정이 준 권한은 0, 그 밖의 권한 행은 table_privs 와 모순되지 않는다.
        var privileges = (await RunAsync(session, "copy.table_privs", Binds("copy.table_privs", owner, tableName))).Rows;
        var otherGrantors = await CountAsync(session, "copy.other_grantor_count", ("OWN", owner), ("TBL", tableName));
        Assert.True(otherGrantors <= privileges.Count, "다른 계정이 준 권한 수가 객체 권한 행 수보다 많다");
    }

    [TiberoItFact]
    public async Task Foreign_key_selects_pair_child_and_parent_columns_when_the_schema_has_a_foreign_key()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = TiberoSessionQuery.Str((await RunAsync(session, "copy.session_user")).Rows[0][0])!;
        var fk = (await TiberoSessionQuery.RunAsync(session,
            "SELECT constraint_name FROM (SELECT c.constraint_name FROM user_constraints c WHERE c.constraint_type = 'R' ORDER BY c.constraint_name) WHERE ROWNUM = 1")).Rows;
        if (fk.Count == 0)
        {
            return; // 외래키가 없는 스키마
        }

        var name = TiberoSessionQuery.Str(fk[0][0])!;
        var rows = (await RunAsync(session, "copy.fk_columns", ("OWNER", owner), ("NAME", name))).Rows;
        Assert.NotEmpty(rows);
        Assert.True(rows.All(r => r.Length == 4 && r.All(v => v is not null)), "copy.fk_columns 행에 빈 값이 있다");

        // 자식·부모 열 짝이 빠짐없이 맞는다: 짝 수 = 이 FK 의 열 수(부모·자식 position 기준이 다르면 짝이 빠진다).
        var fkColumnCount = TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT COUNT(*) FROM user_cons_columns WHERE constraint_name = :n", c => TiberoSessionQuery.Bind(c, "n", name)));
        Assert.True(rows.Count == fkColumnCount, string.Create(CultureInfo.InvariantCulture, $"copy.fk_columns 짝 {rows.Count}개, FK 열 {fkColumnCount}개"));

        // 이 FK 의 부모 표 쪽에서 copy.referencing_fks 가 이 제약을 돌려준다(켜진 FK 이고 자기 참조가 아니면).
        var parent = TiberoSessionQuery.Str(rows[0][3])!;
        var parentOwner = TiberoSessionQuery.Str(rows[0][2])!;
        var referencing = (await RunAsync(session, "copy.referencing_fks", ("OWN", parentOwner), ("TBL", parent))).Rows;
        var status = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT status FROM user_constraints WHERE constraint_name = :n", c => TiberoSessionQuery.Bind(c, "n", name)));
        var selfReference = await TiberoSessionQuery.ScalarAsync(session,
            "SELECT COUNT(*) FROM user_constraints WHERE constraint_name = :n AND table_name = :t", c =>
            {
                TiberoSessionQuery.Bind(c, "n", name);
                TiberoSessionQuery.Bind(c, "t", parent);
            });
        if (TiberoItSessions.Same("ENABLED", status) && TiberoSessionQuery.Long(selfReference) == 0)
        {
            Assert.True(referencing.Any(r => TiberoItSessions.Same(name, TiberoSessionQuery.Str(r[2]))), "copy.referencing_fks 가 켜진 FK 를 부모 쪽에서 돌려주지 않는다");
        }
    }

    [TiberoItFact]
    public async Task Sequence_selects_agree_when_the_schema_has_a_sequence()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = TiberoSessionQuery.Str((await RunAsync(session, "copy.session_user")).Rows[0][0])!;
        var names = (await RunAsync(session, "copy.sequence_names", ("OWN", owner))).Rows;
        if (names.Count == 0)
        {
            return; // 시퀀스가 없는 스키마
        }

        Assert.Equal(names.Select(r => TiberoSessionQuery.Str(r[0])).Order(StringComparer.Ordinal), names.Select(r => TiberoSessionQuery.Str(r[0]))); // 이름 순
        var sequence = TiberoSessionQuery.Str(names[0][0])!;

        var info = (await RunAsync(session, "copy.sequence_info", ("OWN", owner), ("SEQ", sequence))).Rows;
        var increment = (await RunAsync(session, "copy.sequence_increment", ("OWNER", owner), ("NAME", sequence))).Rows;
        Assert.Single(info);
        Assert.Single(increment);
        Assert.True(TiberoSessionQuery.Long(info[0][0]) is not null && TiberoSessionQuery.Long(info[0][1]) is not null, "copy.sequence_info 값을 숫자로 읽지 못했다");
        Assert.True(TiberoSessionQuery.Long(increment[0][0]) == TiberoSessionQuery.Long(info[0][1]), "두 조회의 증가 폭이 다르다");
        Assert.True(TiberoSessionQuery.Long(increment[0][2]) is not null, "copy.sequence_increment 의 캐시 크기를 숫자로 읽지 못했다");
    }

    [TiberoItFact]
    public async Task Identity_cols_answers_none_and_the_trigger_count_does_not_count_other_objects()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = TiberoSessionQuery.Str((await RunAsync(session, "copy.session_user")).Rows[0][0])!;
        Assert.Empty((await RunAsync(session, "copy.identity_cols", ("OWN", owner), ("TBL", NoSuchName))).Rows);

        // copy.trigger_count_all 의 전제: table_name 이 채워진 트리거는 표·뷰에 걸린 것뿐이다(스키마·DB 트리거는 table_name 이 비어 있다).
        // 그렇지 않으면 스키마 이름 같은 값이 표 이름과 겹쳐 트리거 수가 부풀 수 있다. 이 계정의 트리거만 본다(남의 객체는 all_objects 에 안 보일 수 있다).
        var notOnTableOrView = TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT COUNT(*) FROM user_triggers t WHERE t.table_name IS NOT NULL AND t.table_owner = USER"
            + " AND NOT EXISTS (SELECT 1 FROM user_objects o WHERE o.object_name = t.table_name AND o.object_type IN ('TABLE', 'VIEW'))"));
        Assert.True(notOnTableOrView == 0, string.Create(CultureInfo.InvariantCulture, $"테이블·뷰가 아닌 대상에 table_name 이 채워진 트리거 {notOnTableOrView}개"));

        // 보이는 SYS 표 하나: 소유자 아닌 쪽의 객체 권한 조회가 파싱되고 건수가 음수가 아니다.
        var sysTable = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT table_name FROM (SELECT table_name FROM all_tables WHERE owner = 'SYS' AND table_name NOT LIKE 'BIN$%' ORDER BY table_name) WHERE ROWNUM = 1"));
        if (sysTable is null)
        {
            return;
        }

        Assert.True(await CountAsync(session, "copy.trigger_count_all", ("OWN", "SYS"), ("TBL", sysTable)) >= 0, "SYS 테이블의 트리거 수가 음수다");
        Assert.True(await CountAsync(session, "copy.other_grantor_count", ("OWN", "SYS"), ("TBL", sysTable)) >= 0, "SYS 테이블의 다른 계정 권한 수가 음수다");
        Assert.True((await RunAsync(session, "copy.table_privs", ("OWN", "SYS"), ("TBL", sysTable))).Columns.Count == 2, "copy.table_privs 열 수");
    }

    [TiberoItFact]
    public async Task Undo_retention_is_read_as_a_number_from_one_row()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var rows = (await RunAsync(session, "copy.parameter_value", ("PNAME", "undo_retention"))).Rows;
        // 0행이면 이름·뷰가 어긋난 것이다(Core 는 늘 「알 수 없음」으로만 끝난다). 실측 목적이라 한 행을 요구한다.
        Assert.True(rows.Count == 1, string.Create(CultureInfo.InvariantCulture, $"undo_retention 이 {rows.Count}행이다"));
        Assert.True(TiberoSessionQuery.Long(rows[0][0]) is not null, "undo_retention 값을 숫자로 읽지 못했다");

        // 없는 이름은 행이 없다(호출 쪽이 「알 수 없음」으로 본다).
        Assert.Empty((await RunAsync(session, "copy.parameter_value", ("PNAME", "ort_no_such_parameter"))).Rows);
    }

    private static Task<TiberoSessionQuery.Table> RunAsync(ISession session, string id, params (string Name, string Value)[] binds)
        => TiberoSessionQuery.RunAsync(session, TiberoCatalogQuery.Sql(session, id), command =>
        {
            foreach (var (name, value) in binds)
            {
                TiberoSessionQuery.Bind(command, name, value);
            }
        });

    private static async Task<long> CountAsync(ISession session, string id, params (string Name, string Value)[] binds)
    {
        var table = await RunAsync(session, id, binds);
        Assert.Single(table.Rows);
        Assert.Single(table.Columns);
        return TiberoSessionQuery.Long(table.Rows[0][0]) ?? throw new InvalidOperationException(id + ": 건수를 숫자로 읽지 못했다");
    }

    /// <summary>id 가 쓰는 바인드 이름에 맞춰 (이름, 값) 목록을 만든다. 첫 값은 소유자 자리, 둘째는 이름 자리.</summary>
    private static (string Name, string Value)[] Binds(string id, string owner, string name) => id switch
    {
        "copy.session_user" or "copy.session_roles" or "copy.session_privs" => [],
        "copy.sequence_names" => [("OWN", owner)],
        "copy.sequence_info" => [("OWN", owner), ("SEQ", name)],
        "copy.parameter_value" => [("PNAME", name)],
        "copy.constraint_owner" or "copy.index_owner" or "copy.object_exists" => [("OWN", owner), ("NAME", name)],
        "copy.table_exists" or "copy.sequence_increment" or "copy.fk_columns" => [("OWNER", owner), ("NAME", name)],
        _ => [("OWN", owner), ("TBL", name)],
    };

    /// <summary>오류 번호(TBR-nnnn·ODP-nnnn)만 뽑는다. 없으면 예외 형식 이름.</summary>
    private static string ErrorCode(Exception ex)
    {
        var match = ErrorCodePattern().Match(ex.Message);
        return match.Success ? match.Value : ex.GetType().Name;
    }

    [GeneratedRegex(@"\b(?:TBR|ODP)-\d+\b")]
    private static partial Regex ErrorCodePattern();
}
