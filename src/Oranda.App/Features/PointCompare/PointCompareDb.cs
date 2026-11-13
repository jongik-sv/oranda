using System.Globalization;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Flashback;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Features.PointCompare;

/// <summary>질의·반영이 서버에서 실패했을 때. <see cref="Error"/> 는 분류한 오류(없으면 null).</summary>
internal sealed class PointCompareDbException(string message, DbError? error) : Exception(message)
{
    public DbError? Error { get; } = error;
}

/// <summary>화면 3 이 DB 에 하는 일(시험에서 가짜로 바꾼다). 모든 메서드는 실패하면 <see cref="PointCompareDbException"/>.</summary>
internal interface IPointCompareDb
{
    Dbms Dbms { get; }

    IRollbackDialect Dialect { get; }

    /// <summary>표 열·PK(시노님을 푼 실제 표). 뷰면 null.</summary>
    Task<RollbackTable?> TableAsync(DbObjectRef table, CancellationToken ct);

    Task<IReadOnlyList<object?[]>> QueryAsync(CompareQuery query, CancellationToken ct);

    /// <summary>지금 시점 번호. 못 읽으면 null.</summary>
    Task<long?> CurrentPointAsync(CancellationToken ct);

    /// <summary>서버 시각으로 <paramref name="minutesAgo"/> 분 전의 시점 번호(클라이언트 시계·시간대와 무관). 못 읽으면 null.</summary>
    Task<long?> PointMinutesAgoAsync(decimal minutesAgo, CancellationToken ct);

    /// <summary>undo_retention(초). 못 읽으면 null.</summary>
    Task<long?> UndoRetentionAsync(CancellationToken ct);

    /// <summary>표 정의가 마지막으로 바뀐 시각. 못 읽으면 null.</summary>
    Task<DateTimeOffset?> LastDdlAsync(DbObjectRef table, CancellationToken ct);

    /// <summary>쓰기 문장 하나(커밋 안 함, 기록 방식 None — RD-8). 영향 행 수.</summary>
    Task<long> ExecuteAsync(string sql, CancellationToken ct);
}

/// <summary>열린 세션 위의 실제 구현. 읽기·쓰기는 실행 엔진으로 보낸다(정책 관문은 반영 단추가 확인한 문장으로 통과).</summary>
internal sealed class SessionPointCompareDb(ISession session, IRollbackDialect dialect, ExecutionState state, Func<IExecutionService> execution,
                                            IMetadataProvider metadata, ISqlCatalog? catalog) : IPointCompareDb
{
    private static readonly ExecutionOptions ReadOptions = new() { Source = HistorySource.Editor, SkipHistory = true, Recording = ChangeRecording.None };

    public Dbms Dbms => dialect.Dbms;

    public IRollbackDialect Dialect => dialect;

    public ISession Session => session;

    public async Task<RollbackTable?> TableAsync(DbObjectRef table, CancellationToken ct)
    {
        var info = await metadata.GetTableAsync(session, table, ct).ConfigureAwait(true);
        if (info.Table.ObjectType.Contains("VIEW", StringComparison.OrdinalIgnoreCase) || info.Columns.Count == 0)
        {
            return null;
        }

        var columns = info.Columns.OrderBy(c => c.Position)
            .Select(c => new RollbackColumn(c.Name, c.DataType, dialect.KindOf(c))
            {
                Scale = c.Scale,
                IsVirtual = c.IsVirtual,
                IsIdentity = c.IsIdentity,
                IsInvisible = c.IsInvisible,
            })
            .ToList();
        var keys = info.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey)?.Columns ?? [];
        return new RollbackTable(new DbObjectRef(info.Table.Owner, info.Table.Name, "TABLE"), columns, keys);
    }

    public Task<IReadOnlyList<object?[]>> QueryAsync(CompareQuery query, CancellationToken ct) => ReadAsync(query.Sql, query.Binds, ct);

    public async Task<long?> CurrentPointAsync(CancellationToken ct)
    {
        foreach (var sql in dialect.PointQueries)
        {
            try
            {
                if (ToLong((await ReadAsync(sql, Empty, ct).ConfigureAwait(true)).First0()) is { } n)
                {
                    return n;
                }
            }
            catch (PointCompareDbException)
            {
                // 다음 방법
            }
        }

        return null;
    }

    public async Task<long?> PointMinutesAgoAsync(decimal minutesAgo, CancellationToken ct)
    {
        var fn = dialect.Dbms == Dbms.Tibero ? "TIMESTAMP_TO_TSN" : "TIMESTAMP_TO_SCN";
        var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal) { ["m"] = new(BindDataType.Number, minutesAgo) };
        try
        {
            return ToLong((await ReadAsync($"SELECT {fn}(SYSTIMESTAMP - NUMTODSINTERVAL(:m, 'MINUTE')) FROM DUAL", binds, ct).ConfigureAwait(true)).First0());
        }
        catch (PointCompareDbException)
        {
            return null;
        }
    }

    public async Task<long?> UndoRetentionAsync(CancellationToken ct)
    {
        if (catalog is null)
        {
            return null;
        }

        var value = await FlashbackEnvironment.ReadUndoRetentionAsync(catalog, session.Server, async text =>
        {
            try
            {
                return (await ReadAsync(text, Empty, ct).ConfigureAwait(true)).First0();
            }
            catch (PointCompareDbException)
            {
                return null;
            }
        }).ConfigureAwait(true);
        return ToLong(value);
    }

    public async Task<DateTimeOffset?> LastDdlAsync(DbObjectRef table, CancellationToken ct)
    {
        if (catalog is null)
        {
            return null;
        }

        try
        {
            var text = catalog.Get("dbinfo.flashback_ddl", session.Server).Sql;
            var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["owner"] = new(BindDataType.Varchar2, table.Owner),
                ["name"] = new(BindDataType.Varchar2, table.Name),
            };
            var rows = await ReadAsync(text, binds, ct).ConfigureAwait(true);
            return rows.Count > 0 && FlashbackRunner.ToDateTime(rows[0][0]) is { } d ? new DateTimeOffset(d) : null;
        }
        catch (Exception e) when (e is CatalogQueryNotFoundException or PointCompareDbException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<long> ExecuteAsync(string sql, CancellationToken ct)
    {
        var stmt = new SqlStatement(sql, default, StatementKind.Dml) { Verb = sql[..sql.IndexOf(' ', StringComparison.Ordinal)] };
        var options = new ExecutionOptions
        {
            Source = HistorySource.GridEdit,
            Recording = ChangeRecording.None,
            PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { sql }, // 사용자가 미리보기에서 「반영」을 눌렀다
        };
        var result = await RunAsync(stmt, Empty, options, ct).ConfigureAwait(true);
        return result.RowsAffected ?? 0;
    }

    private async Task<IReadOnlyList<object?[]>> ReadAsync(string sql, IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT", BindNames = [.. binds.Keys] };
        var result = await RunAsync(stmt, binds, ReadOptions, ct).ConfigureAwait(true);
        if (result.ResultSet is not { } rs)
        {
            throw new PointCompareDbException("비교 질의가 결과를 돌려주지 않았습니다.", null);
        }

        return await FlashbackRunner.ReadAllAsync(rs, ct).ConfigureAwait(true);
    }

    private async Task<ExecutionResult> RunAsync(SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions options, CancellationToken ct)
    {
        if (state.IsBroken(session.Id))
        {
            throw new PointCompareDbException("세션이 끊겼습니다. 다시 접속한 뒤 하세요.", null);
        }

        var exec = execution();
        var result = await Task.Run(() => exec.ExecuteAsync(session, stmt, binds, options, ct), CancellationToken.None).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        if (result.Error is { } error)
        {
            throw new PointCompareDbException(error.Message.ReplaceLineEndings(" "), error);
        }

        if (result.Status != ExecutionStatus.Succeeded)
        {
            throw new PointCompareDbException("문장을 실행하지 못했습니다: " + result.Status, null);
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, BindValue> Empty = new Dictionary<string, BindValue>();

    internal static long? ToLong(object? v) => v switch
    {
        null or DBNull => null,
        decimal d => (long)d,
        long l => l,
        int i => i,
        double f => (long)f,
        string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        string s when decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) => (long)m,
        _ => null,
    };
}

internal static class PointCompareRows
{
    /// <summary>첫 행의 첫 값(행이 없으면 null).</summary>
    public static object? First0(this IReadOnlyList<object?[]> rows) => rows.Count > 0 && rows[0].Length > 0 ? rows[0][0] : null;
}

/// <summary>식별자 따옴표: 그리드 DML 과 같은 규칙(예약어도 감싼다).</summary>
internal static class PointCompareSql
{
    public static string Quote(string name) => RowDmlBuilder.QuoteIdentifier(name);
}
