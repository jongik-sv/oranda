using Oranda.Core.Rollback;

namespace Oranda.Core.Execution;

/// <summary>
/// 실행 중 사용자에게 물어야 할 때 실행 엔진이 부르는 창구. 구현: Oranda.App(TSK-06-02, UI 계약 문서).
/// 화면이 없는 환경(시험)에서는 가짜 구현을 넣는다.
/// </summary>
public interface IExecutionInteraction
{
    /// <summary>정책 관문의 확인 요청. true = 진행.</summary>
    Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct);

    /// <summary>
    /// 정책 관문의 확인 요청에 원복 스크립트·백업 테이블 제안을 덧붙인 판(되돌리기 지점 설계 10.1).
    /// 기본 구현은 <see cref="ConfirmAsync"/> 를 불러 참이면 <see cref="ConfirmAnswer.Default"/>, 거짓이면 <see cref="ConfirmAnswer.Declined"/>.
    /// </summary>
    async Task<ConfirmAnswer> ConfirmChangeAsync(SqlStatement stmt, PolicyDecision decision, ConfirmExtras extras, CancellationToken ct) =>
        await ConfirmAsync(stmt, decision, ct).ConfigureAwait(false) ? ConfirmAnswer.Default(extras) : ConfirmAnswer.Declined;

    /// <summary>
    /// 여러 문장을 확인 한 번으로 받는다(테이블 편집기·반영 묶음 실행, TE-21·TE-34). 받으면 부르는 쪽이 그 문장 전부를
    /// <see cref="ExecutionOptions.PreConfirmed"/> 에 넣어 문장별로 보낸다(<c>AsSingleBlock</c> 으로 묶지 않음).
    /// 기본 구현은 첫 문장으로 <see cref="ConfirmChangeAsync"/> 를 부르고, 문장 묶음·검사 결과는 <see cref="ConfirmExtras.Batch"/>·<see cref="ConfirmExtras.Findings"/> 에 싣는다.
    /// 문장이 없으면 묻지 않고 true.
    /// </summary>
    /// <param name="decision">보일 판단(문장들 가운데 확인이 필요한 것, 다시 입력이 있으면 그것)</param>
    /// <param name="profile">문장을 보낼 접속(확인 창 머리 띠). null 이면 창이 지금 보이는 접속을 쓴다.</param>
    async Task<bool> ConfirmBatchAsync(IReadOnlyList<SqlStatement> statements, PolicyDecision decision,
                                       IReadOnlyList<Oranda.Core.Schema.TableEdit.CheckFinding> findings,
                                       Oranda.Core.Connection.ConnectionProfile? profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(decision);
        if (statements.Count == 0)
        {
            return true;
        }

        var extras = ConfirmExtras.None with { Batch = statements, Findings = findings ?? [], Profile = profile };
        var answer = await ConfirmChangeAsync(statements[0], decision, extras, ct).ConfigureAwait(false);
        return answer.Proceed;
    }

    /// <summary>치환 변수 값 입력. null = 취소.</summary>
    Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct);

    /// <summary>스크립트 오류 처리 묻기(<see cref="ScriptErrorMode.Ask"/>).</summary>
    Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct);
}
