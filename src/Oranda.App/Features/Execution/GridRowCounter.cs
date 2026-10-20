using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Execution;

/// <summary>
/// 결과 그리드 「전체 행 수 세기」(<c>oranda.grid.countRows</c>, GRID-09)의 실행 쪽. 결과를 만든 질의를 <c>SELECT COUNT(*) FROM (…)</c> 로 감싸
/// 「바인드 변수」 목록의 지금 값으로 실행 엔진(치환 변수 → 정책 관문 → 드라이버)에 보낸다. 세는 동안 세션을 「실행 중」으로 두어
/// 다른 실행과 겹치지 않게 한다(<c>PlsqlRunViewModel</c> 과 같은 방식, 취소는 결과 영역의 단추가 토큰으로 한다).
/// 실패는 이유를 담은 예외로, 취소는 <see cref="OperationCanceledException"/> 으로 알린다 — 결과 영역이 상태 줄에 보인다.
/// </summary>
public sealed class GridRowCounter(ExecutionState state, BindVariableStore binds, Func<ISession?> session, Func<IExecutionService> execution)
{
    /// <summary>앱이 대신 실행하는 도우미 질의라 이력에 남기지 않는다(정책 관문은 그대로 거친다).</summary>
    private static readonly ExecutionOptions Options = new() { Source = HistorySource.Editor, SkipHistory = true };

    /// <summary>
    /// 세는 문장. 원래 본문을 인라인 뷰로 감싼다 — 끝에 <c>--</c> 주석이 있어도 닫는 괄호가 주석에 먹히지 않게 줄을 바꾼다.
    /// 바인드·치환 변수 이름은 원래 문장 것을 그대로 쓴다.
    /// </summary>
    public static SqlStatement BuildCountStatement(SqlStatement query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query with
        {
            Text = "SELECT COUNT(*) FROM (\n" + query.Text + "\n)",
            Range = default,
            Kind = StatementKind.Query,
            Verb = "SELECT",
        };
    }

    /// <summary>전체 행 수. 결과가 비어 읽을 수 없으면 null.</summary>
    public Task<long?> CountAsync(SqlStatement query, CancellationToken ct) => CountAsync(query, null, ct);

    /// <summary>
    /// 전체 행 수. <paramref name="extra"/> 는 「바인드 변수」 목록보다 먼저 쓰는 값(거른 결과의 셀 값 바인드, GRID-10).
    /// 결과가 비어 읽을 수 없으면 null.
    /// </summary>
    public async Task<long?> CountAsync(SqlStatement query, IReadOnlyDictionary<string, BindValue>? extra, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Kind != StatementKind.Query)
        {
            throw new InvalidOperationException("질의(SELECT) 결과만 셀 수 있습니다.");
        }

        if (session() is not { } s)
        {
            throw new InvalidOperationException("접속이 없습니다. 접속한 뒤 다시 세세요.");
        }

        if (state.IsBroken(s.Id))
        {
            throw new InvalidOperationException("세션이 끊겼습니다. 다시 접속한 뒤 세세요.");
        }

        if (state.IsBusy(s.Id))
        {
            throw new InvalidOperationException("다른 문장을 실행하는 중입니다. 끝난 뒤 다시 세세요.");
        }

        // 목록에 없는 이름은 더하지 않는다(세기는 목록·포커스를 건드리지 않는다). 원래 실행 뒤 값을 바꿨으면 지금 값으로 센다.
        var values = BuildBinds(binds, query.BindNames, extra);

        var stmt = BuildCountStatement(query);
        state.BeginRun(s.Id);
        try
        {
            var exec = execution();
            var result = await Task.Run(() => exec.ExecuteAsync(s, stmt, values, Options, ct), CancellationToken.None).ConfigureAwait(true);
            return await ReadCountAsync(result, ct).ConfigureAwait(true);
        }
        finally
        {
            state.EndRun(s.Id);
        }
    }

    /// <summary>
    /// 문장에 나온 바인드 이름마다 값을 만든다: <paramref name="extra"/> 에 있으면 그 값, 없으면 「바인드 변수」 목록의 지금 값.
    /// 문장에 없는 이름은 넘기지 않는다(드라이버가 남는 매개변수를 오류로 본다). 목록에 없거나 형식이 틀리면 이유를 담은 예외.
    /// </summary>
    internal static Dictionary<string, BindValue> BuildBinds(BindVariableStore store, IReadOnlyList<string> names,
                                                             IReadOnlyDictionary<string, BindValue>? extra)
    {
        var fromStore = extra is null ? names : [.. names.Where(n => !extra.ContainsKey(n))];
        if (!store.TryBuild(fromStore, out var values, out var error))
        {
            throw new InvalidOperationException(error ?? "바인드 값을 읽을 수 없습니다.");
        }

        if (extra is not null)
        {
            foreach (var name in names)
            {
                if (extra.TryGetValue(name, out var value))
                {
                    values[name] = value;
                }
            }
        }

        return values;
    }

    private static async Task<long?> ReadCountAsync(ExecutionResult result, CancellationToken ct)
    {
        var rs = result.ResultSet;
        try
        {
            switch (result.Status)
            {
                case ExecutionStatus.Cancelled:
                    throw new OperationCanceledException(ct);
                case ExecutionStatus.Denied:
                    throw new InvalidOperationException(result.Policy?.Message ?? "정책 관문이 막았습니다.");
                case ExecutionStatus.Failed:
                    throw new InvalidOperationException(DbErrorText.Format(result.Error, "실행하지 못했습니다."));
            }

            if (rs is null)
            {
                return null;
            }

            var page = await Task.Run(() => rs.GetPageAsync(0, ct), CancellationToken.None).ConfigureAwait(true);
            return page.RowCount > 0 && page.Columns.Count > 0 ? ToCount(page.Columns[0][0]) : null;
        }
        finally
        {
            if (rs is not null)
            {
                await rs.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>COUNT(*) 값: NUMBER 는 decimal 이거나 드라이버 원시 문자열이다.</summary>
    private static long? ToCount(object? value) => value switch
    {
        null or DBNull => null,
        decimal d => (long)d,
        string text when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (long)d,
        string => null,
        IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
        _ => null,
    };
}
