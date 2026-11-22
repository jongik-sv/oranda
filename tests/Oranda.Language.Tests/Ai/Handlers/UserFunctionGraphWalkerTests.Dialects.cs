using Oranda.Core.Ai.Gate;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Language.Ai.Handlers;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers;

/// <summary>
/// 사용자 함수 확인의 질의 글은 세션 DBMS 의 카탈로그(<c>app.user_fn_*</c>)에서 고른다(agent-cli-1122 w-tibero-fn).
/// Tibero 는 의존 뷰가 <c>parent_obj_*</c>, 동의어 뷰가 <c>org_object_*</c> 이고 링크 열이 없다. 카탈로그 원문은 저장소의 .sql 파일을 그대로 읽는다.
/// </summary>
public partial class UserFunctionGraphWalkerTests
{
    private static readonly Lazy<SqlCatalog> Catalog = new(LoadCatalog);

    private static readonly ServerInfo Oracle19 = TestData.Server("19.0.0.0.0");

    private static readonly ServerInfo Tibero6 = TestData.Server("6.0.0") with { Dbms = Dbms.Tibero };

    private static UserFunctionQueries OracleQueries => UserFunctionQueries.From(Catalog.Value, Oracle19);

    private static UserFunctionQueries TiberoQueries => UserFunctionQueries.From(Catalog.Value, Tibero6);

    [Fact]
    public void Tibero_reads_parent_obj_and_org_object_columns_while_oracle_keeps_its_columns()
    {
        var oracle = OracleQueries;
        var tibero = TiberoQueries;

        Assert.Contains("referenced_owner, referenced_name, referenced_type, referenced_link_name", oracle.Dependencies, StringComparison.Ordinal);
        Assert.Contains("table_owner, table_name, db_link", oracle.Synonyms, StringComparison.Ordinal);

        Assert.Contains("parent_obj_owner, parent_obj_name, parent_obj_type", tibero.Dependencies, StringComparison.Ordinal);
        Assert.DoesNotContain("referenced_", tibero.Dependencies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("org_object_owner, org_object_name", tibero.Synonyms, StringComparison.Ordinal);
        Assert.DoesNotContain("table_owner", tibero.Synonyms, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s.db_link", tibero.Synonyms, StringComparison.OrdinalIgnoreCase);

        // 다섯 글 모두 조건 자리가 있고, 채우면 남지 않는다.
        foreach (var text in new[] { tibero.Objects, tibero.Dependencies, tibero.Synonyms, tibero.Targets, tibero.Source, oracle.Source })
        {
            var filled = UserFunctionQueries.Fill(text, "(owner = 'APP' AND name = 'F')");
            Assert.DoesNotContain("@PAIRS@", filled, StringComparison.Ordinal);
            Assert.Contains("(owner = 'APP' AND name = 'F')", filled, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Checker_sends_the_tibero_catalog_text_on_a_tibero_session_and_is_unverified_without_one()
    {
        var exec = new FakeExecutionService(); // 결과 집합 없이 성공 = 예상 밖 모양 → 닫힌 쪽
        var checker = new UserFunctionSafetyChecker(exec, Catalog.Value);
        var verdict = await checker.CheckAsync(new FakeSession(server: Tibero6), ["FN_X"], "APP", default);

        Assert.Equal(UserFunctionVerdict.Unverified, verdict);
        var sent = Assert.Single(exec.Calls).Statement.Text;
        Assert.Equal(UserFunctionQueries.Fill(TiberoQueries.Objects, "(owner = 'APP' AND object_name = 'FN_X')"), sent);

        // 그 DBMS 용 글이 카탈로그에 없으면 질의하지 않고 확인 못 함.
        var empty = new UserFunctionSafetyChecker(exec, new SqlCatalog([]));
        Assert.Equal(UserFunctionVerdict.Unverified, await empty.CheckAsync(new FakeSession(server: Tibero6), ["FN_X"], "APP", default));
        Assert.Single(exec.Calls);
    }

    [Fact]
    public async Task Tibero_public_dual_points_to_an_underscore_table_and_a_function_reading_it_is_safe()
    {
        // Tibero 실측: SELECT … FROM DUAL 이 든 함수는 PUBLIC.DUAL(동의어) → SYS._VT_DUAL(표) 에 기댄다.
        var d = new FakeDictionary();
        d.Add("APP", "FN_DUAL", "FUNCTION", ["FUNCTION fn_dual RETURN NUMBER IS"], ("PUBLIC", "DUAL", "SYNONYM"), ("SYS", "STANDARD", "PACKAGE"));
        d.Synonyms[("PUBLIC", "DUAL")] = ("SYS", "_VT_DUAL", string.Empty);
        d.Add("SYS", "_VT_DUAL", "TABLE", []);

        Assert.Equal(UserFunctionVerdict.Safe, await Walk(d, TiberoQueries, "FN_DUAL"));
        Assert.Equal(UserFunctionVerdict.Safe, await Walk(d, OracleQueries, "FN_DUAL")); // 판정 규칙은 DBMS 와 무관
    }

    [Fact]
    public async Task Tibero_synonym_to_an_autonomous_function_and_a_remote_synonym_are_refused()
    {
        var d = new FakeDictionary();
        d.Add("APP", "FN_VIA", "FUNCTION", ["FUNCTION fn_via RETURN NUMBER IS"], ("APP", "SYN_AUTO", "SYNONYM"));
        d.Synonyms[("APP", "SYN_AUTO")] = ("APP", "FN_AUTO", string.Empty);
        d.Add("APP", "FN_AUTO", "FUNCTION", ["FUNCTION fn_auto RETURN NUMBER IS", "PRAGMA AUTONOMOUS_TRANSACTION;"]);
        d.Add("APP", "FN_REMOTE", "FUNCTION", ["FUNCTION fn_remote RETURN NUMBER IS"], ("PUBLIC", "R_T", "SYNONYM"));
        d.Synonyms[("PUBLIC", "R_T")] = ("APP2", "\"R_T\"@LNK", "LNK"); // Tibero 원격 동의어: 이름 끝 @링크

        Assert.Equal(UserFunctionVerdict.Autonomous, await Walk(d, TiberoQueries, "FN_VIA"));
        Assert.Equal(UserFunctionVerdict.External, await Walk(d, TiberoQueries, "FN_REMOTE"));
    }

    private static Task<UserFunctionVerdict> Walk(FakeDictionary d, UserFunctionQueries queries, params string[] functions) =>
        new UserFunctionGraphWalker(d.QueryAsync, queries).CheckAsync(functions, "APP", CancellationToken.None).AsTask();

    /// <summary>저장소의 Oracle·Tibero 카탈로그 <c>app/user_fn_*.sql</c> 원문(제품 어셈블리에 실리는 것과 같은 파일).</summary>
    private static SqlCatalog LoadCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var sources = new List<CatalogSource>();
        foreach (var project in new[] { "Oranda.Oracle", "Oranda.Tibero" })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(dir.FullName, "src", project, "Catalog", "app"), "user_fn_*.sql"))
            {
                sources.Add(new CatalogSource("app/" + Path.GetFileName(file), File.ReadAllText(file)));
            }
        }

        Assert.Equal(10, sources.Count);
        return new SqlCatalog(sources);
    }
}
