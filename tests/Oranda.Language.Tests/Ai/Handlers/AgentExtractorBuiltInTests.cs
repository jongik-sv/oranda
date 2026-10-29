using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers;

/// <summary>
/// get_tuning_context 가 sql_unsafe 로 거부되던 원인: 내장 함수 목록에 없는 흔한 낱말(ROLLUP·PIVOT·NTH_VALUE 등)이 사용자 함수로 분류됐다.
/// 가짜 SQL 로 그 꼴을 재현하고, 진짜 사용자 함수·시퀀스는 계속 걸러지는지 함께 확인한다.
/// </summary>
public class AgentExtractorBuiltInTests
{
    private sealed class AnyTable : IMetadataProvider
    {
        private readonly FakeMetadataProvider _inner = new();

        public Task<NameStep?> GetNameStepAsync(ISession s, string owner, string name, CancellationToken ct) =>
            Task.FromResult<NameStep?>(NameStep.TableOrView);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => _inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => _inner.ListObjectsAsync(s, q, ct);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => _inner.GetTableAsync(s, table, ct);

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetProceduresAsync(s, obj, ct);

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetSourceAsync(s, obj, ct);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => _inner.GetCompileErrorsAsync(s, obj, ct);
    }

    [Fact]
    public async Task Common_builtins_are_not_user_functions()
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var info = await x.AnalyzeAsync(new FakeSession(), "SELECT NTH_VALUE(a, 2) OVER (ORDER BY b), NANVL(c, 0), TO_NCHAR(d) FROM t1", null, CancellationToken.None);

        Assert.False(info.CallsUserFunction);
        Assert.False(info.IsUncertain);
        Assert.Equal(1, info.StatementCount);
    }

    /// <summary>
    /// 구문 낱말(ROLLUP·CUBE·SETS·PIVOT·UNPIVOT)은 같은 이름의 사용자 함수가 있으면 그것이 불리므로 사용자 함수 후보로 넘긴다.
    /// 확인기는 그런 객체가 없으면 문제 삼지 않는다(UserFunctionGraphWalkerTests). 분류는 확실하다.
    /// </summary>
    [Theory]
    [InlineData("WITH A AS (  /**/  SELECT g, SUM(v) s FROM t1 GROUP BY ROLLUP (g)), B AS (SELECT k FROM t2 WHERE z = :v1) SELECT * FROM A JOIN B ON A.g = B.k", "ROLLUP")]
    [InlineData("SELECT g, SUM(v) FROM t1 GROUP BY CUBE (g, h)", "CUBE")]
    [InlineData("SELECT g, SUM(v) FROM t1 GROUP BY GROUPING SETS ((g), (h))", "SETS")]
    [InlineData("SELECT * FROM t1 PIVOT (SUM(a) FOR b IN ('x' AS x, 'y' AS y))", "PIVOT")]
    [InlineData("SELECT * FROM t1 UNPIVOT (v FOR k IN (a, b))", "UNPIVOT")]
    public async Task Clause_words_are_passed_as_candidates_the_checker_may_find_absent(string sql, string word)
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var info = await x.AnalyzeAsync(new FakeSession(), sql, null, CancellationToken.None);

        Assert.Contains(word, info.UserFunctions ?? []);
        Assert.Contains(word, AgentStatementReferenceExtractor.MaybeUserFunctions);
        Assert.False(info.IsUncertain);
    }

    [Fact]
    public async Task Xml_query_functions_are_user_function_candidates_and_quoted_lowercase_calls_are_uncertain()
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var xml = await x.AnalyzeAsync(new FakeSession(), "SELECT * FROM XMLTABLE('fn:collection(\"oradb:/HR/SALARIES\")/ROW')", null, CancellationToken.None);
        var quoted = await x.AnalyzeAsync(new FakeSession(), "SELECT \"myFunc\"(a) FROM t1", null, CancellationToken.None);

        Assert.Contains("XMLTABLE", xml.UserFunctions ?? []); // 범위 밖 표를 읽을 수 있어 내장으로 보지 않는다
        Assert.True(quoted.IsUncertain); // 대문자로 바꾸면 다른 객체(MYFUNC)를 확인하게 된다
    }

    [Theory]
    [InlineData("SELECT \"A.B\"(1) FROM t1")]
    [InlineData("SELECT app.\"PKG X\".f(1) FROM t1")]
    public async Task Quoted_call_names_with_non_identifier_characters_are_uncertain(string sql)
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var info = await x.AnalyzeAsync(new FakeSession(), sql, null, CancellationToken.None);

        Assert.True(info.IsUncertain); // 점으로 이은 이름("A.B" → A.B)은 다른 객체를 확인하게 된다
    }

    [Fact]
    public async Task Tables_across_a_database_link_are_remote_references_not_uncertain()
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var info = await x.AnalyzeAsync(new FakeSession(), "SELECT a FROM t1@remote_db", null, CancellationToken.None);

        Assert.True(info.HasRemoteReference);
        Assert.False(info.IsUncertain); // 게이트가 실행 도구에서는 거부 대신 커서 실측으로 대체하게
    }

    [Theory]
    [InlineData("SELECT my_fn(a) FROM t1")]
    [InlineData("SELECT pkg.f(a) FROM t1 GROUP BY ROLLUP (a)")]
    [InlineData("SELECT seq_x.NEXTVAL FROM dual")]
    [InlineData("WITH A AS (/**/ SELECT rollup_fn(a) x FROM t1) SELECT * FROM A")]
    public async Task Real_user_functions_and_sequences_are_still_flagged(string sql)
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var info = await x.AnalyzeAsync(new FakeSession(), sql, null, CancellationToken.None);

        Assert.True(info.CallsUserFunction);
    }

    [Theory]
    [InlineData("SELECT my_fn(a), pkg.f(b), app.pkg.g(c), MY_FN(d) FROM t1", "MY_FN,PKG.F,APP.PKG.G", false)]
    [InlineData("SELECT seq_x.NEXTVAL FROM dual", "", true)]
    [InlineData("SELECT dbms_lock.sleep(1), my_fn(a) FROM t1", "MY_FN", true)]
    [InlineData("SELECT sys.utl_http.request(a) FROM t1", "", true)]
    [InlineData("SELECT httpuritype(a) FROM t1", "", true)]
    public async Task Calls_are_split_into_user_function_candidates_and_forbidden_calls(string sql, string functions, bool forbidden)
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var info = await x.AnalyzeAsync(new FakeSession(), sql, null, CancellationToken.None);

        Assert.True(info.CallsUserFunction);
        Assert.Equal(forbidden, info.HasForbiddenCall);
        Assert.Equal(functions, string.Join(",", info.UserFunctions ?? []));
    }
}
