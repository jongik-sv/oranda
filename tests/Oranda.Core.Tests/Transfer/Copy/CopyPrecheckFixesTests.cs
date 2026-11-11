using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 사전 점검 항목을 그 자리에서 고치는 처리(PC1, 결정 D-066)의 순수 함수: 항목 종류마다 낼 처리(<see cref="CopyPrecheckFixes.For"/>),
/// 고른 처리를 작업에 적용(<see cref="CopyPrecheckFixes.Apply"/>), 「대상에 열 추가」 문장(<see cref="CopyAddColumnDdl"/>, Oracle·Tibero 대상).
/// </summary>
public sealed class CopyPrecheckFixesTests
{
    private static readonly CopyFixContext Dev = new(Dbms.Oracle, Dbms.Oracle, TargetIsDevelopment: true);
    private static readonly CopyFixContext Test = new(Dbms.Oracle, Dbms.Oracle, TargetIsDevelopment: false);

    private static CopyJob Job(params TableRule[] rules) => new()
    {
        Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC_USER"), Target = new ConnectionRef("t", "CMN_USER"),
        SourceOwner = "SRC_USER", TargetOwner = "CMN_USER", MaskSalt = "AA==", Tables = rules,
        Options = new CopyOptions { ConcurrentTables = 3 },
    };

    private static TableRule Rule(string table = "ORDERS", TargetAction action = TargetAction.DeleteAllInsert) => new(table, RuleKind.All, action);

    private static TableInfo Orders(string owner, params ColumnInfo[] extra) =>
        new(new DbObjectRef(owner, "ORDERS", "TABLE"), [new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false }, .. extra]);

    private static PrecheckReport Report(TableRule rule, IReadOnlyList<CheckItem> items, TableInfo? source = null, TableInfo? target = null,
                                         Func<CopyTablePlan, CopyTablePlan>? plan = null)
    {
        var made = new CopyTablePlan(rule, source ?? Orders("SRC_USER"), target ?? Orders("CMN_USER"), ["ID"], []);
        return new PrecheckReport(items, [plan?.Invoke(made) ?? made], [], new Dictionary<string, long>());
    }

    private static ColumnInfo Note => new("NOTE", 2, "VARCHAR2") { Length = 40, IsNullable = false, DefaultValue = "'x'", Comment = "메모" };

    // ---- 처리 만들기(항목 종류마다) ----

    [Fact]
    public void Missing_target_table_offers_create_if_missing_only_on_a_development_target_and_only_when_it_is_off()
    {
        var rule = Rule();
        var job = Job(rule);
        var item = new CheckItem("ORDERS", "TargetMissing", CheckLevel.Error, "대상에 테이블이 없습니다.");
        var report = new PrecheckReport([item], [], [], new Dictionary<string, long>());

        var fix = Assert.Single(CopyPrecheckFixes.For(job, report, item, Dev));
        Assert.Equal(CopyFixKind.CreateIfMissing, fix.Kind);
        Assert.Equal("ORDERS", fix.TableKey);
        Assert.Equal("없으면 만들기 켜기", fix.Title);
        Assert.True(fix.ChangesJob);

        Assert.Empty(CopyPrecheckFixes.For(job, report, item, Test)); // 개발 접속이 아니면 표를 만들 수 없다
        var on = Job(rule with { CreateIfMissing = true });
        Assert.Empty(CopyPrecheckFixes.For(on, report, item, Dev)); // 이미 켰는데 막혔다: 같은 처리로는 고칠 수 없다
        var recreate = Job(Rule(action: TargetAction.Recreate));
        Assert.Empty(CopyPrecheckFixes.For(recreate, report, item, Dev));
    }

    [Fact]
    public void Column_not_in_target_offers_skip_or_add_column_with_the_alter_preview()
    {
        var rule = Rule();
        var job = Job(rule);
        var item = new CheckItem("ORDERS", "ColumnNotInTarget", CheckLevel.Confirm, "ORDERS.NOTE: 대상에 없는 열이라 건너뜁니다.") { Column = "NOTE" };
        var report = Report(rule, [item], source: Orders("SRC_USER", Note));

        var fixes = CopyPrecheckFixes.For(job, report, item, Dev);

        Assert.Equal([CopyFixKind.SkipColumn, CopyFixKind.AddColumn], fixes.Select(f => f.Kind));
        Assert.All(fixes, f => Assert.Equal("NOTE", f.Column));
        Assert.Null(fixes[0].Preview);
        Assert.Equal("ALTER TABLE \"CMN_USER\".\"ORDERS\" ADD (\"NOTE\" VARCHAR2(40 CHAR))", fixes[1].Preview);
        Assert.NotEqual(fixes[0].Id, fixes[1].Id);

        // 개발 접속이 아니면 열을 더하지 않는다: 빼고 복사만.
        Assert.Equal([CopyFixKind.SkipColumn], CopyPrecheckFixes.For(job, report, item, Test).Select(f => f.Kind));
    }

    [Fact]
    public void Column_that_cannot_be_mapped_to_the_target_dbms_offers_only_skip()
    {
        var rule = Rule();
        var item = new CheckItem("ORDERS", "ColumnNotInTarget", CheckLevel.Confirm, "…") { Column = "OLD" };
        var report = Report(rule, [item], source: Orders("SRC_USER", new ColumnInfo("OLD", 2, "LONG")));

        var fixes = CopyPrecheckFixes.For(Job(rule), report, item, new CopyFixContext(Dbms.Oracle, Dbms.Tibero, TargetIsDevelopment: true));

        Assert.Equal([CopyFixKind.SkipColumn], fixes.Select(f => f.Kind));
    }

    [Fact]
    public void Existing_rule_options_become_fixes_and_run_decisions_are_marked_as_not_changing_the_job()
    {
        var rule = Rule();
        var job = Job(rule);
        CheckItem[] items =
        [
            new("ORDERS", "UnsupportedType", CheckLevel.Error, "…") { Column = "GEO", Choices = [CheckChoice.SkipColumn] },
            new("ORDERS", CopyCheckCodes.AddColumnBlocked, CheckLevel.Error, "…") { Column = "NOTE" },
            new("ORDERS", "ChildRowsMayBlock", CheckLevel.Confirm, "…") { Choices = [CheckChoice.SwitchToMerge, CheckChoice.DisableForeignKeys] },
            new("ORDERS", "TargetTriggers", CheckLevel.Confirm, "…") { Choices = [CheckChoice.DisableTriggers] },
            new("ORDERS", "SequenceGap", CheckLevel.Confirm, "…") { Column = "ID", Choices = [CheckChoice.ProceedSequenceGap] },
            new("ORDERS", "SequenceGap", CheckLevel.Pass, "…") { Column = "ID", Sequence = "ORDERS_SEQ" },
            new(null, CopyCheckCodes.ConcurrentCopy, CheckLevel.Confirm, "…"),
            new("ORDERS", CopyCheckCodes.FlashbackNoPrivilege, CheckLevel.Error, "…"),
        ];
        var report = Report(rule, items, plan: p => p with
        {
            CanDisableTriggers = true,
            DisablableForeignKeys = [new DisabledObject(DisabledKind.ForeignKey, "CMN_USER", "ORDER_ITEMS", "FK_ITEMS")],
        });

        var kinds = items.Select(i => CopyPrecheckFixes.For(job, report, i, Dev).Select(f => f.Kind).ToList()).ToList();

        Assert.Equal([CopyFixKind.SkipColumn], kinds[0]);
        Assert.Equal([CopyFixKind.SkipColumn], kinds[1]);
        Assert.Equal([CopyFixKind.SwitchToMerge, CopyFixKind.DisableForeignKeys], kinds[2]);
        Assert.Equal([CopyFixKind.DisableTriggers], kinds[3]);
        Assert.Equal([CopyFixKind.ProceedSequenceGap], kinds[4]);
        Assert.Equal([CopyFixKind.LinkSequence], kinds[5]);
        Assert.Equal([CopyFixKind.OneTableAtATime], kinds[6]);
        Assert.Equal([CopyFixKind.OneTableAtATime, CopyFixKind.ConsistentReadOff], kinds[7]);

        var trigger = Assert.Single(CopyPrecheckFixes.For(job, report, items[3], Dev));
        Assert.False(trigger.ChangesJob);
        Assert.Equal(CheckChoice.DisableTriggers, trigger.Decision);
    }

    [Fact]
    public void Items_without_a_known_fix_get_none_and_trigger_disabling_needs_the_privilege()
    {
        var rule = Rule();
        var job = Job(rule);
        var report = Report(rule, []); // 끌 권한 없음(CanDisableTriggers = false)

        Assert.Empty(CopyPrecheckFixes.For(job, report, new CheckItem("ORDERS", "NarrowerType", CheckLevel.Confirm, "…") { Column = "ID" }, Dev));
        Assert.Empty(CopyPrecheckFixes.For(job, report, new CheckItem("ORDERS", CopyCheckCodes.SequenceDefaultDropped, CheckLevel.Confirm, "…"), Dev));
        Assert.Empty(CopyPrecheckFixes.For(job, report, new CheckItem("ORDERS", "TargetTriggers", CheckLevel.Confirm, "…") { Choices = [CheckChoice.DisableTriggers] }, Dev));
        Assert.Empty(CopyPrecheckFixes.For(job, report, new CheckItem(null, CopyCheckCodes.ConcurrentCopy, CheckLevel.Pass, "…"), Dev));
    }

    [Fact]
    public void Attach_puts_the_fixes_on_the_report_items_and_leaves_the_rest()
    {
        var rule = Rule();
        CheckItem[] items =
        [
            new("ORDERS", "TargetMissing", CheckLevel.Error, "…"),
            new("ORDERS", "NoPrimaryKey", CheckLevel.Confirm, "…"),
        ];

        var attached = CopyPrecheckFixes.Attach(Job(rule), new PrecheckReport(items, [], [], new Dictionary<string, long>()), Dev);

        Assert.Single(attached.Items[0].Fixes);
        Assert.Same(items[1], attached.Items[1]);
    }

    // ---- 작업에 적용 ----

    [Fact]
    public void Apply_sets_rule_values_as_per_table_overrides_and_job_options()
    {
        var job = Job(Rule(), Rule("CODES") with { SkipColumns = ["NOTE"] });
        CopyPrecheckFix Fix(CopyFixKind kind, string? table = "ORDERS", string? column = null, string? sequence = null) =>
            new($"x:{table}:{column}:{kind}", CopyPrecheckFixes.TitleOf(kind), table, kind) { Column = column, Sequence = sequence };

        var changed = CopyPrecheckFixes.Apply(job,
        [
            Fix(CopyFixKind.CreateIfMissing),
            Fix(CopyFixKind.SwitchToMerge),
            Fix(CopyFixKind.SkipColumn, column: "GEO"),
            Fix(CopyFixKind.AddColumn, "CODES", "NOTE"),
            Fix(CopyFixKind.LinkSequence, column: "ID", sequence: "ORDERS_SEQ"),
            Fix(CopyFixKind.OneTableAtATime, table: null),
            Fix(CopyFixKind.ConsistentReadOff, table: null),
            Fix(CopyFixKind.DisableTriggers), // 실행 결정: 작업은 그대로
        ]);

        var orders = changed.Tables[0];
        Assert.True(orders.CreateIfMissing);
        Assert.Equal(TargetAction.MergeByPk, orders.Action);
        Assert.Equal(RuleOverrides.CreateIfMissing | RuleOverrides.Action, orders.Overrides); // 전체 기본값을 바꿔도 되돌아가지 않는다
        Assert.Equal(["GEO"], orders.SkipColumns);
        var codes = changed.Tables[1];
        Assert.Equal(["NOTE"], codes.AddColumns);
        Assert.Empty(codes.SkipColumns); // 열을 더하면 건너뛰기에서 뺀다
        Assert.Equal(new SequenceRule("ORDERS_SEQ", "ORDERS", "ID"), Assert.Single(changed.Sequences));
        Assert.Null(changed.Options.ConcurrentTables);
        Assert.False(changed.ConsistentRead);

        // 「그 열 빼고 복사」는 「대상에 열 추가」를 되돌린다(비면 null — 파일에 쓰지 않는다).
        var back = CopyPrecheckFixes.Apply(changed, [Fix(CopyFixKind.SkipColumn, "CODES", "NOTE")]);
        Assert.Null(back.Tables[1].AddColumns);
        Assert.Equal(["NOTE"], back.Tables[1].SkipColumns);
    }

    [Fact]
    public void Apply_with_only_run_decisions_or_unknown_tables_returns_the_same_job()
    {
        var job = Job(Rule());

        Assert.Same(job, CopyPrecheckFixes.Apply(job, [new CopyPrecheckFix("a", "t", "ORDERS", CopyFixKind.DisableTriggers)]));
        Assert.Same(job, CopyPrecheckFixes.Apply(job, [new CopyPrecheckFix("b", "t", "NOT_IN_JOB", CopyFixKind.CreateIfMissing)]));
        Assert.Same(job, CopyPrecheckFixes.Apply(job, []));
    }

    [Fact]
    public void Add_columns_round_trip_through_the_job_file_and_are_omitted_when_empty()
    {
        var job = Job(Rule() with { AddColumns = ["NOTE"] }, Rule("CODES"));

        var json = System.Text.Json.JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob)!;

        Assert.Equal(["NOTE"], back.Tables[0].AddColumns);
        Assert.Null(back.Tables[1].AddColumns);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(json, "\"addColumns\""));
        Assert.Equal(CopyJobSchema.Current, back.SchemaVersion); // 스키마 버전은 그대로(예전 앱이 버려도 지금처럼 빼고 복사한다)
    }

    // ---- 열 추가 문장 ----

    [Fact]
    public void Add_column_ddl_for_an_oracle_target_is_nullable_without_default_identity_or_comment()
    {
        var add = CopyAddColumnDdl.Plan(Dbms.Oracle, Dbms.Oracle, Note with { IsIdentity = true, IsInvisible = true }, "CMN_USER", "ORDERS", null, out var refused);

        Assert.Null(refused);
        Assert.NotNull(add);
        Assert.Equal("ALTER TABLE \"CMN_USER\".\"ORDERS\" ADD (\"NOTE\" VARCHAR2(40 CHAR))", add.Ddl);
        Assert.True(add.Column.IsNullable);
        Assert.Null(add.Column.DefaultValue);
        Assert.False(add.Column.IsIdentity);
        Assert.False(add.Column.IsInvisible);
        Assert.Null(add.Column.Comment);

        var number = CopyAddColumnDdl.Plan(Dbms.Oracle, Dbms.Oracle, new ColumnInfo("AMT", 3, "NUMBER") { Precision = 12, Scale = 2 }, "CMN_USER", "ORDERS", null, out _);
        Assert.Equal("ALTER TABLE \"CMN_USER\".\"ORDERS\" ADD (\"AMT\" NUMBER(12,2))", number!.Ddl);
    }

    [Fact]
    public void Add_column_ddl_for_a_tibero_target_uses_the_oracle_form_and_the_mapped_type()
    {
        // Tibero 사전의 VARCHAR 는 VARCHAR2(n CHAR) 로(표 만들기와 같은 형식 글).
        var same = CopyAddColumnDdl.Plan(Dbms.Tibero, Dbms.Tibero, new ColumnInfo("NOTE", 2, "VARCHAR") { Length = 30 }, "CMN_USER", "ORDERS", null, out _);
        Assert.Equal("ALTER TABLE \"CMN_USER\".\"ORDERS\" ADD (\"NOTE\" VARCHAR2(30 CHAR))", same!.Ddl);

        // 교차(Oracle → Tibero): Tibero RAW 한도를 넘는 RAW 는 BLOB 으로.
        var cross = CopyAddColumnDdl.Plan(Dbms.Oracle, Dbms.Tibero, new ColumnInfo("PHOTO", 2, "RAW") { Length = 3000 }, "CMN_USER", "ORDERS", null, out _);
        Assert.Equal("ALTER TABLE \"CMN_USER\".\"ORDERS\" ADD (\"PHOTO\" BLOB)", cross!.Ddl);
    }

    [Fact]
    public void Add_column_is_refused_for_virtual_and_unmappable_columns()
    {
        Assert.Null(CopyAddColumnDdl.Plan(Dbms.Oracle, Dbms.Oracle, new ColumnInfo("V", 2, "NUMBER") { IsVirtual = true }, "CMN_USER", "ORDERS", null, out var virtualReason));
        Assert.Contains("가상 열", virtualReason, StringComparison.Ordinal);

        Assert.Null(CopyAddColumnDdl.Plan(Dbms.Oracle, Dbms.Tibero, new ColumnInfo("OLD", 2, "LONG"), "CMN_USER", "ORDERS", null, out var longReason));
        Assert.Contains("Tibero", longReason, StringComparison.Ordinal);
    }
}
