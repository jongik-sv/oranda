using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 객체·소스·열 검색, 참조 관계, 휴지통을 실제 서버로 읽는다(읽기 전용). 서버·드라이버가 없으면 아무것도 하지 않는다.
/// 접속 계정 이름이 든 값은 <see cref="TiberoItSessions.Same"/> 로 참·거짓만 본다(Assert.Equal·Contains·All 은 실패 때 값을 찍는다).
/// </summary>
[Trait("Category", "Integration")]
public class TiberoSchemaAdminIt
{
    /// <summary>
    /// 이 계정의 <c>all_objects</c> 에 실제로 보이는 SYS 표·뷰 이름 하나를 골라 그 이름으로 찾는다. 빈 결과면 질의 결함이다.
    /// SYS.DUAL 은 쓰지 않는다: 비 DBA 계정의 Tibero <c>all_objects</c> 에는 SYS.DUAL 이 없다(win144 #39 실측, 이름 조건만 끄면 SYS 객체는 나옴).
    /// </summary>
    [TiberoItFact]
    public async Task Objects_found_by_an_exact_name_come_with_status_and_a_row_limit()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var provider = new TiberoSchemaAdminProvider();
        var name = TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(session,
            "SELECT object_name FROM (SELECT object_name FROM all_objects WHERE owner = 'SYS' AND object_type IN ('TABLE', 'VIEW') " +
            "AND object_name NOT LIKE 'BIN$%' ORDER BY object_name) WHERE ROWNUM = 1"));
        Assert.True(name is not null, "all_objects 에 보이는 SYS 표·뷰가 없다");

        // 이름의 _ 는 LIKE 와일드카드라 막는다(다른 이름이 걸리지 않게).
        var pattern = name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var rows = await provider.SearchObjectsAsync(session, new ObjectSearchQuery { Owner = "SYS", NamePattern = pattern, Limit = 10 }, CancellationToken.None);

        if (rows.Count == 0)
        {
            Assert.Fail("all_objects 에 보이는 SYS." + name + " 이 안 나왔다(0행). 진단: " + await DiagnoseObjectSearchAsync(session, pattern));
        }

        Assert.True(rows.Count <= 10, "max_rows(10)를 넘었다: " + rows.Count.ToString(CultureInfo.InvariantCulture) + "행");
        var outside = rows.Count(r => r.Ref.Owner != "SYS" || r.Ref.Name != name);
        var noStatus = rows.Count(r => r.Status.Length == 0);
        Assert.True(outside == 0 && noStatus == 0, string.Create(CultureInfo.InvariantCulture,
            $"{rows.Count}행 중 소유자·이름 거르기 밖 {outside}행, 상태 빈 행 {noStatus}행, 유형 [{string.Join(", ", rows.Select(r => r.Ref.ObjectType))}]"));
    }

    /// <summary>
    /// 객체 검색이 SYS 객체를 놓칠 때 어느 조건이 행을 지우는지 찾는다: 같은 바인드로 원문, 조건 하나씩 끈 문장의 행 수를 늘어놓는다.
    /// 값은 행 수·뷰 접두어·오류 앞부분뿐이다(접속 값 없음).
    /// </summary>
    private static async Task<string> DiagnoseObjectSearchAsync(TiberoSession session, string namePattern)
    {
        var id = TiberoSchemaAdminProvider.ObjectSearchId;
        var sql = TiberoCatalogQuery.Sql(session, id, TiberoSchemaAdminProvider.Replacements(session, id));
        var binds = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["owner"] = "SYS", ["types"] = null, ["name_pattern"] = namePattern, ["invalid_only"] = 0, ["created_after"] = null,
            ["modified_after"] = null, ["min_size"] = null, ["max_size"] = null, ["max_rows"] = 10,
        };
        (string Name, string Condition)[] conditions =
        [
            ("types", "(:types IS NULL OR INSTR(:types, '|' || o.object_type || '|') > 0)"),
            ("java", "(o.object_type NOT LIKE 'JAVA %' OR INSTR(:types, '|JAVA ') > 0)"),
            ("name", "(:name_pattern IS NULL OR o.object_name LIKE :name_pattern ESCAPE '\\')"),
            ("invalid", "(:invalid_only = 0 OR o.status <> 'VALID')"),
            ("created", "(:created_after IS NULL OR o.created > :created_after)"),
            ("modified", "(:modified_after IS NULL OR o.last_ddl_time > :modified_after)"),
            ("min_size", "(:min_size IS NULL OR seg.bytes >= :min_size)"),
            ("max_size", "(:max_size IS NULL OR seg.bytes <= :max_size)"),
            ("rownum", "ROWNUM <= :max_rows"),
        ];

        var parts = new List<string> { "pfx=" + TiberoSessionQuery.ViewPrefix(session), "full=" + await CountAsync(sql) };
        foreach (var (name, condition) in conditions)
        {
            parts.Add("-" + name + "=" + (sql.Contains(condition, StringComparison.Ordinal) ? await CountAsync(sql.Replace(condition, "1 = 1", StringComparison.Ordinal)) : "없음"));
        }

        var all = sql;
        foreach (var (_, condition) in conditions)
        {
            all = all.Replace(condition, "1 = 1", StringComparison.Ordinal);
        }

        parts.Add("-all=" + await CountAsync(all));
        return string.Join(" ", parts);

        async Task<string> CountAsync(string text)
        {
            try
            {
                var table = await TiberoSessionQuery.RunAsync(session, text, c =>
                {
                    foreach (var (name, value) in binds.Where(b => text.Contains(":" + b.Key, StringComparison.Ordinal)))
                    {
                        TiberoSessionQuery.Bind(c, name, value);
                    }
                });
                return table.Rows.Count.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                var message = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
                return "오류(" + message + ")";
            }
        }
    }

    [TiberoItFact]
    public async Task Objects_of_the_login_schema_come_back_filtered_by_owner_and_type_within_the_limit()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var user = it.Env.User.ToUpperInvariant();

        var rows = await new TiberoSchemaAdminProvider().SearchObjectsAsync(session,
            new ObjectSearchQuery { Owner = user, ObjectTypes = ["TABLE"], Limit = 5 }, CancellationToken.None);

        Assert.True(rows.Count <= 5, "max_rows 를 넘었다");
        var onlyOwnTables = rows.All(r => TiberoItSessions.Same(user, r.Ref.Owner) && r.Ref.ObjectType == "TABLE");
        Assert.True(onlyOwnTables, "소유자·유형 거르기 밖의 객체가 왔다");
    }

    [TiberoItFact]
    public async Task Columns_named_dummy_are_found_and_the_login_schemas_columns_respect_the_limit()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var user = it.Env.User.ToUpperInvariant();
        var provider = new TiberoSchemaAdminProvider();

        var dummy = await provider.SearchColumnsAsync(session, new ColumnSearchQuery { Owner = "SYS", NamePattern = "dummy", Limit = 10 }, CancellationToken.None);
        var own = await provider.SearchColumnsAsync(session, new ColumnSearchQuery { Owner = user, Limit = 5 }, CancellationToken.None);

        Assert.True(dummy.Rows.Count is >= 1 and <= 10, "SYS.DUAL.DUMMY 가 안 나왔거나 max_rows 를 넘었다");
        Assert.True(dummy.Rows.All(h => h.ColumnName == "DUMMY" && h.Table.Owner == "SYS" && h.DataType.Length > 0), "열 이름·소유자 거르기 밖의 열이 왔다");
        Assert.True(own.Rows.Count <= 5, "max_rows 를 넘었다");
        var onlyOwn = own.Rows.All(h => TiberoItSessions.Same(user, h.Table.Owner) && h.ColumnName.Length > 0 && h.DataType.Length > 0);
        Assert.True(onlyOwn, "소유자 거르기 밖의 열이 왔다");
    }

    /// <summary>접속 스키마의 소스를 일반 글자로 찾는다. 결과 내용은 보지 않고, 상한과 소유자 거르기만 본다.</summary>
    [TiberoItFact]
    public async Task Source_search_of_the_login_schema_respects_the_limit()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var user = it.Env.User.ToUpperInvariant();

        var result = await new TiberoSchemaAdminProvider().SearchSourceAsync(session,
            new SourceSearchQuery { Owner = user, Text = "end", Limit = 3 }, CancellationToken.None);

        Assert.True(result.Rows.Count <= 3, "max_rows 를 넘었다");
        var onlyOwn = result.Rows.All(h => TiberoItSessions.Same(user, h.Ref.Owner) && h.Line >= 1);
        Assert.True(onlyOwn, "소유자 거르기 밖의 줄이 왔다");
    }

    /// <summary>괄호가 닫히지 않은 정규식은 서버가 거부해 <see cref="SearchPatternException"/> 이 된다(소스를 읽기 전에 검사한다).</summary>
    [TiberoItFact]
    public async Task A_regex_with_an_unclosed_group_is_a_pattern_error()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SearchPatternException>(() => new TiberoSchemaAdminProvider().SearchSourceAsync(session,
            new SourceSearchQuery { Owner = "SYS", Text = "(", UseRegex = true, Limit = 1 }, CancellationToken.None));

        // 실측 기록: 서버가 내는 정규식 오류 번호(서버 범위 1~89999 여야 패턴 오류로 바뀐다). 번호·메시지에 접속 값은 없다.
        var inner = TiberoSchemaAdminProvider.PatternError(thrown.InnerException!);
        Assert.True(inner?.Number is > -90000 and < 90000 and not 0, "정규식 오류 번호가 서버 범위가 아니다: " + inner?.Code);
    }

    /// <summary>
    /// 낱말 단위 찾기는 검색 글자를 POSIX 문자 묶음(<c>[^[:alnum:]_$#]</c>)으로 감싼다 — Tibero 가 이를 받는지 실측한다
    /// (못 받으면 낱말 단위 찾기마다 서버 오류가 난다).
    /// </summary>
    [TiberoItFact]
    public async Task Whole_word_source_search_runs_on_the_server()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();

        var result = await new TiberoSchemaAdminProvider().SearchSourceAsync(session,
            new SourceSearchQuery { Owner = "SYS", Text = "put_line", WholeWord = true, ObjectTypes = ["PACKAGE"], Limit = 3 }, CancellationToken.None);

        Assert.True(result.Rows.Count <= 3, "max_rows 를 넘었다");
    }

    [TiberoItFact]
    public async Task Recycle_bin_lists_without_an_error()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();

        var items = await new TiberoSchemaAdminProvider().ListRecycleBinAsync(session, CancellationToken.None);

        Assert.True(items.All(i => i.ObjectName.Length > 0), "휴지통 항목에 이름이 없다");
    }

    /// <summary>DBMS_OUTPUT 패키지의 참조 관계를 읽는다. 있는 만큼만 보고, 모든 줄에 이름이 있는지만 본다.</summary>
    [TiberoItFact]
    public async Task References_of_dbms_output_come_back_with_names()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();

        var refs = await new TiberoSchemaAdminProvider().GetReferencesAsync(session, new DbObjectRef("SYS", "DBMS_OUTPUT", "PACKAGE"), CancellationToken.None);

        Assert.True(refs.All(r => r.Ref.Owner.Length > 0 && r.Ref.Name.Length > 0 && r.Ref.ObjectType.Length > 0), "참조 줄에 빈 이름이 있다");
    }
}
