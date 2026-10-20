using System.Diagnostics;
using System.Reflection;
using System.Text;
using Oranda.Core.Events;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Completion;
using Oranda.Testing;

namespace Oranda.Language.Tests.Completion;

// 소유: TSK-04-05. 자동완성 문맥(L1)·후보·150 ms 예산·인자 도움말.
public class CompletionTests
{
    private static readonly LanguageService Language = new();

    /// <summary>`|` 자리를 커서로 보고 문맥을 구한다.</summary>
    private static CompletionContext Ctx(string withCaret)
    {
        int offset = withCaret.IndexOf('|', StringComparison.Ordinal);
        Assert.True(offset >= 0);
        return Language.GetCompletionContext(withCaret.Remove(offset, 1), offset);
    }

    private static (string Text, int Offset) Caret(string withCaret)
    {
        int offset = withCaret.IndexOf('|', StringComparison.Ordinal);
        return (withCaret.Remove(offset, 1), offset);
    }

    // ---- 문맥 ---------------------------------------------------------------------------------

    [Fact]
    public void Alias_dot_before_from_gives_columns_of_that_table()
    {
        var c = Ctx("SELECT e.| FROM emp e");
        Assert.Equal(CompletionContextKind.Column, c.Kind);
        Assert.Equal("E", c.Qualifier);
        Assert.Equal("EMP", c.Aliases["E"]);
        Assert.Equal("EMP", c.Aliases["emp"]); // 테이블 이름 자신도 키, 대소문자 무시
        Assert.Equal(new Oranda.Core.Text.TextRange(9, 0), c.ReplaceRange);
    }

    [Fact]
    public void Prefix_after_alias_dot_is_the_replace_range()
    {
        var c = Ctx("SELECT e.en| FROM emp e");
        Assert.Equal(CompletionContextKind.Column, c.Kind);
        Assert.Equal("en", c.Prefix);
        Assert.Equal(new Oranda.Core.Text.TextRange(9, 2), c.ReplaceRange);
    }

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("select * from em|")]
    [InlineData("SELECT * FROM emp e, |")]
    [InlineData("SELECT * FROM emp e JOIN |")]
    [InlineData("SELECT * FROM emp e LEFT OUTER JOIN d|")]
    [InlineData("INSERT INTO |")]
    [InlineData("UPDATE |")]
    [InlineData("DELETE FROM |")]
    [InlineData("MERGE INTO |")]
    [InlineData("MERGE INTO t USING |")]
    [InlineData("TRUNCATE TABLE |")]
    [InlineData("DESC |")]
    [InlineData("SELECT a FROM t1;\nSELECT * FROM |")]
    public void Table_position(string sql) => Assert.Equal(CompletionContextKind.TableOrView, Ctx(sql).Kind);

    [Fact]
    public void Schema_dot_in_from_gives_objects_of_that_schema()
    {
        var c = Ctx("SELECT * FROM hr.|");
        Assert.Equal(CompletionContextKind.TableOrView, c.Kind);
        Assert.Equal("HR", c.Qualifier);
    }

    [Theory]
    [InlineData("SELECT |")]
    [InlineData("SELECT a, | FROM emp")]
    [InlineData("SELECT * FROM emp WHERE |")]
    [InlineData("SELECT * FROM emp WHERE sal > |")]
    [InlineData("SELECT * FROM emp e JOIN dept d ON |")]
    [InlineData("SELECT * FROM emp ORDER BY |")]
    [InlineData("UPDATE emp SET |")]
    [InlineData("SELECT nvl(|")]
    public void Expression_position_gives_columns(string sql)
    {
        var c = Ctx(sql);
        Assert.Equal(CompletionContextKind.Column, c.Kind);
        Assert.Null(c.Qualifier);
    }

    [Theory]
    [InlineData("|")]
    [InlineData("SEL|")]
    [InlineData("SELECT * FROM emp |")]
    [InlineData("SELECT * FROM emp e |")]
    [InlineData("SELECT ename |")]
    [InlineData("BEGIN |")]
    [InlineData("SELECT x INTO |")]
    public void Keyword_position(string sql) => Assert.Equal(CompletionContextKind.Keyword, Ctx(sql).Kind);

    [Fact]
    public void Join_alias_and_where_unqualified_collects_all_tables()
    {
        var c = Ctx("SELECT * FROM emp e JOIN dept d ON e.deptno = d.|");
        Assert.Equal(CompletionContextKind.Column, c.Kind);
        Assert.Equal("D", c.Qualifier);
        Assert.Equal("DEPT", c.Aliases["D"]);
        Assert.Equal("EMP", c.Aliases["E"]);
    }

    [Fact]
    public void Qualified_table_alias_and_schema_table_name()
    {
        var c = Ctx("SELECT hr.emp.| FROM hr.emp, scott.dept AS d");
        Assert.Equal(CompletionContextKind.Column, c.Kind);
        Assert.Equal("HR.EMP", c.Qualifier);
        Assert.Equal("HR.EMP", c.Aliases["EMP"]);
        Assert.Equal("SCOTT.DEPT", c.Aliases["D"]);
    }

    [Fact]
    public void Package_dot_gives_members()
    {
        var c = Ctx("BEGIN\n  dbms_output.|\nEND;");
        Assert.Equal(CompletionContextKind.PackageMember, c.Kind);
        Assert.Equal("DBMS_OUTPUT", c.Qualifier);

        var two = Ctx("BEGIN hr.emp_pkg.hi| END;");
        Assert.Equal(CompletionContextKind.PackageMember, two.Kind);
        Assert.Equal("HR.EMP_PKG", two.Qualifier);
        Assert.Equal("hi", two.Prefix);
    }

    [Fact]
    public void Hint_comment_gives_hint_names_but_not_inside_hint_arguments()
    {
        var c = Ctx("SELECT /*+ ful| */ * FROM emp");
        Assert.Equal(CompletionContextKind.Hint, c.Kind);
        Assert.Equal("ful", c.Prefix);
        Assert.Equal(CompletionContextKind.Hint, Ctx("SELECT /*+ |").Kind);
        Assert.Equal(CompletionContextKind.None, Ctx("SELECT /*+ INDEX(e | */ * FROM emp e").Kind);
    }

    [Theory]
    [InlineData("SELECT 1 FROM dual -- e.|")]
    [InlineData("SELECT 'abc e.| FROM dual")]
    [InlineData("SELECT /* e.| */ 1 FROM dual")]
    [InlineData("SELECT 12| FROM dual")]
    [InlineData("SELECT :b| FROM dual")]
    public void Comments_strings_numbers_and_binds_give_nothing(string sql)
        => Assert.Equal(CompletionContextKind.None, Ctx(sql).Kind);

    [Fact]
    public void Aliases_come_only_from_the_statement_under_the_caret()
    {
        var c = Ctx("SELECT x.a FROM t1 x;\nSELECT x.| FROM t2 x;\nSELECT 1 FROM t3 x");
        Assert.Equal("T2", c.Aliases["X"]);
        Assert.False(c.Aliases.ContainsKey("T1"));
        Assert.False(c.Aliases.ContainsKey("T3"));

        var slash = Ctx("BEGIN NULL; END;\n/\nSELECT y.| FROM t4 y");
        Assert.Equal("T4", slash.Aliases["Y"]);
    }

    [Fact]
    public void Inner_scope_alias_wins_inside_subquery()
    {
        var c = Ctx("SELECT e.ename FROM emp e WHERE EXISTS (SELECT 1 FROM dept e WHERE e.| = 1)");
        Assert.Equal("DEPT", c.Aliases["E"]);

        var outer = Ctx("SELECT e.| FROM emp e WHERE EXISTS (SELECT 1 FROM dept e)");
        Assert.Equal("EMP", outer.Aliases["E"]);
    }

    [Fact]
    public void Extract_from_is_not_a_table()
    {
        var c = Ctx("SELECT EXTRACT(YEAR FROM hiredate), e.| FROM emp e");
        Assert.False(c.Aliases.ContainsKey("HIREDATE"));
        Assert.Equal(CompletionContextKind.Column, c.Kind);
    }

    [Fact]
    public void Current_schema_gives_schema_names()
        => Assert.Equal(CompletionContextKind.Schema, Ctx("ALTER SESSION SET CURRENT_SCHEMA = |").Kind);

    [Fact]
    public void Out_of_range_offset_gives_none()
    {
        Assert.Equal(CompletionContextKind.None, Language.GetCompletionContext("select", -1).Kind);
        Assert.Equal(CompletionContextKind.None, Language.GetCompletionContext("select", 7).Kind);
    }

    /// <summary>TRD 4.5 L1 「1만 줄 문서에서 16 ms 이하」. Release 기준 16 ms, Debug(JIT 최적화 끔)·병렬 시험 부하는 6배 여유.</summary>
    [Trait("Category", "Perf")]
    [Fact]
    public void Context_on_10k_line_document_is_fast()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("SELECT e.ename, d.dname FROM emp e JOIN dept d ON e.deptno = d.deptno WHERE e.sal > ").Append(i).Append(";\n");
        }

        sb.Append("SELECT e.");
        var text = sb.ToString() + " FROM emp e";
        int offset = sb.Length;
        for (int i = 0; i < 3; i++)
        {
            Language.GetCompletionContext(text, offset);
        }

        long best = long.MaxValue;
        CompletionContext c = CompletionContext.None;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            c = Language.GetCompletionContext(text, offset);
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }

        Assert.Equal(CompletionContextKind.Column, c.Kind);
        Assert.Equal("EMP", c.Aliases["E"]);
        long limit = Optimized ? 16 : 100;
        Assert.True(best <= limit, $"10k 줄 문맥 {best} ms (한도 {limit} ms)");
    }

    private static bool Optimized =>
        typeof(LanguageAssembly).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    // ---- 후보 ---------------------------------------------------------------------------------

    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");
    private static readonly DbObjectRef Dept = new("SCOTT", "DEPT", "TABLE");
    private static readonly DbObjectRef EmpView = new("SCOTT", "EMP_V", "VIEW");
    private static readonly DbObjectRef EmpPkg = new("SCOTT", "EMP_PKG", "PACKAGE");
    private static readonly DbObjectRef DbmsOutput = new("SYS", "DBMS_OUTPUT", "PACKAGE");

    private static FakeMetadataProvider Meta()
    {
        var m = new FakeMetadataProvider();
        m.Schemas.AddRange(["HR", "SCOTT", "SYS"]);
        m.Objects.AddRange([Emp, Dept, EmpView, EmpPkg, new DbObjectRef("SCOTT", "EMP_SYN", "SYNONYM"), new DbObjectRef("HR", "JOBS", "TABLE"), DbmsOutput]);
        m.Tables[Emp] = new TableInfo(Emp,
        [
            new ColumnInfo("EMPNO", 1, "NUMBER") { Precision = 4, IsNullable = false },
            new ColumnInfo("ENAME", 2, "VARCHAR2") { Length = 10, Comment = "이름" },
            new ColumnInfo("SAL", 3, "NUMBER") { Precision = 7, Scale = 2 },
            new ColumnInfo("Mixed Case", 4, "DATE"),
        ]);
        m.Tables[Dept] = new TableInfo(Dept, [new ColumnInfo("DEPTNO", 1, "NUMBER"), new ColumnInfo("DNAME", 2, "VARCHAR2")]);
        m.Procedures[EmpPkg] =
        [
            new ProcedureInfo("HIRE", false, [new ArgumentInfo("P_NAME", 1, "VARCHAR2", ArgumentDirection.In), new ArgumentInfo("P_SAL", 2, "NUMBER", ArgumentDirection.In) { HasDefault = true }]),
            new ProcedureInfo("FIRE", false, [new ArgumentInfo("P_ID", 1, "NUMBER", ArgumentDirection.In)]),
            new ProcedureInfo("SALARY", true, [new ArgumentInfo("P_ID", 1, "NUMBER", ArgumentDirection.In)]) { ReturnType = "NUMBER" },
        ];
        m.Procedures[DbmsOutput] = [new ProcedureInfo("PUT_LINE", false, [new ArgumentInfo("A", 1, "VARCHAR2", ArgumentDirection.In)])];
        return m;
    }

    private static Task<CompletionResult> Complete(CompletionProvider p, ISession? s, string withCaret)
    {
        var (text, offset) = Caret(withCaret);
        return p.GetCompletionsAsync(s, text, offset, CancellationToken.None);
    }

    [Trait("Category", "Perf")]
    [Fact]
    public async Task Acceptance_alias_dot_lists_emp_columns_within_150ms()
    {
        using var p = new CompletionProvider(Meta());
        var s = new FakeSession();
        await Complete(p, s, "SELECT e.| FROM emp e"); // 데우기(JIT)
        p.Lookup.Clear();

        var sw = Stopwatch.StartNew();
        var r = await Complete(p, s, "SELECT e.| FROM emp e");
        sw.Stop();

        Assert.False(r.IsPartial);
        Assert.Equal(["EMPNO", "ENAME", "SAL", "Mixed Case"], r.Items.Select(i => i.Label));
        Assert.All(r.Items, i => Assert.Equal(CompletionItemKind.Column, i.Kind));
        Assert.Equal("NUMBER(4) NOT NULL", r.Items[0].Detail);
        Assert.Equal("VARCHAR2(10) — 이름", r.Items[1].Detail);
        Assert.Equal("NUMBER(7,2)", r.Items[2].Detail);
        Assert.Equal("\"Mixed Case\"", r.Items[3].InsertText);
        Assert.Equal(new Oranda.Core.Text.TextRange(9, 0), r.ReplaceRange);
        Assert.True(sw.ElapsedMilliseconds < 150, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Lower_case_typing_inserts_lower_case_names()
    {
        using var p = new CompletionProvider(Meta());
        var r = await Complete(p, new FakeSession(), "select e.en| from emp e");
        var item = Assert.Single(r.Items);
        Assert.Equal("ENAME", item.Label);
        Assert.Equal("ename", item.InsertText);
    }

    [Fact]
    public async Task From_lists_tables_views_synonyms_and_schemas_filtered_by_prefix()
    {
        using var p = new CompletionProvider(Meta());
        var s = new FakeSession();
        var all = await Complete(p, s, "SELECT * FROM |");
        Assert.Contains(all.Items, i => i is { Label: "EMP", Kind: CompletionItemKind.Table });
        Assert.Contains(all.Items, i => i is { Label: "EMP_V", Kind: CompletionItemKind.View });
        Assert.Contains(all.Items, i => i is { Label: "EMP_SYN", Kind: CompletionItemKind.Synonym });
        Assert.Contains(all.Items, i => i is { Label: "HR", Kind: CompletionItemKind.Schema });
        Assert.DoesNotContain(all.Items, i => i.Label == "EMP_PKG"); // 패키지는 FROM 후보가 아니다
        Assert.DoesNotContain(all.Items, i => i.Label == "JOBS"); // 다른 스키마

        var em = await Complete(p, s, "SELECT * FROM em|");
        Assert.Equal(["EMP", "EMP_SYN", "EMP_V"], em.Items.Select(i => i.Label).Order(StringComparer.Ordinal));
        Assert.Equal(new Oranda.Core.Text.TextRange(14, 2), em.ReplaceRange);

        var hr = await Complete(p, s, "SELECT * FROM hr.|");
        Assert.Equal("JOBS", Assert.Single(hr.Items).Label);
    }

    [Fact]
    public async Task Package_dot_lists_members_with_signatures()
    {
        using var p = new CompletionProvider(Meta());
        var r = await Complete(p, new FakeSession(), "BEGIN emp_pkg.| END;");
        Assert.Equal(["HIRE", "FIRE", "SALARY"], r.Items.Select(i => i.Label));
        Assert.Equal("HIRE(P_NAME IN VARCHAR2, P_SAL IN NUMBER := …)", r.Items[0].Detail);
        Assert.Equal(CompletionItemKind.Function, r.Items[2].Kind);
        Assert.Equal("SALARY(P_ID IN NUMBER) RETURN NUMBER", r.Items[2].Detail);

        var sys = await Complete(p, new FakeSession(), "BEGIN DBMS_OUTPUT.| END;");
        Assert.Equal("PUT_LINE", Assert.Single(sys.Items).Label);

        var schema = await Complete(p, new FakeSession(), "BEGIN hr.| END;");
        Assert.Equal("JOBS", Assert.Single(schema.Items).Label);
    }

    [Fact]
    public async Task Unqualified_column_position_lists_columns_of_all_tables_and_functions()
    {
        using var p = new CompletionProvider(Meta());
        var r = await Complete(p, new FakeSession(), "SELECT * FROM emp e JOIN dept d ON e.deptno = d.deptno WHERE |");
        Assert.Contains(r.Items, i => i.Label == "ENAME" && i.Detail!.StartsWith("EMP · ", StringComparison.Ordinal));
        Assert.Contains(r.Items, i => i.Label == "DNAME");
        Assert.Contains(r.Items, i => i is { Label: "NVL", Kind: CompletionItemKind.Function });
        Assert.True(r.Items.ToList().FindIndex(i => i.Kind == CompletionItemKind.Column) < r.Items.ToList().FindIndex(i => i.Kind == CompletionItemKind.Function));
    }

    [Fact]
    public async Task Without_session_gives_keywords_and_hints_only()
    {
        using var p = new CompletionProvider(Meta());
        var kw = await Complete(p, null, "sel|");
        Assert.Equal("select", Assert.Single(kw.Items).InsertText);

        var hints = await Complete(p, null, "SELECT /*+ use_|");
        Assert.Contains(hints.Items, i => i is { Label: "USE_NL", Kind: CompletionItemKind.Hint, InsertText: "use_nl" });

        var cols = await Complete(p, null, "SELECT e.| FROM emp e");
        Assert.Empty(cols.Items);
        Assert.False(cols.IsPartial);

        var none = await Complete(p, null, "SELECT 'e.|' FROM dual");
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task Unknown_table_or_provider_error_gives_no_items_not_an_exception()
    {
        using var p = new CompletionProvider(Meta());
        var r = await Complete(p, new FakeSession(), "SELECT x.| FROM nowhere x");
        Assert.Empty(r.Items);
    }

    /// <summary>PRD 「150 ms 안에 응답 못 하면 캐시 결과 먼저」. 느린 조회는 뒤에서 끝나 다음 호출이 쓴다.</summary>
    [Fact]
    public async Task Slow_metadata_returns_cached_items_first_then_full_list()
    {
        var slow = new SlowMetadata(Meta());
        using var p = new CompletionProvider(slow, null, TimeSpan.FromMilliseconds(150));
        var s = new FakeSession();

        // 객체 목록은 빠르게 캐시에 들어가 있다.
        await Complete(p, s, "SELECT * FROM |");

        slow.Gate = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();
        var first = await Complete(p, s, "SELECT * FROM emp e WHERE e.| = 1");
        sw.Stop();
        Assert.True(first.IsPartial);
        Assert.Empty(first.Items); // 열은 아직 캐시에 없다
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");

        var tables = await Complete(p, s, "SELECT * FROM emp, |");
        Assert.Contains(tables.Items, i => i.Label == "DEPT"); // 캐시 결과는 바로

        slow.Gate.SetResult();
        await slow.LastTableCall!;
        var second = await Complete(p, s, "SELECT * FROM emp e WHERE e.| = 1");
        Assert.False(second.IsPartial);
        Assert.Contains(second.Items, i => i.Label == "ENAME");
    }

    [Fact]
    public async Task Metadata_invalidated_event_drops_the_memory_cache_of_that_db()
    {
        var bus = new FakeEventBus();
        var meta = Meta();
        using var p = new CompletionProvider(meta, bus, CompletionProvider.DefaultBudget);
        var s = new FakeSession();
        await Complete(p, s, "SELECT * FROM |");
        Assert.True(p.Lookup.Count > 0);

        meta.Objects.Add(new DbObjectRef("SCOTT", "BONUS", "TABLE"));
        Assert.DoesNotContain((await Complete(p, s, "SELECT * FROM |")).Items, i => i.Label == "BONUS");

        bus.Publish(new MetadataInvalidated(s.DbKey, []));
        Assert.Contains((await Complete(p, s, "SELECT * FROM |")).Items, i => i.Label == "BONUS");
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var slow = new SlowMetadata(Meta()) { Gate = new TaskCompletionSource() };
        using var p = new CompletionProvider(slow, null, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(50);
        var (text, offset) = Caret("SELECT e.| FROM emp e");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.GetCompletionsAsync(new FakeSession(), text, offset, cts.Token));
        slow.Gate.SetResult();
    }

    // ---- 인자 도움말 ---------------------------------------------------------------------------

    [Fact]
    public async Task Parameter_hints_find_the_call_and_its_signatures()
    {
        var (text, offset) = Caret("BEGIN emp_pkg.hire('A', nvl(x, 1), |); END;");
        var call = ParameterHints.FindCallSite(text, offset);
        Assert.NotNull(call);
        Assert.Equal("EMP_PKG", call.Package);
        Assert.Equal("HIRE", call.Name);
        Assert.Equal(2, call.ArgumentIndex);

        var sigs = await ParameterHints.GetSignaturesAsync(Meta(), new FakeSession(), call, CancellationToken.None);
        Assert.Equal("HIRE(P_NAME IN VARCHAR2, P_SAL IN NUMBER := …)", ParameterHints.Format(Assert.Single(sigs)));

        var (t2, o2) = Caret("BEGIN dbms_output.put_line(|");
        var sys = await ParameterHints.GetSignaturesAsync(Meta(), new FakeSession(), ParameterHints.FindCallSite(t2, o2)!, CancellationToken.None);
        Assert.Equal("PUT_LINE", Assert.Single(sys).Name);

        Assert.Null(ParameterHints.FindCallSite("SELECT 1 FROM dual", 8));
        var (t3, o3) = Caret("SELECT (1 + |");
        Assert.Null(ParameterHints.FindCallSite(t3, o3));
    }

    /// <summary>테이블 조회만 <see cref="Gate"/> 가 열릴 때까지 붙잡는 제공자.</summary>
    private sealed class SlowMetadata(FakeMetadataProvider inner) : IMetadataProvider
    {
        public TaskCompletionSource? Gate { get; set; }

        public Task<TableInfo>? LastTableCall { get; private set; }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => inner.ListObjectsAsync(s, q, ct);

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
        {
            async Task<TableInfo> Run()
            {
                if (Gate is { } g)
                {
                    await g.Task.ConfigureAwait(false);
                }

                return await inner.GetTableAsync(s, table, ct).ConfigureAwait(false);
            }

            return LastTableCall = Run();
        }

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetProceduresAsync(s, obj, ct);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => inner.GetDdlAsync(s, obj, o, ct);

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetSourceAsync(s, obj, ct);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetCompileErrorsAsync(s, obj, ct);
    }
}
