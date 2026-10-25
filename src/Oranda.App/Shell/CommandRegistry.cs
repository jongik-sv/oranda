using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Features;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// <see cref="ICommandRegistry"/> 구현 틀. 처리기는 처음 실행할 때 <c>handlerFactory</c> 로 만든다(시작 시간, D-006).
/// 문맥 키 계산·메뉴 트리 만들기는 셸(TSK-03-01) 몫.
/// 기능 게이트(TRD 10.6·10.7.1, TSK-07-02): 전체 항목을 들고 게이트로 거른 스냅숏을 낸다. <see cref="Commands"/>·<see cref="Find"/>·
/// <see cref="CanExecute"/>·<see cref="ExecuteAsync"/> 모두 거른 결과라 메뉴·툴바·우클릭·팔레트·단축키가 한꺼번에 따라온다.
/// 게이트 <see cref="IFeatureGate.Changed"/> 에 다시 계산하고 보이는 집합이 바뀌었을 때만 <see cref="CommandsChanged"/> 를 낸다
/// (게이트 스레드에서 — 구독자가 UI 로 넘긴다). 꺼진 명령도 id·별칭은 계속 차지한다(다시 켜면 그대로).
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Func<Type, object> _handlerFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly IFeatureGate? _features;
    private IReadOnlyList<CommandDefinition> _snapshot = [];

    /// <param name="registrations">스캔 결과(거르기 전 전체).</param>
    /// <param name="handlerFactory">처리기 생성기. null 이면 인자 없는 생성자.</param>
    /// <param name="features">기능 게이트. null 이면 거르지 않는다.</param>
    public CommandRegistry(IEnumerable<CommandRegistration> registrations, Func<Type, object>? handlerFactory = null, IFeatureGate? features = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _handlerFactory = handlerFactory ?? (t => Activator.CreateInstance(t)!);
        _features = features;
        foreach (var r in registrations)
        {
            Add(r.Definition, new Entry(r.Definition, r.HandlerType, null));
        }

        Rebuild();
        if (features is not null)
        {
            features.Changed += (_, _) => OnFeaturesChanged();
        }
    }

    public event EventHandler? CommandsChanged;

    public IReadOnlyList<CommandDefinition> Commands => _snapshot;

    public CommandDefinition? Find(string idOrAlias) => Lookup(idOrAlias)?.Definition;

    public bool CanExecute(string idOrAlias, IContextKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return Lookup(idOrAlias) is { } e && keys.Evaluate(e.Definition.Enablement);
    }

    public async Task<bool> ExecuteAsync(string idOrAlias, IContextKeys keys, object? parameter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (Lookup(idOrAlias) is not { } e || !keys.Evaluate(e.Definition.Enablement))
        {
            return false;
        }

        var handler = e.Handler ?? (ICommandHandler)_handlerFactory(e.HandlerType!);
        await handler.ExecuteAsync(new CommandContext(e.Definition.Id, keys, parameter), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public IDisposable Register(CommandDefinition definition, ICommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            Add(definition, new Entry(definition, null, handler));
            Rebuild();
        }

        CommandsChanged?.Invoke(this, EventArgs.Empty);
        return new Unregister(() =>
        {
            lock (_gate)
            {
                _byId.Remove(definition.Id);
                foreach (var alias in definition.Aliases)
                {
                    _aliases.Remove(alias);
                }

                Rebuild();
            }

            CommandsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>등록돼 있으나 기능 게이트가 끈 명령인가(id·별칭). 미등록이면 false. 키맵이 꺼진 명령의 키를 흘려보낼 때 쓴다.</summary>
    public bool IsFeatureDisabled(string idOrAlias)
    {
        ArgumentNullException.ThrowIfNull(idOrAlias);
        lock (_gate)
        {
            return FindEntry(idOrAlias) is { Enabled: false };
        }
    }

    private Entry? Lookup(string idOrAlias)
    {
        ArgumentNullException.ThrowIfNull(idOrAlias);
        lock (_gate)
        {
            return FindEntry(idOrAlias) is { Enabled: true } e ? e : null;
        }
    }

    // 거르기 전 전체에서 id·별칭으로 찾는다. _gate 안에서 부른다.
    private Entry? FindEntry(string idOrAlias) =>
        _byId.TryGetValue(idOrAlias, out var e) ? e
        : _aliases.TryGetValue(idOrAlias, out var id) ? _byId[id]
        : null;

    // 게이트가 다시 해석했다: 켬 여부를 다시 계산하고 보이는 집합이 바뀌었을 때만 알린다.
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
            CommandsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Add(CommandDefinition definition, Entry entry)
    {
        if (_byId.ContainsKey(definition.Id) || _aliases.ContainsKey(definition.Id))
        {
            throw new InvalidOperationException($"명령 ID '{definition.Id}' 가 이미 등록되어 있다.");
        }

        foreach (var alias in definition.Aliases)
        {
            if (_byId.ContainsKey(alias) || _aliases.ContainsKey(alias))
            {
                throw new InvalidOperationException($"명령 별칭 '{alias}' 가 이미 쓰이고 있다.");
            }
        }

        _byId.Add(definition.Id, entry);
        foreach (var alias in definition.Aliases)
        {
            _aliases.Add(alias, definition.Id);
        }
    }

    private void Rebuild()
    {
        foreach (var e in _byId.Values)
        {
            e.Enabled = IsEnabled(e);
        }

        _snapshot = _byId.Values.Where(e => e.Enabled).Select(e => e.Definition).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
    }

    // 실행 중 Register 한 처리기는 그 인스턴스 형으로 꼬리표를 찾는다(TRD 10.7.1: 같은 규칙).
    private bool IsEnabled(Entry e) =>
        _features is null || _features.IsCommandEnabled(e.Definition.Id, e.HandlerType ?? e.Handler!.GetType());

    private sealed record Entry(CommandDefinition Definition, Type? HandlerType, ICommandHandler? Handler)
    {
        /// <summary>게이트로 켜졌나(<see cref="Rebuild"/> 가 _gate 안에서 고친다).</summary>
        public bool Enabled { get; set; } = true;
    }
}
