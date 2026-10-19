namespace Oranda.Core.Execution;

/// <summary>
/// 실행 중 사용자에게 물어야 할 때 실행 엔진이 부르는 창구. 구현: Oranda.App(TSK-06-02, UI 계약 문서).
/// 화면이 없는 환경(시험)에서는 가짜 구현을 넣는다.
/// </summary>
public interface IExecutionInteraction
{
    /// <summary>정책 관문의 확인 요청. true = 진행.</summary>
    Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct);

    /// <summary>치환 변수 값 입력. null = 취소.</summary>
    Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct);

    /// <summary>스크립트 오류 처리 묻기(<see cref="ScriptErrorMode.Ask"/>).</summary>
    Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct);
}
