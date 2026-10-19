using Oranda.Core.Composition;
using Oranda.Core.Language;
using Oranda.Core.Text;

namespace Oranda.Language.Navigation;

/// <summary>정의로 이동·참조 찾기용 식별자 판별(TSK-04-06). L1 토큰 기반 — 판별 로직은 <see cref="SymbolAnalyzer"/>.</summary>
[OrandaService(typeof(ISymbolLocator))]
internal sealed class SymbolLocator : ISymbolLocator
{
    public SymbolReference? GetSymbolAt(string text, int offset) => SymbolAnalyzer.GetSymbolAt(text, offset);

    public IReadOnlyList<TextRange> FindReferences(string text, int offset) => SymbolAnalyzer.FindReferences(text, offset);
}
