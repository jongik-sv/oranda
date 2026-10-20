using Oranda.Core.Sessions;

namespace Oranda.Core.Language;

/// <summary>
/// SQL 점검(lint, TSK-13-04). 실행 전에 흔한 실수를 <see cref="DiagnosticSeverity.Warning"/> 으로 알린다. 문법 분석(<see cref="ILanguageService.AnalyzeAsync"/>)과
/// 별도 단계이고, DB 를 부르지 않는다: 메타데이터가 필요한 규칙은 <paramref name="session"/> 의 메모리 캐시에 이미 있을 때만 판정하고 없으면 건너뛴다.
/// 진단 코드는 <c>LINT.&lt;규칙&gt;</c>, 메시지는 이유와 고치는 법 한 줄.
/// </summary>
public interface ILintService
{
    /// <summary>문서 전체를 점검한다. 범위는 문서 오프셋. 스레드 풀에서 부를 수 있고 취소할 수 있다.</summary>
    IReadOnlyList<Diagnostic> Lint(string text, ISession? session, CancellationToken ct);
}
