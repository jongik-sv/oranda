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

    /// <summary>넣을 때 이름 앞에 붙이는 스키마(스키마를 쓰지 않고 찾은 다른 스키마의 객체). 없으면 이름만 넣는다.</summary>
    public string? Qualifier { get; init; }
}

public sealed record CompletionResult(IReadOnlyList<CompletionItem> Items, TextRange ReplaceRange)
{
    public bool IsPartial { get; init; }

    /// <summary>
    /// 담지 못한 후보가 있다(스키마를 쓰지 않은 빈 접두어라 다른 스키마를 빼거나, 한도에서 잘랐다). 글자를 더 치면 다시 물어야 한다:
    /// 편집기 목록은 열릴 때 한 번 채워지고 뒤의 입력은 그 안에서만 거른다.
    /// </summary>
    public bool IsIncomplete { get; init; }
}
