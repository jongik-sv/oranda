using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Oranda.App.Shell.Settings;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Settings;

namespace Oranda.App.Shell.Features;

/// <summary>
/// 기능 게이트(TRD 10.4·10.5, TSK-07-02). 만들 때는 「설정 읽기 전」 = 모든 기능 켬이다. <see cref="Resolve"/> 를 처음 부른 값
/// (<c>ShellController.LoadAsync</c> 가 설정을 읽은 직후)이 「앱 시작 때 켬」이 되고, 그 뒤 <c>SettingsChanged("features")</c> 마다
/// 다시 해석한다. 해석 1~4단계는 <see cref="FeatureCatalog.Resolve"/>, 5단계(StartupBound 는 시작 때 값과 AND, 다르면
/// <see cref="PendingRestart"/>)는 여기서 한다. 결과가 바뀌면 <c>feature.&lt;id&gt;</c> 문맥 키를 고치고 <see cref="Changed"/> 를 낸다
/// (부른 스레드에서 — 설정 읽기는 스레드 풀이다). 꼬리표(TRD 10.2)는 (id, 형)마다 캐시한다. 여러 스레드에서 물어도 된다.
/// </summary>
public sealed class FeatureGate : IFeatureGate
{
    private readonly object _lock = new();
    private readonly SettingsService? _settings;
    private readonly ContextKeyService? _keys;
    private readonly ConcurrentDictionary<(string? Id, Type Type), string?> _tags = new();
    private State _state = State.AllOn;
    private IReadOnlySet<string>? _startup;
    private IReadOnlySet<string> _switchedOff = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 기능보다 작은 단위로 설정이 끄는 명령(TSK-13-44): (명령 id, 설정 섹션, 켜짐 조건). 꺼지면 기능이 꺼진 명령처럼 레지스트리에서 빠진다
    /// (메뉴·우클릭·팔레트·단축키가 함께 따른다). 설정이 없는 게이트(시험)에서는 늘 켬이다.
    /// </summary>
    internal static readonly IReadOnlyList<(string CommandId, string Section, Func<AppSettings, bool> IsOn)> SettingSwitches =
    [
        ("oranda.grid.flashback.history", "grid", s => s.Grid.FlashbackTrackEnabled),
    ];

    /// <summary>설정·문맥 키 없이(시험). <see cref="Resolve"/> 를 직접 부른다.</summary>
    public FeatureGate()
        : this(null, null, null)
    {
    }

    /// <param name="settings">있으면 <c>SettingsChanged("features")</c> 때 <see cref="SettingsService.Current"/> 를 다시 해석한다.</param>
    /// <param name="events">설정 바뀜 구독. null 이면 구독하지 않는다.</param>
    /// <param name="keys">있으면 <c>feature.&lt;id&gt;</c> 문맥 키를 맞춘다.</param>
    public FeatureGate(SettingsService? settings, IEventBus? events, ContextKeyService? keys)
    {
        _settings = settings;
        _keys = keys;
        PublishKeys(_state);
        if (settings is not null && events is not null)
        {
            events.Subscribe<SettingsChanged>(this, e =>
            {
                if (e.Section == FeaturesSettings.SectionName)
                {
                    Resolve(_settings!.Current.Features);
                }
                else if (SettingSwitches.Any(w => w.Section == e.Section))
                {
                    RefreshSwitches();
                }
            });
        }
    }

    /// <summary>설정이 끈 명령 id(<see cref="SettingSwitches"/>)를 다시 읽는다. 바뀌었으면 <see cref="Changed"/> 를 낸다.</summary>
    /// <returns>바뀌었으면 true.</returns>
    public bool RefreshSwitches()
    {
        lock (_lock)
        {
            if (!UpdateSwitchesLocked())
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // _lock 안에서 부른다. 설정이 없으면(시험) 늘 켬.
    private bool UpdateSwitchesLocked()
    {
        if (_settings is null)
        {
            return false;
        }

        var current = _settings.Current;
        var off = new HashSet<string>(SettingSwitches.Where(w => !w.IsOn(current)).Select(w => w.CommandId), StringComparer.Ordinal);
        if (off.SetEquals(_switchedOff))
        {
            return false;
        }

        Volatile.Write(ref _switchedOff, off);
        return true;
    }

    /// <summary>다시 해석해 결과가 바뀐 뒤(켜진 집합·조회 전용·재시작 대기 중 하나라도). 스레드 보장 없음.</summary>
    public event EventHandler? Changed;

    /// <summary>시작 때 값을 정했나(<see cref="Resolve"/> 를 한 번이라도 불렀나).</summary>
    public bool IsResolved
    {
        get
        {
            lock (_lock)
            {
                return _startup is not null;
            }
        }
    }

    /// <summary>지금 실제로 켜진 기능 id(사본, 카탈로그가 아는 id 만).</summary>
    public IReadOnlySet<string> EnabledFeatures => new HashSet<string>(Volatile.Read(ref _state).Enabled, StringComparer.Ordinal);

    public bool ReadOnlyRole => Volatile.Read(ref _state).ReadOnlyRole;

    public IReadOnlyCollection<string> PendingRestart => Volatile.Read(ref _state).Pending;

    /// <summary>
    /// 설정 섹션을 해석한다(TRD 10.5). 처음 부르면 그 1~4단계 결과가 「앱 시작 때 켬」이 된다. 결과가 앞과 다르면 문맥 키를 고치고
    /// <see cref="Changed"/> 를 낸다. 같으면 아무 일도 하지 않는다(features 없음 = 기본 Role SQL 개발자라 처음 해석은 사건을 낸다, D-013).
    /// </summary>
    /// <param name="settings">null 이면 섹션 없음 = 기본 Role(SQL 개발자, D-013).</param>
    /// <returns>결과가 바뀌어 사건을 냈으면 true.</returns>
    public bool Resolve(FeaturesSettings? settings)
    {
        settings ??= new FeaturesSettings();
        State next;
        lock (_lock)
        {
            var current = FeatureCatalog.Resolve(settings);
            _startup ??= current;
            var enabled = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<string>();
            foreach (var f in FeatureCatalog.Features)
            {
                var now = current.Contains(f.Id);
                if (!f.StartupBound)
                {
                    if (now)
                    {
                        enabled.Add(f.Id);
                    }

                    continue;
                }

                var atStart = _startup.Contains(f.Id);
                if (now && atStart)
                {
                    enabled.Add(f.Id);
                }

                if (now != atStart)
                {
                    pending.Add(f.Id);
                }
            }

            next = new State(enabled, FeatureCatalog.IsReadOnly(settings), pending.ToArray());
            var switched = UpdateSwitchesLocked(); // 설정을 처음 읽은 직후에도 설정이 끈 명령을 맞춘다
            if (next.SameAs(_state) && !switched)
            {
                return false;
            }

            Volatile.Write(ref _state, next);
        }

        PublishKeys(next);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool IsEnabled(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        return !FeatureCatalog.IsKnownFeature(featureId) || Volatile.Read(ref _state).Enabled.Contains(featureId);
    }

    public bool IsCommandEnabled(string commandId, Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        ArgumentNullException.ThrowIfNull(handlerType);
        return !Volatile.Read(ref _switchedOff).Contains(commandId) && IsTagEnabled(commandId, handlerType);
    }

    public bool IsPanelEnabled(string panelId, Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(panelId);
        ArgumentNullException.ThrowIfNull(viewModelType);
        return IsTagEnabled(panelId, viewModelType);
    }

    public bool IsTypeEnabled(Type registeredType)
    {
        ArgumentNullException.ThrowIfNull(registeredType);
        return IsTagEnabled(null, registeredType);
    }

    /// <summary>등록 항목이 드는 기능(TRD 10.2, 캐시). null = 공통.</summary>
    public string? FeatureOf(string? registrationId, Type registeredType)
    {
        ArgumentNullException.ThrowIfNull(registeredType);
        return _tags.GetOrAdd((registrationId, registeredType), k => FeatureCatalog.FeatureFor(k.Id, k.Type));
    }

    private bool IsTagEnabled(string? id, Type type) => FeatureOf(id, type) is not { } feature || IsEnabled(feature);

    private void PublishKeys(State state) =>
        _keys?.SetMany(FeatureIds.All.Select(id => (FeatureIds.ContextKey(id), state.Enabled.Contains(id))));

    /// <summary>해석 결과 한 벌(바꾸지 않는다 — 통째로 갈아 끼운다).</summary>
    private sealed record State(IReadOnlySet<string> Enabled, bool ReadOnlyRole, IReadOnlyCollection<string> Pending)
    {
        public static readonly State AllOn = new(new HashSet<string>(FeatureIds.All, StringComparer.Ordinal), false, []);

        public bool SameAs(State other) =>
            ReadOnlyRole == other.ReadOnlyRole
            && Enabled.SetEquals(other.Enabled)
            && Pending.Count == other.Pending.Count
            && Pending.All(other.Pending.Contains);
    }
}
