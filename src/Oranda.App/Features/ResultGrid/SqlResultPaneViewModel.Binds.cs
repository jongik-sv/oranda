using Oranda.Core.Execution;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 이 결과를 만든 실행의 바인드 값(서버 재조회가 같은 값을 쓰게).
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>
    /// 이 결과를 만들 때 쓴 바인드 값(문장에 나온 이름만). 「바인드 변수」 목록에서 지우거나 값을 바꿔도, 목록에 없는 MyBatis 실행 값도
    /// 서버에서 다시 읽는 일(내보내기·행 수 세기·거르기)이 이 값을 쓴다. 바인드가 없거나 모르면 null.
    /// </summary>
    public IReadOnlyDictionary<string, BindValue>? RunBinds { get; init; }

    /// <summary>
    /// 서버에서 이 결과의 문장을 다시 읽을 때 쓸 바인드 값: 실행 때 값에 셀 값 바인드·따라가기로 받은 값을 얹는다(같은 이름은 뒤쪽이 이긴다).
    /// 문장에 없는 이름도 들어 있을 수 있으니 쓰는 쪽이 문장의 <c>BindNames</c> 로 거른다.
    /// </summary>
    public IReadOnlyDictionary<string, BindValue> RerunBinds()
    {
        var all = new Dictionary<string, BindValue>(RunBinds ?? new Dictionary<string, BindValue>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in _filterBinds)
        {
            all[name] = value;
        }

        return all;
    }
}
