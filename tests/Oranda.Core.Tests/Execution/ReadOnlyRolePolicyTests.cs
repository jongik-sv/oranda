using System.Globalization;
using System.Text;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.History;
using Oranda.Testing;

namespace Oranda.Core.Tests.Execution;

/// <summary>
/// TSK-07-05 정책 관문 ReadOnlyRole 규칙(TRD 10.7.4, D-006): 조회 전용 Role 하나만 고르면 질의·트랜잭션 제어·SQL*Plus·EXPLAIN 만 통과,
/// DML·DDL·PL/SQL·분류 불확실은 거부. 다른 Role 과 함께 고르거나 게이트가 없으면 규칙이 꺼진다. 자리는 읽기 전용 프로필 바로 뒤·라이선스 앞.
/// 실제 분류기(언어 계층)로 가르는 경계 사례는 App 시험 <c>ReadOnlyRoleExecutionTests</c> 에 있다(Core 시험은 Language 를 참조하지 않는다).
/// </summary>
public sealed class ReadOnlyRolePolicyTests
{
    private static PolicyContext Ctx(ConnectionEnvironment env = ConnectionEnvironment.Development, bool readOnly = false,
                                     LicensePack held = LicensePack.None, LicensePack required = LicensePack.None,
                                     HistorySource source = HistorySource.Editor) =>
        new(TestData.Profile(env, readOnly, held), TestData.Server(), source) { RequiredLicense = required };

    private static SqlStatement Stmt(string text, StatementKind kind, string? verb = null) =>
        new(text, default, kind) { Verb = verb ?? text.Split(' ')[0].ToUpperInvariant() };

    private static PolicyDecision Eval(ExecutionPolicy policy, PolicyContext ctx, SqlStatement stmt) =>
        policy.EvaluateAsync(ctx, stmt, default).AsTask().GetAwaiter().GetResult();

    private static ExecutionPolicy Viewer() => new(FakeFeatureGate.ForRoles(RoleIds.Viewer));

    private static readonly CompositeFormat DeniedFormat = CompositeFormat.Parse(FeatureTexts.ReadOnlyRoleDeniedMessage);

    private static string Denied(string what) => string.Format(CultureInfo.InvariantCulture, DeniedFormat, what);

    // ---- 정상: 조회 전용 Role 에서도 통과 ----

    [Theory]
    [InlineData("SELECT * FROM emp", StatementKind.Query, "SELECT")]
    [InlineData("WITH x AS (SELECT 1 a FROM dual) SELECT * FROM x", StatementKind.Query, "WITH")]
    [InlineData("(SELECT 1 FROM dual)", StatementKind.Query, null)]
    [InlineData("COMMIT", StatementKind.TransactionControl, "COMMIT")]
    [InlineData("ROLLBACK", StatementKind.TransactionControl, "ROLLBACK")]
    [InlineData("SAVEPOINT a", StatementKind.TransactionControl, "SAVEPOINT")]
    [InlineData("SET TRANSACTION READ ONLY", StatementKind.TransactionControl, "SET TRANSACTION")]
    [InlineData("SET SERVEROUTPUT ON", StatementKind.SqlPlus, "SET")]
    [InlineData("DESC emp", StatementKind.SqlPlus, "DESC")]
    [InlineData("EXPLAIN PLAN FOR SELECT 1 FROM dual", StatementKind.Other, "EXPLAIN")]
    [InlineData("explain plan for update emp set sal = 1", StatementKind.Other, "EXPLAIN")]
    public void Viewer_only_allows_reads(string text, StatementKind kind, string? verb)
    {
        var d = Eval(Viewer(), Ctx(), new SqlStatement(text, default, kind) { Verb = verb });

        Assert.Equal(PolicyDecisionKind.Allow, d.Kind);
        Assert.Equal(PolicyRule.None, d.Rule);
    }

    // ---- 거부: DML·DDL·PL/SQL·CALL·분류 불확실 ----

    [Theory]
    [InlineData("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml, "UPDATE")]
    [InlineData("DELETE FROM emp WHERE id = 1", StatementKind.Dml, "DELETE")]
    [InlineData("INSERT INTO emp VALUES (1)", StatementKind.Dml, "INSERT")]
    [InlineData("MERGE INTO emp e USING dual ON (1 = 1) WHEN MATCHED THEN UPDATE SET sal = 1", StatementKind.Dml, "MERGE")]
    [InlineData("CALL p()", StatementKind.Dml, "CALL")]
    [InlineData("LOCK TABLE emp IN EXCLUSIVE MODE", StatementKind.Dml, "LOCK")]
    [InlineData("CREATE TABLE t (x NUMBER)", StatementKind.Ddl, "CREATE")]
    [InlineData("ALTER SYSTEM KILL SESSION '1,2'", StatementKind.Ddl, "ALTER SYSTEM")]
    [InlineData("ALTER SESSION SET nls_date_format = 'YYYY'", StatementKind.Ddl, "ALTER SESSION")]
    [InlineData("DROP TABLE emp", StatementKind.Ddl, "DROP")]
    [InlineData("TRUNCATE TABLE emp", StatementKind.Ddl, "TRUNCATE")]
    [InlineData("GRANT SELECT ON emp TO u", StatementKind.Ddl, "GRANT")]
    [InlineData("BEGIN NULL; END;", StatementKind.PlSql, "BEGIN")]
    [InlineData("DECLARE x NUMBER; BEGIN NULL; END;", StatementKind.PlSql, "DECLARE")]
    [InlineData("SET ROLE dba", StatementKind.Other, "SET")]
    public void Viewer_only_denies_changes_before_anything_else(string text, StatementKind kind, string verb)
    {
        var d = Eval(Viewer(), Ctx(), Stmt(text, kind, verb));

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.ReadOnlyRole, d.Rule);
        Assert.Equal(Denied(verb), d.Message);
        Assert.Null(d.RetypeText);
    }

    [Fact]
    public void Uncertain_classification_is_denied_as_dml()
    {
        var unknown = new SqlStatement("FOO bar", default, StatementKind.Query) { IsClassificationUncertain = true };
        var noVerb = new SqlStatement("VALUES x", default, StatementKind.Other) { IsClassificationUncertain = true };
        var table = new SqlStatement("TABLE emp", default, StatementKind.Other) { Verb = "TABLE", IsClassificationUncertain = true };

        var d = Eval(Viewer(), Ctx(), unknown);
        Assert.Equal(PolicyRule.ReadOnlyRole, d.Rule);
        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(Denied("DML"), Eval(Viewer(), Ctx(), noVerb).Message); // 동사를 모르면 DML 로 적는다
        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(Viewer(), Ctx(), table).Rule);
    }

    [Fact]
    public void Plsql_without_verb_is_described_by_kind()
    {
        var d = Eval(Viewer(), Ctx(), new SqlStatement("<<lbl>> BEGIN NULL; END;", default, StatementKind.PlSql));

        Assert.Equal(Denied("PL/SQL"), d.Message);
    }

    [Theory]
    [InlineData(HistorySource.Editor)]
    [InlineData(HistorySource.Script)]
    [InlineData(HistorySource.GridEdit)]
    [InlineData(HistorySource.SchemaBrowser)]
    [InlineData(HistorySource.Monitor)]
    [InlineData(HistorySource.Extension)]
    public void Denial_does_not_depend_on_the_calling_feature(HistorySource source)
    {
        var d = Eval(Viewer(), Ctx(source: source), Stmt("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml));

        Assert.Equal(PolicyRule.ReadOnlyRole, d.Rule);
    }

    // ---- 꺼짐: 다른 Role 과 함께·Role 없음·게이트 없음 ----

    [Theory]
    [InlineData(RoleIds.Viewer, RoleIds.SqlDev)]
    [InlineData(RoleIds.Viewer, RoleIds.Tuner)]
    [InlineData(RoleIds.SqlDev)]
    [InlineData(RoleIds.Dba)]
    public void Other_roles_turn_the_rule_off(params string[] roles)
    {
        var policy = new ExecutionPolicy(FakeFeatureGate.ForRoles(roles));

        Assert.Same(PolicyDecision.Allowed, Eval(policy, Ctx(), Stmt("CREATE TABLE t (x NUMBER)", StatementKind.Ddl)));
        Assert.Same(PolicyDecision.Allowed, Eval(policy, Ctx(), Stmt("BEGIN NULL; END;", StatementKind.PlSql)));
        Assert.Same(PolicyDecision.Allowed, Eval(policy, Ctx(), Stmt("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml)));
    }

    [Fact]
    public void No_gate_or_no_roles_keeps_the_old_rules()
    {
        var update = Stmt("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml);

        Assert.Same(PolicyDecision.Allowed, Eval(new ExecutionPolicy(), Ctx(), update));
        Assert.Same(PolicyDecision.Allowed, Eval(new ExecutionPolicy(null), Ctx(), update));
        Assert.Same(PolicyDecision.Allowed, Eval(new ExecutionPolicy(new FakeFeatureGate()), Ctx(), update)); // Role 고른 적 없음 = 모두 켬
        Assert.Equal(PolicyRule.ProductionChange, Eval(new ExecutionPolicy(new FakeFeatureGate()), Ctx(ConnectionEnvironment.Production), update).Rule);
    }

    // ---- 규칙 순서 ----

    [Fact]
    public void Read_only_profile_wins_over_the_role()
    {
        var d = Eval(Viewer(), Ctx(readOnly: true), Stmt("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml));

        Assert.Equal(PolicyRule.ReadOnlyProfile, d.Rule);
        Assert.DoesNotContain("Role", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Role_comes_before_license_and_confirm_rules()
    {
        var policy = Viewer();

        // 라이선스 부족 + 쓰기 → Role 이 먼저 거부한다.
        Assert.Equal(PolicyRule.ReadOnlyRole,
            Eval(policy, Ctx(required: LicensePack.Diagnostics), Stmt("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml)).Rule);

        // 운영 + WHERE 없는 DML + 위험 명령 → 확인 창 대신 거부(묻지도 않는다).
        var drop = Stmt("DROP TABLE emp", StatementKind.Ddl) with { DangerousTarget = "EMP" };
        var d = Eval(policy, Ctx(ConnectionEnvironment.Production), drop);
        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.ReadOnlyRole, d.Rule);
        var unfiltered = Stmt("DELETE FROM emp", StatementKind.Dml) with { IsUnfilteredDml = true };
        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(policy, Ctx(), unfiltered).Rule);
    }

    [Fact]
    public void Reads_still_follow_the_license_rule()
    {
        var d = Eval(Viewer(), Ctx(required: LicensePack.Diagnostics), Stmt("SELECT * FROM dba_hist_snapshot", StatementKind.Query));

        Assert.Equal(PolicyRule.License, d.Rule);
    }

    [Fact]
    public void Reads_in_production_are_not_confirmed()
    {
        Assert.Same(PolicyDecision.Allowed, Eval(Viewer(), Ctx(ConnectionEnvironment.Production), Stmt("SELECT 1 FROM dual", StatementKind.Query)));
    }

    // ---- 되돌림: 게이트 값은 문장마다 다시 읽는다 ----

    [Fact]
    public void Changing_the_role_applies_from_the_next_statement()
    {
        var gate = FakeFeatureGate.ForRoles(RoleIds.Viewer);
        var policy = new ExecutionPolicy(gate);
        var update = Stmt("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml);

        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(policy, Ctx(), update).Rule);

        gate.ReadOnlyRole = false; // R6 + R1 로 바꿈
        Assert.Same(PolicyDecision.Allowed, Eval(policy, Ctx(), update));

        gate.ReadOnlyRole = true; // 다시 R6 만
        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(policy, Ctx(), update).Rule);
    }

    [Fact]
    public void Retype_option_does_not_change_the_role_rule()
    {
        var policy = new ExecutionPolicy(FakeFeatureGate.ForRoles(RoleIds.Viewer)) { RetypeDangerousNames = false };

        Assert.Equal(PolicyRule.ReadOnlyRole, Eval(policy, Ctx(), Stmt("TRUNCATE TABLE emp", StatementKind.Ddl)).Rule);
    }

    [Fact]
    public async Task Cancelled_token_throws_before_evaluating()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Viewer().EvaluateAsync(Ctx(), Stmt("SELECT 1 FROM dual", StatementKind.Query), cts.Token));
    }
}
