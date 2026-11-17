using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 변환 창 리뷰에서 나온 경우: 묶음엔 계획 전체(UPDATE 포함), 이름을 바꾼 변환 뒤 남은 편집, 중간 실패 뒤 초기화, 낡은 검사 버리기, WHERE 없는 UPDATE 표시,
/// 서버 구조가 바뀌면 열지 않음, 확인 블록 실패 뒤 남은 단계는 2단계부터, 형식 계열 되살리기.
/// </summary>
public class ColumnConversionReviewFixTests
{
    private static readonly Rules Dialect = new();

    private static TableEditSession Edit(Action<TableEditSession>? change = null)
    {
        var engine = CoreTableEditEngine.Instance;
        var edit = new TableEditSession(engine.FromTable(OrdersTable(), Dialect), Dialect, new TableEditContext(128, ConnectionEnvironment.Development), engine);
        var status = edit.Columns.Single(c => c.Name == "STATUS");
        status.DataType = "NUMBER";
        status.LengthText = "12";
        change?.Invoke(edit);
        return edit;
    }

    private static ColumnConversionViewModel Vm(TableEditSession edit, ColumnConversionServices? services = null) =>
        new(new FakeSession(), edit, edit.Columns.Single(c => c.Column.DataType == "NUMBER" && c.Column.OriginalPosition == 3).Key, OrdersTable(),
            services ?? Services());

    private static ColumnConversionServices Services(IChangeBundleService? bundles = null, IColumnConversionChecks? conversions = null) => new()
    {
        Exec = new TableEditRunTests.FakeExec(),
        Checks = new TableEditRunTests.FakeChecks(),
        Conversions = conversions ?? new ColumnConversionTests.FakeConversions(),
        Bundles = bundles,
    };

    [Fact]
    public async Task A_finished_conversion_goes_into_the_current_bundle_whole_with_the_update()
    {
        var bundles = new Bundles { HasCurrent = true };
        var vm = Vm(Edit(), Services(bundles));
        await vm.CheckAsync();

        await vm.RunAsync();

        var drafts = Assert.Single(bundles.Added);
        Assert.Equal(vm.Plan!.Statements.Select(s => s.Text), drafts.Select(d => d.Text));
        Assert.Contains(drafts, d => d.Text.StartsWith("UPDATE", StringComparison.Ordinal) && d.Warning == "변환 DML");
    }

    [Fact]
    public void The_copy_update_asks_like_an_unfiltered_update()
    {
        var vm = Vm(Edit());

        var update = Assert.Single(vm.Plan!.Statements, s => s.Kind == StatementKind.Dml);
        Assert.True(update.IsUnfilteredDml);
    }

    [Fact]
    public async Task A_check_result_that_arrives_after_the_expression_changed_is_dropped()
    {
        var slow = new SlowConversions();
        var vm = Vm(Edit(), Services(conversions: slow));
        var check = vm.CheckAsync();

        vm.Expression = "TO_NUMBER(TRIM(STATUS))"; // 검사가 도는 사이 식을 바꿈
        slow.Release.SetResult();
        await check;

        Assert.False(vm.HasChecked);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public void A_failed_guard_block_restarts_the_remaining_script_from_the_copy_step()
    {
        var vm = Vm(Edit());
        var plan = vm.Plan!;
        var guard = plan.Statements.ToList().FindIndex(s => plan.StepOf(plan.Statements.ToList().IndexOf(s)).Kind == ColumnConversionStepKind.DropOriginal);
        var copy = plan.Statements.ToList().FindIndex(s => s.Kind == StatementKind.Dml);

        Assert.Equal(copy, ColumnConversionViewModel.RemainingStart(plan, guard));
        Assert.Equal(guard + 1, ColumnConversionViewModel.RemainingStart(plan, guard + 1)); // DROP 문장 자체가 실패하면 그 문장부터
    }

    [Fact]
    public void After_a_renaming_conversion_only_the_other_edits_remain()
    {
        var edit = Edit(e =>
        {
            e.Columns.Single(c => c.Name == "STATUS").Name = "STATE"; // 형식과 함께 이름도
            e.Columns.Single(c => c.Name == "OLD_FLAG").Comment = "다른 편집";
        });
        var vm = Vm(edit);
        var after = OrdersTable() with
        {
            Columns = [.. OrdersTable().Columns.Select(c => c.Name == "STATUS" ? new ColumnInfo("STATE", 4, "NUMBER") { Precision = 12, Comment = "상태" } : c)],
            Constraints = [.. OrdersTable().Constraints.Where(k => k.Kind != ConstraintKind.Check), new ConstraintInfo("SYS_C0099999", ConstraintKind.Check, ["STATE"]) { Condition = "STATE IN ('A','B')" }],
        };
        var newOriginal = edit.Engine.FromTable(after, edit.Dialect);

        var remaining = ObjectDetailViewModel.ConversionRemaining(edit, vm, newOriginal);

        var diff = edit.Engine.Diff(newOriginal, remaining, edit.Dialect);
        var only = Assert.Single(diff.Columns);
        Assert.Equal("OLD_FLAG", only.Edited!.Name);
        Assert.Equal(ColumnChange.Comment, only.Changes);
        Assert.Empty(diff.Constraints); // 다시 만든 서버 이름 CHECK 는 지울 줄이 아니다
    }

    [Fact]
    public async Task The_detail_refuses_to_convert_when_the_server_table_changed_since_editing_started()
    {
        var (vm, meta, _) = CreateDetail();
        vm.ConversionChecks = new ColumnConversionTests.FakeConversions();
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);
        var status = vm.TableEdit!.Columns.Single(c => c.Name == "STATUS");
        status.DataType = "NUMBER";
        meta.Table = OrdersTable() with { Columns = [.. OrdersTable().Columns, new ColumnInfo("ADDED_ELSEWHERE", 5, "DATE")] };
        var shown = false;
        vm.ShowConversion = _ =>
        {
            shown = true;
            return Task.CompletedTask;
        };

        Assert.False(await vm.OpenConversionAsync(status.Key));

        Assert.False(shown);
        Assert.True(vm.TableEditNeedsRefresh);
        Assert.Contains("새로 고침", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversion_that_stopped_midway_resets_the_edit_to_the_reread_table()
    {
        var (vm, meta, _) = CreateDetail();
        var exec = new TableEditRunTests.FakeExec();
        vm.Exec = exec;
        vm.ConversionChecks = new ColumnConversionTests.FakeConversions();
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);
        var status = vm.TableEdit!.Columns.Single(c => c.Name == "STATUS");
        status.DataType = "NUMBER";
        vm.TableEdit.Columns.Single(c => c.Name == "OLD_FLAG").Comment = "다른 편집";
        vm.ShowConversion = async conversion =>
        {
            await conversion.CheckAsync();
            exec.FailAt = 1; // 2단계 UPDATE 에서 멈춤(1단계 ADD 는 됨)
            await conversion.RunAsync();
            meta.Table = OrdersTable() with { Columns = [.. OrdersTable().Columns, new ColumnInfo("STATUS$1", 5, "NUMBER") { Precision = 12 }] };
        };

        Assert.True(await vm.OpenConversionAsync(status.Key));

        Assert.False(vm.TableEdit!.IsDirty); // 임시 열이 지울 줄로 남지 않는다 — 다시 읽은 상태에서 다시 시작
        Assert.Contains("남은 단계 스크립트", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_a_type_away_and_back_restores_the_original_sizes()
    {
        var edit = Edit();
        var flag = edit.Columns.Single(c => c.Name == "OLD_FLAG"); // CHAR(1 BYTE)

        flag.DataType = "NUMBER";
        Assert.Null(flag.Column.Length);
        flag.DataType = "CHAR";

        Assert.Equal((1, CharSemantics.Bytes), (flag.Column.Length!.Value, flag.Column.CharSemantics!.Value));
        Assert.DoesNotContain(edit.Changes.Columns, d => d.RowKey == flag.Key);

        var id = edit.Columns.Single(c => c.Name == "ORDER_ID"); // NUMBER(10)
        id.DataType = "FLOAT";
        Assert.Null(id.Column.Precision); // 10진 정밀도를 2진 FLOAT 정밀도로 옮기지 않는다
        id.DataType = "varchar2(30 char)";
        Assert.Equal((30, CharSemantics.Characters), (id.Column.Length!.Value, id.Column.CharSemantics!.Value));
        Assert.Null(id.Column.Precision);
    }

    private sealed class Bundles : IChangeBundleService
    {
        public bool HasCurrent { get; init; }

        public List<IReadOnlyList<ChangeBundleDraft>> Added { get; } = [];

        public Task<ChangeBundleInfo?> GetCurrentAsync(CancellationToken ct) =>
            Task.FromResult(HasCurrent ? new ChangeBundleInfo(1, "11/20", DateTimeOffset.Now, DateTimeOffset.Now, Dbms.Oracle, ["SAMPLE"], true) : null);

        public ChangeBundleOrigin? OriginOf(Guid sessionId) => null;

        public Task<ChangeBundleInfo?> CreateAsync(string name, ChangeBundleOrigin? origin, bool makeCurrent, CancellationToken ct) => throw new NotSupportedException();

        public Task<ChangeBundleAddResult> AddAsync(ChangeBundleOrigin origin, IReadOnlyList<ChangeBundleDraft> statements, ChangeBundleItemSource source, CancellationToken ct)
        {
            Added.Add(statements);
            return Task.FromResult(new ChangeBundleAddResult(ChangeBundleAddOutcome.Done, 1, statements.Count, 0, 0, 0));
        }

        public Task<ChangeBundleAddResult> AddExecutedAsync(ChangeBundleOrigin origin, SqlStatement statement, ChangeBundleItemSource source, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordRunAsync(long itemId, ChangeBundleOrigin origin, bool succeeded, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class SlowConversions : IColumnConversionChecks
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dbms Dbms => Dbms.Oracle;

        public async Task<ConversionFailures> FailingRowsAsync(ISession s, DbObjectRef table, ColumnDraft source, ColumnDraft target, string expression, CancellationToken ct)
        {
            await Release.Task;
            return ConversionFailures.None;
        }

        public Task<IReadOnlyList<ForeignKeyRef>> ReferencingForeignKeysAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ForeignKeyRef>>([]);

        public Task<ConversionFailures> ConvertedCheckViolationsAsync(
            ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, string condition, CancellationToken ct) =>
            Task.FromResult(ConversionFailures.None);

        public Task<ConversionFailures> ConvertedDuplicateKeysAsync(
            ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, IReadOnlyList<string> keyColumns, CancellationToken ct) =>
            Task.FromResult(ConversionFailures.None);
    }

    private sealed class Rules : ITableEditDialect
    {
        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "DATE", "CHAR", "FLOAT"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;
    }
}
