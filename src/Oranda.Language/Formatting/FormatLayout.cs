using Oranda.Core.Formatting;

namespace Oranda.Language.Formatting;

/// <summary>
/// 배치 중인 조각 1개. 기본 엔진(<see cref="FormatEngine"/>)이 간격·들여쓰기를 정한 뒤 확장 규칙
/// (TSK-05-02 <c>LayoutRules</c>, <c>FormatEngine.ApplyLayoutRules</c> 부분 메서드)이 이 값을 고치고, 마지막에 렌더링한다.
/// 확장 규칙은 항목을 더하거나 빼지 않는다(토큰 동일성은 렌더링 뒤 다시 검사한다).
/// </summary>
internal sealed class FormatLayout(List<FormatItem> items, FormatterOptions options)
{
    public List<FormatItem> Items { get; } = items;

    public FormatterOptions Options { get; } = options;

    /// <summary>주석·원문 줄을 뺀 코드 항목의 <see cref="Items"/> 위치.</summary>
    public List<int> Code { get; } = BuildCode(items);

    static List<int> BuildCode(List<FormatItem> items)
    {
        var code = new List<int>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsCode) code.Add(i);
        }

        return code;
    }
}
