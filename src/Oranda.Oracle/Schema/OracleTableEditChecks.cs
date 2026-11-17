using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>
/// 테이블 편집기 실행 전 검사·가상 식 미리보기의 Oracle 읽기(설계 5장·7.6·10장, TE-33). 문장은 카탈로그 <c>tableedit.*</c> 에서만 꺼내고(TC-03),
/// 바인드로 보낼 수 없는 이름·식은 <c>@KEY@</c> 자리에 넣는다(이름은 늘 따옴표, 식·조건은 사용자 원문).
/// 실행 엔진 밖에서 세션 명령 잠금(D-041)만 잡고 읽는다 — 이력·변경 일지·반영 묶음에 남지 않고 정책 확인도 뜨지 않는다(읽기만).
/// 취소(<paramref name="ct"/>, 부르는 쪽의 시간 제한)는 서버 명령 취소로 옮기고 <see cref="OperationCanceledException"/> 을 낸다.
/// 판정 근거 = 실측 <c>docs/design/oracle-table-edit-probe.md</c>.
/// </summary>
[OrandaService(typeof(ITableEditChecks), Dbms = Dbms.Oracle)]
internal sealed class OracleTableEditChecks : ITableEditChecks
{
    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    public Dbms Dbms => Dbms.Oracle;

    public Task<long> CountRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) =>
        ScalarAsync(s, "tableedit.count_rows", [("TABLE", Qualified(table))], _ => { }, ct);

    public async Task<long?> NumRowsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var rows = await RowsAsync(s, "tableedit.num_rows", [], c => BindTable(c, table), ct);
        return rows.Count > 0 && rows[0][0] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// 문자 = <c>LENGTH</c>(글자 기준·N 계열)/<c>LENGTHB</c>(바이트 기준·기준 없음) &gt; 새 길이, RAW = <c>UTL_RAW.LENGTH</c>.
    /// NUMBER = 지금 정의보다 정밀도·소수를 줄이면 NULL 아닌 행 모두(데이터가 있으면 ORA-01440, 넣을 수 있는 값이어도), 아니면 반올림 뒤 정수 자리 넘침.
    /// 그 밖의 형식은 0.
    /// </summary>
    public async Task<long> OverflowCountAsync(ISession s, DbObjectRef table, string column, ColumnDraft target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(target);
        var type = BaseType(target.DataType);
        (string, string)[] args = [("TABLE", Qualified(table)), ("COL", SqlIdentifier.QuoteAlways(column))];
        if (LengthFunction(type, target.CharSemantics) is { } fn)
        {
            if (target.Length is not { } max)
            {
                return 0;
            }

            return await ScalarAsync(s, "tableedit.length_overflow", [.. args, ("LENGTH", fn)],
                c => c.Parameters.Add("max_length", OracleDbType.Int32, max, System.Data.ParameterDirection.Input), ct);
        }

        if (type != "NUMBER")
        {
            return 0;
        }

        var current = await RowsAsync(s, "tableedit.column_def", [], c =>
        {
            BindTable(c, table);
            c.Parameters.Add("column_name", column);
        }, ct);
        if (current.Count == 0 || !string.Equals(current[0][0] as string, "NUMBER", StringComparison.Ordinal))
        {
            return 0; // 열이 없거나 형식 자체를 바꾼다 — 형식 변경 판정(NonNullCountAsync, ORA-01439) 몫이다
        }

        var oldExplicitPrecision = Int(current[0][1]);
        var (oldPrecision, oldScale) = NumberShape(oldExplicitPrecision, Int(current[0][2]));
        var (newPrecision, newScale) = NumberShape(target.Precision, target.Scale);
        if (Decreases(oldExplicitPrecision, oldPrecision, oldScale, newPrecision, newScale))
        {
            return await NonNullCountAsync(s, table, column, ct);
        }

        if (newScale == FloatScale || oldExplicitPrecision is not null)
        {
            return 0; // 줄이지 않으면 담긴 값은 다 들어간다(정밀도를 쓴 열은 정수 자리도 줄지 않았다)
        }

        return await ScalarAsync(s, "tableedit.number_overflow", args, c =>
        {
            c.Parameters.Add("scale", OracleDbType.Int32, newScale, System.Data.ParameterDirection.Input);
            c.Parameters.Add("digits", OracleDbType.Int32, newPrecision - newScale, System.Data.ParameterDirection.Input);
        }, ct);
    }

    public Task<long> NullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) =>
        ScalarAsync(s, "tableedit.null_count", [("TABLE", Qualified(table)), ("COL", SqlIdentifier.QuoteAlways(column))], _ => { }, ct);

    public Task<long> NonNullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) =>
        ScalarAsync(s, "tableedit.nonnull_count", [("TABLE", Qualified(table)), ("COL", SqlIdentifier.QuoteAlways(column))], _ => { }, ct);

    public Task<long> DuplicateKeyCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, CancellationToken ct)
    {
        RequireColumns(columns);
        var quoted = columns.Select(SqlIdentifier.QuoteAlways).ToList();
        return ScalarAsync(s, "tableedit.duplicate_keys",
            [
                ("TABLE", Qualified(table)),
                ("COLS", string.Join(", ", quoted)),
                ("ALL_NULL", string.Join(" AND ", quoted.Select(q => q + " IS NULL"))),
            ], _ => { }, ct);
    }

    public Task<long> OrphanCountAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> columns, DbObjectRef referencedTable, IReadOnlyList<string> referencedColumns, CancellationToken ct)
    {
        RequireColumns(columns);
        ArgumentNullException.ThrowIfNull(referencedTable);
        ArgumentNullException.ThrowIfNull(referencedColumns);
        if (referencedColumns.Count != columns.Count)
        {
            throw new ArgumentException("참조 열 수가 키 열 수와 다릅니다.", nameof(referencedColumns));
        }

        var own = columns.Select(c => "c." + SqlIdentifier.QuoteAlways(c)).ToList();
        var match = own.Select((c, i) => "p." + SqlIdentifier.QuoteAlways(referencedColumns[i]) + " = " + c);
        return ScalarAsync(s, "tableedit.orphans",
            [
                ("TABLE", Qualified(table)),
                ("REF_TABLE", Qualified(referencedTable)),
                ("ANY_NULL", string.Join(" OR ", own.Select(c => c + " IS NULL"))),
                ("MATCH", string.Join(" AND ", match)),
            ], _ => { }, ct);
    }

    public Task<long> CheckViolationCountAsync(ISession s, DbObjectRef table, string condition, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        return ScalarAsync(s, "tableedit.check_violations", [("TABLE", Qualified(table)), ("CONDITION", condition)], _ => { }, ct);
    }

    public async Task<ExpressionPreview> PreviewExpressionAsync(
        ISession s, DbObjectRef table, string expression, IReadOnlyList<string> columns, int rows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(columns);
        var extra = string.Concat(columns.Select(c => ", " + SqlIdentifier.QuoteAlways(c)));
        try
        {
            var result = await QueryAsync(s, "tableedit.preview_expression",
                [("TABLE", Qualified(table)), ("EXPRESSION", expression), ("COLUMNS", extra)],
                c => c.Parameters.Add("max_rows", OracleDbType.Int32, Math.Max(rows, 0), System.Data.ParameterDirection.Input),
                text: true, ct);
            return new ExpressionPreview(["EXPRESSION_VALUE", .. columns], [.. result.Select(r => (IReadOnlyList<string?>)[.. r.Select(v => (string?)v)])]);
        }
        catch (OracleException ex) when (!ct.IsCancellationRequested)
        {
            return new ExpressionPreview([], [], ex.Message);
        }
    }

    public async Task<string?> ValidateExpressionAsync(ISession s, string owner, IReadOnlyList<ColumnDraft> columns, string expression, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        try
        {
            await QueryAsync(s, "tableedit.validate_expression", [("EXPRESSION", expression), ("SOURCE", EmptyRowSource(columns))], _ => { }, text: false, ct);
            return null;
        }
        catch (OracleException ex) when (!ct.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    public async Task<bool> NameExistsAsync(ISession s, string owner, string name, TableEditNameKind kind, DbObjectRef? table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(name);
        var (id, bind) = kind switch
        {
            TableEditNameKind.Column => ("tableedit.column_name_exists", (Action<OracleCommand>)(c =>
            {
                var t = table ?? throw new ArgumentNullException(nameof(table), "열 이름 검사에는 테이블이 있어야 합니다.");
                BindTable(c, t);
                c.Parameters.Add("column_name", name);
            })),
            TableEditNameKind.Table => ("tableedit.table_name_exists", BindOwnerName(owner, name)),
            TableEditNameKind.Constraint => ("tableedit.constraint_name_exists", BindOwnerName(owner, name)),
            TableEditNameKind.Index => ("tableedit.index_name_exists", BindOwnerName(owner, name)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return await ScalarAsync(s, id, [], bind, ct) > 0;
    }

    public async Task<IReadOnlyList<DbObjectRef>> DependentsAsync(ISession s, DbObjectRef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var rows = await RowsAsync(s, "tableedit.dependents", [], c => BindTable(c, table), ct);
        return [.. rows.Select(r => new DbObjectRef((string)r[0]!, (string)r[1]!, (string)r[2]!))];
    }

    /// <summary>소수 자리 제한이 없는 NUMBER 의 소수 표시(비교용 큰 값).</summary>
    internal const int FloatScale = 127;

    /// <summary>
    /// 비교용 NUMBER 모양(정밀도, 소수). <c>NUMBER</c> = (38, 제한 없음), <c>NUMBER(*,s)</c> = (38, s), <c>NUMBER(p)</c> = (p, 0), <c>NUMBER(p,s)</c> = (p, s).
    /// 실측 3: 사전은 <c>NUMBER</c> 를 정밀도·소수 NULL, <c>NUMBER(*,0)</c>·<c>INTEGER</c> 를 정밀도 NULL·소수 0 으로 둔다.
    /// </summary>
    internal static (int Precision, int Scale) NumberShape(int? precision, int? scale) => (precision, scale) switch
    {
        (null, null) => (38, FloatScale),
        (null, { } s) => (38, s),
        ({ } p, null) => (p, 0),
        ({ } p, { } s) => (p, s),
    };

    /// <summary>
    /// 데이터가 있으면 서버가 막는 NUMBER 줄이기인가(ORA-01440, 실측 12c·12i): 정밀도가 줄거나, 소수가 줄거나,
    /// 정밀도를 쓴 열에서 정수 자리(정밀도 − 소수)가 준다(<c>(10,2)</c> → <c>(10,4)</c>, <c>(5)</c> → <c>(5,1)</c>).
    /// 정밀도를 안 쓴 <c>NUMBER(*,0)</c> → <c>NUMBER(*,2)</c> 는 된다.
    /// </summary>
    internal static bool Decreases(int? oldExplicitPrecision, int oldPrecision, int oldScale, int newPrecision, int newScale) =>
        newPrecision < oldPrecision
        || newScale < oldScale
        || (oldExplicitPrecision is not null && newPrecision - newScale < oldPrecision - oldScale);

    /// <summary>길이 비교 함수. 문자·RAW 가 아니면 null.</summary>
    internal static string? LengthFunction(string type, CharSemantics? semantics) => type switch
    {
        "NVARCHAR2" or "NCHAR" => "LENGTH",
        "VARCHAR2" or "VARCHAR" or "CHAR" => semantics == CharSemantics.Characters ? "LENGTH" : "LENGTHB",
        "RAW" => "UTL_RAW.LENGTH",
        _ => null,
    };

    /// <summary>
    /// 새 테이블 식 확인용 빈 행 원천: 가상·지울 열·형식 없는 열은 빼고 <c>CAST(NULL AS 형식) AS "열"</c>. 열이 없으면 <c>NULL AS "DUMMY"</c>.
    /// 길이를 아직 안 쓴 가변 문자·RAW 는 가장 큰 길이로 둔다(문법 확인만 하므로).
    /// </summary>
    internal static string EmptyRowSource(IReadOnlyList<ColumnDraft> columns)
    {
        var parts = columns
            .Where(c => !c.IsDropped && !c.IsVirtual && !string.IsNullOrWhiteSpace(c.DataType) && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => "CAST(NULL AS " + CastType(c) + ") AS " + SqlIdentifier.QuoteAlways(c.Name))
            .ToList();
        return parts.Count == 0 ? "NULL AS \"DUMMY\"" : string.Join(", ", parts);
    }

    /// <summary>CAST 형식 표기(<c>VARCHAR2(10 CHAR)</c>, <c>NUMBER(*,2)</c> …).</summary>
    internal static string CastType(ColumnDraft c)
    {
        var raw = c.DataType!.Trim();
        var type = BaseType(raw);
        if (raw.Contains('(', StringComparison.Ordinal))
        {
            return raw.ToUpperInvariant(); // TIMESTAMP(6) 처럼 형식 이름에 이미 괄호가 있다
        }

        switch (type)
        {
            case "VARCHAR2" or "VARCHAR" or "CHAR" or "NVARCHAR2" or "NCHAR" or "RAW":
                var length = c.Length ?? (type switch { "CHAR" or "NCHAR" => 1, "VARCHAR2" or "VARCHAR" => 4000, _ => 2000 });
                var unit = type is "VARCHAR2" or "VARCHAR" or "CHAR" && c.CharSemantics is { } sem
                    ? (sem == CharSemantics.Characters ? " CHAR" : " BYTE")
                    : string.Empty;
                return type + "(" + length.ToString(CultureInfo.InvariantCulture) + unit + ")";
            case "NUMBER":
                return (c.Precision, c.Scale) switch
                {
                    (null, null) => "NUMBER",
                    (null, { } s) => "NUMBER(*," + s.ToString(CultureInfo.InvariantCulture) + ")",
                    ({ } p, null) => "NUMBER(" + p.ToString(CultureInfo.InvariantCulture) + ")",
                    ({ } p, { } s) => "NUMBER(" + p.ToString(CultureInfo.InvariantCulture) + "," + s.ToString(CultureInfo.InvariantCulture) + ")",
                };
            default:
                return type;
        }
    }

    private static string BaseType(string? dataType)
    {
        var t = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? t : t[..paren].TrimEnd();
    }

    private static string Qualified(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return SqlIdentifier.QuoteAlways(table.Owner) + "." + SqlIdentifier.QuoteAlways(table.Name);
    }

    private static void RequireColumns(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("키 열이 없습니다.", nameof(columns));
        }
    }

    private static void BindTable(OracleCommand c, DbObjectRef table)
    {
        c.Parameters.Add("owner", table.Owner);
        c.Parameters.Add("name", table.Name);
    }

    private static Action<OracleCommand> BindOwnerName(string owner, string name) => c =>
    {
        c.Parameters.Add("owner", owner);
        c.Parameters.Add("name", name);
    };

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static async Task<long> ScalarAsync(ISession s, string id, (string Key, string Value)[] args, Action<OracleCommand> bind, CancellationToken ct)
    {
        var rows = await RowsAsync(s, id, args, bind, ct);
        return rows.Count > 0 && rows[0][0] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    }

    private static Task<List<object?[]>> RowsAsync(ISession s, string id, (string Key, string Value)[] args, Action<OracleCommand> bind, CancellationToken ct) =>
        QueryAsync(s, id, args, bind, text: false, ct);

    /// <summary>
    /// 카탈로그 문장 하나를 읽는다: <c>@PFX@</c>(dba/all) → <c>@KEY@</c> 채우기(한 번에, 값 안의 자리 모양은 다시 바꾸지 않음) → 세션 명령 잠금 → 명령.
    /// <paramref name="text"/> 이면 값을 Oracle 값의 글 표기로 읽는다(미리보기: 38자리 NUMBER 도 .NET decimal 로 바꾸다 넘치지 않게).
    /// 취소는 <see cref="OracleCommand.Cancel"/> 로 서버에 보내고 <see cref="OperationCanceledException"/> 으로 바꾼다.
    /// </summary>
    private static async Task<List<object?[]>> QueryAsync(
        ISession s, string id, (string Key, string Value)[] args, Action<OracleCommand> bind, bool text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var prefix = s.Server.Capabilities.CanQueryDictionary ? "dba" : "all";
        var sql = CatalogText.Fill(Catalog.Value.Get(id, s.Server).Sql.Replace("@PFX@", prefix, StringComparison.Ordinal), args);

        var gate = session.CommandGate; // 세션 연결의 명령 잠금(D-041): 실행·결과 페이지·메타데이터 읽기와 한 줄로 선다
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = sql;
            bind(cmd);
            await using var registration = ct.Register(static c => TryCancel((OracleCommand)c!), cmd);
            await using var reader = (OracleDataReader)await cmd.ExecuteReaderAsync(ct);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null
                        : text ? reader.GetProviderSpecificValue(i)?.ToString()
                        : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (OracleException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct); // ORA-01013 사용자 취소 = 부르는 쪽 시간 제한
        }
        catch (OracleException ex) when (session.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void TryCancel(OracleCommand cmd)
    {
        try
        {
            cmd.Cancel();
        }
        catch (InvalidOperationException)
        {
            // 이미 끝났거나 닫힌 명령
        }
        catch (OracleException)
        {
            // 취소 신호를 보내지 못함 — ExecuteReaderAsync(ct) 가 따로 끝낸다
        }
    }
}
