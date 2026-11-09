using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Testing;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Transfer.Copy;

/// <summary>
/// Tibero 다시 만들기 단계(TB4)를 가짜 행 실행기·트리거 DDL 읽기로 본다(서버 없음). 서버로 끝까지 도는 확인은 <c>TiberoRecreateStepIt</c>.
/// 가짜 실행기는 받은 글을 Tibero 카탈로그의 id 로 되돌려 그 id 의 행을 준다(단계가 Tibero 파일 글을 그대로 보내는지도 함께 본다).
/// </summary>
public sealed class TiberoRecreateStepTests
{
    private static readonly SqlCatalog Catalog = TiberoEmbeddedCatalog.Load();

    private static readonly string[] Ids =
        ["copy.recreate_triggers", "copy.recreate_grants", "copy.recreate_referencing_fks", "copy.recreate_source_fks", "copy.recreate_recycle_bin"];

    private const string OffCreate = "CREATE OR REPLACE TRIGGER \"SCOTT\".\"T_OFF\" BEFORE INSERT ON EMP FOR EACH ROW\nBEGIN\n  :NEW.NOTE := 'off';\nEND;";

    private const string OnCreate = "CREATE OR REPLACE TRIGGER \"SCOTT\".\"T_ON\" BEFORE INSERT ON EMP FOR EACH ROW\nBEGIN\n  :NEW.NOTE := 'on';\nEND;";

    private readonly Dictionary<string, List<object?[]>> _rows = new(StringComparer.Ordinal);

    private readonly List<(string Id, (string Name, string Value)[] Binds)> _calls = [];

    private readonly Dictionary<(string Owner, string Name), string> _triggerDdl = new();

    private static FakeSession Session() => new(server: TestData.Server("6"));

    private TiberoRecreateStep Step() => new(QueryAsync, TriggerDdlAsync);

    [Fact]
    public async Task Capture_turns_a_disabled_trigger_into_create_plus_disable_and_keeps_trigger_objects_aligned()
    {
        _rows["copy.recreate_triggers"] = [["SCOTT", "T_OFF", "DISABLED"], ["SCOTT", "T_ON", "ENABLED"]];
        // 메타데이터 제공자 모양: 생성 문장 + 상태 문장(끝 표시 있음·없음 둘 다).
        _triggerDdl[("SCOTT", "T_OFF")] = OffCreate + "\n/\nALTER TRIGGER \"SCOTT\".\"T_OFF\" DISABLE;";
        _triggerDdl[("SCOTT", "T_ON")] = OnCreate + "\nALTER TRIGGER \"SCOTT\".\"T_ON\" ENABLE";

        var kept = await Step().CaptureAsync(Session(), "SCOTT", "EMP", CancellationToken.None);

        Assert.Equal([OffCreate, "ALTER TRIGGER \"SCOTT\".\"T_OFF\" DISABLE", OnCreate], kept.Triggers);
        Assert.Equal(
            [new DisabledObject(DisabledKind.Trigger, "SCOTT", "EMP", "T_OFF"), new DisabledObject(DisabledKind.Trigger, "SCOTT", "EMP", "T_OFF"),
             new DisabledObject(DisabledKind.Trigger, "SCOTT", "EMP", "T_ON")],
            kept.TriggerObjects);
        Assert.Empty(kept.Grants);
        Assert.Empty(kept.ReferencingForeignKeys);

        var triggerCall = Assert.Single(_calls, c => c.Id == "copy.recreate_triggers");
        Assert.Equal(new[] { ("OWN", "SCOTT"), ("TBL", "EMP") }, triggerCall.Binds);
    }

    [Fact]
    public async Task Capture_fails_when_the_trigger_ddl_is_empty()
    {
        _rows["copy.recreate_triggers"] = [["SCOTT", "T_ON", "ENABLED"]];
        _triggerDdl[("SCOTT", "T_ON")] = "  ";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Step().CaptureAsync(Session(), "SCOTT", "EMP", CancellationToken.None));
    }

    [Fact]
    public async Task Grants_quote_the_grantee_leave_public_bare_and_add_grant_option_only_for_yes()
    {
        _rows["copy.recreate_grants"] = [["HR", "SELECT", "YES"], ["PUBLIC", "SELECT", "NO"], ["app user", "UPDATE", null]];

        var kept = await Step().CaptureAsync(Session(), "SCOTT", "EMP", CancellationToken.None);

        Assert.Equal(
            ["GRANT SELECT ON \"SCOTT\".\"EMP\" TO \"HR\" WITH GRANT OPTION", "GRANT SELECT ON \"SCOTT\".\"EMP\" TO PUBLIC", "GRANT UPDATE ON \"SCOTT\".\"EMP\" TO \"app user\""],
            kept.Grants);
        Assert.Equal(new[] { ("OWN", "SCOTT"), ("TBL", "EMP") }, Assert.Single(_calls, c => c.Id == "copy.recreate_grants").Binds);
    }

    [Fact]
    public async Task Referencing_fks_are_grouped_per_constraint_in_catalog_order_with_aligned_constraint_objects()
    {
        _rows["copy.recreate_referencing_fks"] =
        [
            ["HR", "DEPT_EMP", "FK1", "CASCADE", "C1", "P1", "ENABLED", null, null],
            ["HR", "DEPT_EMP", "FK1", "CASCADE", "C2", "P2", "ENABLED", null, null],
            ["SCOTT", "BONUS", "SCOTT_CON100", "NO ACTION", "E", "ID", "DISABLED", null, null],
        ];

        var kept = await Step().CaptureAsync(Session(), "SCOTT", "EMP", CancellationToken.None);

        Assert.Equal(
            [
                "ALTER TABLE \"HR\".\"DEPT_EMP\" ADD CONSTRAINT \"FK1\" FOREIGN KEY (\"C1\", \"C2\") REFERENCES \"SCOTT\".\"EMP\" (\"P1\", \"P2\") ON DELETE CASCADE",
                // 참조 FK 는 Oracle 처럼 서버 이름도 그대로 쓴다(같은 DB 에서 지운 FK 를 되붙인다).
                "ALTER TABLE \"SCOTT\".\"BONUS\" ADD CONSTRAINT \"SCOTT_CON100\" FOREIGN KEY (\"E\") REFERENCES \"SCOTT\".\"EMP\" (\"ID\") DISABLE",
            ],
            kept.ReferencingForeignKeys);
        Assert.Equal(
            [new DisabledObject(DisabledKind.ForeignKey, "HR", "DEPT_EMP", "FK1"), new DisabledObject(DisabledKind.ForeignKey, "SCOTT", "BONUS", "SCOTT_CON100")],
            kept.ReferencingConstraints);
    }

    [Fact]
    public void Foreign_key_ddl_keeps_set_null_and_never_writes_a_deferrable_clause()
    {
        object?[][] rows = [["SCOTT", "EMP", "FK", "SET NULL", "DEPTNO", "DEPTNO", "ENABLED", "DEFERRABLE", "DEFERRED"]];

        var ddl = TiberoRecreateStep.ForeignKeyDdl("SCOTT", "EMP", null, "SCOTT", "DEPT", rows);

        Assert.Equal("ALTER TABLE \"SCOTT\".\"EMP\" ADD FOREIGN KEY (\"DEPTNO\") REFERENCES \"SCOTT\".\"DEPT\" (\"DEPTNO\") ON DELETE SET NULL", ddl);
        Assert.DoesNotContain("DEFERRABLE", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_fks_keep_only_job_parents_drop_server_names_and_use_the_target_owner()
    {
        _rows["copy.recreate_source_fks"] =
        [
            ["SRC", "EMP", "EMP_DEPT_FK", "NO ACTION", "DEPTNO", "DEPTNO", "ENABLED", null, null, "DEPT"],
            ["SRC", "EMP", "SRC_CON200", "CASCADE", "MGR", "EMPNO", "ENABLED", null, null, "EMP_BOSS"],
            ["SRC", "EMP", "SYS_C0012", "NO ACTION", "LOC", "ID", "DISABLED", null, null, "LOC"],
            ["SRC", "EMP", "EMP_PROJ_FK", "NO ACTION", "PROJ", "ID", "ENABLED", null, null, "PROJ"],
        ];

        var fks = await Step().SourceForeignKeysAsync(Session(), "SRC", "EMP", new HashSet<string>(["DEPT", "EMP_BOSS", "LOC"], StringComparer.Ordinal), "TGT",
            CancellationToken.None);

        Assert.Equal(
            new (string Parent, string Ddl, string? Name)[]
            {
                ("DEPT", "ALTER TABLE \"TGT\".\"EMP\" ADD CONSTRAINT \"EMP_DEPT_FK\" FOREIGN KEY (\"DEPTNO\") REFERENCES \"TGT\".\"DEPT\" (\"DEPTNO\")", "EMP_DEPT_FK"),
                ("EMP_BOSS", "ALTER TABLE \"TGT\".\"EMP\" ADD FOREIGN KEY (\"MGR\") REFERENCES \"TGT\".\"EMP_BOSS\" (\"EMPNO\") ON DELETE CASCADE", null),
                ("LOC", "ALTER TABLE \"TGT\".\"EMP\" ADD FOREIGN KEY (\"LOC\") REFERENCES \"TGT\".\"LOC\" (\"ID\") DISABLE", null),
            },
            fks);
        Assert.Equal(new[] { ("OWN", "SRC"), ("TBL", "EMP") }, Assert.Single(_calls, c => c.Id == "copy.recreate_source_fks").Binds);
    }

    [Fact]
    public async Task Source_fks_with_no_parent_in_the_job_are_empty()
    {
        _rows["copy.recreate_source_fks"] = [["SRC", "EMP", "EMP_DEPT_FK", "NO ACTION", "DEPTNO", "DEPTNO", "ENABLED", null, null, "DEPT"]];

        var fks = await Step().SourceForeignKeysAsync(Session(), "SRC", "EMP", new HashSet<string>(["OTHER"], StringComparer.Ordinal), "TGT", CancellationToken.None);

        Assert.Empty(fks);
    }

    [Fact]
    public async Task Recycle_bin_returns_the_bin_names_for_the_original_table_name()
    {
        _rows["copy.recreate_recycle_bin"] = [["SCOTT_TBL_1"], [null], ["SCOTT_TBL_2"], ["SCOTT_TBL_1"]];

        var bin = await Step().RecycleBinAsync(Session(), "EMP", CancellationToken.None);

        Assert.Equal(2, bin.Count);
        Assert.Contains("SCOTT_TBL_1", bin);
        Assert.Contains("SCOTT_TBL_2", bin);
        Assert.Equal(new[] { ("TBL", "EMP") }, Assert.Single(_calls, c => c.Id == "copy.recreate_recycle_bin").Binds);
    }

    [Theory]
    [InlineData("SCOTT_CON100002390", true)] // 6.2.15 실측 꼴: <소유자>_CON<숫자>
    [InlineData("SCOTT_CON7", true)]
    [InlineData("_SYS_CON100002390", true)]
    [InlineData("SYS_C0012", true)]
    [InlineData("BIN$abc==$0", true)]
    [InlineData("SCOTT_CON", false)]
    [InlineData("SCOTT_CON12A", false)]
    [InlineData("SCOTT_CONSTRAINT1", false)]
    [InlineData("HR_CON12", false)] // 다른 소유자 꼴은 사용자 이름
    [InlineData("EMP_DEPT_FK", false)]
    [InlineData("MY_SYS_FK", false)]
    public void Server_names_are_the_owner_con_number_form_and_the_sys_prefixes(string name, bool server)
    {
        Assert.Equal(server, TiberoRecreateStep.IsServerName(name, "SCOTT"));
    }

    [Theory]
    [InlineData(OnCreate + "\n/\nALTER TRIGGER \"SCOTT\".\"T_ON\" ENABLE;", OnCreate)]
    [InlineData(OnCreate + "\nALTER TRIGGER \"SCOTT\".\"T_ON\" DISABLE", OnCreate)]
    [InlineData(OnCreate + "\n/\n", OnCreate)]
    [InlineData("  " + OnCreate + "  ", OnCreate)]
    [InlineData("CREATE TRIGGER t1 BEFORE INSERT ON emp FOR EACH ROW\nBEGIN\n  NULL;\nEND;", "CREATE OR REPLACE TRIGGER t1 BEFORE INSERT ON emp FOR EACH ROW\nBEGIN\n  NULL;\nEND;")]
    [InlineData("create  trigger t1 AFTER DELETE ON emp\nBEGIN\n  NULL;\nEND;", "CREATE OR REPLACE TRIGGER t1 AFTER DELETE ON emp\nBEGIN\n  NULL;\nEND;")]
    public void Trigger_create_keeps_only_the_create_statement_ending_in_end(string ddl, string expected)
    {
        Assert.Equal(expected, TiberoRecreateStep.TriggerCreate(ddl));
    }

    [Fact]
    public void Trigger_create_does_not_cut_an_alter_trigger_inside_the_body()
    {
        const string Body = "CREATE OR REPLACE TRIGGER \"S\".\"T\" AFTER INSERT ON X\nBEGIN\n  EXECUTE IMMEDIATE 'ALTER TRIGGER Y ENABLE';\nEND;";

        Assert.Equal(Body, TiberoRecreateStep.TriggerCreate(Body));
        Assert.Equal(Body, TiberoRecreateStep.TriggerCreate(Body + "\nALTER TRIGGER \"S\".\"T\" ENABLE"));
    }

    [Fact]
    public void Constructors_reject_null_delegates_and_null_bulk_service()
    {
        Assert.Throws<ArgumentNullException>(() => new TiberoRecreateStep(null!, TriggerDdlAsync));
        Assert.Throws<ArgumentNullException>(() => new TiberoRecreateStep(QueryAsync, null!));
        Assert.Throws<ArgumentNullException>(() => new TiberoRecreateStep((Oranda.Core.Execution.IBulkExecutionService)null!));
    }

    private Task<List<object?[]>> QueryAsync(ISession session, string sql, (string Name, string Value)[] binds, CancellationToken ct)
    {
        var id = Ids.SingleOrDefault(i => string.Equals(Catalog.Get(i, 6, Dbms.Tibero).Sql, sql, StringComparison.Ordinal))
                 ?? throw new InvalidOperationException("Tibero 카탈로그 recreate 글이 아닌 질의: " + sql);
        _calls.Add((id, binds));
        return Task.FromResult(_rows.TryGetValue(id, out var rows) ? rows : []);
    }

    private Task<string> TriggerDdlAsync(ISession session, string owner, string name, CancellationToken ct) =>
        Task.FromResult(_triggerDdl.TryGetValue((owner, name), out var ddl) ? ddl : throw new InvalidOperationException("없는 트리거"));
}
