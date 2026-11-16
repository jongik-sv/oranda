
namespace Oranda.App.Features.DataCopy;

/// <summary>전체 기본값 「규칙」: 자동 = 기간이 있고 기간 열이 있는 테이블은 기간, 아니면 모든 행.</summary>
public enum DefaultKindChoice
{
    Auto,
    All,
    Period,
    Exclude,
}
