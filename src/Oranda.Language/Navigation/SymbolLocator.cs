using Oranda.Core.Composition;
using Oranda.Core.Language;
using Oranda.Core.Text;

namespace Oranda.Language.Navigation;

/// <summary>정의로 이동·참조 찾기용 식별자 판별. 소유: TSK-04-06. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ISymbolLocator))]
internal sealed class SymbolLocator : ISymbolLocator
{
    public SymbolReference? GetSymbolAt(string text, int offset)
        => throw new NotImplementedException("TSK-04-06");

    public IReadOnlyList<TextRange> FindReferences(string text, int offset)
        => throw new NotImplementedException("TSK-04-06");
}
