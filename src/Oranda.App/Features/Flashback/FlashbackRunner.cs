using System.Globalization;
using Oranda.App.Features.Execution;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// 플래시백 질의의 실행 쪽(TSK-13-05). 앱이 대신 실행하는 도우미 질의라 이력에 남기지 않고(정책 관문은 그대로 거친다),
/// 실행하는 동안 세션을 「실행 중」으로 둔다. 실패는 이유를 담은 예외(ORA 번호가 있으면 <see cref="FlashbackErrorAdvisor"/> 한 줄을 덧붙인다).
/// </summary>
internal sealed class FlashbackRunner(ExecutionState state, Func<IExecutionService> execution)
{
    private static readonly ExecutionOptions Options = new() { Source = HistorySource.Editor, SkipHistory = true };

    /// <summary>질의를 실행해 결과 집합을 돌려준다(수명은 받은 쪽).</summary>
    public async Task<IResultSet> QueryAsync(ISession s, FlashbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(query);
        return await BusyAsync(s, async () =>
        {
            try
            {
                var exec = execution();
                var result = await Task.Run(() => exec.ExecuteAsync(s, query.Statement, query.Binds, Options, ct), CancellationToken.None).ConfigureAwait(true);
                return await GridFilterRunner.TakeResultAsync(result, ct, "플래시백 질의가 결과를 돌려주지 않았습니다.").ConfigureAwait(true);
            }
            catch (InvalidOperationException e) when (FlashbackErrorAdvisor.Explain(e.Message) is { } why)
            {
                throw new InvalidOperationException(why + " (" + e.Message.ReplaceLineEndings(" ") + ")", e);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>결과 집합의 모든 행을 읽고 닫는다(앞으로만 가는 커서라 한 번만 읽을 수 있다).</summary>
    public static async Task<List<object?[]>> ReadAllAsync(IResultSet rs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rs);
        await using (rs.ConfigureAwait(true))
        {
            var rows = new List<object?[]>();
            for (var p = 0; ; p++)
            {
                var page = await rs.GetPageAsync(p, ct).ConfigureAwait(true);
                for (var r = 0; r < page.RowCount; r++)
                {
                    var row = new object?[rs.Columns.Count];
                    for (var c = 0; c < row.Length; c++)
                    {
                        row[c] = page.Columns[c][r];
                    }

                    rows.Add(row);
                }

                if (page.IsLast || page.RowCount == 0)
                {
                    return rows;
                }
            }
        }
    }

    /// <summary>환경 안내 한 줄: undo_retention. 못 읽으면(권한 ORA-00942 · 이 버전 카탈로그 없음) 「알 수 없음」.</summary>
    public async Task<string> EnvironmentAsync(ISession s, ISqlCatalog catalog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(catalog);
        var value = await FlashbackEnvironment.ReadUndoRetentionAsync(catalog, s.Server, async text =>
        {
            var stmt = new SqlStatement(text, default, StatementKind.Query) { Verb = "SELECT" };
            var rs = await QueryAsync(s, new FlashbackQuery(stmt, new Dictionary<string, BindValue>()), ct).ConfigureAwait(true);
            var rows = await ReadAllAsync(rs, ct).ConfigureAwait(true);
            return rows.Count > 0 ? rows[0][0] : null;
        }).ConfigureAwait(true);
        return FlashbackEnvironment.Describe(value);
    }

    /// <summary>표 정의가 마지막으로 바뀐 시각(<c>all_objects.last_ddl_time</c>). 못 읽으면 null.</summary>
    public async Task<DateTime?> LastDdlAsync(ISession s, ISqlCatalog catalog, Oranda.Core.Metadata.DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(table);
        try
        {
            var text = catalog.Get("dbinfo.flashback_ddl", s.Server).Sql;
            var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["owner"] = new BindValue(BindDataType.Varchar2, table.Owner),
                ["name"] = new BindValue(BindDataType.Varchar2, table.Name),
            };
            var stmt = new SqlStatement(text, default, StatementKind.Query) { Verb = "SELECT", BindNames = ["owner", "name"] };
            var rows = await ReadAllAsync(await QueryAsync(s, new FlashbackQuery(stmt, binds), ct).ConfigureAwait(true), ct).ConfigureAwait(true);
            return rows.Count > 0 ? ToDateTime(rows[0][0]) : null;
        }
        catch (Exception e) when (e is CatalogQueryNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    internal static DateTime? ToDateTime(object? value) => value switch
    {
        DateTime d => d,
        DateTimeOffset o => o.DateTime,
        string t when DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
        _ => null,
    };

    private async Task<T> BusyAsync<T>(ISession s, Func<Task<T>> body)
    {
        if (state.IsBroken(s.Id))
        {
            throw new InvalidOperationException("세션이 끊겼습니다. 다시 접속한 뒤 하세요.");
        }

        if (state.IsBusy(s.Id))
        {
            throw new InvalidOperationException("다른 문장을 실행하는 중입니다. 끝난 뒤 다시 하세요.");
        }

        state.BeginRun(s.Id);
        try
        {
            return await body().ConfigureAwait(true);
        }
        finally
        {
            state.EndRun(s.Id);
        }
    }
}

/// <summary>undo 보존 시간 같은 서버 환경 안내(TSK-13-05). 읽기 실패는 앱을 멈추지 않고 「알 수 없음」.</summary>
internal static class FlashbackEnvironment
{
    public const string CatalogId = "dbinfo.flashback_env";

    /// <summary>undo_retention(초)을 읽는다. 카탈로그에 이 서버 버전용이 없거나 읽을 권한이 없으면 null.</summary>
    public static async Task<object?> ReadUndoRetentionAsync(ISqlCatalog catalog, ServerInfo server, Func<string, Task<object?>> run)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            return await run(catalog.Get(CatalogId, server).Sql).ConfigureAwait(true);
        }
        catch (Exception e) when (e is CatalogQueryNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static string Describe(object? seconds)
    {
        if (seconds is null or DBNull || !decimal.TryParse(Convert.ToString(seconds, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) || s < 0)
        {
            return "undo_retention: 알 수 없음(v$parameter 를 읽을 권한이 없을 수 있음)";
        }

        var minutes = decimal.Round(s / 60m, 1);
        return $"undo_retention: {s.ToString("0", CultureInfo.InvariantCulture)}초(약 {minutes.ToString("0.#", CultureInfo.InvariantCulture)}분). 이보다 오래된 값은 읽지 못할 수 있음";
    }
}
