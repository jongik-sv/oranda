using Oranda.Core.Language;
using Oranda.Language.Completion;

namespace Oranda.Language;

/// <summary>자동완성 문맥(현재 절, 별칭 표). 소유: TSK-04-05.</summary>
internal sealed partial class LanguageService
{
    /// <summary>
    /// L1 토큰으로 문맥을 정한 뒤(<see cref="CompletionContextAnalyzer"/>), L2 가 있으면 <see cref="RefineCompletionContext"/> 로 다듬는다.
    /// DB 를 모른다. 1만 줄 문서에서도 커서가 든 문장까지만 렉싱한다.
    /// </summary>
    public CompletionContext GetCompletionContext(string text, int offset)
    {
        var context = CompletionContextAnalyzer.Analyze(text, offset);
        RefineCompletionContext(text, offset, ref context);
        return context;
    }

    /// <summary>
    /// L2(ANTLR, TSK-04-03) 자리. 분석 결과(부분 질의 범위·CTE 열·정확한 절)로 L1 문맥을 고칠 때 Analysis 쪽 partial 에서 구현한다.
    /// 구현이 없으면 호출이 빠진다(L1 결과 그대로).
    /// </summary>
    partial void RefineCompletionContext(string text, int offset, ref CompletionContext context);
}
