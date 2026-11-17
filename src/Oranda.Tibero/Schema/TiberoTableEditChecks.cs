using System.Data.Common;
using System.Globalization;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 테이블 편집기 실행 전 검사·가상 식 미리보기 읽기(Tibero, 설계 10장·7.6, TE-33). 메타데이터 공급자와 같은 세션 읽기 경로(<see cref="TiberoSessionQuery"/>)로 읽어
/// 실행 이력·변경 일지·반영 묶음에 남지 않는다. 읽기만 한다.
/// <list type="bullet">
/// <item>이름은 사전 표기를 받아 <see cref="SqlIdentifier.Quote"/> 로 감싼다(소유자는 늘 붙임). 사전 질의는 바인드.</item>
/// <item>취소: <paramref name="ct"/> 가 오면 서버 명령을 취소하고(<c>DbCommand.Cancel</c>, 드라이버 TBR-12040) <see cref="OperationCanceledException"/> 을 낸다.</item>
/// <item>의존 객체는 Tibero 열 이름(<c>parent_obj_*</c>)을 쓰는 기존 <c>meta.detail_dependencies</c> 의 USED_BY 쪽을 쓴다.</item>
/// </list>
/// </summary>
[OrandaService(typeof(ITableEditChecks), Dbms = Dbms.Tibero)]
internal sealed class TiberoTableEditChecks : ITableEditChecks
{
    /// <summary>사용자 식·조건을 닫는 괄호. 줄을 바꿔 닫아 식 끝의 <c>--</c> 주석이 괄호를 먹지 않게 한다.</summary>
    private const string Close = "\n)";

    public Dbms Dbms => Dbms.Tibero;

    public Task<long> CountRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        CountAsync(s, TableEditCheckSql.CountSql(Checked(table), "1 = 1"), ct);

    public async Task<long?> NumRowsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        // Tibero 는 통계를 모은 적 없는 테이블도 NUM_ROWS = 0 을 준다(실측) → LAST_ANALYZED 가 없으면 모름.
        var value = await ScalarAsync(s,
            "SELECT CASE WHEN last_analyzed IS NULL THEN NULL ELSE num_rows END FROM all_tables WHERE owner = :owner AND table_name = :name",
            ct, ("owner", table.Owner), ("name", table.Name)).ConfigureAwait(false);
        return TiberoSessionQuery.Long(value);
    }

    /// <summary>
    /// 문자·RAW = Core <see cref="TableEditCheckSql.OverflowWhere"/>(「그 행 보기」와 같은 조건), NUMBER = 정수 자리 넘침(<see cref="OverflowCondition"/>,
    /// 검사 계획은 정밀도·소수 줄이기를 NULL 아닌 행으로 보므로 보통 부르지 않는다). 그 밖의 형식 = 0.
    /// </summary>
    public Task<long> OverflowCountAsync(ISession s, DbObjectRef table, string column, ColumnDraft target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        var where = Family(target.DataType) switch
        {
            "VARCHAR2" or "CHAR" or "NVARCHAR2" or "NCHAR" or "RAW" when target.Length is not null => TableEditCheckSql.OverflowWhere(column, target),
            "NUMBER" => OverflowCondition("t." + Column(column), target),
            _ => null,
        };
        return where is null ? Task.FromResult(0L) : CountAsync(s, TableEditCheckSql.CountSql(Checked(table), where), ct);
    }

    public Task<long> NullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) =>
        CountAsync(s, TableEditCheckSql.CountSql(Checked(table), TableEditCheckSql.NullWhere(Checked(column))), ct);

    public Task<long> NonNullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) =>
        CountAsync(s, TableEditCheckSql.CountSql(Checked(table), TableEditCheckSql.NonNullWhere(Checked(column))), ct);

    /// <summary>
    /// 겹치는 행 수(모든 키가 NULL 인 행은 빼고, 일부 NULL 은 NULL 끼리 같게 — UK 규칙). 건수는 GROUP BY 한 번(Core 「그 행 보기」 조건의
    /// 행마다 EXISTS 는 큰 테이블에서 시간 제한에 걸린다). GROUP BY 도 NULL 끼리 묶으므로 건수는 Core 조건과 같다.
    /// </summary>
    public Task<long> DuplicateKeyCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, CancellationToken ct)
    {
        var keys = Columns(columns).Select(Column).ToList();
        var allNull = string.Join(" AND ", keys.Select(k => k + " IS NULL"));
        return CountAsync(s,
            $"SELECT NVL(SUM(cnt), 0) FROM (SELECT COUNT(*) AS cnt FROM {Name(table)} WHERE NOT ({allNull}) GROUP BY {string.Join(", ", keys)} HAVING COUNT(*) > 1)",
            ct);
    }

    public Task<long> OrphanCountAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> columns, DbObjectRef referencedTable, IReadOnlyList<string> referencedColumns, CancellationToken ct)
    {
        Columns(columns);
        Columns(referencedColumns);
        ArgumentNullException.ThrowIfNull(referencedTable);
        if (columns.Count != referencedColumns.Count)
        {
            throw new ArgumentException("FK 열과 참조 열의 수가 다르다.", nameof(referencedColumns));
        }

        return CountAsync(s, TableEditCheckSql.CountSql(Checked(table), TableEditCheckSql.OrphanWhere(columns, referencedTable, referencedColumns)), ct);
    }

    /// <summary>어기는 행 수. 조건 끝에 줄을 바꿔 닫아 <c>--</c> 주석이 괄호를 먹지 않게 한다.</summary>
    public Task<long> CheckViolationCountAsync(ISession s, DbObjectRef table, string condition, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        return CountAsync(s, TableEditCheckSql.CountSql(Checked(table), TableEditCheckSql.CheckViolationWhere(condition + "\n")), ct);
    }

    public async Task<ExpressionPreview> PreviewExpressionAsync(
        ISession s, DbObjectRef table, string expression, IReadOnlyList<string> columns, int rows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        var select = string.Join(", ", new[] { $"({expression}{Close}" }.Concat(columns.Select(Column)));
        var sql = $"SELECT {select} FROM {Name(table)} WHERE ROWNUM <= {rows.ToString(CultureInfo.InvariantCulture)}";
        string[] names = [expression, .. columns];
        try
        {
            var result = await RunAsync(s, sql, ct).ConfigureAwait(false);
            return new ExpressionPreview(names, [.. result.Rows.Select(r => (IReadOnlyList<string?>)[.. r.Select(TiberoSessionQuery.Str)])]);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && TiberoErrorClassifier.ToDbError(ex, sql) is { } error)
        {
            return new ExpressionPreview(names, [], error.Message);
        }
    }

    public async Task<string?> ValidateExpressionAsync(ISession s, string owner, IReadOnlyList<ColumnDraft> columns, string expression, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var typed = columns.Where(c => !c.IsDropped && !c.IsVirtual && !string.IsNullOrWhiteSpace(c.DataType) && !string.IsNullOrWhiteSpace(c.Name)).ToList();
        var row = typed.Count == 0
            ? "SELECT 1 AS dummy$ FROM dual"
            : "SELECT " + string.Join(", ", typed.Select(c => $"CAST(NULL AS {TypeText(c)}) AS {Column(c.Name)}")) + " FROM dual";
        var sql = $"SELECT ({expression}{Close} FROM ({row}) WHERE 1 = 0";
        try
        {
            _ = await RunAsync(s, sql, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && TiberoErrorClassifier.ToDbError(ex, sql) is { } error)
        {
            return error.Message;
        }
    }

    public async Task<bool> NameExistsAsync(ISession s, string owner, string name, TableEditNameKind kind, DbObjectRef? table, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var (sql, binds) = kind switch
        {
            TableEditNameKind.Column => (
                "SELECT COUNT(*) FROM all_tab_cols WHERE owner = :owner AND table_name = :tname AND column_name = :name",
                new (string, object?)[] { ("owner", (table ?? throw new ArgumentNullException(nameof(table))).Owner), ("tname", table.Name), ("name", name) }),
            TableEditNameKind.Table => (
                "SELECT COUNT(*) FROM all_objects WHERE owner = :owner AND object_name = :name AND subobject_name IS NULL"
                + " AND object_type IN ('TABLE', 'VIEW', 'SYNONYM', 'SEQUENCE', 'PROCEDURE', 'FUNCTION', 'PACKAGE', 'TYPE', 'MATERIALIZED VIEW', 'LIBRARY', 'JAVA')",
                new (string, object?)[] { ("owner", owner), ("name", name) }),
            TableEditNameKind.Index => (
                "SELECT COUNT(*) FROM all_indexes WHERE owner = :owner AND index_name = :name",
                new (string, object?)[] { ("owner", owner), ("name", name) }),
            TableEditNameKind.Constraint => (
                "SELECT COUNT(*) FROM all_constraints WHERE owner = :owner AND constraint_name = :name",
                new (string, object?)[] { ("owner", owner), ("name", name) }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return TiberoSessionQuery.Long(await ScalarAsync(s, sql, ct, binds).ConfigureAwait(false)) > 0;
    }

    public async Task<IReadOnlyList<DbObjectRef>> DependentsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        // 카탈로그 원문을 이 클래스의 실행 경로로 보낸다(취소 → 서버 명령 취소·OperationCanceledException 이 다른 검사와 같게).
        var result = await RunAsync(s, TiberoCatalogQuery.Sql(s, "meta.detail_dependencies"), ct,
            ("owner", table.Owner), ("name", table.Name), ("type", "TABLE")).ConfigureAwait(false);
        return
        [
            .. result.Rows
                .Where(r => string.Equals(TiberoSessionQuery.Str(r[0]), "USED_BY", StringComparison.Ordinal))
                .Select(r => new DbObjectRef(TiberoSessionQuery.Str(r[1])!, TiberoSessionQuery.Str(r[2])!, TiberoSessionQuery.Str(r[3])!)),
        ];
    }

    /// <summary>
    /// 새 정의에 담기지 않는 값의 조건. 문자 = 길이 기준(글자 = LENGTH, 바이트·모름 = LENGTHB — N 형식은 늘 글자), RAW = 바이트,
    /// 숫자 = 정수 자리(<c>ABS(c) &gt;= 10^(p-s)</c>, 소수 자리 줄이기는 반올림이라 넘침이 아니다). 판정할 것이 없으면 null.
    /// </summary>
    internal static string? OverflowCondition(string column, ColumnDraft target)
    {
        var type = (target.DataType ?? string.Empty).Trim().ToUpperInvariant();
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? type : type[..paren]).Trim();
        switch (name)
        {
            case "VARCHAR" or "VARCHAR2" or "CHAR" when target.Length is { } n:
                return target.CharSemantics == CharSemantics.Characters ? Invariant($"LENGTH({column}) > {n}") : Invariant($"LENGTHB({column}) > {n}");
            case "NVARCHAR" or "NVARCHAR2" or "NCHAR" when target.Length is { } n:
                return Invariant($"LENGTH({column}) > {n}");
            case "RAW" when target.Length is { } n:
                return Invariant($"LENGTH(RAWTOHEX({column})) / 2 > {n}");
            case "NUMBER" or "NUMERIC" or "DECIMAL" when target.Precision is { } p:
                return Invariant($"ABS({column}) >= POWER(10, {p - (target.Scale ?? 0)})");
            default:
                return null;
        }
    }

    /// <summary>형식 계열(괄호 앞 이름, Tibero 사전 표기 VARCHAR·NVARCHAR 는 VARCHAR2·NVARCHAR2 로).</summary>
    private static string Family(string? dataType)
    {
        var type = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? type : type[..paren]).Trim();
        return name switch
        {
            "VARCHAR" => "VARCHAR2",
            "NVARCHAR" => "NVARCHAR2",
            "NUMERIC" or "DECIMAL" or "INTEGER" or "INT" or "SMALLINT" => "NUMBER",
            _ => name,
        };
    }

    /// <summary>빈 행 만들기용 형식 표기(<c>CAST(NULL AS …)</c>). 길이 기준은 쓴 것만.</summary>
    internal static string TypeText(ColumnDraft c)
    {
        var type = c.DataType!.Trim().ToUpperInvariant();
        if (type.Contains('(', StringComparison.Ordinal))
        {
            return type;
        }

        return type switch
        {
            "VARCHAR" or "VARCHAR2" or "CHAR" when c.Length is { } n =>
                Invariant($"{type}({n}{c.CharSemantics switch { CharSemantics.Characters => " CHAR", CharSemantics.Bytes => " BYTE", _ => string.Empty }})"),
            "NVARCHAR" or "NVARCHAR2" or "NCHAR" or "RAW" when c.Length is { } n => Invariant($"{type}({n})"),
            // 길이를 아직 안 쓴 칸: 식 해석만 하므로 넉넉한 길이로(길이 없는 VARCHAR2·RAW 는 구문 오류라 멀쩡한 식이 ✖ 가 된다).
            "VARCHAR" or "VARCHAR2" => "VARCHAR2(4000)",
            "NVARCHAR" or "NVARCHAR2" => "NVARCHAR2(2000)",
            "RAW" => "RAW(2000)",
            "NUMBER" when c.Precision is { } p => Invariant($"NUMBER({p},{c.Scale ?? 0})"),
            "NUMBER" when c.Scale is { } sc => Invariant($"NUMBER(*,{sc})"),
            _ => type,
        };
    }

    private static string Name(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Quote(table.Owner) + "." + Quote(table.Name);
    }

    private static string Column(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return Quote(column);
    }

    /// <summary>대문자 규칙을 벗어나거나 예약어면 따옴표(설계 8.2).</summary>
    private static string Quote(string name) => SqlIdentifier.Quote(name, TableEditDefaults.IsReservedWord);

    /// <summary>열 목록을 확인해 그대로 돌려준다(이름은 Core 가 감싼다).</summary>
    private static IReadOnlyList<string> Columns(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0 || columns.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("열이 하나 이상 있어야 한다.", nameof(columns));
        }

        return columns;
    }

    private static DbObjectRef Checked(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table;
    }

    private static string Checked(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return column;
    }

    private static async Task<long> CountAsync(ISession s, string sql, CancellationToken ct) =>
        TiberoSessionQuery.Long(await ScalarAsync(s, sql, ct).ConfigureAwait(false)) ?? 0;

    private static async Task<object?> ScalarAsync(ISession s, string sql, CancellationToken ct, params (string Name, object? Value)[] binds)
    {
        var result = await RunAsync(s, sql, ct, binds).ConfigureAwait(false);
        return result.Rows.Count == 0 || result.Columns.Count == 0 ? null : result.Rows[0][0];
    }

    /// <summary>세션 읽기 경로로 보낸다. 취소가 오면 서버 명령을 취소하고, 그 때문에 난 드라이버 오류는 <see cref="OperationCanceledException"/> 으로 바꾼다.</summary>
    private static async Task<TiberoSessionQuery.Table> RunAsync(ISession s, string sql, CancellationToken ct, params (string Name, object? Value)[] binds)
    {
        ArgumentNullException.ThrowIfNull(s);
        ct.ThrowIfCancellationRequested();
        CancellationTokenRegistration registration = default;
        try
        {
            return await TiberoSessionQuery.RunAsync(s, sql, command =>
            {
                foreach (var (name, value) in binds)
                {
                    TiberoSessionQuery.Bind(command, name, value);
                }

                registration = ct.Register(static state => TryCancel((DbCommand)state!), command);
            }, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("테이블 편집 검사 질의가 시간 제한·취소로 멈췄다.", ex, ct);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void TryCancel(DbCommand command)
    {
        try
        {
            command.Cancel();
        }
#pragma warning disable CA1031 // 취소는 최선 노력: 타이머 스레드에서 불려도 프로세스를 죽이지 않는다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
