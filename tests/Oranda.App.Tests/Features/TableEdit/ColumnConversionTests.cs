using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 「변환해서 바꾸기…」(설계 18.9, 시안 ⑧, T17 화면): 기본 변환식·단계, 검사 ✖ 막음(스크립트 저장은 됨), 실행 = 테이블 편집기 길, 중간 실패 = 끝난 단계 ✔ + 남은 단계 스크립트,
/// 다시 만들기 = 스크립트만, DML 이 든 의뢰 스크립트, 변환식을 고치면 다시 만듦, 검사 읽기 없으면 막음, 객체 상세의 ✖ 줄 단추와 변환 뒤 다시 읽기.
/// </summary>
public class ColumnConversionTests
{
    private static readonly Rules Dialect = new();

    private static (ColumnConversionViewModel Vm, Rig Rig) Create(ConnectionEnvironment env = ConnectionEnvironment.Development, bool checks = true)
    {
        var table = OrdersTable();
        var engine = CoreTableEditEngine.Instance;
        var edit = new TableEditSession(engine.FromTable(table, Dialect), Dialect, new TableEditContext(128, env), engine);
        var status = edit.Columns.Single(c => c.Name == "STATUS");
        status.DataType = "NUMBER";
        status.LengthText = "12";
        var rig = new Rig();
        var services = new ColumnConversionServices
        {
            Exec = rig.Exec,
            Policy = rig.Policy,
            Interaction = rig.Interaction,
            Checks = checks ? new TableEditRunTests.FakeChecks() : null,
            Conversions = checks ? rig.Conversions : null,
            OpenEditor = (text, _) =>
            {
                rig.Opened.Add(text);
                return true;
            },
            PickFile = (_, _) => Task.FromResult<string?>(rig.SavePath),
            Today = () => new DateOnly(2026, 11, 17),
        };
        return (new ColumnConversionViewModel(new FakeSession(env), edit, status.Key, table, services), rig);
    }

    [Fact]
    public void Opens_with_the_default_expression_and_the_swap_steps()
    {
        var (vm, _) = Create();

        Assert.Contains("SAMPLE.ORDERS.STATUS", vm.Heading, StringComparison.Ordinal);
        Assert.Contains("NUMBER(12)", vm.Heading, StringComparison.Ordinal);
        Assert.Contains("STATUS", vm.Expression, StringComparison.Ordinal);
        Assert.True(vm.IsSwap);
        Assert.Equal([1, 2, 3, 4], vm.Steps.Take(4).Select(s => s.Number));
        Assert.Contains("DML", vm.Steps[1].Note, StringComparison.Ordinal);
        Assert.Contains("되돌릴 수 없음", vm.Steps[2].Note, StringComparison.Ordinal);
        Assert.False(vm.CanRun); // 검사 전에는 실행하지 않는다
        Assert.True(vm.CanSaveScript);
    }

    [Fact]
    public async Task Failing_rows_block_run_and_bundle_but_not_saving()
    {
        var (vm, rig) = Create();
        rig.Conversions.Failures = new ConversionFailures(3) { ViewRowsSql = "SELECT * FROM SAMPLE.ORDERS WHERE …" };

        await vm.CheckAsync();

        Assert.True(vm.IsBlocked);
        Assert.False(vm.CanRun);
        Assert.False(vm.CanAddToBundle);
        Assert.True(vm.CanSaveScript); // TE-19
        var row = Assert.Single(vm.Findings, f => f.Text.StartsWith('✖'));
        Assert.Contains("3건", row.Text, StringComparison.Ordinal);
        Assert.True(vm.OpenViewRows(row.ViewRowsSql!));
        Assert.Null(await vm.RunAsync());
        Assert.Empty(rig.Exec.Sent);
    }

    [Fact]
    public async Task Clean_check_runs_every_plan_statement_through_the_table_editor_path()
    {
        var (vm, rig) = Create();
        await vm.CheckAsync();
        Assert.False(vm.IsBlocked);
        Assert.True(vm.CanRun);
        var planned = vm.Plan!.Statements;

        var result = await vm.RunAsync();

        Assert.Equal(TableEditRunOutcome.Succeeded, result!.Outcome);
        Assert.True(vm.Succeeded);
        Assert.Equal(planned, rig.Exec.Sent);
        Assert.Contains(rig.Exec.Sent, s => s.Kind == StatementKind.Dml); // 2단계 UPDATE
        Assert.Equal(Oranda.Core.History.HistorySource.SchemaBrowser, rig.Exec.Options.Single().Source); // 자동 넣기 밖(UPDATE 가 빠지지 않게)
        Assert.All(vm.Steps, s => Assert.True(s.IsDone));
        Assert.False(vm.CanRun); // 두 번 돌리지 않는다
    }

    [Fact]
    public async Task Production_conversion_asks_once()
    {
        var (vm, rig) = Create(ConnectionEnvironment.Production);
        await vm.CheckAsync();

        await vm.RunAsync();

        Assert.Single(rig.Interaction.Batches);
        Assert.Equal(vm.Plan!.Statements.Select(s => s.Text).ToHashSet(), rig.Exec.Options.Single().PreConfirmed!.ToHashSet());
    }

    [Fact]
    public async Task A_failure_midway_marks_done_steps_and_offers_the_remaining_script()
    {
        var (vm, rig) = Create();
        await vm.CheckAsync();
        var drop = vm.Plan!.Statements.ToList().FindIndex(s => s.Text.Contains("DROP COLUMN", StringComparison.Ordinal));
        rig.Exec.FailAt = drop;

        var result = await vm.RunAsync();

        Assert.Equal(TableEditRunOutcome.PartiallyFailed, result!.Outcome);
        Assert.True(vm.Applied);
        Assert.False(vm.Succeeded);
        Assert.Equal(vm.Plan.CompletedSteps(drop), vm.Steps.Count(s => s.IsDone));
        Assert.True(vm.HasRemaining);
        Assert.Contains("DROP COLUMN", vm.RemainingScript, StringComparison.Ordinal);
        Assert.Contains("단계까지 끝난 뒤 멈췄습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.OpenRemainingInEditor());
        Assert.Equal(vm.RemainingScript, rig.Opened[^1]);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public async Task Rebuild_is_a_script_only_method()
    {
        var (vm, rig) = Create();
        vm.IsRebuild = true;
        await vm.CheckAsync();

        Assert.False(vm.CanRun);
        Assert.False(vm.CanAddToBundle);
        Assert.NotEmpty(vm.RebuildScript);
        Assert.True(vm.OpenRebuildInEditor());
        Assert.Equal(vm.RebuildScript, rig.Opened[^1]);
    }

    [Fact]
    public async Task Saving_the_swap_writes_a_request_script_with_the_dml_header()
    {
        var (vm, rig) = Create();
        rig.SavePath = Path.Combine(Path.GetTempPath(), "oranda-conv-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            Assert.Equal(rig.SavePath, await vm.SaveScriptAsync());
            var text = await File.ReadAllTextAsync(rig.SavePath);
            Assert.Contains("-- DML 포함", text, StringComparison.Ordinal);
            Assert.Contains("UPDATE SAMPLE.ORDERS SET", text, StringComparison.Ordinal);
            Assert.Contains("COMMIT;", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(rig.SavePath);
        }
    }

    [Fact]
    public async Task Changing_the_expression_rebuilds_the_plan_and_clears_the_check()
    {
        var (vm, _) = Create();
        await vm.CheckAsync();
        Assert.True(vm.HasChecked);

        vm.Expression = "TO_NUMBER(TRIM(STATUS))";

        Assert.False(vm.HasChecked);
        Assert.False(vm.CanRun);
        Assert.Contains(vm.Plan!.Statements, s => s.Text.Contains("TO_NUMBER(TRIM(STATUS))", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_conversion_check_readers_running_is_blocked()
    {
        var (vm, _) = Create(checks: false);

        await vm.CheckAsync();

        Assert.True(vm.IsBlocked);
        Assert.Contains("변환 검사 읽기가 없어", vm.Findings.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_detail_offers_conversion_on_the_incompatible_type_line_and_rereads_after()
    {
        var (vm, meta, _) = CreateDetail();
        var rig = new Rig();
        vm.Exec = rig.Exec;
        vm.TableEditPolicy = rig.Policy;
        vm.TableEditInteraction = rig.Interaction;
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();
        vm.ConversionChecks = rig.Conversions;
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);
        var status = vm.TableEdit!.Columns.Single(c => c.Name == "STATUS");
        status.DataType = "NUMBER";
        ((FakeTableEditEngine)vm.TableEditEngine!).Findings = [new CheckFinding(CheckSeverity.Block, CheckCode.IncompatibleTypeChange, status.Key) { Subject = "STATUS" }];
        await vm.RunTableEditAsync();
        var line = Assert.Single(vm.TableEditFindingRows);
        Assert.Equal(status.Key, line.ConversionRowKey);

        // 창에서 검사·실행한 뒤 닫힘. 서버는 STATUS 가 NUMBER 가 됨.
        vm.ShowConversion = async conversion =>
        {
            await conversion.CheckAsync();
            await conversion.RunAsync();
            meta.Table = OrdersTable() with { Columns = [.. OrdersTable().Columns.Select(c => c.Name == "STATUS" ? c with { DataType = "NUMBER", Length = null, CharSemantics = null } : c)] };
        };
        ((FakeTableEditEngine)vm.TableEditEngine!).Findings = [];

        Assert.True(await vm.OpenConversionAsync(status.Key));

        Assert.NotEmpty(rig.Exec.Sent);
        Assert.True(vm.IsTableEditing);
        Assert.Equal("NUMBER", vm.TableEdit!.Original.Columns.Single(c => c.Name == "STATUS").DataType); // 다시 읽은 원본
        Assert.DoesNotContain(vm.TableEdit.Changes.Columns, d => d.Edited?.Name == "STATUS" && d.Changes.HasFlag(ColumnChange.Type));
        Assert.Contains("형식 변환을 마쳤다", vm.StatusText, StringComparison.Ordinal);
    }

    private sealed class Rig
    {
        public TableEditRunTests.FakeExec Exec { get; } = new();

        public TableEditRunTests.FakePolicy Policy { get; } = new();

        public TableEditRunTests.FakeInteraction Interaction { get; } = new();

        public FakeConversions Conversions { get; } = new();

        public List<string> Opened { get; } = [];

        public string? SavePath { get; set; }
    }

    internal sealed class FakeConversions : IColumnConversionChecks
    {
        public ConversionFailures Failures { get; set; } = ConversionFailures.None;

        public Dbms Dbms => Dbms.Oracle;

        public Task<ConversionFailures> FailingRowsAsync(ISession s, DbObjectRef table, ColumnDraft source, ColumnDraft target, string expression, CancellationToken ct) =>
            Task.FromResult(Failures);

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

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "DATE"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;
    }
}
