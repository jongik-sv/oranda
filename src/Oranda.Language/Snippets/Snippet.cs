namespace Oranda.Language.Snippets;

/// <summary>라이브러리 항목 종류.</summary>
public enum SnippetKind
{
    /// <summary>삽입하는 코드 조각.</summary>
    Snippet,

    /// <summary>옵티마이저 힌트 설명.</summary>
    Hint,

    /// <summary>내장 함수 설명.</summary>
    Function,

    /// <summary>의사 컬럼 설명.</summary>
    PseudoColumn,
}

/// <summary>
/// 코드 조각·힌트·함수·의사 컬럼 한 건. <see cref="Body"/> 는 자리 표시자 문법(<c>$1</c>, <c>${1:기본값}</c>, <c>$0</c>)을 쓴다.
/// </summary>
/// <param name="Prefix">입력 줄임말(대소문자 무시). 예: <c>sel</c>.</param>
/// <param name="Name">표시 이름.</param>
/// <param name="Description">한 줄 설명.</param>
/// <param name="Body">삽입할 본문(조각) 또는 사용 예(그 밖의 종류).</param>
/// <param name="Kind">종류.</param>
/// <param name="IsUser">사용자가 만든 것인지.</param>
public sealed record Snippet(string Prefix, string Name, string Description, string Body, SnippetKind Kind = SnippetKind.Snippet, bool IsUser = false)
{
    /// <summary>자리 표시자를 풀지 않은 삽입 글자. 조각이 아닌 종류는 <see cref="Body"/> 그대로.</summary>
    public string Example => SnippetExpander.Expand(Body).Text;
}
