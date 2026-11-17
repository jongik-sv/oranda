using System.Runtime.CompilerServices;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 실행 연결(T7, 설계 10·11장): 운영 확인 한 번(TE-34) + <c>PreConfirmed</c> 문장별 실행(TE-21), 개발 ⚠ 창 없음(TE-20), ✖ 막음, 정책 거부,
/// 성공 뒤 다시 열기·편집 끔, 중간 실패 뒤 남은 편집 다시 계산(TE-5), 미커밋 경고, 가상 식 미리보기(7.6).
/// </summary>
public class TableEditRunTests
{
    private static async Task<(ObjectDetailViewModel Vm, Rig Rig)> OpenAsync(ConnectionEnvironment env = ConnectionEnvironment.Development, bool pending = false)
    {
        var (vm, meta, probe) = CreateDetail();
        var rig = new Rig(meta, probe, (FakeTableEditEngine)vm.TableEditEngine!);
        vm.Exec = rig.Exec;
        vm.TableEditPolicy = rig.Policy;
        vm.TableEditInteraction = rig.Interaction;
        vm.TableEditChecks = rig.Checks;
        await vm.OpenAsync(new FakeSession(env) { Pending = pending }, Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        return (vm, rig);
    }

    /// <summary>3문장: STATUS 길이 MODIFY, OLD_FLAG DROP, 새 열 ADD.</summary>
    private static void EditThree(ObjectDetailViewModel vm)
    {
        var edit = vm.TableEdit!;
        edit.Columns.Single(c => c.Name == "STATUS").LengthText = "20";
        edit.ToggleDrop(edit.Columns.Single(c => c.Name == "OLD_FLAG").Key);
        edit.AddColumn()!.Name = "TOTAL_VAT";
        Assert.Equal(3, edit.Script.Statements.Count);
    }

    [Fact]
    public async Task Production_multi_statement_confirms_once_and_sends_each_statement_preconfirmed()
    {
        var (vm, rig) = await OpenAsync(ConnectionEnvironment.Production);
        EditThree(vm);
        rig.Engine.Findings = [new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, "c4") { Subject = "OLD_FLAG" }];
        var shown = vm.TableEdit!.Script.SqlStatements;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Succeeded, result!.Outcome);
        var batch = Assert.Single(rig.Interaction.Batches); // 확인 창 한 번(TE-34)
        Assert.Equal(shown, batch.Statements); // 미리보기에 보인 같은 문장 객체
        Assert.Contains(batch.Findings, f => f.Code == CheckCode.Irreversible); // 검사 줄을 같은 창에
        Assert.Equal(shown, rig.Exec.Sent); // 문장별(한 블록으로 묶지 않음)
        Assert.All(rig.Exec.Sent, s => Assert.Equal(StatementKind.Ddl, s.Kind));
        var options = Assert.Single(rig.Exec.Options);
        Assert.Equal(TableEditRunner.Source, options.Source);
        Assert.Equal(shown.Select(s => s.Text).ToHashSet(), options.PreConfirmed!.ToHashSet());
        Assert.Empty(rig.Probe.Asked); // 검사 창을 따로 띄우지 않는다
        Assert.Equal(ConnectionEnvironment.Production, rig.Interaction.LastProfile?.Environment); // 머리 띠 = 이 상세의 접속
        Assert.False(vm.IsTableEditing); // 성공 → 다시 열고 편집 끔
        Assert.Contains("invalidate ORDERS", rig.Probe.Calls);
    }

    [Fact]
    public async Task Production_declined_sends_nothing_and_keeps_the_edit()
    {
        var (vm, rig) = await OpenAsync(ConnectionEnvironment.Production);
        EditThree(vm);
        rig.Interaction.Answer = false;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Declined, result!.Outcome);
        Assert.Empty(rig.Exec.Sent);
        Assert.True(vm.IsTableEditing);
        Assert.Equal(3, vm.TableEdit!.Script.Statements.Count);
    }

    [Fact]
    public async Task Development_with_only_warnings_runs_without_a_window_and_shows_nothing_left()
    {
        var (vm, rig) = await OpenAsync();
        EditThree(vm);
        rig.Engine.Findings = [new CheckFinding(CheckSeverity.Warning, CheckCode.ServerName, "k2") { Subject = "SYS_C0012345" }];

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Succeeded, result!.Outcome);
        Assert.Empty(rig.Interaction.Batches); // 개발 접속: 정책 확인 생략(D-065)
        Assert.Empty(rig.Probe.Asked); // ⚠ 만이면 창 없음(TE-20)
        Assert.Null(rig.Exec.Options.Single().PreConfirmed);
        Assert.Equal(3, rig.Exec.Sent.Count);
    }

    [Fact]
    public async Task Test_connection_warnings_ask_once_and_declining_stops()
    {
        var (vm, rig) = await OpenAsync(ConnectionEnvironment.Test);
        rig.Policy.Kind = PolicyDecisionKind.Allow; // 테스트 접속 DDL 은 정책 확인이 없다
        EditThree(vm);
        rig.Engine.Findings = [new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, "c4") { Subject = "OLD_FLAG" }];
        rig.Probe.Answer = false;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Declined, result!.Outcome);
        Assert.Contains("OLD_FLAG", Assert.Single(rig.Probe.Asked), StringComparison.Ordinal);
        Assert.Empty(rig.Exec.Sent);
    }

    [Fact]
    public async Task Blocking_finding_stops_before_policy_and_execution_and_stays_visible()
    {
        var (vm, rig) = await OpenAsync(ConnectionEnvironment.Production);
        EditThree(vm);
        rig.Engine.Findings =
        [
            new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, "c4") { Subject = "OLD_FLAG" },
            new CheckFinding(CheckSeverity.Block, CheckCode.Overflow, "c3") { Subject = "STATUS", Count = 37 },
        ];

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Blocked, result!.Outcome);
        Assert.Empty(rig.Interaction.Batches);
        Assert.Empty(rig.Exec.Sent);
        Assert.True(vm.HasBlockingFindings);
        Assert.StartsWith("✖ STATUS", vm.TableEditFindingsText, StringComparison.Ordinal); // ✖ 가 먼저
        Assert.Contains("37", vm.TableEditFindingsText, StringComparison.Ordinal);
        Assert.True(vm.CanSaveTableEditScript); // ✖ 가 있어도 스크립트 저장은 된다(TE-19)

        vm.TableEdit!.TableComment = "고침";
        Assert.False(vm.HasTableEditFindings); // 편집이 바뀌면 지난 검사 줄은 지운다
    }

    [Fact]
    public async Task View_rows_opens_the_finding_select_in_a_new_editor_tab()
    {
        var (vm, rig) = await OpenAsync();
        EditThree(vm);
        const string sql = "SELECT * FROM SAMPLE.ORDERS WHERE LENGTH(STATUS) > 20";
        rig.Engine.Findings = [new CheckFinding(CheckSeverity.Block, CheckCode.Overflow, "c3") { Subject = "STATUS", Count = 2, ViewRowsSql = sql }];
        (string Text, Guid? Profile)? opened = null;
        vm.DdlEditorOpener = (text, profile) =>
        {
            opened = (text, profile);
            return true;
        };

        await vm.RunTableEditAsync();

        var row = Assert.Single(vm.TableEditFindingRows);
        Assert.True(row.HasViewRows);
        Assert.True(vm.OpenViewRows(row.ViewRowsSql!));
        Assert.StartsWith(sql, opened!.Value.Text, StringComparison.Ordinal);
        Assert.False(vm.CanAddToBundle); // 반영 묶음이 들어오기 전에는 숨김
    }

    [Fact]
    public async Task An_edit_changed_while_the_confirm_window_is_open_is_not_sent()
    {
        var (vm, rig) = await OpenAsync(ConnectionEnvironment.Production);
        EditThree(vm);
        rig.Interaction.During = () => vm.TableEdit!.TableComment = "확인 중에 고침";

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Declined, result!.Outcome);
        Assert.Empty(rig.Exec.Sent); // 확인받은 문장과 지금 미리보기가 다르다
        Assert.Equal(4, vm.TableEdit!.Script.Statements.Count);
    }

    [Fact]
    public async Task Reread_failure_after_a_partial_run_blocks_running_until_refresh()
    {
        var (vm, rig) = await OpenAsync();
        EditThree(vm);
        rig.Exec.FailAt = 1;
        rig.Meta.FailReads = true;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.PartiallyFailed, result!.Outcome);
        Assert.True(vm.TableEditNeedsRefresh);
        Assert.False(vm.CanRunTableEdit); // 반영된 문장을 다시 보내지 않게
        Assert.Contains("새로 고침", vm.StatusText, StringComparison.Ordinal);

        rig.Meta.FailReads = false;
        rig.Probe.Answer = true;
        Assert.True(await vm.RefreshAsync());
        await vm.SetTableEditingAsync(true);
        Assert.False(vm.TableEditNeedsRefresh);
    }

    [Fact]
    public async Task Policy_denial_sends_nothing()
    {
        var (vm, rig) = await OpenAsync(ConnectionEnvironment.Production);
        EditThree(vm);
        rig.Policy.Kind = PolicyDecisionKind.Deny;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Denied, result!.Outcome);
        Assert.Empty(rig.Exec.Sent);
        Assert.Empty(rig.Interaction.Batches);
    }

    [Fact]
    public async Task Partial_failure_recomputes_remaining_statements_on_the_reread_table()
    {
        var (vm, rig) = await OpenAsync();
        var edit = vm.TableEdit!;
        edit.Columns.Single(c => c.Name == "STATUS").Name = "STATE"; // 1: 이름(MODIFY 꼴, 가짜 엔진)
        edit.AddColumn()!.Name = "TOTAL_VAT"; // 2: ADD
        edit.TableComment = "주문 머리"; // 3: COMMENT
        Assert.Equal(3, edit.Script.Statements.Count);
        rig.Exec.FailAt = 1; // 2번째 문장 실패
        rig.Meta.Table = OrdersTable() with // 다시 읽으면 1번째 문장만 반영됨
        {
            Columns = [.. OrdersTable().Columns.Select(c => c.Name == "STATUS" ? c with { Name = "STATE" } : c)],
            Constraints = [.. OrdersTable().Constraints.Select(k => k with { Columns = [.. k.Columns.Select(c => c == "STATUS" ? "STATE" : c)] })],
        };

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.PartiallyFailed, result!.Outcome);
        Assert.Equal(1, result.SucceededCount);
        Assert.True(vm.IsTableEditing);
        Assert.Same(edit, vm.TableEdit);
        Assert.Equal(2, edit.Script.Statements.Count); // ADD + COMMENT 만 남음
        Assert.Contains(edit.Script.Statements, s => s.Statement.Text.Contains("ADD TOTAL_VAT", StringComparison.Ordinal));
        Assert.DoesNotContain(edit.Script.Statements, s => s.Statement.Text.Contains("STATE", StringComparison.Ordinal));
        Assert.Equal("STATE", edit.Original.Columns[2].Name);
        Assert.Contains("2번째 문장이 실패", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("read ORDERS", rig.Probe.Calls.Skip(2)); // 다시 읽음

        // 편집을 끄면 낡은 상세 표를 다시 읽는다.
        rig.Probe.Answer = true;
        var reads = rig.Meta.DetailReads;
        Assert.False(await vm.SetTableEditingAsync(false));
        Assert.Equal(reads + 1, rig.Meta.DetailReads);
    }

    [Fact]
    public async Task First_statement_failure_keeps_every_edit()
    {
        var (vm, rig) = await OpenAsync();
        EditThree(vm);
        rig.Exec.FailAt = 0;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Failed, result!.Outcome);
        Assert.Equal(3, vm.TableEdit!.Script.Statements.Count);
        Assert.DoesNotContain("invalidate ORDERS", rig.Probe.Calls.Skip(1)); // 바뀐 것이 없어 다시 읽지 않는다
    }

    [Fact]
    public async Task Uncommitted_session_changes_ask_before_ddl_commits_them()
    {
        var (vm, rig) = await OpenAsync(pending: true);
        EditThree(vm);
        rig.Probe.Answer = false;

        var result = await vm.RunTableEditAsync();

        Assert.Equal(TableEditRunOutcome.Declined, result!.Outcome);
        Assert.Contains("커밋하지 않은 변경", Assert.Single(rig.Probe.Asked), StringComparison.Ordinal);
        Assert.Empty(rig.Exec.Sent);
    }

    [Fact]
    public async Task Read_only_connection_cannot_run()
    {
        var (vm, _, _) = CreateDetail();
        vm.Exec = new FakeExec();
        await vm.OpenAsync(new FakeSession { ReadOnly = true }, Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.TableComment = "x";

        Assert.False(vm.CanRunTableEdit);
        Assert.Null(await vm.RunTableEditAsync());
        Assert.True(vm.CanSaveTableEditScript); // 스크립트 저장은 된다
    }

    [Fact]
    public async Task Virtual_expression_preview_reads_five_rows_with_the_used_columns()
    {
        var (vm, rig) = await OpenAsync();
        var row = vm.TableEdit!.AddColumn()!;
        row.Name = "DOUBLE_ID";
        rig.Checks.Preview = new ExpressionPreview(["ORDER_ID*2", "ORDER_ID"], [["2", "1"], [null, null]]);

        row.VirtualExpression = "order_id*2";
        await HeadlessHostless.WaitAsync(() => !vm.VirtualPreviewText.Contains("계산 중", StringComparison.Ordinal));

        var call = Assert.Single(rig.Checks.Previews);
        Assert.Equal("order_id*2", call.Expression);
        Assert.Equal(["ORDER_ID"], call.Columns);
        Assert.Equal(5, call.Rows);
        Assert.Contains("ORDER_ID=1 → 2", vm.VirtualPreviewText, StringComparison.Ordinal);
        Assert.Contains("ORDER_ID=NULL → NULL", vm.VirtualPreviewText, StringComparison.Ordinal);

        rig.Checks.Preview = new ExpressionPreview([], [], "ORA-00904: invalid identifier");
        row.VirtualExpression = "nope*2";
        await HeadlessHostless.WaitAsync(() => vm.VirtualPreviewText.Contains('✖'));
        Assert.Contains("ORA-00904", vm.VirtualPreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Virtual_expression_on_a_new_column_waits_until_after_running()
    {
        var (vm, rig) = await OpenAsync();
        var a = vm.TableEdit!.AddColumn()!;
        a.Name = "QTY";
        var v = vm.TableEdit.AddColumn()!;
        v.Name = "AMOUNT";

        v.VirtualExpression = "QTY * 2";

        Assert.Contains("실행 뒤에 확인", vm.VirtualPreviewText, StringComparison.Ordinal);
        Assert.Empty(rig.Checks.Previews);
    }

    [Fact]
    public async Task Default_confirm_batch_asks_once_with_the_first_statement_and_carries_batch_and_findings()
    {
        var interaction = new MinimalInteraction();
        IExecutionInteraction i = interaction;
        var stmts = new[] { Stmt("ALTER TABLE SAMPLE.ORDERS DROP COLUMN A"), Stmt("ALTER TABLE SAMPLE.ORDERS ADD (B NUMBER)") };
        var findings = new[] { new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, "c1") { Subject = "A" } };

        Assert.True(await i.ConfirmBatchAsync(stmts, new PolicyDecision(PolicyDecisionKind.Confirm), findings, null, CancellationToken.None));

        var (stmt, extras) = Assert.Single(interaction.Asked);
        Assert.Same(stmts[0], stmt);
        Assert.Equal(stmts, extras.Batch);
        Assert.Equal(findings, extras.Findings);
        Assert.True(await i.ConfirmBatchAsync([], new PolicyDecision(PolicyDecisionKind.Confirm), [], null, CancellationToken.None));
        Assert.Single(interaction.Asked); // 문장이 없으면 묻지 않는다
    }

    [Fact]
    public async Task App_confirm_window_shows_every_batch_statement_auto_commit_notice_and_findings()
    {
        var dialogs = new CaptureDialogs();
        var interaction = new AppExecutionInteraction { Dialogs = dialogs };
        var stmts = new[] { Stmt("ALTER TABLE SAMPLE.ORDERS DROP COLUMN A"), Stmt("ALTER TABLE SAMPLE.ORDERS ADD (B NUMBER)"), Stmt("COMMENT ON TABLE SAMPLE.ORDERS IS 'x'") };
        var findings = new[] { new CheckFinding(CheckSeverity.Warning, CheckCode.LongLock, "$table") { Count = 1_240_000 } };

        Assert.True(await ((IExecutionInteraction)interaction).ConfirmBatchAsync(stmts, new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "운영 접속"), findings, null, CancellationToken.None));

        var request = Assert.Single(dialogs.Requests);
        Assert.Equal(3, request.StatementCount);
        Assert.Equal(string.Join(Environment.NewLine, stmts.Select(s => s.Text + ";")), request.Preview);
        Assert.Contains("자동 커밋", request.Notice, StringComparison.Ordinal);
        Assert.Contains("⚠ 행이 많은 테이블", request.Notice, StringComparison.Ordinal);
        Assert.Contains("1,240,000", request.Notice, StringComparison.Ordinal);
    }

    private static SqlStatement Stmt(string text) => new(text, new Oranda.Core.Text.TextRange(0, text.Length), StatementKind.Ddl);

    private sealed class Rig(FakeMeta meta, Probe probe, FakeTableEditEngine engine)
    {
        public FakeMeta Meta { get; } = meta;

        public Probe Probe { get; } = probe;

        public FakeTableEditEngine Engine { get; } = engine;

        public FakeExec Exec { get; } = new();

        public FakePolicy Policy { get; } = new();

        public FakeInteraction Interaction { get; } = new();

        public FakeChecks Checks { get; } = new();
    }

    internal sealed class FakeExec : IExecutionService
    {
        public List<SqlStatement> Sent { get; } = [];

        public List<ExecutionOptions> Options { get; } = [];

        /// <summary>이 번호(0부터)의 문장이 실패한다. null = 모두 성공.</summary>
        public int? FailAt { get; set; }

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                         ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            Assert.Equal(ScriptErrorMode.Stop, mode);
            Options.Add(options!);
            for (var i = 0; i < stmts.Count; i++)
            {
                await Task.Yield();
                Sent.Add(stmts[i]);
                if (FailAt == i)
                {
                    yield return new ExecutionResult(stmts[i], ExecutionStatus.Failed) { Error = new DbError("ORA-01441", "ORA-01441: cannot decrease column length") };
                    yield break;
                }

                yield return new ExecutionResult(stmts[i], ExecutionStatus.Succeeded);
            }
        }
    }

    internal sealed class FakePolicy : IExecutionPolicy
    {
        /// <summary>운영 접속 DDL = 확인(SAFE-03). 개발은 관문이 확인을 생략한다.</summary>
        public PolicyDecisionKind Kind { get; set; } = PolicyDecisionKind.Confirm;

        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(Kind == PolicyDecisionKind.Allow ? PolicyDecision.Allowed : new PolicyDecision(Kind, PolicyRule.ProductionChange, "운영 접속 변경"));
    }

    internal sealed class FakeInteraction : IExecutionInteraction
    {
        public List<(IReadOnlyList<SqlStatement> Statements, IReadOnlyList<CheckFinding> Findings)> Batches { get; } = [];

        public bool Answer { get; set; } = true;

        public ConnectionProfile? LastProfile { get; private set; }

        /// <summary>확인 창이 떠 있는 동안 할 일(그새 편집 고치기).</summary>
        public Action? During { get; set; }

        public Task<bool> ConfirmBatchAsync(IReadOnlyList<SqlStatement> statements, PolicyDecision decision, IReadOnlyList<CheckFinding> findings,
                                            ConnectionProfile? profile, CancellationToken ct)
        {
            Batches.Add((statements, findings));
            LastProfile = profile;
            During?.Invoke();
            return Task.FromResult(Answer);
        }

        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => throw new InvalidOperationException("문장마다 묻지 않는다");

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MinimalInteraction : IExecutionInteraction
    {
        public List<(SqlStatement Statement, ConfirmExtras Extras)> Asked { get; } = [];

        public Task<ConfirmAnswer> ConfirmChangeAsync(SqlStatement stmt, PolicyDecision decision, ConfirmExtras extras, CancellationToken ct)
        {
            Asked.Add((stmt, extras));
            return Task.FromResult(ConfirmAnswer.Default(extras));
        }

        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CaptureDialogs : IExecutionDialogs
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) => throw new NotSupportedException();

        public Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> PreviewDmlAsync(string text, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class FakeChecks : ITableEditChecks
    {
        public List<(string Expression, IReadOnlyList<string> Columns, int Rows)> Previews { get; } = [];

        public ExpressionPreview Preview { get; set; } = new([], []);

        public Dbms Dbms => Dbms.Oracle;

        public Task<ExpressionPreview> PreviewExpressionAsync(ISession s, DbObjectRef table, string expression, IReadOnlyList<string> columns, int rows, CancellationToken ct)
        {
            Previews.Add((expression, columns, rows));
            return Task.FromResult(Preview);
        }

        public Task<string?> ValidateExpressionAsync(ISession s, string owner, IReadOnlyList<ColumnDraft> columns, string expression, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<long> CountRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(0L);

        public Task<long?> NumRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<long> OverflowCountAsync(ISession s, DbObjectRef table, string column, ColumnDraft target, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> NullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> NonNullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> DuplicateKeyCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> OrphanCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, DbObjectRef referencedTable, IReadOnlyList<string> referencedColumns, CancellationToken ct) =>
            Task.FromResult(0L);

        public Task<long> CheckViolationCountAsync(ISession s, DbObjectRef table, string condition, CancellationToken ct) => Task.FromResult(0L);

        public Task<bool> NameExistsAsync(ISession s, string owner, string name, TableEditNameKind kind, DbObjectRef? table, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<DbObjectRef>> DependentsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult<IReadOnlyList<DbObjectRef>>([]);
    }
}

/// <summary>UI 스레드 없이 비동기 조건 기다리기(가상 식 미리보기는 끝까지 기다리지 않고 뒤에서 돈다).</summary>
internal static class HeadlessHostless
{
    public static async Task WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("조건이 이뤄지지 않았다");
            }

            await Task.Delay(10);
        }
    }
}
