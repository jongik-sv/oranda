using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Expanded;

/// <summary>시나리오 콤보의 한 항목. 저장할 때마다 시나리오 record 가 새로 만들어지므로 ID 로 이어 붙인다.</summary>
public sealed class MyBatisScenarioItem : ObservableObject
{
    public MyBatisScenarioItem(MyBatisScenario scenario)
    {
        Scenario = scenario;
    }

    public long Id => Scenario.Id;

    public MyBatisScenario Scenario { get; private set; }

    public string Name => Scenario.Name;

    /// <summary>툴팁: 값이 든 파라미터 이름들(없으면 「값 없음」).</summary>
    public string Note
    {
        get
        {
            var names = Scenario.Values
                .Where(v => v.Value.Kind != MyBatisScenarioValueKind.Null && !(v.Value.Kind == MyBatisScenarioValueKind.List && (v.Value.Items?.Count ?? 0) == 0))
                .Select(v => v.Key)
                .ToList();
            return names.Count == 0 ? UiText.Get("MyBatis.scenario.noValues", "값 없음") : string.Join(", ", names);
        }
    }

    internal void Update(MyBatisScenario next)
    {
        var nameChanged = !string.Equals(Scenario.Name, next.Name, StringComparison.Ordinal);
        Scenario = next;
        if (nameChanged)
        {
            OnPropertyChanged(nameof(Name));
        }

        OnPropertyChanged(nameof(Note));
    }

    public override string ToString() => Name;
}
