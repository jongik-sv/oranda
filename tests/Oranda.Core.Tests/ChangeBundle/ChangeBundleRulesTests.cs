using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Testing;

namespace Oranda.Core.Tests.ChangeBundle;

// 소유: te-bundle(T5). 자동 넣기 조건(테이블 편집기 설계 9.3)·해시 정규화.
public sealed class ChangeBundleRulesTests
{
    private static SqlStatement Ddl(string text = "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))", string verb = "ALTER")
        => new(text, default, StatementKind.Ddl) { Verb = verb };

    private static ExecutionResult Ok(SqlStatement s) => new(s, ExecutionStatus.Succeeded);

    private static ChangeBundleInfo Bundle(Dbms? dbms = Dbms.Oracle, params string[] owners)
        => new(1, "릴리스 1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, dbms, owners, true);

    [Theory]
    [InlineData(HistorySource.Editor, true)]
    [InlineData(HistorySource.Script, true)]
    [InlineData(HistorySource.TableEditor, true)]
    [InlineData(HistorySource.ChangeBundle, false)]
    [InlineData(HistorySource.SchemaBrowser, false)] // 열 순서 바꾸기
    [InlineData(HistorySource.DataCopy, false)]
    [InlineData(HistorySource.Agent, false)]
    [InlineData(HistorySource.Monitor, false)]
    [InlineData(HistorySource.GridEdit, false)]
    public void Only_editor_script_and_table_editor_sources_are_candidates(HistorySource source, bool expected)
    {
        var s = Ddl();
        Assert.Equal(expected, ChangeBundleRules.IsCandidate(s, Ok(s), new ExecutionOptions { Source = source }));
    }

    [Fact]
    public void Bundle_run_source_is_not_added()
    {
        var s = Ddl();
        Assert.False(ChangeBundleRules.IsCandidate(s, Ok(s), new ExecutionOptions { Source = HistorySource.ChangeBundle }));
    }

    [Theory]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Denied)]
    public void Failed_statements_are_not_added(ExecutionStatus status)
    {
        var s = Ddl();
        Assert.False(ChangeBundleRules.IsCandidate(s, new ExecutionResult(s, status), new ExecutionOptions()));
    }

    [Fact]
    public void Non_ddl_internal_runs_and_session_statements_are_not_candidates()
    {
        var dml = new SqlStatement("UPDATE SAMPLE.ORDERS SET X = 1", default, StatementKind.Dml) { Verb = "UPDATE" };
        Assert.False(ChangeBundleRules.IsCandidate(dml, Ok(dml), new ExecutionOptions()));

        var plsql = new SqlStatement("BEGIN NULL; END;", default, StatementKind.PlSql) { Verb = "BEGIN" };
        Assert.False(ChangeBundleRules.IsCandidate(plsql, Ok(plsql), new ExecutionOptions()));

        var ddl = Ddl();
        Assert.False(ChangeBundleRules.IsCandidate(ddl, Ok(ddl), new ExecutionOptions { SkipHistory = true }));

        var session = Ddl("ALTER SESSION SET CURRENT_SCHEMA = SAMPLE", "ALTER SESSION");
        Assert.False(ChangeBundleRules.IsCandidate(session, Ok(session), new ExecutionOptions()));
        var system = Ddl("ALTER SYSTEM FLUSH SHARED_POOL", "ALTER SYSTEM");
        Assert.False(ChangeBundleRules.IsCandidate(system, Ok(system), new ExecutionOptions()));
    }

    [Fact]
    public void Dbms_must_match_and_an_empty_bundle_accepts_any()
    {
        Assert.Equal(ChangeBundleDecision.Add, ChangeBundleRules.Decide(Bundle(Dbms.Oracle, "SAMPLE"), Dbms.Oracle, "SAMPLE"));
        Assert.Equal(ChangeBundleDecision.DbmsMismatch, ChangeBundleRules.Decide(Bundle(Dbms.Oracle, "SAMPLE"), Dbms.Tibero, "SAMPLE"));
        Assert.Equal(ChangeBundleDecision.Add, ChangeBundleRules.Decide(Bundle(null, "SAMPLE"), Dbms.Tibero, "SAMPLE"));
    }

    [Fact]
    public void Owner_must_be_a_bundle_target_unless_the_list_is_empty()
    {
        var bundle = Bundle(Dbms.Oracle, "SAMPLE", "SAMPLE_APP");
        Assert.Equal(ChangeBundleDecision.Add, ChangeBundleRules.Decide(bundle, Dbms.Oracle, "SAMPLE_APP"));
        Assert.Equal(ChangeBundleDecision.OwnerNotTarget, ChangeBundleRules.Decide(bundle, Dbms.Oracle, "SCOTT"));
        Assert.Equal(ChangeBundleDecision.OwnerNotTarget, ChangeBundleRules.Decide(bundle, Dbms.Oracle, "sample")); // 이름 규칙 = 대문자
        Assert.Equal(ChangeBundleDecision.OwnerNotTarget, ChangeBundleRules.Decide(bundle, Dbms.Oracle, null));
        Assert.Equal(ChangeBundleDecision.Add, ChangeBundleRules.Decide(Bundle(Dbms.Oracle), Dbms.Oracle, "SCOTT"));
        Assert.Equal(ChangeBundleDecision.DbmsMismatch, ChangeBundleRules.Decide(bundle, Dbms.Tibero, "SCOTT")); // DBMS 를 먼저 본다(알림이 나가야 한다)
    }

    [Fact]
    public void Source_maps_to_item_source()
    {
        Assert.Equal(ChangeBundleItemSource.TableEditor, ChangeBundleRules.SourceOf(HistorySource.TableEditor));
        Assert.Equal(ChangeBundleItemSource.SqlEditor, ChangeBundleRules.SourceOf(HistorySource.Editor));
        Assert.Equal(ChangeBundleItemSource.SqlEditor, ChangeBundleRules.SourceOf(HistorySource.Script));
    }

    [Fact]
    public void Hash_ignores_whitespace_runs_line_endings_and_trailing_semicolon_but_not_case()
    {
        var a = StatementHash.Compute("ALTER TABLE SAMPLE.ORDERS\r\n  ADD (NOTE VARCHAR2(100))");
        Assert.Equal(a, StatementHash.Compute("  ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100));\n"));
        Assert.Equal(a, StatementHash.Compute("ALTER\tTABLE SAMPLE.ORDERS\nADD (NOTE VARCHAR2(100)) ; "));
        Assert.NotEqual(a, StatementHash.Compute("alter table sample.orders add (note varchar2(100))"));
        Assert.NotEqual(a, StatementHash.Compute("ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(101))"));
        Assert.Equal("A B", StatementHash.Normalize(" A \n\n B ;"));

        // 리터럴·따옴표 이름·주석 안 공백은 그대로다.
        Assert.NotEqual(StatementHash.Compute("COMMENT ON TABLE SAMPLE.ORDERS IS 'a  b'"), StatementHash.Compute("COMMENT ON TABLE SAMPLE.ORDERS IS 'a b'"));
        Assert.Equal("COMMENT ON TABLE T IS 'it''s  x' -- a  b c", StatementHash.Normalize("COMMENT  ON TABLE\nT IS 'it''s  x'   -- a  b\n c"));
        Assert.Equal("ALTER TABLE \"My  T\" ADD (X NUMBER) /* a  b */ ;x", StatementHash.Normalize("ALTER  TABLE \"My  T\"\nADD (X NUMBER) /* a  b */ ;x"));
        Assert.Equal("X 'unclosed  y", StatementHash.Normalize("X   'unclosed  y"));
        Assert.Equal(string.Empty, StatementHash.Normalize("  ;  "));
        Assert.Throws<ArgumentNullException>(() => StatementHash.Compute(null!));
    }

    [Fact]
    public void Origin_uses_session_user_as_current_schema_by_default()
    {
        var session = new FakeSession(TestData.Profile(ConnectionEnvironment.Test), TestData.Server());
        var origin = ChangeBundleOrigin.From(session);
        Assert.Equal(new ChangeBundleOrigin(Dbms.Oracle, "SCOTT", ConnectionEnvironment.Test, "test"), origin);
        Assert.Equal("SAMPLE", ChangeBundleOrigin.From(session, "SAMPLE").CurrentSchema);

        var noUser = TestData.Server() with { SessionUser = null };
        Assert.Equal("SCOTT", ChangeBundleOrigin.From(TestData.Profile(), noUser).CurrentSchema); // 프로필 사용자 scott → 대문자
    }

    [Fact]
    public void Run_marks_pick_the_environment_column()
    {
        var t = DateTimeOffset.UnixEpoch;
        var marks = new ChangeBundleRunMarks(t, null, t.AddDays(1));
        Assert.Equal(t, marks.For(ConnectionEnvironment.Development));
        Assert.Null(marks.For(ConnectionEnvironment.Test));
        Assert.Equal(t.AddDays(1), marks.For(ConnectionEnvironment.Production));
        Assert.Null(ChangeBundleRunMarks.None.For(ConnectionEnvironment.Development));
    }
}
