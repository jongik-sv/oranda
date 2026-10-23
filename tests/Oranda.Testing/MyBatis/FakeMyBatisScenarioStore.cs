using Oranda.Core.MyBatis;

namespace Oranda.Testing.MyBatis;

/// <summary>
/// 메모리에 시나리오를 두는 가짜 저장소. ID 는 1부터 늘고, 같은 문장(경로 + 문장 id)의 목록은 <see cref="MyBatisScenario.SortOrder"/> 순이다.
/// 경로는 대소문자 무시로 비교한다. 비밀 이름 값 버림은 실제 저장소 몫이라 여기서는 하지 않는다.
/// </summary>
public sealed class FakeMyBatisScenarioStore : IMyBatisScenarioStore
{
    private readonly List<MyBatisScenario> _items = [];
    private long _nextId = 1;

    /// <summary>저장된 시나리오 전부(저장 순서).</summary>
    public IReadOnlyList<MyBatisScenario> All => _items;

    /// <summary>저장·이름·복제·지움·순서·옮김마다 난다(옮김은 옛 문장과 새 문장 각각). 가짜는 부른 스레드에서 바로 낸다.</summary>
    public event EventHandler<MyBatisScenarioChangedEventArgs>? Changed;

    public Task<IReadOnlyList<MyBatisScenario>> ListAsync(string mapperPath, string? ns, string statementId, CancellationToken ct)
    {
        var found = _items.Where(s => SamePath(s.MapperPath, mapperPath) && s.StatementId == statementId).ToList();
        if (found.Count == 0 && ns is not null)
        {
            found = _items.Where(s => s.Namespace == ns && s.StatementId == statementId).ToList();
        }

        IReadOnlyList<MyBatisScenario> result = found.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();
        return Task.FromResult(result);
    }

    public Task<MyBatisScenario> SaveAsync(MyBatisScenario scenario, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var now = DateTimeOffset.UtcNow;
        MyBatisScenario saved;
        if (scenario.Id == 0)
        {
            var order = Group(scenario.MapperPath, scenario.StatementId).Select(s => s.SortOrder + 1).DefaultIfEmpty(0).Max();
            saved = scenario with { Id = _nextId++, SortOrder = order, CreatedAt = now, UpdatedAt = now };
            _items.Add(saved);
        }
        else
        {
            var index = IndexOf(scenario.Id);
            var old = _items[index];
            saved = scenario with { SortOrder = old.SortOrder, CreatedAt = old.CreatedAt, UpdatedAt = now };
            _items[index] = saved;
        }

        Raise(saved);
        return Task.FromResult(saved);
    }

    public Task RenameAsync(long id, string name, CancellationToken ct)
    {
        var index = IndexOf(id);
        _items[index] = _items[index] with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
        Raise(_items[index]);
        return Task.CompletedTask;
    }

    public Task<MyBatisScenario> DuplicateAsync(long id, string newName, CancellationToken ct)
    {
        var origin = _items[IndexOf(id)];
        foreach (var later in Group(origin.MapperPath, origin.StatementId).Where(s => s.SortOrder > origin.SortOrder).ToList())
        {
            _items[IndexOf(later.Id)] = later with { SortOrder = later.SortOrder + 1 };
        }

        var now = DateTimeOffset.UtcNow;
        var copy = origin with { Id = _nextId++, Name = newName, SortOrder = origin.SortOrder + 1, CreatedAt = now, UpdatedAt = now };
        _items.Add(copy);
        Raise(copy);
        return Task.FromResult(copy);
    }

    public Task DeleteAsync(long id, CancellationToken ct)
    {
        var index = IndexOf(id);
        var removed = _items[index];
        _items.RemoveAt(index);
        Raise(removed);
        return Task.CompletedTask;
    }

    public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var index = IndexOf(orderedIds[i]);
            _items[index] = _items[index] with { SortOrder = i };
        }

        if (orderedIds.Count > 0)
        {
            Raise(_items[IndexOf(orderedIds[0])]);
        }

        return Task.CompletedTask;
    }

    public Task<int> MoveStatementAsync(string mapperPath, string oldId, string newId, CancellationToken ct)
    {
        var moved = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (SamePath(_items[i].MapperPath, mapperPath) && _items[i].StatementId == oldId)
            {
                _items[i] = _items[i] with { StatementId = newId, UpdatedAt = DateTimeOffset.UtcNow };
                moved++;
            }
        }

        if (moved > 0)
        {
            Changed?.Invoke(this, new MyBatisScenarioChangedEventArgs(mapperPath, oldId));
            Changed?.Invoke(this, new MyBatisScenarioChangedEventArgs(mapperPath, newId));
        }

        return Task.FromResult(moved);
    }

    private void Raise(MyBatisScenario scenario) =>
        Changed?.Invoke(this, new MyBatisScenarioChangedEventArgs(scenario.MapperPath, scenario.StatementId));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<MyBatisScenario> Group(string mapperPath, string statementId) =>
        _items.Where(s => SamePath(s.MapperPath, mapperPath) && s.StatementId == statementId);

    private int IndexOf(long id)
    {
        var index = _items.FindIndex(s => s.Id == id);
        return index >= 0 ? index : throw new KeyNotFoundException($"시나리오 {id} 이(가) 없습니다.");
    }
}
