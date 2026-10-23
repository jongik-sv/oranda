using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Testing;

namespace Oranda.Core.Tests.Execution;

// 소유: TSK-06-01. 정책 관문 규칙 순서(TRD 4.9): 읽기 전용 → 라이선스 → 운영 확인 → WHERE 없는 DML → 위험 명령.
public class ExecutionPolicyTests
{
    private static readonly ExecutionPolicy Policy = new();

    private static PolicyContext Ctx(ConnectionEnvironment env = ConnectionEnvironment.Development, bool readOnly = false,
                                     LicensePack held = LicensePack.None, LicensePack required = LicensePack.None,
                                     string? serverPacks = null) =>
        new(TestData.Profile(env, readOnly, held), TestData.Server() with { ManagementPackAccess = serverPacks }, HistorySource.Editor)
        {
            RequiredLicense = required,
        };

    private static SqlStatement Stmt(string text, StatementKind kind, string? verb = null) =>
        new(text, default, kind) { Verb = verb ?? text.Split(' ')[0].ToUpperInvariant() };

    private static PolicyDecision Eval(PolicyContext ctx, SqlStatement stmt) =>
        Policy.EvaluateAsync(ctx, stmt, default).AsTask().GetAwaiter().GetResult();

    [Fact]
    public void Plain_query_is_allowed()
    {
        var d = Eval(Ctx(), Stmt("SELECT 1 FROM dual", StatementKind.Query));

        Assert.Same(PolicyDecision.Allowed, d);
    }

    [Theory]
    [InlineData("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml)]
    [InlineData("CREATE TABLE t (x NUMBER)", StatementKind.Ddl)]
    [InlineData("BEGIN NULL; END;", StatementKind.PlSql)]
    public void Read_only_profile_denies_changes(string text, StatementKind kind)
    {
        var d = Eval(Ctx(readOnly: true), Stmt(text, kind));

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.ReadOnlyProfile, d.Rule);
        Assert.False(string.IsNullOrEmpty(d.Message));
    }

    [Theory]
    [InlineData("SELECT * FROM emp", StatementKind.Query, "SELECT")]
    [InlineData("COMMIT", StatementKind.TransactionControl, "COMMIT")]
    [InlineData("SET SERVEROUTPUT ON", StatementKind.SqlPlus, "SET")]
    [InlineData("EXPLAIN PLAN FOR SELECT 1 FROM dual", StatementKind.Other, "EXPLAIN")]
    public void Read_only_profile_allows_reads(string text, StatementKind kind, string verb)
    {
        var d = Eval(Ctx(readOnly: true), Stmt(text, kind, verb));

        Assert.Equal(PolicyDecisionKind.Allow, d.Kind);
    }

    [Fact]
    public void Uncertain_classification_is_treated_as_dml()
    {
        var stmt = new SqlStatement("FOO bar", default, StatementKind.Query) { IsClassificationUncertain = true };

        Assert.Equal(StatementKind.Dml, ExecutionPolicy.EffectiveKind(stmt));
        Assert.Equal(PolicyRule.ReadOnlyProfile, Eval(Ctx(readOnly: true), stmt).Rule);
        Assert.Equal(PolicyRule.ProductionChange, Eval(Ctx(ConnectionEnvironment.Production), stmt).Rule);
    }

    [Theory]
    [InlineData(LicensePack.Diagnostics, LicensePack.None)]
    [InlineData(LicensePack.DiagnosticsAndTuning, LicensePack.None)]
    [InlineData(LicensePack.DiagnosticsAndTuning, LicensePack.Diagnostics)]
    public void License_tag_above_declaration_is_denied(LicensePack required, LicensePack held)
    {
        var d = Eval(Ctx(held: held, required: required), Stmt("SELECT * FROM dba_hist_snapshot", StatementKind.Query));

        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
        Assert.Equal(PolicyRule.License, d.Rule);
    }

    [Theory]
    [InlineData(LicensePack.Diagnostics, LicensePack.Diagnostics)]
    [InlineData(LicensePack.Diagnostics, LicensePack.DiagnosticsAndTuning)]
    [InlineData(LicensePack.DiagnosticsAndTuning, LicensePack.DiagnosticsAndTuning)]
    public void License_tag_within_declaration_is_allowed(LicensePack required, LicensePack held)
    {
        var d = Eval(Ctx(held: held, required: required), Stmt("SELECT * FROM v$active_session_history", StatementKind.Query));

        Assert.Equal(PolicyDecisionKind.Allow, d.Kind);
    }

    [Theory]
    [InlineData("NONE", LicensePack.Diagnostics, true)]
    [InlineData("DIAGNOSTIC", LicensePack.DiagnosticsAndTuning, true)]
    [InlineData("DIAGNOSTIC", LicensePack.Diagnostics, false)]
    [InlineData("DIAGNOSTIC+TUNING", LicensePack.DiagnosticsAndTuning, false)]
    [InlineData(null, LicensePack.DiagnosticsAndTuning, false)]
    public void Server_pack_access_lowers_declaration(string? server, LicensePack required, bool denied)
    {
        var d = Eval(Ctx(held: LicensePack.DiagnosticsAndTuning, required: required, serverPacks: server),
                     Stmt("SELECT * FROM v$sql_monitor", StatementKind.Query));

        Assert.Equal(denied, d.Kind == PolicyDecisionKind.Deny);
    }

    [Fact]
    public void Read_only_rule_comes_before_license()
    {
        var d = Eval(Ctx(readOnly: true, required: LicensePack.Diagnostics), Stmt("DELETE FROM t WHERE 1=0", StatementKind.Dml));

        Assert.Equal(PolicyRule.ReadOnlyProfile, d.Rule);
    }

    [Theory]
    [InlineData("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml)]
    [InlineData("CREATE INDEX i ON t (x)", StatementKind.Ddl)]
    [InlineData("BEGIN p; END;", StatementKind.PlSql)]
    public void Production_change_needs_confirmation(string text, StatementKind kind)
    {
        var d = Eval(Ctx(ConnectionEnvironment.Production), Stmt(text, kind));

        Assert.Equal(PolicyDecisionKind.Confirm, d.Kind);
        Assert.Equal(PolicyRule.ProductionChange, d.Rule);
        Assert.Null(d.RetypeText);
    }

    [Theory]
    [InlineData("ALTER SESSION SET STATISTICS_LEVEL = ALL")]
    [InlineData("ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD'")]
    public void Production_alter_session_is_allowed(string text) =>
        Assert.Equal(PolicyDecisionKind.Allow, Eval(Ctx(ConnectionEnvironment.Production), Stmt(text, StatementKind.Ddl, "ALTER SESSION")).Kind);

    [Theory]
    [InlineData("ALTER INDEX scott.emp_pk MONITORING USAGE")]
    [InlineData("alter index emp_pk nomonitoring usage")]
    public void Production_index_monitoring_is_allowed(string text) =>
        Assert.Equal(PolicyDecisionKind.Allow, Eval(Ctx(ConnectionEnvironment.Production), Stmt(text, StatementKind.Ddl, "ALTER")).Kind);

    [Theory]
    [InlineData("ALTER TABLE emp ADD (c2 NUMBER)", "ALTER")]
    [InlineData("ALTER USER scott IDENTIFIED BY x", "ALTER")]
    [InlineData("ALTER INDEX emp_pk REBUILD", "ALTER")]
    [InlineData("ALTER PACKAGE pkg COMPILE", "ALTER")]
    [InlineData("ALTER INDEX emp_pk MONITORING USAGE; DROP TABLE emp", "ALTER")]
    public void Production_other_alter_still_needs_confirmation(string text, string verb) =>
        Assert.Equal(PolicyRule.ProductionChange, Eval(Ctx(ConnectionEnvironment.Production), Stmt(text, StatementKind.Ddl, verb)).Rule);

    [Fact]
    public void Production_query_is_allowed()
    {
        Assert.Equal(PolicyDecisionKind.Allow, Eval(Ctx(ConnectionEnvironment.Production), Stmt("SELECT 1 FROM dual", StatementKind.Query)).Kind);
    }

    [Fact]
    public void Same_change_on_development_is_allowed()
    {
        Assert.Equal(PolicyDecisionKind.Allow, Eval(Ctx(), Stmt("UPDATE emp SET sal = 1 WHERE id = 1", StatementKind.Dml)).Kind);
    }

    [Theory]
    [InlineData(ConnectionEnvironment.Development)]
    [InlineData(ConnectionEnvironment.Test)]
    public void Unfiltered_dml_warns_on_every_connection(ConnectionEnvironment env)
    {
        var stmt = Stmt("DELETE FROM emp", StatementKind.Dml) with { IsUnfilteredDml = true };

        var d = Eval(Ctx(env), stmt);

        Assert.Equal(PolicyDecisionKind.Confirm, d.Kind);
        Assert.Equal(PolicyRule.UnfilteredDml, d.Rule);
        Assert.Contains("WHERE", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_and_unfiltered_combine_messages_with_first_rule()
    {
        var stmt = Stmt("UPDATE emp SET sal = 0", StatementKind.Dml) with { IsUnfilteredDml = true };

        var d = Eval(Ctx(ConnectionEnvironment.Production), stmt);

        Assert.Equal(PolicyRule.ProductionChange, d.Rule);
        Assert.Contains("운영", d.Message, StringComparison.Ordinal);
        Assert.Contains("WHERE", d.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DROP TABLE scott.emp", "DROP", "scott.emp")]
    [InlineData("TRUNCATE TABLE emp", "TRUNCATE", "emp")]
    [InlineData("ALTER SYSTEM FLUSH SHARED_POOL", "ALTER SYSTEM", "SYSTEM")]
    public void Dangerous_command_requires_retyping_target(string text, string verb, string target)
    {
        var stmt = new SqlStatement(text, default, StatementKind.Ddl) { Verb = verb, DangerousTarget = target };

        var d = Eval(Ctx(), stmt);

        Assert.Equal(PolicyDecisionKind.Confirm, d.Kind);
        Assert.Equal(PolicyRule.DangerousCommand, d.Rule);
        Assert.Equal(target, d.RetypeText);
    }

    [Fact]
    public void Production_drop_keeps_retype_text()
    {
        var stmt = new SqlStatement("DROP TABLE emp", default, StatementKind.Ddl) { Verb = "DROP", DangerousTarget = "emp" };

        var d = Eval(Ctx(ConnectionEnvironment.Production), stmt);

        Assert.Equal(PolicyRule.ProductionChange, d.Rule);
        Assert.Equal("emp", d.RetypeText);
    }

    [Fact]
    public async Task Retype_option_off_skips_dangerous_rule()
    {
        var policy = new ExecutionPolicy { RetypeDangerousNames = false };
        var stmt = new SqlStatement("DROP TABLE emp", default, StatementKind.Ddl) { Verb = "DROP", DangerousTarget = "emp" };

        var d = await policy.EvaluateAsync(Ctx(), stmt, default);

        Assert.Equal(PolicyDecisionKind.Allow, d.Kind);
    }

    [Fact]
    public void Read_only_denies_dangerous_before_retype()
    {
        var stmt = new SqlStatement("DROP TABLE emp", default, StatementKind.Ddl) { Verb = "DROP", DangerousTarget = "emp" };

        Assert.Equal(PolicyRule.ReadOnlyProfile, Eval(Ctx(readOnly: true), stmt).Rule);
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Policy.EvaluateAsync(Ctx(), Stmt("SELECT 1 FROM dual", StatementKind.Query), cts.Token).AsTask());
    }
}
