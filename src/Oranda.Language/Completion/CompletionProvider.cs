using Oranda.Core.Composition;
using Oranda.Core.Language;
using Oranda.Core.Sessions;

namespace Oranda.Language.Completion;

/// <summary>자동완성 후보(문맥 + 메타데이터, 150 ms 넘으면 캐시 먼저). 소유: TSK-04-05. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ICompletionProvider))]
internal sealed class CompletionProvider : ICompletionProvider
{
    public Task<CompletionResult> GetCompletionsAsync(ISession? session, string text, int offset, CancellationToken ct)
        => throw new NotImplementedException("TSK-04-05");
}
