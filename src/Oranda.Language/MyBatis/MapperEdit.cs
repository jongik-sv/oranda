namespace Oranda.Language.MyBatis;

/// <summary>
/// 매퍼 원문 조각 바꾸기 하나(MB-01). 계약(TSK-01-02). 요소는 id 로 가리킨다(열 때의 문서 기준).
/// 종류: <see cref="ReplaceElement"/>·<see cref="InsertElement"/>·<see cref="DeleteElement"/>·<see cref="RenameElement"/>.
/// 한 요소에는 편집 하나만 둔다: 원문을 고치고 id 도 바꿨으면 <see cref="ReplaceElement"/> 하나(새 원문에 새 id 가 들어 있음, Id 는 옛 id).
/// 저장한 문장 목록(세션 <c>Rebase</c> 의 writtenStatementIds)은 새 id 로 적는다.
/// </summary>
public abstract record MapperEdit
{
    private protected MapperEdit()
    {
    }

    /// <summary>요소 전체(여는 태그 ~ 닫는 태그)를 새 원문으로 바꾼다. 문장 탭에서 고친 글이 그대로 들어온다.</summary>
    public sealed record ReplaceElement(string Id, string NewText) : MapperEdit;

    /// <summary>
    /// 새 요소를 넣는다(<paramref name="Id"/> = 새 요소의 id, 일부 저장 체크 목록이 쓴다). <paramref name="AfterId"/> 가 null 이면 <c>&lt;/mapper&gt;</c> 바로 앞. 들여쓰기는 앞 요소를 따르고
    /// 줄 끝은 문서의 줄 끝(<see cref="Oranda.Core.MyBatis.MapperFileFormat.NewLine"/>)이다.
    /// </summary>
    public sealed record InsertElement(string Id, string NewText, string? AfterId = null) : MapperEdit;

    /// <summary>요소를 지운다(앞뒤 빈 줄 하나까지 정리하는 규칙은 구현이 정하고 시험한다).</summary>
    public sealed record DeleteElement(string Id) : MapperEdit;

    /// <summary>id 속성 값만 바꾼다(속성 따옴표·순서는 그대로).</summary>
    public sealed record RenameElement(string Id, string NewId) : MapperEdit;
}
