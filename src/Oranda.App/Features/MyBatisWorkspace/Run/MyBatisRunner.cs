using Oranda.App.Features.Execution;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Run;

/// <summary>
/// 「시나리오 값으로 실행」의 준비와 실행(MB-05, design.md 4.1): 펼친 SQL 을 실행 문장 하나로 만들고, 바인드를 바꾸고,
/// <c>${}</c> 치환이 있으면 값을 보이고(운영 접속이면 확인을 받고), <see cref="ExecutionController.RunPreparedAsync"/> 로 기존 실행 경로(접속 확인·전용 세션·정책 관문·결과 창)에 넘긴다.
/// 정책 관문을 우회하는 경로는 없다. 명령 처리기(<see cref="MyBatisRunCommands"/>)가 서비스를 모아 만들고, 시험은 직접 만든다.
/// </summary>
public sealed class MyBatisRunner(ExecutionController controller, ILanguageService language, Func<IExecutionDialogs>? dialogs = null)
{
    private readonly Func<IExecutionDialogs> _dialogs = dialogs ?? (() => ExecutionDialogs.Current);

    private ExecutionLog Log => controller.Log;

    /// <summary>
    /// 펼친 SQL 을 실행할 문장 하나로 만든다. 문장 분류(Kind·WHERE 없는 DML·위험 대상)는 언어 서비스가 낸 그대로 쓰고,
    /// 범위는 비워 둔다(실행 강조·오류 위치가 매퍼 탭에 찍히지 않게). 문장이 정확히 하나가 아니면 메시지만 남기고 null.
    /// </summary>
    public SqlStatement? Prepare(EvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parts = language.Split(result.Sql, SplitOptions.Default).Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (parts.Count == 1)
        {
            return parts[0] with { Range = default };
        }

        Log.AddMessage(
            MessageKind.Warning,
            parts.Count == 0
                ? "펼친 SQL 이 비어 있어 실행하지 않았습니다."
                : $"펼친 SQL 에 문장이 {parts.Count}개 있어 실행하지 않았습니다. 한 번에 문장 하나만 실행할 수 있습니다.");
        return null;
    }

    /// <summary>
    /// <paramref name="tab"/>(문장 탭)의 접속으로 <paramref name="result"/> 의 펼친 SQL 과 바인드를 실행한다.
    /// 바인드 변환 실패·문장 수 오류·확인 거절이면 실행하지 않는다(이유는 메시지 창). true = 실행을 끝까지 마쳤다.
    /// </summary>
    /// <paramref name="rowType"/> = 「바인드 변수」 창 행 형식(<see cref="MyBatisStatementEval.RowTypeOf"/>).
    public async Task<bool> RunAsync(SqlEditorViewModel tab, EvalResult result, Func<string, BindDataType?>? rowType = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(result);
        var binds = MyBatisRunBinds.Build(result.Binds, out var error, rowType);
        if (binds is null)
        {
            Log.AddMessage(MessageKind.Error, error ?? "바인드 값을 읽을 수 없습니다.");
            return false;
        }

        if (Prepare(result) is not { } stmt || !await controller.EnsureConnectionAsync(tab, ct).ConfigureAwait(true))
        {
            return false;
        }

        // 실행 중인 접속이면 묻기 전에 그만둔다(확인 창을 띄운 뒤 실행이 거절되지 않게).
        var session = controller.SessionFor(tab);
        if (session is not null && controller.State.IsBusy(session.Id))
        {
            return false;
        }

        if (result.Substitutions.Count > 0 && !await ConfirmSubstitutionsAsync(session, stmt, result.Substitutions, ct).ConfigureAwait(true))
        {
            return false;
        }

        return await controller.RunPreparedAsync(tab, stmt, binds, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// <c>${}</c> 치환 값을 메시지 창에 보이고, 운영 접속이면 확인을 받는다. true = 계속 진행.
    /// 운영 접속의 DML 은 정책 관문이 실행 직전에 한 번 더 묻는다(의도한 동작).
    /// </summary>
    private async Task<bool> ConfirmSubstitutionsAsync(ISession? session, SqlStatement stmt, IReadOnlyList<MyBatisSubstitution> substitutions, CancellationToken ct)
    {
        var lines = substitutions.Select(s => $"${{{s.Name}}} = {s.Value ?? "null"}").ToList();
        foreach (var line in lines)
        {
            Log.AddMessage(MessageKind.Info, line);
        }

        if (session?.Profile is not { Environment: ConnectionEnvironment.Production } profile)
        {
            return true;
        }

        var message = "MyBatis ${} 치환은 값을 SQL 글자에 그대로 넣습니다. 아래 값으로 실행할까요?" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        var decision = new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, message);
        if (await _dialogs().ConfirmAsync(new ConfirmRequest(stmt, decision, profile), ct).ConfigureAwait(true))
        {
            return true;
        }

        Log.AddMessage(MessageKind.Info, "${} 치환 값 확인에서 취소해 실행하지 않았습니다.");
        return false;
    }

    /// <summary>
    /// 계산 상태의 문장이 열려 있는 탭: 활성 탭이 그 문장 탭이면 그것, 아니면 같은 세션의 그 문장 탭, 그래도 없으면 세션에게 열게 한다.
    /// </summary>
    public static SqlEditorViewModel? TabOf(MyBatisStatementEval eval, SqlEditorHost host)
    {
        ArgumentNullException.ThrowIfNull(eval);
        ArgumentNullException.ThrowIfNull(host);
        bool Matches(SqlEditorViewModel tab) =>
            ReferenceEquals(host.WorkspaceOf(tab)?.MyBatis, eval.Session) && eval.Session.StatementIdOf(tab) == eval.StatementId;

        if (host.Active is { } active && Matches(active))
        {
            return active;
        }

        return host.Tabs.FirstOrDefault(Matches) ?? eval.Session.OpenStatement(eval.StatementId);
    }
}
