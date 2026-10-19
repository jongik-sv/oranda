using Oranda.Core.Language;

namespace Oranda.Language;

/// <summary>자동완성 문맥(현재 절, 별칭 표). 소유: TSK-04-05.</summary>
internal sealed partial class LanguageService
{
    public CompletionContext GetCompletionContext(string text, int offset)
        => throw new NotImplementedException("TSK-04-05");
}
