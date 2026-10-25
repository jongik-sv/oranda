using Oranda.Core.Features;
using Oranda.Core.Settings;

namespace Oranda.Testing;

/// <summary>
/// 시험용 <see cref="IFeatureGate"/>(TSK-07-01 공용 헬퍼). 켜진 기능 집합·<see cref="ReadOnlyRole"/>·<see cref="PendingRestart"/> 를 시험이 정하고
/// <see cref="RaiseChanged"/> 로 <see cref="Changed"/> 를 낸다. 명령·패널·형 질문은 실제 꼬리표 규칙
/// (<see cref="FeatureCatalog.FeatureFor(string?, Type)"/>)으로 기능을 찾아 답하므로, 예컨대 myBatis 를 끄면
/// <c>Oranda.App.Features.MyBatisWorkspace</c> 의 시작 작업·상태 칸·명령이 모두 꺼진 것으로 나온다. 기본은 모두 켬.
/// 여러 스레드에서 물어도 된다.
/// </summary>
public sealed class FakeFeatureGate : IFeatureGate
{
    private readonly object _gate = new();
    private readonly List<Type> _typeQueries = [];
    private HashSet<string> _enabled;
    private IReadOnlyCollection<string> _pendingRestart = [];
    private bool _readOnlyRole;

    /// <summary>모든 기능 켬.</summary>
    public FakeFeatureGate()
        : this(FeatureIds.All)
    {
    }

    /// <summary>주어진 기능만 켬(모르는 id 도 그대로 담는다).</summary>
    public FakeFeatureGate(IEnumerable<string> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = new HashSet<string>(enabled, StringComparer.Ordinal);
    }

    public event EventHandler? Changed;

    /// <summary>설정 섹션을 카탈로그 해석(1~4단계)으로 옮긴 게이트. <see cref="ReadOnlyRole"/> 도 맞춘다.</summary>
    public static FakeFeatureGate For(FeaturesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new FakeFeatureGate(FeatureCatalog.Resolve(settings)) { ReadOnlyRole = FeatureCatalog.IsReadOnlyRole(settings.Roles) };
    }

    /// <summary>Role 만 고른 설정(<c>roles</c> = 인자, 덮어쓰기 없음)의 게이트.</summary>
    public static FakeFeatureGate ForRoles(params string[] roles) => For(new FeaturesSettings { Roles = roles });

    /// <summary>지금 켜진 기능 id(사본).</summary>
    public IReadOnlySet<string> EnabledFeatures
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_enabled, StringComparer.Ordinal);
            }
        }
    }

    /// <summary><see cref="IsTypeEnabled"/> 로 물어 온 형(순서대로). 시작 작업·상태 칸을 거르는 자리를 시험할 때 쓴다.</summary>
    public IReadOnlyList<Type> TypeQueries
    {
        get
        {
            lock (_gate)
            {
                return [.. _typeQueries];
            }
        }
    }

    public bool ReadOnlyRole
    {
        get
        {
            lock (_gate)
            {
                return _readOnlyRole;
            }
        }

        set
        {
            lock (_gate)
            {
                _readOnlyRole = value;
            }
        }
    }

    public IReadOnlyCollection<string> PendingRestart
    {
        get
        {
            lock (_gate)
            {
                return _pendingRestart;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _pendingRestart = [.. value];
            }
        }
    }

    /// <summary>기능을 켠다(사건은 내지 않는다 — 필요하면 <see cref="RaiseChanged"/>).</summary>
    public FakeFeatureGate Enable(params string[] featureIds)
    {
        lock (_gate)
        {
            _enabled.UnionWith(featureIds);
        }

        return this;
    }

    /// <summary>기능을 끈다(사건은 내지 않는다).</summary>
    public FakeFeatureGate Disable(params string[] featureIds)
    {
        lock (_gate)
        {
            _enabled.ExceptWith(featureIds);
        }

        return this;
    }

    /// <summary>켜진 집합을 통째로 바꾸고 <paramref name="raiseChanged"/> 면 <see cref="Changed"/> 를 낸다.</summary>
    public void SetEnabled(IEnumerable<string> featureIds, bool raiseChanged = true)
    {
        ArgumentNullException.ThrowIfNull(featureIds);
        lock (_gate)
        {
            _enabled = new HashSet<string>(featureIds, StringComparer.Ordinal);
        }

        if (raiseChanged)
        {
            RaiseChanged();
        }
    }

    /// <summary>실제 게이트가 다시 해석한 뒤처럼 <see cref="Changed"/> 를 부른 스레드에서 낸다.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>모르는 id 는 켬(공통 취급), 아는 id 는 켜진 집합에 있으면 켬.</summary>
    public bool IsEnabled(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        if (!FeatureCatalog.IsKnownFeature(featureId))
        {
            return true;
        }

        lock (_gate)
        {
            return _enabled.Contains(featureId);
        }
    }

    public bool IsCommandEnabled(string commandId, Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        return IsTagged(commandId, handlerType);
    }

    public bool IsPanelEnabled(string panelId, Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(panelId);
        return IsTagged(panelId, viewModelType);
    }

    public bool IsTypeEnabled(Type registeredType)
    {
        ArgumentNullException.ThrowIfNull(registeredType);
        lock (_gate)
        {
            _typeQueries.Add(registeredType);
        }

        return IsTagged(null, registeredType);
    }

    private bool IsTagged(string? id, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return FeatureCatalog.FeatureFor(id, type) is not { } feature || IsEnabled(feature);
    }
}
