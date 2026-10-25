using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.Core.Features;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// <see cref="IToolPanelRegistry"/> 구현 틀. 도킹 배치·View 생성은 셸(TSK-03-01) 몫.
/// 기능 게이트(TRD 10.6, TSK-07-02): 전체 항목을 들고 게이트로 거른 스냅숏을 낸다(<see cref="Panels"/>·<see cref="Find"/>).
/// 게이트가 다시 해석해 보이는 집합이 바뀌면 <see cref="PanelsChanged"/>(게이트 스레드에서). 개발 빌드 전용 거르기는 만들 때 한 번.
/// </summary>
public sealed class ToolPanelRegistry : IToolPanelRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ToolPanelDefinition> _byId = new(StringComparer.Ordinal);
    private readonly IFeatureGate? _features;
    private IReadOnlyList<ToolPanelDefinition> _snapshot = [];
    private Dictionary<string, ToolPanelDefinition> _enabled = new(StringComparer.Ordinal);

    /// <param name="definitions">스캔 결과.</param>
    /// <param name="includeDevBuildOnly">개발 빌드이면 true(<see cref="ToolPanelDefinition.DevBuildOnly"/> 도 넣음).</param>
    /// <param name="features">기능 게이트. null 이면 거르지 않는다.</param>
    public ToolPanelRegistry(IEnumerable<ToolPanelDefinition> definitions, bool includeDevBuildOnly, IFeatureGate? features = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _features = features;
        foreach (var d in definitions.Where(d => includeDevBuildOnly || !d.DevBuildOnly))
        {
            Add(d);
        }

        Rebuild();
        if (features is not null)
        {
            features.Changed += (_, _) => OnFeaturesChanged();
        }
    }

    public event EventHandler? PanelsChanged;

    public IReadOnlyList<ToolPanelDefinition> Panels => _snapshot;

    public ToolPanelDefinition? Find(string id)
    {
        lock (_gate)
        {
            return _enabled.GetValueOrDefault(id);
        }
    }

    public IDisposable Register(ToolPanelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            Add(definition);
            Rebuild();
        }

        PanelsChanged?.Invoke(this, EventArgs.Empty);
        return new Unregister(() =>
        {
            lock (_gate)
            {
                _byId.Remove(definition.Id);
                Rebuild();
            }

            PanelsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Add(ToolPanelDefinition d)
    {
        if (!_byId.TryAdd(d.Id, d))
        {
            throw new InvalidOperationException(
                $"도구 창 ID '{d.Id}' 가 겹친다: {_byId[d.Id].ViewModelType.FullName}, {d.ViewModelType.FullName}");
        }
    }

    // 게이트가 다시 해석했다: 보이는 집합이 바뀌었을 때만 알린다.
    private void OnFeaturesChanged()
    {
        bool changed;
        lock (_gate)
        {
            var before = _snapshot;
            Rebuild();
            changed = !before.SequenceEqual(_snapshot);
        }

        if (changed)
        {
            PanelsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Rebuild()
    {
        _enabled = _byId.Values
            .Where(d => _features is null || _features.IsPanelEnabled(d.Id, d.ViewModelType))
            .ToDictionary(d => d.Id, StringComparer.Ordinal);
        _snapshot = _enabled.Values
            .OrderBy(d => d.DefaultLocation)
            .ThenBy(d => d.Order)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary><see cref="IStatusBarRegistry"/> 구현 틀.</summary>
public sealed class StatusBarRegistry : IStatusBarRegistry
{
    public StatusBarRegistry(IEnumerable<StatusBarItemDefinition> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
        var dup = list.GroupBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            throw new InvalidOperationException($"상태 표시줄 구간 ID '{dup.Key}' 가 겹친다.");
        }

        Items = list;
    }

    public IReadOnlyList<StatusBarItemDefinition> Items { get; }
}

internal sealed class Unregister(Action action) : IDisposable
{
    private Action? _action = action;

    public void Dispose() => System.Threading.Interlocked.Exchange(ref _action, null)?.Invoke();
}
