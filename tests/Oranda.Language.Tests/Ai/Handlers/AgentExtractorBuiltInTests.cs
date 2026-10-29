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

    [Theory]
    [InlineData("WITH A AS (  /**/  SELECT g, SUM(v) s FROM t1 GROUP BY ROLLUP (g)), B AS (SELECT k FROM t2 WHERE z = :v1) SELECT * FROM A JOIN B ON A.g = B.k")]
    [InlineData("SELECT g, SUM(v) FROM t1 GROUP BY CUBE (g, h)")]
    [InlineData("SELECT g, SUM(v) FROM t1 GROUP BY GROUPING SETS ((g), (h))")]
    [InlineData("SELECT * FROM t1 PIVOT (SUM(a) FOR b IN ('x' AS x, 'y' AS y))")]
    [InlineData("SELECT * FROM t1 UNPIVOT (v FOR k IN (a, b))")]
    [InlineData("SELECT NTH_VALUE(a, 2) OVER (ORDER BY b), NANVL(c, 0), TO_NCHAR(d) FROM t1")]
    public async Task Common_builtin_and_clause_words_are_not_user_functions(string sql)
    {
        var x = new AgentStatementReferenceExtractor(new LanguageService(), new AnyTable());

        var info = await x.AnalyzeAsync(new FakeSession(), sql, null, CancellationToken.None);

        Assert.False(info.CallsUserFunction);
        Assert.False(info.IsUncertain);
        Assert.Equal(1, info.StatementCount);
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
}
