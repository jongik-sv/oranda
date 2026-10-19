using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// <see cref="ICommandRegistry"/> 구현 틀. 처리기는 처음 실행할 때 <c>handlerFactory</c> 로 만든다(시작 시간, D-006).
/// 문맥 키 계산·메뉴 트리 만들기는 셸(TSK-03-01) 몫.
/// </summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Func<Type, object> _handlerFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private IReadOnlyList<CommandDefinition> _snapshot = [];

    public CommandRegistry(IEnumerable<CommandRegistration> registrations, Func<Type, object>? handlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _handlerFactory = handlerFactory ?? (t => Activator.CreateInstance(t)!);
        foreach (var r in registrations)
        {
            Add(r.Definition, new Entry(r.Definition, r.HandlerType, null));
        }

        Rebuild();
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

    private Entry? Lookup(string idOrAlias)
    {
        ArgumentNullException.ThrowIfNull(idOrAlias);
        lock (_gate)
        {
            if (_byId.TryGetValue(idOrAlias, out var e))
            {
                return e;
            }

            return _aliases.TryGetValue(idOrAlias, out var id) ? _byId[id] : null;
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

    private void Rebuild() =>
        _snapshot = _byId.Values.Select(e => e.Definition).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();

    private sealed record Entry(CommandDefinition Definition, Type? HandlerType, ICommandHandler? Handler);
}
