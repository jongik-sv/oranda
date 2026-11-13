using Oranda.Core.Execution;
using Oranda.Core.Rollback;

namespace Oranda.Testing.Rollback;

/// <summary>
/// DB 없는 <see cref="IRollbackChannel"/>. 보낸 SQL·바인드를 보낸 차례대로 기록하고, 미리 정한 규칙(먼저 더한 것이 먼저)으로 결과를 돌려준다.
/// 맞는 규칙이 없는 질의는 빈 결과다. <c>maxRows</c> 보다 많은 행을 준비하면 거기서 자르고 <see cref="RollbackRows.Truncated"/> 를 켠다.
/// </summary>
public sealed class FakeRollbackChannel : IRollbackChannel
{
    private readonly object _gate = new();
    private readonly List<Sent> _sent = [];
    private readonly List<Rule> _rules = [];

    /// <summary>보낸 것 하나. <see cref="IsQuery"/> = <see cref="QueryAsync"/>, 아니면 <see cref="ExecuteAsync"/>.</summary>
    public sealed record Sent(string Sql, IReadOnlyDictionary<string, BindValue> Binds, int MaxRows, bool IsQuery);

    private sealed record Rule(Func<string, bool> Match, Func<Sent, CancellationToken, Task<IReadOnlyList<object?[]>>> Respond, bool Once)
    {
        public bool Used { get; set; }
    }

    public IReadOnlyList<Sent> SentItems
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>보낸 SQL 글만(차례대로).</summary>
    public IReadOnlyList<string> SentSql => [.. SentItems.Select(s => s.Sql)];

    /// <summary>SQL 에 <paramref name="contains"/> 가 들어 있으면(대소문자 무시) <paramref name="rows"/> 를 돌려준다.</summary>
    public FakeRollbackChannel When(string contains, params object?[][] rows) => When(Contains(contains), rows);

    public FakeRollbackChannel When(Func<string, bool> match, params object?[][] rows) =>
        Add(match, (_, _) => Task.FromResult<IReadOnlyList<object?[]>>(rows), once: false);

    /// <summary>한 번만 쓰는 규칙(같은 질의가 두 번째에 다른 값을 내게 할 때).</summary>
    public FakeRollbackChannel WhenOnce(string contains, params object?[][] rows) =>
        Add(Contains(contains), (_, _) => Task.FromResult<IReadOnlyList<object?[]>>(rows), once: true);

    /// <summary>보낸 것을 보고 행을 만든다.</summary>
    public FakeRollbackChannel When(Func<string, bool> match, Func<Sent, IReadOnlyList<object?[]>> respond) =>
        Add(match, (s, _) => Task.FromResult(respond(s)), once: false);

    /// <summary>서버 오류: <see cref="RollbackChannelException"/> 을 던진다.</summary>
    public FakeRollbackChannel Fail(string contains, DbError error) => Fail(Contains(contains), new RollbackChannelException(error));

    public FakeRollbackChannel Fail(Func<string, bool> match, Exception exception) =>
        Add(match, (_, _) => Task.FromException<IReadOnlyList<object?[]>>(exception), once: false);

    /// <summary>취소될 때까지 기다린다(제한 시간 시험). 취소되면 <see cref="OperationCanceledException"/>.</summary>
    public FakeRollbackChannel Hang(string contains) =>
        Add(Contains(contains), async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return [];
        }, once: false);

    /// <summary>권한 없음(<see cref="DbErrorKind.InsufficientPrivileges"/>) 오류를 만든다.</summary>
    public static DbError PrivilegeError() =>
        new("ORA-01031", "ORA-01031: insufficient privileges") { Number = 1031, Kind = DbErrorKind.InsufficientPrivileges };

    public Task<RollbackRows> QueryAsync(string sql, IReadOnlyDictionary<string, BindValue> binds, int maxRows, CancellationToken ct) =>
        RunAsync(new Sent(sql, binds ?? new Dictionary<string, BindValue>(), maxRows, IsQuery: true), ct);

    public async Task ExecuteAsync(string sql, CancellationToken ct) =>
        await RunAsync(new Sent(sql, new Dictionary<string, BindValue>(), 0, IsQuery: false), ct).ConfigureAwait(false);

    private async Task<RollbackRows> RunAsync(Sent sent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Rule? rule;
        lock (_gate)
        {
            _sent.Add(sent);
            rule = _rules.FirstOrDefault(r => !(r.Once && r.Used) && r.Match(sent.Sql));
            if (rule is { Once: true })
            {
                rule.Used = true;
            }
        }

        if (rule is null)
        {
            return new RollbackRows([], false);
        }

        var rows = await rule.Respond(sent, ct).ConfigureAwait(false);
        if (sent.IsQuery && sent.MaxRows > 0 && rows.Count > sent.MaxRows)
        {
            return new RollbackRows([.. rows.Take(sent.MaxRows)], true);
        }

        return new RollbackRows(rows, false);
    }

    private FakeRollbackChannel Add(Func<string, bool> match, Func<Sent, CancellationToken, Task<IReadOnlyList<object?[]>>> respond, bool once)
    {
        lock (_gate)
        {
            _rules.Add(new Rule(match, respond, once));
        }

        return this;
    }

    private static Func<string, bool> Contains(string text) => sql => sql.Contains(text, StringComparison.OrdinalIgnoreCase);
}
