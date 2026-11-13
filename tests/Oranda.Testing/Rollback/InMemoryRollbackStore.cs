using Oranda.Core.Rollback;

namespace Oranda.Testing.Rollback;

/// <summary>
/// 메모리 <see cref="IRollbackStore"/>. Id 를 1부터 붙이고, 검색은 최근 순(<see cref="RollbackGroup.StartedAt"/> 내림차순)으로 DbKey·표·글로 거른 뒤 Limit 개까지.
/// 문서는 직렬화하지 않고 그대로 보관한다. <see cref="RollbackScriptState.Open"/> 묶음은 <see cref="MarkOrphansAsync"/> 가 「확인 필요」로 바꾼다.
/// </summary>
public sealed class InMemoryRollbackStore : IRollbackStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, RollbackGroup> _groups = [];
    private readonly Dictionary<long, RollbackScriptDocument> _documents = [];
    private long _nextId = 1;

    public bool CanStoreScripts { get; set; } = true;

    /// <summary>참이면 <see cref="SaveAsync"/> 가 <see cref="IOException"/> 을 던진다(저장 실패 시험).</summary>
    public bool FailSaves { get; set; }

    /// <summary>참이면 <see cref="PruneAsync"/> 가 <see cref="IOException"/> 을 던진다(정리 실패 시험).</summary>
    public bool FailPrunes { get; set; }

    public int SaveCount { get; private set; }

    public IReadOnlyList<RollbackGroup> Groups
    {
        get
        {
            lock (_gate)
            {
                return [.. _groups.Values.OrderBy(g => g.Id)];
            }
        }
    }

    public RollbackScriptDocument? Document(long id)
    {
        lock (_gate)
        {
            return _documents.GetValueOrDefault(id);
        }
    }

    public Task<long> SaveAsync(RollbackGroup group, RollbackScriptDocument? document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        ct.ThrowIfCancellationRequested();
        if (FailSaves)
        {
            throw new IOException("저장 실패(시험)");
        }

        lock (_gate)
        {
            SaveCount++;
            var id = group.Id == 0 ? _nextId++ : group.Id;
            _nextId = Math.Max(_nextId, id + 1);
            _groups[id] = group with { Id = id, HasScriptFile = document is not null || (group.Id != 0 && _documents.ContainsKey(id)) };
            if (document is not null)
            {
                _documents[id] = document with { GroupId = id };
            }

            return Task.FromResult(id);
        }
    }

    public Task<IReadOnlyList<RollbackGroup>> SearchAsync(RollbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            IEnumerable<RollbackGroup> q = _groups.Values;
            if (query.DbKey is { } key)
            {
                q = q.Where(g => string.Equals(g.DbKey, key, StringComparison.Ordinal));
            }

            if (query.Table is { } table)
            {
                q = q.Where(g => g.Tables.Any(t => string.Equals(t.Owner, table.Owner, StringComparison.Ordinal)
                                                   && string.Equals(t.Name, table.Name, StringComparison.Ordinal)));
            }

            if (!string.IsNullOrEmpty(query.Text) && query.Text is { } text)
            {
                q = q.Where(g => g.ProfileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                                 || g.Tables.Any(t => (t.Owner + "." + t.Name).Contains(text, StringComparison.OrdinalIgnoreCase))
                                 || g.Statements.Any(s => s.Summary.Contains(text, StringComparison.OrdinalIgnoreCase)));
            }

            IReadOnlyList<RollbackGroup> result = [.. q.OrderByDescending(g => g.StartedAt).ThenByDescending(g => g.Id).Take(query.Limit)];
            return Task.FromResult(result);
        }
    }

    public Task<RollbackGroup?> GetAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_groups.GetValueOrDefault(id));
        }
    }

    public Task<RollbackScriptDocument?> LoadDocumentAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_documents.GetValueOrDefault(id));
        }
    }

    public Task<int> MarkOrphansAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var open = _groups.Values.Where(g => g.State == RollbackScriptState.Open).ToList();
            foreach (var g in open)
            {
                _groups[g.Id] = g with
                {
                    State = RollbackScriptState.Unverified,
                    EndKind = TransactionEndKind.Unknown,
                    EndSource = TransactionEndSource.AppRestart,
                    StateReason = "앱이 끝나 결과를 모름",
                };
            }

            return Task.FromResult(open.Count);
        }
    }

    public Task PruneAsync(int retentionDays, CancellationToken ct)
    {
        if (FailPrunes)
        {
            throw new IOException("정리 실패(시험)");
        }

        lock (_gate)
        {
            var limit = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            foreach (var g in _groups.Values.Where(g => (g.EndedAt ?? g.StartedAt) < limit).ToList())
            {
                _groups.Remove(g.Id);
                _documents.Remove(g.Id);
            }
        }

        return Task.CompletedTask;
    }
}
