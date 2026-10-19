using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.Core.Language;

/// <summary>
/// 언어 서비스(TRD 4.5). L1(렉서·경량 파서)은 동기, L2(ANTLR)는 비동기. DB 를 모른다.
/// 구현: Oranda.Language <c>LanguageService</c>(partial — 분리·분류 TSK-04-01, 분석 TSK-04-03, 자동완성 문맥 TSK-04-05).
/// </summary>
public interface ILanguageService
{
    /// <summary>문서 전체를 실행 단위 문장으로 나눈다. TSK-04-01.</summary>
    IReadOnlyList<SqlStatement> Split(string text, SplitOptions o);

    /// <summary>커서 위치의 문장. 문장 밖(빈 줄 등)이면 null. TSK-04-01.</summary>
    SqlStatement? GetStatementAt(string text, int offset, SplitOptions o);

    /// <summary>L2 문법 오류. TSK-04-03.</summary>
    Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct);

    /// <summary>자동완성 문맥(현재 절, 별칭 표). TSK-04-05.</summary>
    CompletionContext GetCompletionContext(string text, int offset);
}

/// <summary>문장 분리 규칙. 설정 <c>execution.splitOnBlankLine</c> 에서 온다.</summary>
public sealed record SplitOptions
{
    public static SplitOptions Default { get; } = new();

    public bool SplitOnBlankLine { get; init; }

    /// <summary>SQL*Plus 명령 줄을 <see cref="StatementKind.SqlPlus"/> 문장으로 인식.</summary>
    public bool RecognizeSqlPlus { get; init; } = true;
}

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>문법 오류·경고 1건. <see cref="Range"/> 는 문서 오프셋.</summary>
public sealed record Diagnostic(TextRange Range, DiagnosticSeverity Severity, string Message)
{
    public string? Code { get; init; }
}

/// <summary>커서 위치가 어떤 후보를 기대하는지.</summary>
public enum CompletionContextKind
{
    None,
    Keyword,
    Schema,
    TableOrView,
    Column,
    PackageMember,
    Hint,
}

/// <summary>
/// 자동완성 문맥. <see cref="Qualifier"/> 는 <c>.</c> 앞 이름(별칭·스키마·패키지),
/// <see cref="Aliases"/> 는 현재 문장의 별칭 → 객체 이름.
/// </summary>
public sealed record CompletionContext(CompletionContextKind Kind, string Prefix, TextRange ReplaceRange)
{
    public static CompletionContext None { get; } = new(CompletionContextKind.None, string.Empty, default);

    public string? Qualifier { get; init; }

    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();
}
