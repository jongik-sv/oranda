using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Path = Avalonia.Controls.Shapes.Path;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// 스키마 브라우저 세부 종류·상태 표식: 종류·상태 → 아이콘 뱃지·흐림·툴팁(<see cref="SchemaLooks"/>), 목록을 보인 뒤 표식이 붙고(<see cref="ObjectMarksRefreshed"/> 로 다시),
/// 테이블 펼침(열·인덱스·제약)은 이미 읽은 메타데이터로 뱃지를 고른다. 화면은 Headless 로 뱃지 컨트롤이 그려지는지까지 본다.
/// </summary>
public sealed class SchemaBrowserMarksTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static SchemaLook Table(string? subKind) => SchemaLooks.ForObject("table", Emp, subKind is null ? null : new ObjectMark(subKind, null), invalid: false);

    [Fact]
    public void Plain_objects_have_no_badge_and_no_tip()
    {
        Assert.Empty(Table(null).Badges);
        Assert.Null(Table(null).Tip);
        Assert.False(Table(null).Dimmed);
        Assert.Empty(SchemaLooks.ForObject("objType", new DbObjectRef("SCOTT", "T_OBJ", "TYPE"), null, false).Badges); // 객체 타입은 기본
        Assert.Empty(SchemaLooks.ForObject("objSynonym", new DbObjectRef("SCOTT", "S", "SYNONYM"), null, false).Badges); // 개인 동의어·표식 아직 없음
        Assert.Empty(SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IndexType = "NORMAL" }).Badges);
    }

    [Theory]
    [InlineData("IOT", BadgeShape.Diamond, "Teal", "인덱스 구성 테이블(IOT)")]
    [InlineData("CLUSTER", BadgeShape.Ring, "Amber", "클러스터 테이블")]
    [InlineData("GTT_SESSION", BadgeShape.Dot, "Green", "임시 테이블(세션 유지)")]
    [InlineData("GTT_TRANSACTION", BadgeShape.Ring, "Green", "임시 테이블(트랜잭션 유지)")]
    [InlineData("EXTERNAL", BadgeShape.Square, "Blue", "외부 테이블")]
    [InlineData("OBJECT", BadgeShape.Diamond, "Purple", "객체 테이블")]
    public void Table_kinds_get_a_bottom_right_badge_with_the_kind_name(string token, BadgeShape shape, string tone, string tip)
    {
        var badge = Assert.Single(Table(token).Badges);

        Assert.Equal(new SchemaBadge(shape, tone, BadgeCorner.BottomRight, tip), badge);
        Assert.Equal(tip, Table(token).Tip);
    }

    [Fact]
    public void Table_kinds_are_told_apart_by_shape_and_colour_together()
    {
        string[] kinds = ["IOT", "CLUSTER", "GTT_SESSION", "GTT_TRANSACTION", "EXTERNAL", "OBJECT"];
        var looks = kinds.Select(k => Table(k).Badges.Single()).ToList();

        Assert.Equal(kinds.Length, looks.Select(b => (b.Shape, b.Tone)).Distinct().Count());
        Assert.DoesNotContain(looks, b => b.Tone is "Orange" or "Red" or "Neutral"); // 상태 색은 종류에 쓰지 않는다
    }

    [Fact]
    public void Partition_is_a_separate_top_right_badge_with_the_method()
    {
        var look = Table("IOT PART:RANGE-HASH+INTERVAL");

        Assert.Equal([BadgeCorner.BottomRight, BadgeCorner.TopRight], look.Badges.Select(b => b.Corner));
        var part = look.Badges[1];
        Assert.Equal((BadgeShape.Bars, "Purple"), (part.Shape, part.Tone));
        Assert.Equal("파티션: RANGE-HASH, INTERVAL", part.Tip);
        Assert.Equal("인덱스 구성 테이블(IOT)\n파티션: RANGE-HASH, INTERVAL", look.Tip);
    }

    [Fact]
    public void Triggers_show_timing_level_and_event_and_disabled_ones_are_dimmed()
    {
        var trg = new DbObjectRef("SCOTT", "TRG", "TRIGGER");
        var before = SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("BEFORE ROW", null), false);
        Assert.Equal((BadgeShape.Square, "Blue"), (before.Badges.Single().Shape, before.Badges.Single().Tone));
        Assert.Equal("BEFORE 트리거 · 행 단위(FOR EACH ROW)", before.Tip);

        var after = SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("AFTER STATEMENT", "DISABLED"), false);
        Assert.Equal([BadgeShape.Dot, BadgeShape.Bar], after.Badges.Select(b => b.Shape));
        Assert.True(after.Dimmed);
        Assert.Equal("AFTER 트리거\n문장 단위(FOR EACH STATEMENT)\n꺼짐(DISABLED)", after.Tip);

        Assert.Equal(BadgeShape.Diamond, SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("INSTEAD ROW", null), false).Badges[0].Shape);
        Assert.Equal("DDL 이벤트 트리거", SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("BEFORE DDL", null), false).Badges[1].Tip);
        Assert.Equal("DB 이벤트 트리거", SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("AFTER DB", null), false).Badges[1].Tip);
    }

    [Fact]
    public void Unusable_things_get_the_orange_warning_in_the_bottom_left()
    {
        var syn = SchemaLooks.ForObject("objSynonym", new DbObjectRef("SCOTT", "S", "SYNONYM"), new ObjectMark(null, "BROKEN"), false);
        var mv = SchemaLooks.ForObject("objMview", new DbObjectRef("SCOTT", "MV", "MATERIALIZED VIEW"), new ObjectMark("COMPLETE", "STALE"), false);
        var job = SchemaLooks.ForObject("objJob", new DbObjectRef("SCOTT", "J", "JOB"), new ObjectMark(null, "BROKEN"), false);
        var failed = SchemaLooks.ForObject("objJob", new DbObjectRef("SCOTT", "J", "JOB"), new ObjectMark(null, "FAILED:RETRY SCHEDULED"), false);
        var index = SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IsUnusable = true });

        foreach (var look in new[] { syn, mv, job, failed, index })
        {
            var warning = look.Badges.Single(b => b.Corner == BadgeCorner.BottomLeft);
            Assert.Equal((BadgeShape.Warning, "Orange"), (warning.Shape, warning.Tone));
            Assert.False(look.Dimmed);
        }

        Assert.Contains("RETRY SCHEDULED", failed.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_disabled_dim_and_running_shows_play()
    {
        var job = new DbObjectRef("SCOTT", "J", "JOB");

        Assert.True(SchemaLooks.ForObject("objJob", job, new ObjectMark(null, "DISABLED"), false).Dimmed);
        Assert.Equal((BadgeShape.Play, "Green", "실행 중"), SchemaLooks.ForObject("objJob", job, new ObjectMark(null, "RUNNING"), false).Badges.Select(b => (b.Shape, b.Tone, b.Tip)).Single());
        Assert.Empty(SchemaLooks.ForObject("objJob", job, null, false).Badges); // 켜짐·예약됨 = 기본
    }

    [Fact]
    public void Synonyms_show_public_and_target_kind()
    {
        var pub = SchemaLooks.ForObject("objSynonym", new DbObjectRef("PUBLIC", "DUAL", "SYNONYM"), null, false);
        Assert.Equal(("PUBLIC 동의어", BadgeCorner.TopRight), (pub.Badges.Single().Tip, pub.Badges.Single().Corner));

        var toTable = SchemaLooks.ForObject("objSynonym", new DbObjectRef("SCOTT", "S", "SYNONYM"), new ObjectMark("TARGET:MATERIALIZED_VIEW", null), false);
        Assert.Equal("대상: MATERIALIZED VIEW", toTable.Tip);
        var toPackage = SchemaLooks.ForObject("objSynonym", new DbObjectRef("SCOTT", "S", "SYNONYM"), new ObjectMark("TARGET:PACKAGE", null), false);
        Assert.NotEqual((toTable.Badges[0].Shape, toTable.Badges[0].Tone), (toPackage.Badges[0].Shape, toPackage.Badges[0].Tone));
        Assert.Equal("원격 객체(DB 링크)", SchemaLooks.ForObject("objSynonym", new DbObjectRef("SCOTT", "S", "SYNONYM"), new ObjectMark("REMOTE", null), false).Tip);
    }

    [Fact]
    public void Mviews_show_refresh_method_and_on_commit_and_types_show_collections()
    {
        var mv = SchemaLooks.ForObject("objMview", new DbObjectRef("SCOTT", "MV", "MATERIALIZED VIEW"), new ObjectMark("FAST COMMIT", null), false);
        Assert.Equal("빠른 갱신(FAST)\n커밋 때 갱신(ON COMMIT)", mv.Tip);

        string?[] methods = ["FAST", "COMPLETE", "FORCE", "NEVER"];
        Assert.Equal(4, methods.Select(m => SchemaLooks.ForObject("objMview", new DbObjectRef("S", "M", "MATERIALIZED VIEW"), new ObjectMark(m, null), false).Badges.Single())
            .Select(b => (b.Shape, b.Tone)).Distinct().Count());

        var varray = SchemaLooks.ForObject("objType", new DbObjectRef("SCOTT", "T", "TYPE"), new ObjectMark("VARRAY", null), false);
        var nested = SchemaLooks.ForObject("objType", new DbObjectRef("SCOTT", "T", "TYPE"), new ObjectMark("NESTED_TABLE", null), false);
        Assert.Equal(("VARRAY 타입", "중첩 테이블 타입"), (varray.Tip, nested.Tip));
        Assert.NotEqual(varray.Badges[0].Shape, nested.Badges[0].Shape);
    }

    [Fact]
    public void Invalid_objects_keep_the_red_name_and_say_so_in_the_tip()
    {
        var look = SchemaLooks.ForObject("objProcedure", new DbObjectRef("SCOTT", "P", "PROCEDURE"), null, invalid: true);

        Assert.Empty(look.Badges);
        Assert.StartsWith("INVALID", look.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_show_key_not_null_identity_virtual_lob_and_hidden()
    {
        var pk = SchemaLooks.ForColumn(new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false, IsIdentity = true }, isPrimaryKey: true);
        Assert.Equal([BadgeCorner.BottomRight, BadgeCorner.TopLeft], pk.Badges.Select(b => b.Corner));
        Assert.Equal("기본키 열\nIDENTITY 열", pk.Tip);

        var notNull = SchemaLooks.ForColumn(new ColumnInfo("N", 2, "VARCHAR2") { IsNullable = false }, false);
        Assert.Equal(("NOT NULL", BadgeShape.Bar), (notNull.Badges.Single().Tip, notNull.Badges.Single().Shape));

        var nullable = SchemaLooks.ForColumn(new ColumnInfo("C", 3, "CLOB"), false);
        Assert.Equal("LOB 열", nullable.Badges.Single().Tip);
        Assert.Equal("LOB 열\nNULL 허용", nullable.Tip);

        Assert.Equal("가상 열", SchemaLooks.ForColumn(new ColumnInfo("V", 4, "NUMBER") { IsVirtual = true }, false).Badges.Single().Tip);
        var hidden = SchemaLooks.ForColumn(new ColumnInfo("H", 5, "NUMBER") { IsInvisible = true }, false);
        Assert.True(hidden.Dimmed);
        Assert.Contains("숨은 열(INVISIBLE)", hidden.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Indexes_show_unique_bitmap_function_based_reverse_and_partitioned()
    {
        Assert.Equal("유니크 인덱스", SchemaLooks.ForIndex(new IndexInfo("I", true, ["A"])).Tip);
        Assert.Equal("비트맵 인덱스", SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IndexType = "BITMAP" }).Tip);
        Assert.Equal("역방향(REVERSE) 인덱스", SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IndexType = "NORMAL/REV" }).Tip);

        var fbi = SchemaLooks.ForIndex(new IndexInfo("I", true, ["SYS_NC1$"]) { IndexType = "FUNCTION-BASED NORMAL", Expressions = ["UPPER(\"A\")"], IsPartitioned = true });
        Assert.Equal("함수 기반 인덱스\n유니크 인덱스\n파티션 인덱스", fbi.Tip);
        Assert.Equal(3, fbi.Badges.Select(b => b.Corner).Distinct().Count());

        // DESC 만 있는 인덱스(Oracle 은 FUNCTION-BASED NORMAL 로 적는다)는 식이 없어 함수 기반이 아니다.
        Assert.Empty(SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IndexType = "FUNCTION-BASED NORMAL", Descending = [true] }).Badges);
    }

    [Fact]
    public void Constraints_show_kind_novalidate_and_disabled()
    {
        var kinds = new[] { ConstraintKind.PrimaryKey, ConstraintKind.Unique, ConstraintKind.ForeignKey, ConstraintKind.Check }
            .Select(k => SchemaLooks.ForConstraint(new ConstraintInfo("C", k, ["A"]) { Condition = k == ConstraintKind.Check ? "A > 0" : null }).Badges.Single())
            .ToList();
        Assert.Equal(["기본키(PK)", "유니크(UK)", "외래키(FK)", "검사(CHECK)"], kinds.Select(b => b.Tip));
        Assert.Equal(4, kinds.Select(b => (b.Shape, b.Tone)).Distinct().Count());

        var notNull = SchemaLooks.ForConstraint(new ConstraintInfo("C", ConstraintKind.Check, ["A"]) { Condition = "( \"A\" IS NOT NULL )" });
        Assert.Equal("NOT NULL", notNull.Badges.Single().Tip);

        var off = SchemaLooks.ForConstraint(new ConstraintInfo("FK", ConstraintKind.ForeignKey, ["A"]) { IsEnabled = false, IsValidated = false });
        Assert.True(off.Dimmed);
        Assert.Equal("외래키(FK)\n기존 행을 검사하지 않음(NOVALIDATE)\n꺼짐(DISABLED)", off.Tip);
    }

    [Fact]
    public async Task Marks_attach_after_the_list_and_follow_marks_refreshed_events()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Marks[Emp] = new ObjectMark("GTT_SESSION", null);
        await h.Vm.SetSessionAsync(h.Session);
        var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
        Assert.Contains("SCOTT", h.Meta.MarkCalls);
        var tables = scott.Children.Single(c => c.CategoryType == "TABLE");
        await h.Vm.ExpandAsync(tables);
        var emp = tables.Children.Single(c => c.Title == "EMP");
        var dept = tables.Children.Single(c => c.Title == "DEPT");

        Assert.Equal("GTT_SESSION", emp.Mark?.SubKind);
        Assert.StartsWith("임시 테이블(세션 유지)", emp.LookTip, StringComparison.Ordinal); // 시험 대역은 모든 객체를 INVALID 로도 돌려준다(뒤 줄)
        Assert.Null(dept.Mark);

        // 제공자가 뒤에서 표식을 다시 읽었다: 다시 읽어 그린 노드에 붙인다(트리는 다시 짓지 않는다).
        var changed = new List<string>();
        dept.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        h.Meta.Marks.Remove(Emp);
        h.Meta.Marks[new DbObjectRef("SCOTT", "DEPT", "TABLE")] = new ObjectMark("PART:LIST", null);
        h.Bus.Publish(new ObjectMarksRefreshed("OTHER/-/X", "SCOTT")); // 다른 접속은 무시
        Assert.Null(dept.Mark);
        h.Bus.Publish(new ObjectMarksRefreshed(h.Session.DbKey, "SCOTT"));

        Assert.Same(emp, tables.Children.Single(c => c.Title == "EMP"));
        Assert.Null(emp.Mark);
        Assert.Equal("PART:LIST", dept.Mark?.SubKind);
        Assert.Contains(nameof(SchemaNode.Look), changed);
    }

    [Fact]
    public async Task Failing_marks_do_not_block_the_tree()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.FailMarks = true;
        await h.Vm.SetSessionAsync(h.Session);
        var tables = h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == "TABLE");
        await h.Vm.ExpandAsync(tables);

        Assert.Contains(tables.Children, c => c.Title == "EMP");
        Assert.All(tables.Children.Where(c => c.Kind == SchemaNodeKind.Item), c => Assert.Null(c.Mark));
    }

    [Fact]
    public async Task Table_folders_pick_badges_from_the_table_metadata_already_read()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Tables[Emp] = h.Meta.Tables[Emp] with
        {
            Indexes = [new IndexInfo("EMP_PK", true, ["EMPNO"]), new IndexInfo("EMP_X", false, ["ENAME"]) { IsUnusable = true }],
            Constraints = [new ConstraintInfo("EMP_PK", ConstraintKind.PrimaryKey, ["EMPNO"]), new ConstraintInfo("EMP_CK", ConstraintKind.Check, ["ENAME"]) { IsEnabled = false, Condition = "ENAME <> 'X'" }],
        };
        h.Tree = new TriggerTree { Triggers = new SchemaFolderResult<TreeTrigger>([new TreeTrigger("SCOTT", "TRG_OFF", "DISABLED")]) };
        h.Meta.Marks[new DbObjectRef("SCOTT", "TRG_OFF", "TRIGGER")] = new ObjectMark("BEFORE ROW", null);
        await h.Vm.SetSessionAsync(h.Session);
        var tables = h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == "TABLE");
        await h.Vm.ExpandAsync(tables);
        var emp = tables.Children.Single(c => c.Title == "EMP");
        await h.Vm.ExpandAsync(emp);
        foreach (var folder in emp.Children.Take(4))
        {
            await h.Vm.ExpandAsync(folder);
        }

        var columns = emp.Children[0].Children;
        Assert.Equal("기본키 열", columns.Single(c => c.Title == "EMPNO").Look.Badges.Single().Tip);
        Assert.True(columns.Single(c => c.Title == "HIDDEN").IsDimmed);
        Assert.Equal(BadgeShape.Warning, emp.Children[1].Children.Single(c => c.Title == "EMP_X").Look.Badges.Single().Shape);
        var check = emp.Children[2].Children.Single(c => c.Title == "EMP_CK");
        Assert.True(check.IsDimmed);
        var trigger = emp.Children[3].Children.Single();
        Assert.Equal(new ObjectMark("BEFORE ROW", "DISABLED"), trigger.Mark); // 폴더에서 읽은 꺼짐 + 목록 표식의 시점
        Assert.True(trigger.IsDimmed);
        Assert.Equal(1, h.Meta.TableCalls); // 새 질의 없이 펼칠 때 읽은 것만 쓴다
    }

    private sealed class TriggerTree : ISchemaTreeProvider
    {
        public SchemaFolderResult<TreeTrigger> Triggers { get; init; } = new([]);

        public Task<SchemaFolderResult<TreeTrigger>> ListTriggersAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(Triggers);

        public Task<SchemaFolderResult<SnapshotLogRef>> ListSnapshotLogsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(new SchemaFolderResult<SnapshotLogRef>([]));
    }

    [Fact]
    public Task Converter_draws_badges_on_the_icon_and_dims_disabled_ones() => HeadlessHost.Run(() =>
    {
        var plain = SchemaLookConverter.Create(SchemaLook.Plain("table"));
        Assert.IsNotType<Panel>(plain); // 뱃지가 없으면 기본 아이콘 그대로

        var look = Table("IOT PART:HASH");
        var panel = Assert.IsType<Panel>(SchemaLookConverter.Create(look));
        var badges = panel.Children.OfType<Path>().ToList();
        Assert.Equal(look.Badges, badges.Select(p => p.Tag));
        Assert.All(badges, p => Assert.Equal(SchemaLookConverter.BadgeSize, p.Width));
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, badges[1].VerticalAlignment); // 파티션은 오른쪽 위

        var dim = Assert.IsType<Panel>(SchemaLookConverter.Create(SchemaLooks.ForConstraint(new ConstraintInfo("C", ConstraintKind.Check, ["A"]) { IsEnabled = false })));
        Assert.Equal(0.4, dim.Children[0].Opacity);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Tree_row_shows_the_badges_and_the_tooltip_in_the_view() => HeadlessHost.Run(async () =>
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Marks[Emp] = new ObjectMark("EXTERNAL", null);
        await h.Vm.SetSessionAsync(h.Session);
        var view = new SchemaBrowserView { DataContext = h.Vm };
        var window = new Window { Width = 400, Height = 600, Content = view };
        window.Show();
        var tables = h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == "TABLE");
        tables.IsExpanded = true;
        for (var i = 0; i < 20 && !tables.Loaded; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        var badge = view.GetVisualDescendants().OfType<Path>().FirstOrDefault(p => p.Classes.Contains("schema-badge"));
        Assert.NotNull(badge);
        Assert.Equal("외부 테이블", ((SchemaBadge)badge.Tag!).Tip);
        var host = badge.GetVisualAncestors().OfType<ContentControl>().First();
        Assert.StartsWith("외부 테이블", (string?)ToolTip.GetTip(host), StringComparison.Ordinal);
        Assert.NotNull(badge.Fill); // 테마 토큰(OrandaIconBlueBrush)이 풀렸다
        window.Close();
    });
}
