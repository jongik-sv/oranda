using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 새 테이블 탭(테이블 편집기 설계 7.5·18.10, 시안 화면 ①, T8): 빈 객체 상세가 편집 모드로 열림, 열·제약·인덱스만 켜짐, 토글 고정,
/// 소유자·이름·주석·종류 칸, 이름 따라 규칙 이름 바뀜(손으로 고친 이름 제외), 「이 구조로」 복사, 실행 성공 → 같은 탭이 상세로·스키마 목록 다시 받기,
/// 중간 실패 → 만든 테이블 위에 남은 편집, 닫을 때 버림 확인, 작업 저장 연결 지점(18.7). Core 엔진 + 가짜 방언.
/// </summary>
public class NewTableTests
{
    private static readonly DbObjectRef OrderItems = new("SAMPLE", "ORDER_ITEMS", "TABLE");

    private static (ObjectDetailViewModel Vm, FakeMeta Meta, Probe Probe) Create()
    {
        var (vm, meta, probe) = CreateDetail();
        vm.TableEditEngine = CoreTableEditEngine.Instance; // CREATE TABLE 은 Core 빌더 꼴 그대로
        vm.RefreshSchemaTables = (_, owner, _) =>
        {
            probe.Calls.Add("refresh " + owner + " TABLE");
            return Task.CompletedTask;
        };
        return (vm, meta, probe);
    }

    private static async Task<(ObjectDetailViewModel Vm, FakeMeta Meta, Probe Probe)> OpenBlankAsync(ISession? session = null)
    {
        var (vm, meta, probe) = Create();
        Assert.True(await vm.OpenNewTableAsync(session ?? new FakeSession(), "SAMPLE", ["HR", "SAMPLE"]));
        return (vm, meta, probe);
    }

    /// <summary>열 두 개(ID NOT NULL·NAME) + PK, 이름 ORDER_ITEMS, 주석.</summary>
    private static void FillOrderItems(ObjectDetailViewModel vm)
    {
        vm.NewTableName = "order_items";
        vm.NewTableComment = "주문 상품";
        var edit = vm.TableEdit!;
        var id = edit.AddColumn()!;
        id.Name = "ID";
        id.DataType = "NUMBER";
        id.LengthText = "10";
        id.IsNullable = false;
        edit.AddColumn()!.Name = "NAME";
        var pk = edit.AddConstraint(ConstraintKind.PrimaryKey)!;
        pk.ColumnsText = "ID";
    }

    [Fact]
    public async Task Blank_new_table_opens_in_edit_mode_with_only_columns_constraints_and_indexes()
    {
        var (vm, meta, probe) = await OpenBlankAsync();

        Assert.True(vm.IsNewTableMode);
        Assert.Equal("✎ 새 테이블", vm.Title);
        Assert.Null(vm.Target);
        Assert.Equal(["열", "제약", "인덱스", "데이터", "통계", "크기"], vm.Sections.Select(s => s.Title));
        Assert.Equal(["열", "제약", "인덱스"], vm.Sections.Where(s => s.IsAvailable).Select(s => s.Title));
        Assert.Equal("열", vm.SelectedSection?.Title);
        Assert.True(vm.IsTableEditing);
        Assert.True(vm.TableEdit!.IsNewTable);
        Assert.True(vm.IsEditingColumns);
        Assert.True(vm.CanToggleTableEdit); // 토글은 보이되
        Assert.False(vm.CanUseTableEditToggle); // 끌 수 없다(설계 7.5)
        Assert.True(await vm.SetTableEditingAsync(false));
        Assert.True(vm.IsTableEditing);
        Assert.False(vm.CanRefreshDetail);
        Assert.False(vm.CanReorderColumns);
        Assert.Equal(["HR", "SAMPLE"], vm.NewTableOwners);
        Assert.Equal("SAMPLE", vm.NewTableOwner);
        Assert.Equal(string.Empty, vm.NewTableName);
        Assert.Equal(TableKind.Heap, vm.SelectedNewTableKind?.Kind);
        Assert.False(vm.TableEdit.IsDirty);
        Assert.Null(vm.CloseConfirmation()); // 아무것도 안 했으면 닫을 때 묻지 않는다
        Assert.Equal(string.Empty, vm.TableEdit.PreviewText);
        Assert.Equal(0, meta.DetailReads); // 서버에 묻지 않는다
        Assert.Empty(probe.Calls);

        // 흐린 페이지는 고를 수 없다(골라도 그 전 페이지 그대로).
        vm.SelectedSection = vm.Sections.Single(s => s.Title == "통계");
        Assert.Equal("열", vm.SelectedSection?.Title);
    }

    [Fact]
    public async Task Owner_list_always_holds_the_chosen_owner_and_changing_it_moves_the_draft()
    {
        var (vm, _, _) = Create();
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SCOTT", ["HR", "SAMPLE"]));
        Assert.Equal(["HR", "SAMPLE", "SCOTT"], vm.NewTableOwners);

        FillOrderItems(vm);
        vm.NewTableOwner = "HR";

        Assert.Equal("HR", vm.TableEdit!.Draft.Owner);
        Assert.StartsWith("CREATE TABLE HR.ORDER_ITEMS", vm.TableEdit.PreviewText, StringComparison.Ordinal);
        Assert.Equal(new DbObjectRef("HR", "ORDER_ITEMS", "TABLE"), vm.NewTableTarget);

        vm.NewTableOwner = "NOBODY"; // 목록 밖은 받지 않는다
        Assert.Equal("HR", vm.TableEdit.Draft.Owner);
    }

    [Fact]
    public async Task Name_builds_create_table_and_rule_names_follow_it_unless_the_user_renamed_them()
    {
        var (vm, _, _) = await OpenBlankAsync();
        FillOrderItems(vm);
        var edit = vm.TableEdit!;
        var uk = edit.AddConstraint(ConstraintKind.Unique)!;
        uk.ColumnsText = "NAME";
        var index = edit.AddIndex()!;
        index.ColumnsText = "NAME";

        Assert.Equal("ORDER_ITEMS", edit.Draft.Name); // 따옴표 없는 이름은 대문자
        Assert.Equal("order_items", vm.NewTableName); // 칸은 쓴 그대로(쓰는 중 글자가 바뀌지 않게)
        Assert.Equal(["PK_ORDER_ITEMS", "UK_ORDER_ITEMS_01"], edit.Draft.Constraints.Select(c => c.Name));
        Assert.Equal("IX_ORDER_ITEMS_01", edit.Draft.Indexes.Single().Name);
        Assert.StartsWith("CREATE TABLE SAMPLE.ORDER_ITEMS (", edit.PreviewText, StringComparison.Ordinal);
        Assert.Contains("COMMENT ON TABLE SAMPLE.ORDER_ITEMS IS '주문 상품'", edit.PreviewText, StringComparison.Ordinal);

        uk.Name = "UK_MY_NAME"; // 손으로 고친 이름은 따라가지 않는다(설계 7.5)
        vm.NewTableName = "LINES";

        Assert.Equal(["PK_LINES", "UK_MY_NAME"], edit.Draft.Constraints.Select(c => c.Name));
        Assert.Equal("IX_LINES_01", edit.Draft.Indexes.Single().Name);
        Assert.StartsWith("CREATE TABLE SAMPLE.LINES (", edit.PreviewText, StringComparison.Ordinal);
        Assert.True(edit.IsDirty);
        Assert.NotNull(vm.CloseConfirmation());

        vm.NewTableName = "  "; // 이름을 비우면 문장이 없다(실행·저장 못 함)
        Assert.Equal(string.Empty, edit.Draft.Name);
        Assert.False(vm.CanSaveTableEditScript);
        Assert.Null(vm.NewTableTarget);
    }

    [Fact]
    public async Task Kind_choice_sets_temporary_with_on_commit_rows_or_index_organized()
    {
        var (vm, _, _) = await OpenBlankAsync();
        FillOrderItems(vm);
        Assert.Equal(4, vm.NewTableKinds.Count);
        Assert.All(vm.NewTableKinds, k => Assert.True(k.IsEnabled));

        vm.SelectedNewTableKind = vm.NewTableKinds.Single(k => k is { Kind: TableKind.Temporary, PreserveRows: true });
        Assert.Equal(TableKind.Temporary, vm.TableEdit!.Draft.Kind);
        Assert.True(vm.TableEdit.Draft.OnCommitPreserveRows);
        Assert.Contains("CREATE GLOBAL TEMPORARY TABLE SAMPLE.ORDER_ITEMS", vm.TableEdit.PreviewText, StringComparison.Ordinal);
        Assert.Contains("ON COMMIT PRESERVE ROWS", vm.TableEdit.PreviewText, StringComparison.Ordinal);

        vm.SelectedNewTableKind = vm.NewTableKinds.Single(k => k is { Kind: TableKind.Temporary, PreserveRows: false });
        Assert.False(vm.TableEdit.Draft.OnCommitPreserveRows);
        Assert.Contains("ON COMMIT DELETE ROWS", vm.TableEdit.PreviewText, StringComparison.Ordinal);

        vm.SelectedNewTableKind = vm.NewTableKinds.Single(k => k.Kind == TableKind.Iot);
        Assert.Equal(TableKind.Iot, vm.TableEdit.Draft.Kind);
        Assert.Contains("ORGANIZATION INDEX", vm.TableEdit.PreviewText, StringComparison.Ordinal);
        Assert.False(vm.TableEdit.IsRestricted); // 새 테이블은 종류가 달라도 열·제약·인덱스를 고친다

        vm.SelectedNewTableKind = vm.NewTableKinds[0];
        Assert.Equal(TableKind.Heap, vm.TableEdit.Draft.Kind);
    }

    [Fact]
    public async Task Kinds_the_server_cannot_make_are_dimmed_with_the_reason()
    {
        var (vm, _, _) = Create();
        vm.TableEditDialect = new NoIotDialect();
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));

        var iot = vm.NewTableKinds.Single(k => k.Kind == TableKind.Iot);
        Assert.False(iot.IsEnabled);
        Assert.Equal("TableEdit.unsupported.iot", iot.Reason);
        vm.SelectedNewTableKind = iot; // 흐린 것은 고를 수 없다
        Assert.Equal(TableKind.Heap, vm.TableEdit!.Draft.Kind);
        Assert.True(vm.NewTableKinds.Single(k => k.Kind == TableKind.Temporary && k.PreserveRows).IsEnabled);
    }

    [Fact]
    public async Task Copy_structure_reads_the_source_fresh_and_renames_rule_names_for_the_new_table()
    {
        var (vm, _, probe) = Create();

        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE", ["SAMPLE"], copyFrom: Orders));

        Assert.Equal(["invalidate ORDERS", "read ORDERS"], probe.Calls); // 원본은 캐시를 무시하고 새로 읽는다(TE-4)
        var edit = vm.TableEdit!;
        Assert.True(edit.IsNewTable);
        Assert.Equal("ORDERS_COPY", vm.NewTableName);
        Assert.Equal("ORDERS_COPY", edit.Draft.Name);
        Assert.Equal("주문", vm.NewTableComment);
        Assert.Equal(["ORDER_ID", "CUSTOMER_ID", "STATUS", "OLD_FLAG"], edit.Draft.Columns.Select(c => c.Name));
        Assert.Equal(["PK_ORDERS_COPY", "CK_ORDERS_COPY_01"], edit.Draft.Constraints.Select(c => c.Name));
        Assert.Equal(["IX_ORDERS_COPY_01"], edit.Draft.Indexes.Select(i => i.Name)); // PK 인덱스는 제약이 만든다
        Assert.True(edit.IsDirty);
        Assert.StartsWith("CREATE TABLE SAMPLE.ORDERS_COPY (", edit.PreviewText, StringComparison.Ordinal);

        vm.NewTableName = "ORDERS_2024";
        Assert.Equal(["PK_ORDERS_2024", "CK_ORDERS_2024_01"], edit.Draft.Constraints.Select(c => c.Name));
        Assert.Equal(["IX_ORDERS_2024_01"], edit.Draft.Indexes.Select(i => i.Name));
    }

    [Fact]
    public async Task Copy_structure_that_cannot_read_the_source_stays_closed_with_the_reason()
    {
        var (vm, meta, _) = Create();
        meta.FailReads = true;

        Assert.False(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE", copyFrom: Orders));

        Assert.False(vm.IsTableEditing);
        Assert.Contains("ORA-03113", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_dialect_the_tab_says_why_and_does_not_edit()
    {
        var (vm, _, _) = Create();
        vm.TableEditDialect = null;

        Assert.False(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));

        Assert.False(vm.IsTableEditing);
        Assert.Contains("테이블 편집 구현이 없다", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_success_turns_the_same_tab_into_the_table_detail_and_refreshes_the_schema_list()
    {
        var (vm, meta, probe) = Create();
        var hook = new FakeHook();
        vm.NewTableDraftHook = hook;
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));
        var exec = new TableEditRunTests.FakeExec();
        vm.Exec = exec;
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        FillOrderItems(vm);
        var shown = vm.TableEdit!.Script.SqlStatements;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Succeeded, result!.Outcome);
        Assert.Equal(shown, exec.Sent); // 미리보기에 보인 같은 문장
        Assert.StartsWith("CREATE TABLE SAMPLE.ORDER_ITEMS", exec.Sent[0].Text, StringComparison.Ordinal);
        Assert.False(vm.IsNewTableMode);
        Assert.False(vm.IsTableEditing);
        Assert.Equal(OrderItems, vm.Target);
        Assert.Equal("SAMPLE.ORDER_ITEMS", vm.Title);
        Assert.Equal(1, meta.DetailReads); // 같은 탭이 그 테이블 상세로
        Assert.Contains("refresh SAMPLE TABLE", probe.Calls); // 스키마 브라우저 목록 다시 받기
        Assert.Contains("invalidate ORDER_ITEMS", probe.Calls);
        Assert.Contains(vm.Sections, s => s.Title == "일반"); // 진짜 상세 구역
        Assert.All(vm.Sections, s => Assert.True(s.IsAvailable));
        Assert.True(vm.CanUseTableEditToggle);
        Assert.Equal(NewTableDraftEnd.Created, hook.Ended); // 작업 저장 지움(18.7)
    }

    [Fact]
    public async Task Run_failing_on_the_create_statement_keeps_the_new_table_tab()
    {
        var (vm, _, probe) = await OpenBlankAsync();
        vm.Exec = new TableEditRunTests.FakeExec { FailAt = 0 };
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        FillOrderItems(vm);

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Failed, result!.Outcome);
        Assert.True(vm.IsNewTableMode);
        Assert.True(vm.TableEdit!.IsNewTable);
        Assert.Null(vm.Target);
        Assert.Equal("✎ 새 테이블", vm.Title);
        Assert.DoesNotContain(probe.Calls, c => c.StartsWith("refresh", StringComparison.Ordinal));
        Assert.Contains("ORA-01441", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_failure_after_create_edits_the_created_table_with_only_the_rest()
    {
        var (vm, meta, probe) = await OpenBlankAsync();
        vm.Exec = new TableEditRunTests.FakeExec { FailAt = 1 }; // CREATE TABLE 성공, 다음 문장(주석) 실패
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        FillOrderItems(vm);
        meta.Table = new TableInfo(OrderItems,
        [
            new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, IsNullable = false },
            new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 100, CharSemantics = CharSemantics.Bytes },
        ])
        {
            Constraints = [new ConstraintInfo("PK_ORDER_ITEMS", ConstraintKind.PrimaryKey, ["ID"])],
            Indexes = [new IndexInfo("PK_ORDER_ITEMS", true, ["ID"]) { ConstraintName = "PK_ORDER_ITEMS", IndexType = "NORMAL" }],
        };
        var edit = vm.TableEdit!;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.PartiallyFailed, result!.Outcome);
        Assert.False(vm.IsNewTableMode);
        Assert.Equal(OrderItems, vm.Target);
        Assert.Equal("SAMPLE.ORDER_ITEMS", vm.Title);
        Assert.Contains("refresh SAMPLE TABLE", probe.Calls); // 테이블은 생겼다
        Assert.Same(edit, vm.TableEdit); // 같은 편집이 만든 테이블 위로(TE-5)
        Assert.False(edit.IsNewTable);
        Assert.NotEmpty(edit.Script.Statements);
        Assert.All(edit.Script.Statements, s => Assert.StartsWith("COMMENT ON", s.Statement.Text, StringComparison.Ordinal));
        Assert.True(vm.CanUseTableEditToggle); // 이제 기존 테이블 편집
    }

    [Fact]
    public async Task Close_asks_with_the_new_table_question_and_tells_the_draft_hook()
    {
        var hook = new FakeHook();
        var (vm, _, probe) = Create();
        vm.NewTableDraftHook = hook;
        var session = new FakeSession();
        Assert.True(await vm.OpenNewTableAsync(session, "SAMPLE"));
        Assert.Same(session, hook.Session);
        Assert.Same(vm.TableEdit, hook.Edit);
        FillOrderItems(vm);

        var confirm = vm.CloseConfirmation();
        Assert.NotNull(confirm);
        probe.Answer = false;
        Assert.False(await confirm!(CancellationToken.None));
        Assert.Contains("만들던 새 테이블", Assert.Single(probe.Asked), StringComparison.Ordinal);
        Assert.Null(hook.Ended);

        vm.EndNewTableDraft(); // 탭이 닫혔다(버리기로 함)
        Assert.Equal(NewTableDraftEnd.Discarded, hook.Ended);
    }

    [Fact]
    public async Task A_restored_draft_opens_as_it_was()
    {
        var (vm, _, _) = Create();
        var draft = TableDraftFactory.Empty("SAMPLE") with
        {
            Name = "KEPT",
            Comment = "남겨 둔 것",
            Columns = [new ColumnDraft(DraftKeys.New(), "A") { DataType = "NUMBER" }],
        };

        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE", restore: draft));

        Assert.Equal("KEPT", vm.NewTableName);
        Assert.Equal("남겨 둔 것", vm.NewTableComment);
        Assert.Equal("A", Assert.Single(vm.TableEdit!.Draft.Columns).Name);
        Assert.True(vm.TableEdit.IsDirty);
    }

    [Fact]
    public async Task Opening_an_existing_object_after_a_new_table_leaves_new_table_mode()
    {
        var (vm, _, _) = Create();
        var hook = new FakeHook();
        vm.NewTableDraftHook = hook;
        Assert.True(await vm.OpenNewTableAsync(new FakeSession(), "SAMPLE"));

        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);

        Assert.False(vm.IsNewTableMode);
        Assert.False(vm.IsTableEditing);
        Assert.All(vm.Sections, s => Assert.True(s.IsAvailable));
        Assert.Equal(NewTableDraftEnd.Discarded, hook.Ended); // 만들지 않고 떠났다
        vm.EndNewTableDraft(); // 탭을 닫아도 두 번 알리지 않는다
        Assert.Single(hook.Finished);
    }

    [Fact]
    public async Task Header_fields_are_locked_while_the_run_is_in_progress()
    {
        var (vm, _, _) = await OpenBlankAsync();
        FillOrderItems(vm);
        var kind = vm.SelectedNewTableKind;

        vm.IsTableEditRunning = true;
        vm.NewTableName = "OTHER";
        vm.NewTableOwner = "HR";
        vm.NewTableComment = "바꿈";
        vm.SelectedNewTableKind = vm.NewTableKinds.Single(k => k.Kind == TableKind.Iot);

        var draft = vm.TableEdit!.Draft;
        Assert.Equal("ORDER_ITEMS", draft.Name);
        Assert.Equal("order_items", vm.NewTableName); // 칸도 받지 않는다
        Assert.Equal("SAMPLE", draft.Owner);
        Assert.Equal("주문 상품", draft.Comment);
        Assert.Equal(kind, vm.SelectedNewTableKind);

        vm.IsTableEditRunning = false;
        vm.NewTableName = "OTHER";
        Assert.Equal("OTHER", vm.TableEdit.Draft.Name);
    }

    [Fact]
    public async Task Name_box_follows_the_model_when_the_name_changes_elsewhere()
    {
        var (vm, _, _) = await OpenBlankAsync();
        FillOrderItems(vm);

        vm.TableEdit!.RevertAll(); // 원본(이름 없음)으로

        Assert.Equal(string.Empty, vm.NewTableName);
        vm.NewTableName = "order_items"; // 같은 글을 다시 써도 들어간다
        Assert.Equal("ORDER_ITEMS", vm.TableEdit.Draft.Name);
    }

    private sealed class NoIotDialect : ITableEditDialect
    {
        private readonly FakeDialect _inner = new();

        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames => _inner.TypeNames;

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) =>
            feature == TableEditFeature.IndexOrganizedTable ? "TableEdit.unsupported.iot" : null;

        public bool IsServerName(string name) => _inner.IsServerName(name);

        public bool IsReservedWord(string upperName) => false;

        public string NormalizeType(ColumnDraft column) => _inner.NormalizeType(column);

        public string? NormalizeDefault(string? serverText) => _inner.NormalizeDefault(serverText);

        public string? NormalizeExpression(string? serverText) => _inner.NormalizeExpression(serverText);

        public string? NormalizeCondition(string? serverText) => _inner.NormalizeCondition(serverText);
    }

    internal sealed class FakeHook : INewTableDraftHook
    {
        public ISession? Session { get; private set; }

        public TableEditSession? Edit { get; private set; }

        public List<NewTableDraftEnd> Finished { get; } = [];

        /// <summary>끝난 까닭(한 번만 불려야 한다).</summary>
        public NewTableDraftEnd? Ended => Finished.Count == 0 ? null : Assert.Single(Finished);

        public INewTableDraftLink Attach(ISession session, TableEditSession edit)
        {
            Session = session;
            Edit = edit;
            return new Link(this);
        }

        private sealed class Link(FakeHook owner) : INewTableDraftLink
        {
            public void Finish(NewTableDraftEnd reason) => owner.Finished.Add(reason);
        }
    }
}
