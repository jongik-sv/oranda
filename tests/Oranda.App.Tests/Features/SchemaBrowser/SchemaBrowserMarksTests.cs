using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Avalonia.Layout;
using Avalonia.Media;

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
    [InlineData("IOT", "IOT", "Teal", "인덱스 구성 테이블(IOT)")]
    [InlineData("CLUSTER", "CLU", "Amber", "클러스터 테이블")]
    [InlineData("GTT_SESSION", "GTT·S", "Green", "임시 테이블(세션 유지)")]
    [InlineData("GTT_TRANSACTION", "GTT·T", "Green", "임시 테이블(트랜잭션 유지)")]
    [InlineData("EXTERNAL", "EXT", "Blue", "외부 테이블")]
    [InlineData("OBJECT", "OBJ", "Purple", "객체 테이블")]
    public void Table_kinds_get_a_bottom_right_badge_with_the_kind_name(string token, string label, string tone, string tip)
    {
        var badge = Assert.Single(Table(token).Badges);

        Assert.Equal(new SchemaBadge(label, tone, BadgeCorner.BottomRight, tip), badge);
        Assert.Equal(tip, Table(token).Tip);
    }

    [Fact]
    public void Table_kinds_are_told_apart_by_shape_and_colour_together()
    {
        string[] kinds = ["IOT", "CLUSTER", "GTT_SESSION", "GTT_TRANSACTION", "EXTERNAL", "OBJECT"];
        var looks = kinds.Select(k => Table(k).Badges.Single()).ToList();

        Assert.Equal(kinds.Length, looks.Select(b => b.Label).Distinct().Count());
        Assert.DoesNotContain(looks, b => b.Tone is "Orange" or "Red" or "Neutral"); // 상태 색은 종류에 쓰지 않는다
    }

    [Fact]
    public void Partition_is_a_separate_top_right_badge_with_the_method()
    {
        var look = Table("IOT PART:RANGE-HASH+INTERVAL");

        Assert.Equal([BadgeCorner.BottomRight, BadgeCorner.TopRight], look.Badges.Select(b => b.Corner));
        var part = look.Badges[1];
        Assert.Equal(("PART", "Purple"), (part.Label, part.Tone));
        Assert.Equal("파티션: RANGE-HASH, INTERVAL", part.Tip);
        Assert.Equal("인덱스 구성 테이블(IOT)\n파티션: RANGE-HASH, INTERVAL", look.Tip);
    }

    [Fact]
    public void Triggers_show_timing_level_and_event_and_disabled_ones_are_dimmed()
    {
        var trg = new DbObjectRef("SCOTT", "TRG", "TRIGGER");
        var before = SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("BEFORE ROW", null), false);
        Assert.Equal(("BEF", "Blue"), (before.Badges.Single().Label, before.Badges.Single().Tone));
        Assert.Equal("BEFORE 트리거 · 행 단위(FOR EACH ROW)", before.Tip);

        var after = SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("AFTER STATEMENT", "DISABLED"), false);
        Assert.Equal(["AFT", "STMT"], after.Badges.Select(b => b.Label));
        Assert.True(after.Dimmed);
        Assert.Equal("AFTER 트리거\n문장 단위(FOR EACH STATEMENT)\n꺼짐(DISABLED)", after.Tip);

        Assert.Equal("INST", SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("INSTEAD ROW", null), false).Badges[0].Label);
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
            Assert.Equal("Orange", warning.Tone);
            Assert.True(warning.IsWarning);
            Assert.False(look.Dimmed);
        }

        Assert.Contains("RETRY SCHEDULED", failed.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_disabled_dim_and_running_shows_play()
    {
        var job = new DbObjectRef("SCOTT", "J", "JOB");

        Assert.True(SchemaLooks.ForObject("objJob", job, new ObjectMark(null, "DISABLED"), false).Dimmed);
        Assert.Equal(("RUN", "Green", "실행 중"), SchemaLooks.ForObject("objJob", job, new ObjectMark(null, "RUNNING"), false).Badges.Select(b => (b.Label, b.Tone, b.Tip)).Single());
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
        Assert.Equal(("→MV", "→PKG"), (toTable.Badges[0].Label, toPackage.Badges[0].Label));
        Assert.Equal("원격 객체(DB 링크)", SchemaLooks.ForObject("objSynonym", new DbObjectRef("SCOTT", "S", "SYNONYM"), new ObjectMark("REMOTE", null), false).Tip);
    }

    [Fact]
    public void Mviews_show_refresh_method_and_on_commit_and_types_show_collections()
    {
        var mv = SchemaLooks.ForObject("objMview", new DbObjectRef("SCOTT", "MV", "MATERIALIZED VIEW"), new ObjectMark("FAST COMMIT", null), false);
        Assert.Equal("빠른 갱신(FAST)\n커밋 때 갱신(ON COMMIT)", mv.Tip);

        string?[] methods = ["FAST", "COMPLETE", "FORCE", "NEVER"];
        Assert.Equal(4, methods.Select(m => SchemaLooks.ForObject("objMview", new DbObjectRef("S", "M", "MATERIALIZED VIEW"), new ObjectMark(m, null), false).Badges.Single())
            .Select(b => b.Label).Distinct().Count());

        var varray = SchemaLooks.ForObject("objType", new DbObjectRef("SCOTT", "T", "TYPE"), new ObjectMark("VARRAY", null), false);
        var nested = SchemaLooks.ForObject("objType", new DbObjectRef("SCOTT", "T", "TYPE"), new ObjectMark("NESTED_TABLE", null), false);
        Assert.Equal(("VARRAY 타입", "중첩 테이블 타입"), (varray.Tip, nested.Tip));
        Assert.Equal(("VARRAY", "NT"), (varray.Badges[0].Label, nested.Badges[0].Label));
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
        Assert.Equal(("NOT NULL", "NN"), (notNull.Badges.Single().Tip, notNull.Badges.Single().Label));

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
        Assert.Equal(["PK", "UK", "FK", "CHK"], kinds.Select(b => b.Label));

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
        Assert.Equal("UNUSABLE", emp.Children[1].Children.Single(c => c.Title == "EMP_X").Look.Badges.Single().Label);
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
    public Task Icon_is_painted_in_the_kind_colour_with_corner_marks_and_badges_are_text_pills() => HeadlessHost.Run(() =>
    {
        var window = new Window { Width = 200, Height = 100, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        var host = new StackPanel();
        window.Content = host;
        window.Show();

        Assert.IsType<Viewbox>(SchemaLookConverter.Create(Table(null))); // 일반 테이블은 기본 아이콘 그대로
        var iot = Assert.IsType<Panel>(SchemaLookConverter.Create(Table("IOT")));
        host.Children.Add(iot);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((SchemaLookConverter.IconSize, SchemaLookConverter.IconSize), (iot.Width, iot.Height)); // 칸 크기는 아이콘 그대로
        Assert.Equal(Brush(window, "Teal"), IconFill(iot)); // 아이콘 자체를 주 종류 색으로
        var mark = Assert.Single(Marks(iot));
        Assert.Equal(BadgeShape.Diamond, ((SchemaBadge)mark.Tag!).Shape);
        Assert.Equal((SchemaLookConverter.MarkSize, SchemaLookConverter.MarkSize), (mark.Width, mark.Height));
        Assert.InRange(mark.Width, 8, 9);
        Assert.Equal((HorizontalAlignment.Right, VerticalAlignment.Bottom), (mark.HorizontalAlignment, mark.VerticalAlignment));
        Assert.Equal(SchemaLookConverter.MarkOutline, mark.StrokeThickness);
        Assert.Equal(Brush(window, "Teal"), ((ISolidColorBrush)mark.Fill!).Color);
        Assert.Equal(Resource(window, "OrandaBadgeOutlineBrush"), ((ISolidColorBrush)mark.Stroke!).Color); // 테두리는 바탕색
        Assert.InRange(iot.Bounds.Height, 15.9, 16.1);

        // 쓸 수 없음: 왼쪽 아래 주황 세모 + 주 종류는 오른쪽 아래 그대로.
        var stale = Assert.IsType<Panel>(SchemaLookConverter.Create(SchemaLooks.ForObject("objMview", new DbObjectRef("S", "MV", "MATERIALIZED VIEW"), new ObjectMark("COMPLETE", "STALE"), false)));
        host.Children.Add(stale);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Brush(window, "Blue"), IconFill(stale));
        var marks = Marks(stale).ToList();
        Assert.Equal([BadgeShape.Square, BadgeShape.Warning], marks.Select(m => ((SchemaBadge)m.Tag!).Shape));
        Assert.Equal(HorizontalAlignment.Left, marks[1].HorizontalAlignment);
        Assert.Equal(Brush(window, "Orange"), ((ISolidColorBrush)marks[1].Fill!).Color);

        // 꺼진 것: 기본 아이콘 색(종류 색 안 씀) + 표식째 0.4 로 흐리게.
        var dim = Assert.IsType<Panel>(SchemaLookConverter.Create(SchemaLooks.ForConstraint(new ConstraintInfo("C", ConstraintKind.Check, ["A"]) { IsEnabled = false })));
        host.Children.Add(dim);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.4, dim.Opacity);
        Assert.Equal(Brush(window, "Amber"), IconFill(dim)); // key 아이콘 기본 색
        var hidden = SchemaLookConverter.Create(SchemaLooks.ForColumn(new ColumnInfo("H", 5, "NUMBER") { IsInvisible = true }, false));
        Assert.Equal(0.4, hidden!.Opacity);

        Assert.Null(SchemaBadgesConverter.Create(Table(null))); // 표식이 없으면 아무것도 없다
        var broken = SchemaLooks.ForObject("objSynonym", new DbObjectRef("SCOTT", "S", "SYNONYM"), new ObjectMark(null, "BROKEN"), false);
        var warning = (Border)((StackPanel)SchemaBadgesConverter.Create(broken)!).Children.Single();
        Assert.Equal("BROKEN", ((TextBlock)warning.Child!).Text);
        Assert.True(warning.IsSet(Border.BackgroundProperty)); // 쓸 수 없음은 바탕을 채운다(주황, 글자는 바탕색)
        var look = Table("IOT PART:HASH");
        var row = Assert.IsType<StackPanel>(SchemaBadgesConverter.Create(look));
        var pills = row.Children.OfType<Border>().ToList();
        Assert.Equal(look.Badges, pills.Select(p => p.Tag));
        Assert.Equal(["IOT", "PART"], pills.Select(p => ((TextBlock)p.Child!).Text));
        Assert.All(pills, p =>
        {
            Assert.Equal(SchemaBadgesConverter.PillHeight, p.Height); // 아이콘(16)보다 낮아 줄 높이를 바꾸지 않는다
            Assert.Equal(1, p.BorderThickness.Left);
            Assert.Equal(SchemaBadgesConverter.PillFontSize, ((TextBlock)p.Child!).FontSize);
            Assert.False(string.IsNullOrEmpty((string?)ToolTip.GetTip(p)));
        });
        window.Close();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("IOT", "Teal")]
    [InlineData("CLUSTER", "Amber")]
    [InlineData("GTT_SESSION", "Green")]
    [InlineData("GTT_TRANSACTION", "Green")]
    [InlineData("EXTERNAL", "Blue")]
    [InlineData("OBJECT", "Purple")]
    [InlineData("PART:LIST", "Purple")]
    [InlineData("IOT PART:HASH", "Teal")] // 주 종류가 앞선다
    public void Icon_tone_is_the_primary_kind_badge_tone(string mark, string tone)
    {
        var look = Table(mark);

        Assert.Equal(tone, look.IconTone);
        Assert.Equal(look.Badges[0], look.KindBadge);
        Assert.Equal(look.Badges[0].Tone, look.IconTone); // 글자 표식과 짝
    }

    [Fact]
    public void Icon_tone_covers_every_kind_and_keeps_plain_disabled_and_grey_ones_as_they_are()
    {
        var trg = new DbObjectRef("S", "T", "TRIGGER");
        Assert.Equal("Blue", SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("BEFORE ROW", null), false).IconTone);
        Assert.Equal("Purple", SchemaLooks.ForObject("objSynonym", new DbObjectRef("S", "S", "SYNONYM"), new ObjectMark("TARGET:PACKAGE", null), false).IconTone);
        Assert.Equal("Teal", SchemaLooks.ForObject("objSynonym", new DbObjectRef("PUBLIC", "DUAL", "SYNONYM"), null, false).IconTone);
        Assert.Equal("Green", SchemaLooks.ForObject("objMview", new DbObjectRef("S", "M", "MATERIALIZED VIEW"), new ObjectMark("FAST", null), false).IconTone);
        Assert.Equal("Teal", SchemaLooks.ForObject("objType", new DbObjectRef("S", "T", "TYPE"), new ObjectMark("VARRAY", null), false).IconTone);
        Assert.Equal("Purple", SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IndexType = "BITMAP" }).IconTone);
        Assert.Equal("Blue", SchemaLooks.ForIndex(new IndexInfo("I", true, ["A"])).IconTone);
        Assert.Equal("Purple", SchemaLooks.ForConstraint(new ConstraintInfo("C", ConstraintKind.ForeignKey, ["A"])).IconTone);
        Assert.Equal("Amber", SchemaLooks.ForColumn(new ColumnInfo("ID", 1, "NUMBER") { IsNullable = false }, true).IconTone);

        Assert.Null(Table(null).IconTone); // 일반 = 기본 색
        Assert.Null(SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IsUnusable = true }).IconTone); // 쓸 수 없음만 = 기본 색 + 주황 세모
        Assert.Null(SchemaLooks.ForObject("objTrigger", trg, new ObjectMark("BEFORE ROW", "DISABLED"), false).IconTone); // 꺼짐 = 흐린 기본 색
        Assert.Null(SchemaLooks.ForConstraint(new ConstraintInfo("C", ConstraintKind.Check, ["A"]) { Condition = "\"A\" IS NOT NULL" }).IconTone); // 회색 종류는 꺼짐과 헷갈린다
        Assert.Null(SchemaLooks.ForObject("objProcedure", new DbObjectRef("S", "P", "PROCEDURE"), null, invalid: true).IconTone); // INVALID 는 빨간 이름만
    }

    private static readonly string[] TableKinds = ["IOT", "CLUSTER", "GTT_SESSION", "EXTERNAL", "OBJECT", "PART:RANGE"];
    private static readonly string[] TriggerKinds = ["BEFORE ROW", "AFTER ROW", "INSTEAD ROW", "COMPOUND"];
    private static readonly string[] MviewKinds = ["FAST", "COMPLETE", "FORCE", "NEVER"];
    private static readonly string[] SynonymKinds = ["TARGET:TABLE", "TARGET:PACKAGE", "TARGET:SEQUENCE", "TARGET:SYNONYM", "REMOTE"];
    private static readonly string[] WarningLabels = ["BROKEN", "STALE", "FAIL", "UNUSABLE"];

    [Fact]
    public void Corner_mark_shapes_are_one_per_kind_and_kinds_in_a_group_differ_by_colour_or_shape()
    {
        var syn = new DbObjectRef("S", "S", "SYNONYM");
        var groups = new List<List<SchemaBadge>>
        {
            TableKinds.Select(t => Table(t).KindBadge!).ToList(),
            TriggerKinds.Select(m => SchemaLooks.ForObject("objTrigger", new DbObjectRef("S", "T", "TRIGGER"), new ObjectMark(m, null), false).KindBadge!).ToList(),
            MviewKinds.Select(m => SchemaLooks.ForObject("objMview", new DbObjectRef("S", "M", "MATERIALIZED VIEW"), new ObjectMark(m, null), false).KindBadge!).ToList(),
            SynonymKinds.Select(m => SchemaLooks.ForObject("objSynonym", syn, new ObjectMark(m, null), false).KindBadge!).ToList(),
            new[] { ConstraintKind.PrimaryKey, ConstraintKind.Unique, ConstraintKind.ForeignKey, ConstraintKind.Check }.Select(k => SchemaLooks.ForConstraint(new ConstraintInfo("C", k, ["A"]) { Condition = "A > 0" }).KindBadge!).ToList(),
        };

        foreach (var group in groups)
        {
            Assert.All(group, b => Assert.NotEqual(BadgeShape.Warning, b.Shape)); // 세모는 쓸 수 없음만
            Assert.Equal(group.Count, group.Select(b => (b.Tone, b.Shape)).Distinct().Count());
        }

        // 같은 글자는 어디서나 같은 모양(PK 열·PK 제약, 파티션 테이블·파티션 인덱스).
        Assert.Equal(SchemaLooks.ForColumn(new ColumnInfo("ID", 1, "NUMBER"), true).KindBadge!.Shape, SchemaLooks.ForConstraint(new ConstraintInfo("P", ConstraintKind.PrimaryKey, ["ID"])).KindBadge!.Shape);
        Assert.Equal(Table("PART:LIST").KindBadge!.Shape, SchemaLooks.ForIndex(new IndexInfo("I", false, ["A"]) { IsPartitioned = true }).KindBadge!.Shape);
        Assert.All(WarningLabels, l => Assert.Equal(BadgeShape.Warning, new SchemaBadge(l, "Orange", BadgeCorner.BottomLeft, l).Shape));
    }

    private static Color Resource(Window window, string key)
        => Assert.IsAssignableFrom<ISolidColorBrush>(window.TryFindResource(key, window.ActualThemeVariant, out var r) ? r : null).Color;

    private static Color Brush(Window window, string tone) => Resource(window, "OrandaIcon" + tone + "Brush");

    private static Color IconFill(Control look)
        => ((ISolidColorBrush)look.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(p => !p.Classes.Contains("schema-mark")).Fill!).Color;

    private static IEnumerable<Avalonia.Controls.Shapes.Path> Marks(Control look)
        => look.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.Classes.Contains("schema-mark"));

    [Fact]
    public void Every_badge_has_a_short_label_that_tells_kinds_apart()
    {
        var all = new List<SchemaBadge>();
        foreach (var t in new[] { "IOT", "CLUSTER", "GTT_SESSION", "GTT_TRANSACTION", "EXTERNAL", "OBJECT", "PART:RANGE" })
        {
            all.AddRange(Table(t).Badges);
        }

        foreach (var m in new[] { "BEFORE ROW", "AFTER STATEMENT", "INSTEAD ROW", "COMPOUND", "BEFORE DDL", "AFTER DB" })
        {
            all.AddRange(SchemaLooks.ForObject("objTrigger", new DbObjectRef("S", "T", "TRIGGER"), new ObjectMark(m, null), false).Badges);
        }

        Assert.All(all, b => Assert.InRange(b.Label.Length, 2, 6));
        var kinds = all.DistinctBy(b => b.Tip.Split(" · ")[0]).ToList(); // 「BEFORE 트리거 · 행 단위」 는 BEFORE 와 같은 종류
        Assert.Equal(kinds.Count, kinds.Select(b => b.Label).Distinct().Count()); // 서로 다른 종류는 다른 글자
    }

    [Fact]
    public Task Tree_row_shows_the_badges_and_the_tooltip_in_the_view() => HeadlessHost.Run(async () =>
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Marks[Emp] = new ObjectMark("EXTERNAL PART:RANGE", null);
        await h.Vm.SetSessionAsync(h.Session);
        var view = new SchemaBrowserView { DataContext = h.Vm };
        var window = new Window { Width = 400, Height = 600, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        window.Show();
        var tables = h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Single(c => c.CategoryType == "TABLE");
        tables.IsExpanded = true;
        for (var i = 0; i < 20 && !tables.Loaded; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        var pill = view.GetVisualDescendants().OfType<Border>().FirstOrDefault(p => p.Classes.Contains("schema-badge"));
        Assert.NotNull(pill);
        Assert.Equal("EXT", ((TextBlock)pill.Child!).Text);
        Assert.Equal("외부 테이블", ToolTip.GetTip(pill));
        ContentControl IconHost(string title) => view.GetVisualDescendants().OfType<ContentControl>()
            .First(c => c.Content is Panel or Viewbox && c.FindAncestorOfType<TreeViewItem>()?.DataContext is SchemaNode n && n.Title == title);
        var host = IconHost("EMP");
        Assert.StartsWith("외부 테이블", (string?)ToolTip.GetTip(host), StringComparison.Ordinal);
        var icon = Assert.IsType<Panel>(host.Content); // 외부 테이블: 파란 아이콘 + 오른쪽 아래 마름모
        Assert.Equal(Brush(window, "Blue"), IconFill(icon));
        var mark = Assert.Single(Marks(icon));
        Assert.Equal((BadgeShape.Diamond, "Blue"), (((SchemaBadge)mark.Tag!).Shape, ((SchemaBadge)mark.Tag!).Tone));
        Assert.Equal(((ISolidColorBrush)pill.BorderBrush!).Color, ((ISolidColorBrush)mark.Fill!).Color); // 글자 표식과 같은 색
        Assert.IsType<Viewbox>(IconHost("DEPT").Content); // 표식 없는 테이블은 기본 아이콘

        // 크기: 글자 표식은 아이콘보다 낮고, 표식이 붙은 줄도 표식 없는 줄과 높이가 같다.
        Assert.InRange(pill.Bounds.Height, 12, 16);
        Assert.True(pill.Bounds.Width >= pill.Bounds.Height);
        var rows = view.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.DataContext is SchemaNode { Kind: SchemaNodeKind.Item }).ToList();
        var emp = rows.Single(i => ((SchemaNode)i.DataContext!).Title == "EMP");
        var dept = rows.Single(i => ((SchemaNode)i.DataContext!).Title == "DEPT");
        Assert.Equal(dept.Bounds.Height, emp.Bounds.Height);

        // 밝게·어둡게: 테두리·글자는 테마 토큰을 따라 바뀐다. 쓸 수 없음 표식은 바탕을 채운다.
        var light = Assert.IsAssignableFrom<ISolidColorBrush>(pill.BorderBrush).Color;
        Assert.NotNull(((TextBlock)pill.Child!).Foreground);
        var part = view.GetVisualDescendants().OfType<Border>().Single(p => p.Classes.Contains("schema-badge") && ((SchemaBadge)p.Tag!).Label == "PART");
        Assert.True(part.Bounds.X > pill.Bounds.X || part.Bounds.Y >= 0); // 주 종류 뒤에 파티션이 늘어선다
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        var dark = Assert.IsAssignableFrom<ISolidColorBrush>(pill.BorderBrush).Color;
        Assert.NotEqual(light, dark);
        Assert.Equal(dept.Bounds.Height, emp.Bounds.Height);
        Assert.Equal(Brush(window, "Blue"), IconFill(icon)); // 아이콘·모서리 표식도 어두운 토큰으로
        Assert.Equal(dark, ((ISolidColorBrush)mark.Fill!).Color);
        Assert.Equal(Resource(window, "OrandaBadgeOutlineBrush"), ((ISolidColorBrush)mark.Stroke!).Color);

        // 늦게 붙는 표식: 뒤에서 다시 읽은 표식에도 아이콘 색·모서리 표식이 바뀌고 줄 높이는 그대로다.
        var height = dept.Bounds.Height;
        h.Meta.Marks[new DbObjectRef("SCOTT", "DEPT", "TABLE")] = new ObjectMark("GTT_SESSION", null);
        h.Bus.Publish(new ObjectMarksRefreshed(h.Session.DbKey, "SCOTT"));
        Dispatcher.UIThread.RunJobs();
        var late = Assert.IsType<Panel>(IconHost("DEPT").Content);
        Assert.Equal(Brush(window, "Green"), IconFill(late));
        Assert.Equal(BadgeShape.Dot, ((SchemaBadge)Marks(late).Single().Tag!).Shape);
        Assert.Equal(height, dept.Bounds.Height);
        window.Close();
    });
}
