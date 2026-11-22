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
public class ExternalSelectGuardTests
{
    private static readonly ExternalSelectOptions QueryMode = new();
    private static readonly ExternalSelectOptions TabMode = new() { VerifyUserFunctions = true, RequireResolvedNames = true };

    private readonly FakeExtractor _extractor = new();
    private readonly FakeUserFunctions _functions = new();

    private ExternalSelectGuard Guard(bool withChecker = true) => new(new LanguageService(), _extractor, withChecker ? _functions : null);

    private Task<ExternalSelectCheck> CheckAsync(string sql, ExternalSelectOptions options, bool withChecker = true) =>
        Guard(withChecker).CheckAsync(new FakeSession(), sql, options, CancellationToken.None).AsTask();

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

        var check = await CheckAsync("SELECT my_fn(1), pkg.f(2) FROM emp", TabMode with { DefaultSchema = "APP" });

        Assert.Equal(allowed, check.Allowed);
        Assert.Equal(allowed ? null : "sql_unsafe", check.Code);
        Assert.Equal(["MY_FN", "PKG.F"], _functions.LastFunctions);
        Assert.Equal("APP", _functions.LastSchema);
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
    public async Task Current_schema_and_executing_schema_must_resolve_to_the_same_objects()
    {
        _extractor.ByDefaultSchema["APP"] = Info([new AgentObjectRef("APP", "EMP")]);
        _extractor.ByDefaultSchema[""] = Info([new AgentObjectRef("SCOTT", "EMP")]);

        var mismatch = await CheckAsync("SELECT * FROM emp", QueryMode with { DefaultSchema = "APP", ExecutingSchema = "SCOTT" });
        var same = await CheckAsync("SELECT * FROM emp", QueryMode with { DefaultSchema = "scott", ExecutingSchema = "SCOTT" });
        var noCompare = await CheckAsync("SELECT * FROM emp", QueryMode with { DefaultSchema = "APP" });

        Assert.Equal(ExternalSelectRefusal.SchemaMismatch, mismatch.Refusal);
        Assert.Equal("sql_schema_unresolved", mismatch.Code);
        Assert.True(same.Allowed);
        Assert.True(noCompare.Allowed);
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
        var guard = new ExternalSelectGuard(new LanguageService(), new AgentStatementReferenceExtractor(new LanguageService(), meta), new FakeUserFunctions());

        var check = await guard.CheckAsync(new FakeSession(), sql, QueryMode, CancellationToken.None);

        Assert.Equal(code, check.Code);
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

        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct) =>
            ValueTask.FromResult(ByDefaultSchema.TryGetValue(defaultSchema ?? string.Empty, out var info) ? info : Info);

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct) => throw new NotSupportedException();

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
