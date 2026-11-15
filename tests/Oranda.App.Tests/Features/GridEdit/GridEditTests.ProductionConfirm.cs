using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Rollback;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>
/// 운영 접속 그리드 반영의 확인 한 번(사용자 요청 2026-11-16): 운영 확인이 묻는 접속이면 DML 미리보기 창 없이 실행 확인 창 하나에
/// 미리보기 글(바인드 값 줄 포함)·커밋 안내를 싣고, 여러 문장이어도 한 번만 묻는다. 개발 접속은 미리보기 창만. 취소면 실행하지 않는다.
/// 정책 관문은 실제 <see cref="PolicyGate"/>·<see cref="ExecutionPolicy"/>·<see cref="AppExecutionInteraction"/> 을 가짜 대화 상자로 돌린다.
/// </summary>
public sealed partial class GridEditTests
{
    private static readonly ConnectionProfile ProdExample =
        new("prod-example", "scott", "prod-example:1521/s", Environment: ConnectionEnvironment.Production);

    private AppExecutionInteraction? _interaction;

    private CountingDialogs UseRealGate()
    {
        var dialogs = new CountingDialogs();
        var policy = new ExecutionPolicy();
        var interaction = _interaction = new AppExecutionInteraction { Dialogs = dialogs };
        var bus = new EventBus();
        var offer = new ConfirmExtras(new RollbackOffer(true, NotCreatedReason.None) { EstimatedRows = 1, DefaultOn = true }, null);
        GridEditCommands.Policy = () => policy;
        _exec.Gate = async (s, stmt, options, ct) =>
        {
            var extras = stmt.Kind == StatementKind.Dml ? offer : ConfirmExtras.None;
            var gate = await PolicyGate.DecideAsync(policy, interaction, bus, s, stmt, options!, extras, ct);
            if (gate.Outcome == PolicyGateOutcome.Proceed && stmt.Kind == StatementKind.Dml)
            {
                dialogs.ScriptAnswers.Add(gate.Answer.CreateScript);
            }

            return gate.Outcome switch
            {
                PolicyGateOutcome.Proceed => ExecutionStatus.Succeeded,
                PolicyGateOutcome.Denied => ExecutionStatus.Denied,
                _ => ExecutionStatus.Cancelled,
            };
        };
        return dialogs;
    }

    [Fact]
    public async Task Production_apply_asks_once_with_preview_binds_and_no_preview_dialog()
    {
        var dialogs = UseRealGate();
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp", profile: ProdExample);
        GridEditCommands.Active = s;
        var previews = 0;
        GridEditCommands.ConfirmPreview = (_, _, _) => { previews++; return Task.FromResult(true); };
        s.SetCell(0, 1, "X");
        s.SetCell(1, 1, "Y"); // 두 행 = UPDATE 두 문장
        var preview = s.BuildPreview();

        await Run("oranda.grid.edit.apply");

        Assert.Equal(0, previews);
        var asked = Assert.Single(dialogs.Requests);
        Assert.Equal(preview, asked.Preview);
        Assert.Contains("-- :v1 = 'X'", asked.Preview, StringComparison.Ordinal); // 바인드 값 줄
        Assert.Contains("-- :v1 = 'Y'", asked.Preview, StringComparison.Ordinal);
        Assert.Contains("커밋하지 않습니다", asked.Notice, StringComparison.Ordinal);
        var model = new ConfirmDialogModel(asked);
        Assert.Equal(preview, model.StatementText);
        Assert.True(model.ShowsScriptOption); // 원복 줄은 그대로
        Assert.Equal(2, asked.StatementCount);
        Assert.Contains("2문장", model.ScriptOptionText, StringComparison.Ordinal);
        Assert.Contains("운영 접속 'prod-example'", model.Message, StringComparison.Ordinal);
        Assert.Contains("그리드 편집 2문장(UPDATE 2)", model.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionEnvironment.Production, asked.Profile?.Environment);
        Assert.Equal(2, _exec.Dml.Count);
        Assert.Equal([true, true], dialogs.ScriptAnswers); // 확인 1번의 원복 선택이 문장 수만큼(뒤 문장은 첫 답을 다시 쓴다)
        Assert.False(s.IsDirty);
        Assert.Equal(0, _session.Commits);

        // 반영이 끝나면 범위가 닫혀 일반 실행의 운영 확인은 지금처럼 따로 묻는다(정한 동작 4).
        Assert.Null(GridEditConfirmScope.Current);
        var other = new SqlStatement("DELETE FROM emp WHERE empno = 1", default, StatementKind.Dml) { Verb = "DELETE" };
        var decision = await new ExecutionPolicy().EvaluateAsync(new PolicyContext(ProdExample, _session.Server, Oranda.Core.History.HistorySource.Editor), other, default);
        await _interaction!.ConfirmChangeAsync(other, decision, ConfirmExtras.None, default);
        Assert.Equal(2, dialogs.Requests.Count);
        Assert.Null(dialogs.Requests[1].Preview);
        Assert.Null(dialogs.Requests[1].Notice);
    }

    [Fact]
    public async Task Scope_covers_only_this_apply_and_reuses_the_default_when_the_first_had_no_script_option()
    {
        var mine = new SqlStatement("UPDATE SCOTT.EMP SET ENAME = :v1 WHERE EMPNO = :k1", default, StatementKind.Dml) { Verb = "UPDATE" };
        var scope = GridEditConfirmScope.Create("p", false, ProdExample, [mine]);
        Assert.True(scope.Covers(mine));
        Assert.False(scope.Covers(new SqlStatement("DELETE FROM emp", default, StatementKind.Dml)));

        var decision = new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "m");
        var none = new ConfirmExtras(RollbackOffer.NotApplicable(NotCreatedReason.NotTarget), null);
        var on = new ConfirmExtras(new RollbackOffer(true, NotCreatedReason.None) { DefaultOn = true }, null);
        var first = await scope.ConfirmAsync(decision, none, () => Task.FromResult(new ConfirmAnswer(true, false, null)));
        var next = await scope.ConfirmAsync(decision, on, () => throw new InvalidOperationException("다시 묻지 않는다"));

        Assert.True(first.Proceed);
        Assert.True(next.CreateScript); // 첫 창에 확인란이 없었으면 뒤 문장은 제안의 기본값
        Assert.Equal(1, scope.Asked);
    }

    [Fact]
    public async Task Production_apply_and_commit_says_it_commits()
    {
        var dialogs = UseRealGate();
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp", profile: ProdExample);
        GridEditCommands.Active = s;
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        s.SetCell(0, 1, "X");

        await Run("oranda.grid.edit.applyAndCommit");

        Assert.Contains("커밋합니다", Assert.Single(dialogs.Requests).Notice, StringComparison.Ordinal);
        Assert.Equal(1, _session.Commits);
    }

    [Fact]
    public async Task Production_confirm_cancel_runs_nothing_and_keeps_the_edits()
    {
        var dialogs = UseRealGate();
        dialogs.Proceed = false;
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp", profile: ProdExample);
        GridEditCommands.Active = s;
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        var reported = new List<string>();
        GridEditCommands.Report = reported.Add;
        s.SetCell(0, 1, "X");
        s.SetCell(1, 1, "Y");

        await Run("oranda.grid.edit.previewDml");

        Assert.Single(dialogs.Requests);
        Assert.Empty(_exec.Dml);
        Assert.True(s.IsDirty);
        Assert.Equal(GridEditRowState.Modified, s.GetRowState(0)); // 실패 표시 없이 편집 그대로
        Assert.Null(s.Notice);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task Development_apply_shows_only_the_preview_dialog()
    {
        var dialogs = UseRealGate();
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp"); // 기본 = 개발 접속
        GridEditCommands.Active = s;
        var previews = 0;
        GridEditCommands.ConfirmPreview = (_, _, _) => { previews++; return Task.FromResult(true); };
        s.SetCell(0, 1, "X");

        await Run("oranda.grid.edit.apply");

        Assert.Equal(1, previews);
        Assert.Empty(dialogs.Requests);
        Assert.Single(_exec.Dml);
    }

    /// <summary>실행 확인 창의 요청을 세는 가짜 대화 상자.</summary>
    private sealed class CountingDialogs : IExecutionDialogs
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public List<bool> ScriptAnswers { get; } = [];

        public bool Proceed { get; set; } = true;

        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Proceed);
        }

        public Task<ConfirmAnswer> ConfirmChangeAsync(ConfirmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Proceed ? new ConfirmDialogModel(request).Answer(true, true, false, null) : ConfirmAnswer.Declined);
        }

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(
            IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);

        public Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct) => Task.FromResult(UncommittedChoice.Cancel);

        public Task<bool> PreviewDmlAsync(string text, CancellationToken ct) => throw new InvalidOperationException("미리보기는 GridEditCommands.ConfirmPreview 로 센다");
    }
}
