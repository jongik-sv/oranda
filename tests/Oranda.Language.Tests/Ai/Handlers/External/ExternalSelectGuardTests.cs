using Oranda.Core.Ai.Gate;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers.External;

/// <summary>
/// 밖의 에이전트 자동 SELECT 의 문장 검사(<see cref="IExternalSelectGuard"/>, 계획 agent-cli-phase2 §3.1): query 에서 뽑아 run_tab·export_result 가 함께 쓴다.
/// </summary>
public sealed class ExternalSelectGuardTests : IAsyncDisposable
{
    private static readonly ExternalSelectOptions QueryMode = new();
    private static readonly ExternalSelectOptions TabMode = new() { VerifyUserFunctions = true, RequireResolvedNames = true };

    private readonly FakeExtractor _extractor = new();
    private readonly FakeUserFunctions _functions = new();
    private readonly FakeExecutionService _exec = new();
    private readonly FakeSession _session = new();

    /// <summary>세션의 현재 스키마(가드가 검사 직전에 읽는 값). 기본 SCOTT = 접속 사용자.</summary>
    private string? _sessionSchema = "SCOTT";

    public ExternalSelectGuardTests()
    {
        _exec.Responder = call => ExternalFakes.SchemaRow(call, _sessionSchema);
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private ExternalSelectGuard Guard(bool withChecker = true) => new(new LanguageService(), _extractor, withChecker ? _functions : null, _exec);

    private Task<ExternalSelectCheck> CheckAsync(string sql, ExternalSelectOptions options, bool withChecker = true, CancellationToken ct = default) =>
        Guard(withChecker).CheckAsync(_session, sql, options, ct).AsTask();

    [Fact]
    public async Task One_plain_select_passes_and_gives_back_the_statement_with_its_binds()
    {
        var check = await CheckAsync("SELECT * FROM emp WHERE emp_id = :id", TabMode);

        Assert.True(check.Allowed);
        Assert.Null(check.Code);
        Assert.Equal(StatementKind.Query, check.Statement!.Kind);
        Assert.Equal(["id"], check.Statement.BindNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SELECT 1 FROM dual; SELECT 2 FROM dual")]
    [InlineData("DELETE FROM emp")]
    [InlineData("UPDATE emp SET sal = 0")]
    [InlineData("CREATE TABLE t (a NUMBER)")]
    [InlineData("BEGIN NULL; END;")]
    [InlineData("COMMIT")]
    public async Task Anything_but_one_select_is_not_single_query(string sql)
    {
        var check = await CheckAsync(sql, TabMode);

        Assert.False(check.Allowed);
        Assert.Equal(ExternalSelectRefusal.NotSingleQuery, check.Refusal);
        Assert.Equal("sql_not_single_query", check.Code);
        Assert.Null(check.Statement);
    }

    [Fact]
    public async Task Analyzer_seeing_another_kind_or_count_is_not_single_query()
    {
        _extractor.Info = Info() with { StatementCount = 2 };

        var check = await CheckAsync("SELECT * FROM emp", QueryMode);

        Assert.Equal("sql_not_single_query", check.Code);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Locking_db_links_and_forbidden_calls_are_unsafe(bool locking, bool remote, bool forbidden)
    {
        _extractor.Info = Info() with { HasLockingClause = locking, HasRemoteReference = remote, HasForbiddenCall = forbidden };

        var check = await CheckAsync("SELECT * FROM emp", TabMode);

        Assert.Equal(ExternalSelectRefusal.Unsafe, check.Refusal);
        Assert.Equal("sql_unsafe", check.Code);
        Assert.Equal(0, _functions.Calls);
    }

    [Fact]
    public async Task Query_mode_refuses_every_user_function_without_asking_the_checker()
    {
        _extractor.Info = Info() with { CallsUserFunction = true, UserFunctions = ["MY_FN"] };
        _functions.Verdict = UserFunctionVerdict.Safe;

        var check = await CheckAsync("SELECT my_fn(1) FROM emp", QueryMode);

        Assert.Equal(ExternalSelectRefusal.UserFunction, check.Refusal);
        Assert.Equal("sql_unsafe", check.Code);
        Assert.Equal(0, _functions.Calls);
    }

    [Theory]
    [InlineData(UserFunctionVerdict.Safe, true)]
    [InlineData(UserFunctionVerdict.Autonomous, false)]
    [InlineData(UserFunctionVerdict.External, false)]
    [InlineData(UserFunctionVerdict.Unverified, false)]
    public async Task Tab_mode_runs_user_functions_only_when_the_checker_says_safe(UserFunctionVerdict verdict, bool allowed)
    {
        _extractor.Info = Info() with { CallsUserFunction = true, UserFunctions = ["MY_FN", "PKG.F"] };
        _functions.Verdict = verdict;

        _sessionSchema = "APP";

        var check = await CheckAsync("SELECT my_fn(1), pkg.f(2) FROM emp", TabMode);

        Assert.Equal(allowed, check.Allowed);
        Assert.Equal(allowed ? null : "sql_unsafe", check.Code);
        Assert.Equal(["MY_FN", "PKG.F"], _functions.LastFunctions);
        Assert.Equal("APP", _functions.LastSchema); // 함수도 세션이 실행하는 스키마에서 찾는다
    }

    [Fact]
    public async Task Unclassified_calls_and_a_missing_checker_are_refused_even_in_tab_mode()
    {
        _extractor.Info = Info() with { CallsUserFunction = true, UserFunctions = [] };
        var unclassified = await CheckAsync("SELECT x.y() FROM emp", TabMode);

        _extractor.Info = Info() with { CallsUserFunction = true, UserFunctions = ["MY_FN"] };
        var noChecker = await CheckAsync("SELECT my_fn(1) FROM emp", TabMode, withChecker: false);

        Assert.Equal(ExternalSelectRefusal.UserFunction, unclassified.Refusal);
        Assert.Equal(ExternalSelectRefusal.UserFunction, noChecker.Refusal);
        Assert.Equal(0, _functions.Calls);
    }

    [Fact]
    public async Task Unresolved_names_are_refused_only_when_asked()
    {
        _extractor.Info = Info() with { IsUncertain = true };

        var tab = await CheckAsync("SELECT * FROM nope", TabMode);
        var query = await CheckAsync("SELECT * FROM nope", QueryMode);

        Assert.Equal(ExternalSelectRefusal.Unresolved, tab.Refusal);
        Assert.Equal("sql_not_single_query", tab.Code);
        Assert.True(query.Allowed); // query 는 게이트가 이미 거부했다
    }

    [Fact]
    public async Task Names_resolve_in_the_session_current_schema_read_right_before_the_check()
    {
        // 탭 세션이 ALTER SESSION SET CURRENT_SCHEMA = APP(또는 PL/SQL 블록·로그온 트리거)로 바꿨다: 접속 사용자 SCOTT 가 아니라 APP 로 푼다.
        _sessionSchema = "APP";
        _extractor.ByDefaultSchema["APP"] = Info([new AgentObjectRef("APP", "EMP", "TABLE")]);

        var check = await CheckAsync("SELECT * FROM emp", TabMode);

        Assert.True(check.Allowed);
        Assert.Equal(["APP"], _extractor.Schemas);
        var read = Assert.Single(_exec.Calls);
        Assert.Same(_session, read.Session);
        Assert.Equal(ExternalSelectGuard.CurrentSchemaSql, read.Statement.Text);
        Assert.Equal(StatementKind.Query, read.Statement.Kind);
        Assert.Equal(Oranda.Core.History.HistorySource.Agent, read.Options.Source);
        Assert.True(read.Options.SkipHistory);
    }

    [Fact]
    public async Task Caller_schema_and_session_schema_must_resolve_to_the_same_objects()
    {
        _extractor.ByDefaultSchema["APP"] = Info([new AgentObjectRef("APP", "EMP")]);
        _extractor.ByDefaultSchema["SCOTT"] = Info([new AgentObjectRef("SCOTT", "EMP")]);

        var mismatch = await CheckAsync("SELECT * FROM emp", QueryMode with { DefaultSchema = "APP" });
        _extractor.Schemas.Clear();
        var same = await CheckAsync("SELECT * FROM emp", QueryMode with { DefaultSchema = "scott" });
        var sameSchemas = _extractor.Schemas.ToList();
        _sessionSchema = "APP"; // 게이트는 SCOTT 로 봤는데 세션은 APP 로 바뀌어 있다
        var switched = await CheckAsync("SELECT * FROM emp", QueryMode with { DefaultSchema = "SCOTT" });
        var noCompare = await CheckAsync("SELECT * FROM emp", QueryMode);

        Assert.Equal(ExternalSelectRefusal.SchemaMismatch, mismatch.Refusal);
        Assert.Equal("sql_schema_unresolved", mismatch.Code);
        Assert.True(same.Allowed);
        Assert.Equal(["SCOTT"], sameSchemas); // 같은 스키마면 한 번만 푼다
        Assert.Equal(ExternalSelectRefusal.SchemaMismatch, switched.Refusal);
        Assert.True(noCompare.Allowed);
    }

    public static TheoryData<string> SchemaReadFailures => ["failed", "denied", "empty", "blank", "no-rows", "throws"];

    [Theory]
    [MemberData(nameof(SchemaReadFailures))]
    public async Task Unreadable_session_schema_is_refused_before_any_name_is_resolved(string failure)
    {
        _extractor.Info = Info() with { CallsUserFunction = true, UserFunctions = ["MY_FN"] };
        _functions.Verdict = UserFunctionVerdict.Safe;
        _exec.Responder = call => failure switch
        {
            "failed" => new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "secret detail") },
            "denied" => new ExecutionResult(call.Statement, ExecutionStatus.Denied),
            "empty" => ExternalFakes.SchemaRow(call, null),
            "blank" => ExternalFakes.SchemaRow(call, "  "),
            "no-rows" => new ExecutionResult(call.Statement, ExecutionStatus.Succeeded)
            {
                ResultSet = new FakeResultSet([new Oranda.Core.Grid.ResultColumn(0, "CURRENT_SCHEMA", "VARCHAR2", Oranda.Core.Grid.ColumnDataKind.Text)], []),
            },
            _ => throw new InvalidOperationException("ORA-03113 secret detail"),
        };

        var check = await CheckAsync("SELECT my_fn(1) FROM emp", TabMode);

        Assert.False(check.Allowed);
        Assert.Equal(ExternalSelectRefusal.SchemaUnknown, check.Refusal);
        Assert.Equal("sql_schema_unresolved", check.Code);
        Assert.Equal(ExternalSelectGuard.SchemaUnknownText, check.Message);
        Assert.DoesNotContain("secret", check.Message, StringComparison.Ordinal);
        Assert.Empty(_extractor.Schemas);
        Assert.Equal(0, _functions.Calls);
    }

    [Fact]
    public async Task Cancel_during_the_schema_read_is_not_a_refusal()
    {
        using var cts = new CancellationTokenSource();
        _exec.Responder = _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CheckAsync("SELECT * FROM emp", TabMode, ct: cts.Token));
        Assert.Empty(_extractor.Schemas);
    }

    [Fact]
    public async Task Statements_that_are_not_one_select_are_refused_without_reading_the_schema()
    {
        var check = await CheckAsync("DELETE FROM emp", TabMode);

        Assert.Equal(ExternalSelectRefusal.NotSingleQuery, check.Refusal);
        Assert.Empty(_exec.Calls);
    }

    [Theory]
    [InlineData("SELECT * FROM emp FOR UPDATE", "sql_unsafe")]
    [InlineData("SELECT * FROM emp@lnk", "sql_unsafe")]
    [InlineData("SELECT seq_x.NEXTVAL FROM dual", "sql_unsafe")]
    [InlineData("SELECT my_fn(emp_id) FROM emp", "sql_unsafe")]
    public async Task Real_extractor_flags_what_the_guard_refuses(string sql, string code)
    {
        var meta = new FakeMetadataProvider();
        meta.NameSteps[("SCOTT", "EMP")] = NameStep.TableOrView;
        meta.NameSteps[("SYS", "DUAL")] = NameStep.TableOrView;
        var guard = new ExternalSelectGuard(new LanguageService(), new AgentStatementReferenceExtractor(new LanguageService(), meta), new FakeUserFunctions(), _exec);

        var check = await guard.CheckAsync(new FakeSession(), sql, QueryMode, CancellationToken.None);

        Assert.Equal(code, check.Code);
    }

    [Fact]
    public async Task Objects_missing_from_the_dictionary_are_schema_unresolved_in_every_mode_before_anything_runs()
    {
        _extractor.Info = Info([new AgentObjectRef("SCOTT", "EMP"), new AgentObjectRef("SCOTT", "NOPE")]);
        _extractor.Missing.Add("SCOTT.NOPE");

        var tab = await CheckAsync("SELECT * FROM emp, nope", TabMode);
        var query = await CheckAsync("SELECT * FROM emp, nope", QueryMode);

        Assert.Equal(ExternalSelectRefusal.NotFound, tab.Refusal);
        Assert.Equal("sql_schema_unresolved", tab.Code);
        Assert.StartsWith(ExternalSelectGuard.NotFoundText, tab.Message, StringComparison.Ordinal);
        Assert.Contains("SCOTT.NOPE", tab.Message, StringComparison.Ordinal);
        Assert.Null(tab.Statement);
        Assert.Equal(ExternalSelectRefusal.NotFound, query.Refusal); // query 도: 게이트는 사전에 있는지 보지 않는다
        Assert.Equal(["\"SCOTT\".\"EMP\"", "\"SCOTT\".\"NOPE\"", "\"SCOTT\".\"EMP\"", "\"SCOTT\".\"NOPE\""], _extractor.Resolved);
        Assert.All(_exec.Calls, c => Assert.Equal(ExternalSelectGuard.CurrentSchemaSql, c.Statement.Text)); // 실행은 현재 스키마 읽기뿐
    }

    [Fact]
    public async Task Dual_is_never_looked_up()
    {
        _extractor.Info = Info([new AgentObjectRef("SYS", "DUAL"), new AgentObjectRef("SCOTT", "DUAL")]);
        _extractor.Missing.UnionWith(["SYS.DUAL", "SCOTT.DUAL"]);

        var check = await CheckAsync("SELECT 1 FROM dual", TabMode);

        Assert.True(check.Allowed);
        Assert.Empty(_extractor.Resolved);
    }

    /// <summary>실제 추출기 + 가짜 사전: SCOTT.EMP·APP.EMP(표), SCOTT.E_SYN → APP.EMP(개인 시노님), PUBLIC.P_SYN → APP.EMP, PUBLIC.DUAL → SYS.DUAL, SCOTT.DANGLING → APP.GONE(대상 없음).</summary>
    private ExternalSelectGuard RealGuard()
    {
        var meta = new FakeMetadataProvider();
        meta.NameSteps[("SCOTT", "EMP")] = NameStep.TableOrView;
        meta.NameSteps[("SCOTT", "DEPT")] = NameStep.TableOrView;
        meta.NameSteps[("APP", "EMP")] = NameStep.TableOrView;
        meta.NameSteps[("SCOTT", "E_SYN")] = NameStep.Synonym("APP", "EMP");
        meta.NameSteps[("PUBLIC", "P_SYN")] = NameStep.Synonym("APP", "EMP");
        meta.NameSteps[("PUBLIC", "DUAL")] = NameStep.Synonym("SYS", "DUAL");
        meta.NameSteps[("SYS", "DUAL")] = NameStep.TableOrView;
        meta.NameSteps[("SCOTT", "DANGLING")] = NameStep.Synonym("APP", "GONE");

        // 사전 뷰: Oracle 모양(PUBLIC 시노님 → SYS 뷰, V$ → V_$). V$SQL 은 대상이 사전에 안 보인다(권한·유형 — Tibero 실측 전) 고 둔다.
        foreach (var v in new[] { "USER_TABLES", "ALL_OBJECTS", "DBA_TABLES" })
        {
            meta.NameSteps[("PUBLIC", v)] = NameStep.Synonym("SYS", v);
            meta.NameSteps[("SYS", v)] = NameStep.TableOrView;
        }

        foreach (var v in new[] { "V$SESSION", "GV$SESSION", "V$SQL" })
        {
            meta.NameSteps[("PUBLIC", v)] = NameStep.Synonym("SYS", v.Replace("$", "_$", StringComparison.Ordinal));
        }

        meta.NameSteps[("SYS", "V_$SESSION")] = NameStep.TableOrView;
        meta.NameSteps[("SYS", "GV_$SESSION")] = NameStep.TableOrView;
        return new ExternalSelectGuard(new LanguageService(), new AgentStatementReferenceExtractor(new LanguageService(), meta), new FakeUserFunctions(), _exec);
    }

    [Theory]
    [InlineData("SELECT * FROM nope")]
    [InlineData("SELECT * FROM app.nope")]
    [InlineData("SELECT * FROM b.emp")]
    [InlineData("SELECT * FROM emp e WHERE EXISTS (SELECT 1 FROM nope n WHERE n.id = e.id)")]
    [InlineData("SELECT * FROM (SELECT * FROM nope) v")]
    [InlineData("WITH c AS (SELECT * FROM nope) SELECT * FROM c")]
    [InlineData("SELECT * FROM emp UNION ALL SELECT * FROM nope")]
    [InlineData("SELECT * FROM dangling")]
    public async Task Real_extractor_names_missing_from_the_dictionary_are_refused(string sql)
    {
        foreach (var mode in new[] { TabMode, QueryMode })
        {
            var check = await RealGuard().CheckAsync(_session, sql, mode, CancellationToken.None);

            Assert.False(check.Allowed, sql);
            Assert.Equal("sql_schema_unresolved", check.Code);
        }
    }

    [Theory]
    [InlineData("SELECT * FROM emp")]
    [InlineData("SELECT * FROM EMP")]
    [InlineData("SELECT * FROM \"EMP\"")]
    [InlineData("SELECT * FROM scott.emp")]
    [InlineData("SELECT * FROM app.emp")]
    [InlineData("SELECT * FROM \"APP\".\"EMP\"")]
    [InlineData("SELECT e.empno, x.cnt FROM emp e, (SELECT deptno, COUNT(*) cnt FROM dept GROUP BY deptno) x WHERE x.deptno = e.deptno")]
    [InlineData("WITH a AS (SELECT * FROM emp), b AS (SELECT * FROM a) SELECT * FROM b JOIN a ON a.id = b.id")]
    [InlineData("WITH r (n) AS (SELECT 1 FROM dual UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r")]
    [InlineData("SELECT 1 FROM dual")]
    [InlineData("SELECT sysdate FROM sys.dual")]
    [InlineData("SELECT e.ename AS name FROM emp e WHERE e.sal > (SELECT AVG(sal) FROM emp)")]
    [InlineData("SELECT * FROM e_syn")]
    [InlineData("SELECT * FROM p_syn")]
    [InlineData("SELECT table_name FROM user_tables")]
    [InlineData("SELECT owner, object_name FROM all_objects WHERE object_type = 'TABLE'")]
    [InlineData("SELECT * FROM dba_tables")]
    [InlineData("SELECT * FROM sys.dba_tables")]
    [InlineData("SELECT sid, serial# FROM v$session")]
    [InlineData("SELECT * FROM V$SESSION s JOIN gv$session g ON g.sid = s.sid")]
    [InlineData("SELECT sql_id FROM v$sql")]
    [InlineData("SELECT * FROM sys.v_$sql")]
    [InlineData("SELECT * FROM syscat.all_tables")]
    public async Task Real_extractor_ctes_inline_views_dual_aliases_and_synonyms_pass(string sql)
    {
        foreach (var mode in new[] { TabMode, QueryMode })
        {
            var check = await RealGuard().CheckAsync(_session, sql, mode, CancellationToken.None);

            Assert.True(check.Allowed, $"{sql}: {check.Code} {check.Message}");
        }
    }

    [Fact]
    public async Task Dictionary_owner_exemption_does_not_apply_when_the_session_schema_is_that_owner()
    {
        // ALTER SESSION SET CURRENT_SCHEMA = SYS 뒤 소유자 없는 없는 이름은 SYS.NOPE 로 남는다: 시노님을 거친 사전 객체가 아니므로 거부.
        _sessionSchema = "SYS";

        var check = await RealGuard().CheckAsync(_session, "SELECT * FROM nope", TabMode, CancellationToken.None);

        Assert.Equal(ExternalSelectRefusal.NotFound, check.Refusal);
        Assert.Contains("SYS.NOPE", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_extractor_looks_names_up_in_the_session_current_schema()
    {
        // 세션이 APP 로 바뀌었다: DEPT 는 SCOTT 에만 있으므로 거부, EMP 는 APP 에 있어 통과.
        _sessionSchema = "APP";

        var dept = await RealGuard().CheckAsync(_session, "SELECT * FROM dept", TabMode, CancellationToken.None);
        var emp = await RealGuard().CheckAsync(_session, "SELECT * FROM emp", TabMode, CancellationToken.None);

        Assert.Equal(ExternalSelectRefusal.NotFound, dept.Refusal);
        Assert.Contains("APP.DEPT", dept.Message, StringComparison.Ordinal);
        Assert.True(emp.Allowed);
    }

    [Fact]
    public void Gate_flags_for_read_only_or_substituted_runs_block_query()
    {
        var plain = ExternalFakes.Db(new System.Text.Json.Nodes.JsonObject());
        Assert.False(ExternalSelectGuard.GateFlagsUserFunction(plain));
        Assert.True(ExternalSelectGuard.GateFlagsUserFunction(plain with { ReadOnlyExecution = true }));
        Assert.True(ExternalSelectGuard.GateFlagsUserFunction(plain with { Substitutions = [null, "user_function_unverified"] }));
        Assert.False(ExternalSelectGuard.GateFlagsUserFunction(plain with { Substitutions = [null] }));
    }

    private static AgentStatementInfo Info(IReadOnlyList<AgentObjectRef>? objects = null) =>
        new(StatementKind.Query, IsUncertain: false, objects ?? [new AgentObjectRef("SCOTT", "EMP", "TABLE")], StatementCount: 1);

    private sealed class FakeExtractor : IStatementReferenceExtractor
    {
        public AgentStatementInfo Info { get; set; } = ExternalSelectGuardTests.Info();

        public Dictionary<string, AgentStatementInfo> ByDefaultSchema { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>분석마다 받은 기본 스키마(null 은 빈 문자열).</summary>
        public List<string> Schemas { get; } = [];

        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct)
        {
            Schemas.Add(defaultSchema ?? string.Empty);
            return ValueTask.FromResult(ByDefaultSchema.TryGetValue(defaultSchema ?? string.Empty, out var info) ? info : Info);
        }

        /// <summary>사전에 없는 객체 키(<c>OWNER.NAME</c>). 나머지는 모두 있다고 답한다.</summary>
        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

        /// <summary>존재 확인으로 받은 이름(쓰인 그대로).</summary>
        public List<string> Resolved { get; } = [];

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct)
        {
            Resolved.Add(name);
            var parts = name.Split('.', 2).Select(p => p.Trim('"')).ToArray();
            var obj = new AgentObjectRef(parts[0], parts[1]);
            return ValueTask.FromResult(Missing.Contains(obj.Key) ? null : obj);
        }

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeUserFunctions : IUserFunctionSafetyChecker
    {
        public UserFunctionVerdict Verdict { get; set; } = UserFunctionVerdict.Unverified;

        public int Calls { get; private set; }

        public IReadOnlyList<string>? LastFunctions { get; private set; }

        public string? LastSchema { get; private set; }

        public ValueTask<UserFunctionVerdict> CheckAsync(ISession db, IReadOnlyList<string> functions, string? defaultSchema, CancellationToken ct)
        {
            Calls++;
            LastFunctions = functions;
            LastSchema = defaultSchema;
            return ValueTask.FromResult(Verdict);
        }
    }
}
