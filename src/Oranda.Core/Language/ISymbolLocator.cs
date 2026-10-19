using Oranda.Core.Text;

namespace Oranda.Core.Language;

/// <summary>
/// 정의로 이동·객체 정보 팝업·현재 파일 안 참조 찾기(EDIT, TSK-04-06). 텍스트만 본다(DB 조회는 호출자가 메타데이터 제공자로).
/// 구현: Oranda.Language(TSK-04-06).
/// </summary>
public interface ISymbolLocator
{
    /// <summary>커서 위치의 객체 이름(<c>owner.name.member</c> 조각). 식별자가 아니면 null.</summary>
    SymbolReference? GetSymbolAt(string text, int offset);

    /// <summary>같은 문서 안에서 커서 위치 식별자와 같은 것을 가리키는 범위.</summary>
    IReadOnlyList<TextRange> FindReferences(string text, int offset);
}

/// <param name="Range">식별자 전체 범위</param>
/// <param name="Name">객체 이름(별칭은 풀어서)</param>
public sealed record SymbolReference(TextRange Range, string Name)
{
    public string? Owner { get; init; }

    public string? Member { get; init; }
}
