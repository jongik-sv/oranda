using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.History;
using Oranda.Testing;

namespace Oranda.Core.Tests.Execution;

// Tibero 6.2.15 는 읽기 전용 트랜잭션을 서버가 지키지 않아(최상위 -7001, PSM 블록 안은 받지만 쓰기를 막지 않음) 앱 관문만 쓰기를 막는다.
// SELECT … FOR UPDATE 는 분류가 Query 지만 Tibero 에서는 행이 실제로 잠기므로 읽기 전용 프로필 + Tibero 일 때만 쓰기로 보고 거부한다. Oracle 은 그대로(서버가 ORA-01456 으로 막음).
public sealed class TiberoReadOnlyForUpdateTests
{
    private static readonly ExecutionPolicy Policy = new();

    private static PolicyContext Ctx(Dbms dbms, bool readOnly, HistorySource source = HistorySource.Editor) =>
        new(TestData.Profile(readOnly: readOnly) with { Dbms = dbms }, TestData.Server(), source);

    private static SqlStatement Query(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private static PolicyDecision Eval(PolicyContext ctx, SqlStatement stmt) => Eval(Policy, ctx, stmt);

    private static PolicyDecision Eval(ExecutionPolicy policy, PolicyContext ctx, SqlStatement stmt) =>
        policy.EvaluateAsync(ctx, stmt, default).AsTask().GetAwaiter().GetResult();

    // ---- 막힘: 읽기 전용 + Tibero ----

    [Theory]
    [InlineData("SELECT * FROM emp FOR UPDATE")]
    [InlineData("select * from emp for update")]
    [InlineData("SELECT * FROM emp WHERE id = 1 FOR UPDATE OF sal")]
    [InlineData("SELECT * FROM emp FOR UPDATE NOWAIT")]
    [InlineData("SELECT * FROM emp FOR UPDATE WAIT 5")]
    [InlineData("SELECT * FROM emp FOR UPDATE SKIP LOCKED")]
    [InlineData("SELECT * FROM emp FOR UPDATE OF e.sal NOWAIT")]
    [InlineData("SELECT * FROM emp\nFOR\n   UPDATE")]
    [InlineData("SELECT * FROM emp FOR /* lock */ UPDATE")]
    [InlineData("SELECT * FROM emp FOR/**/UPDATE")]
    [InlineData("SELECT * FROM emp FOR -- lock\n UPDATE")]
    [InlineData("SELECT '-- x' AS a FROM emp FOR UPDATE")]
    [InlineData("WITH x AS (SELECT * FROM emp) SELECT * FROM x FOR UPDATE")]
    public void Read_only_tibero_profile_denies_select_for_update(string sql)
    {
        var d = Eval(Ctx(Dbms.Tibero, readOnly: true), Query(sql));

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.ReadOnlyProfile, d.Rule);
        Assert.Equal(ExecutionPolicy.TiberoForUpdateDeniedMessage, d.Message);
    }

    [Fact]
    public void Agent_source_on_a_read_only_tibero_profile_is_denied_too()
    {
        var d = Eval(Ctx(Dbms.Tibero, readOnly: true, HistorySource.Agent), Query("SELECT * FROM emp FOR UPDATE"));

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.ReadOnlyProfile, d.Rule);
    }

    // ---- 지금 동작 그대로 ----

    [Theory]
    [InlineData("SELECT * FROM emp FOR UPDATE")]
    [InlineData("SELECT * FROM emp FOR UPDATE NOWAIT")]
    [InlineData("SELECT * FROM emp FOR UPDATE SKIP LOCKED")]
    public void Oracle_read_only_profile_still_passes_select_for_update_to_the_server(string sql)
    {
        Assert.Same(PolicyDecision.Allowed, Eval(Ctx(Dbms.Oracle, readOnly: true), Query(sql)));
    }

    [Fact]
    public void Tibero_profile_that_is_not_read_only_still_allows_select_for_update()
    {
        Assert.Same(PolicyDecision.Allowed, Eval(Ctx(Dbms.Tibero, readOnly: false), Query("SELECT * FROM emp FOR UPDATE")));
    }

    [Theory]
    [InlineData("SELECT * FROM emp")]
    [InlineData("SELECT * FROM emp WHERE name = 'FOR UPDATE'")]
    [InlineData("SELECT 'a FOR UPDATE b' FROM dual")]
    [InlineData("SELECT * FROM emp -- FOR UPDATE")]
    [InlineData("SELECT * FROM emp /* FOR UPDATE */")]
    [InlineData("SELECT * /* FOR\nUPDATE */ FROM emp")]
    [InlineData("SELECT \"FOR UPDATE\" FROM emp")]
    [InlineData("SELECT q'[x FOR UPDATE y]' FROM dual")]
    [InlineData("SELECT 'it''s' AS a FROM emp WHERE note = 'FOR UPDATE'")]
    [InlineData("SELECT forecast, update_flag, for_update FROM emp")]
    [InlineData("SELECT * FROM emp ORDER BY for_x, update_y")]
    public void Read_only_tibero_profile_allows_plain_queries_and_for_update_text_inside_strings_and_comments(string sql)
    {
        Assert.Same(PolicyDecision.Allowed, Eval(Ctx(Dbms.Tibero, readOnly: true), Query(sql)));
    }

    [Fact]
    public void Other_writes_on_a_read_only_tibero_profile_keep_the_ordinary_message()
    {
        var d = Eval(Ctx(Dbms.Tibero, readOnly: true), new SqlStatement("UPDATE emp SET sal = 1", default, StatementKind.Dml) { Verb = "UPDATE" });

        Assert.Equal(PolicyRule.ReadOnlyProfile, d.Rule);
        Assert.NotEqual(ExecutionPolicy.TiberoForUpdateDeniedMessage, d.Message);
    }

    // ---- 조회 전용 Role 경로 ----

    [Theory]
    [InlineData("SELECT * FROM emp FOR UPDATE")]
    [InlineData("SELECT * FROM emp FOR UPDATE SKIP LOCKED")]
    public void Viewer_role_on_a_tibero_profile_denies_select_for_update_with_the_tibero_reason(string sql)
    {
        var viewer = new ExecutionPolicy(FakeFeatureGate.ForRoles(RoleIds.Viewer));

        var d = Eval(viewer, Ctx(Dbms.Tibero, readOnly: false), Query(sql));

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.ReadOnlyRole, d.Rule);
        Assert.Equal(ExecutionPolicy.TiberoForUpdateDeniedMessage, d.Message);
    }

    [Fact]
    public void Viewer_role_keeps_current_behavior_on_oracle_and_for_plain_tibero_queries()
    {
        var viewer = new ExecutionPolicy(FakeFeatureGate.ForRoles(RoleIds.Viewer));

        Assert.Same(PolicyDecision.Allowed, Eval(viewer, Ctx(Dbms.Oracle, readOnly: false), Query("SELECT * FROM emp FOR UPDATE")));
        Assert.Same(PolicyDecision.Allowed, Eval(viewer, Ctx(Dbms.Tibero, readOnly: false), Query("SELECT 'FOR UPDATE' FROM emp")));
    }

    // ---- 잠금 절 판정기 ----

    [Theory]
    [InlineData("SELECT * FROM t FOR UPDATE", true)]
    [InlineData("SELECT * FROM t FOR   update of a.b NOWAIT", true)]
    [InlineData("SELECT * FROM t FOR/**/UPDATE", true)] // 주석은 공백으로 읽힌다
    [InlineData("SELECT * FROM t", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("SELECT 'unterminated FOR UPDATE", false)]
    [InlineData("SELECT * FROM t /* unterminated FOR UPDATE", false)]
    [InlineData("SELECT * FROM t WHERE a = q'#FOR UPDATE#' FOR UPDATE", true)]
    [InlineData("SELECT * FROM t WHERE a = q'#FOR UPDATE#'", false)]
    public void HasForUpdate_looks_only_outside_strings_quoted_names_and_comments(string? sql, bool expected)
    {
        Assert.Equal(expected, SelectLocking.HasForUpdate(sql));
    }
}
