using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.Core.Language;

/// <summary>
/// 자동완성 후보(EDIT-04). 문맥(<see cref="ILanguageService.GetCompletionContext"/>) + 메타데이터 제공자로 후보를 만든다.
/// 150 ms 안에 못 끝나면 캐시 결과만 먼저 돌려주고 <see cref="CompletionResult.IsPartial"/> = true.
/// 구현: Oranda.Language(TSK-04-05).
/// </summary>
public interface ICompletionProvider
{
    /// <param name="session">접속이 없으면 null(키워드·힌트만)</param>
    /// <param name="text">문서 전체</param>
    /// <param name="offset">커서 위치</param>
    /// <param name="ct">취소</param>
    Task<CompletionResult> GetCompletionsAsync(ISession? session, string text, int offset, CancellationToken ct);
}

public enum CompletionItemKind
{
    Keyword,
    Schema,
    Table,
    View,
    Synonym,
    Column,
    Package,
    Procedure,
    Function,
    Hint,
    Snippet,
}

public sealed record CompletionItem(string Label, CompletionItemKind Kind, string InsertText)
{
    public string? Detail { get; init; }
}

public sealed record CompletionResult(IReadOnlyList<CompletionItem> Items, TextRange ReplaceRange)
{
    public bool IsPartial { get; init; }
}
