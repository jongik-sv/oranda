using Oranda.Core.Language;

namespace Oranda.Language;

/// <summary>L2 ANTLR 분석(바뀐 문장만 재파싱). 소유: TSK-04-03.</summary>
internal sealed partial class LanguageService
{
    public Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-03");
}
