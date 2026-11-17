using System.Data;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>
/// 열 형식 변경 자동 변환의 Oracle 검사 읽기(설계 18.9). 문장은 카탈로그 <c>tableedit.conversion_*</c>·<c>tableedit.referencing_fks</c> 에서만 꺼낸다(TC-03).
/// 「변환 안 되는 행」: 12.2 이상이고 기본 변환식이면 <c>VALIDATE_CONVERSION</c> 집합 질의(정확한 건수·「그 행 보기」 조건),
/// 그 밖은 PL/SQL 1차(모든 행을 새 형식 변수에 담아 봄 — 실패 없으면 0) → 실패하면 2차(행마다 담아 세고 앞 100행 ROWID).
/// 2차가 시간 제한·오류로 끝나면 1차가 본 실패로 「1건 이상」을 돌려준다. 변환식 문법 오류는 「1건 이상」 + 서버 오류(✖).
/// 사용자 변환식은 바인드한 SELECT 한 문장(<see cref="ValueQuery"/>)으로만 PL/SQL 블록에 들어간다(동적 SQL — 블록 문법으로 새지 않음).
/// 블록은 취소(ORA-01013)를 다시 던져 시간 제한이 듣는다. 5단계가 되살릴 CHECK·PK·UK 는 변환한 값으로 미리 센다.
/// 실측(<c>docs/design/oracle-table-edit-probe.md</c> 6장): <c>VALIDATE_CONVERSION</c> 은 정밀도를 받지 않아(ORA-43910) NUMBER(p,s) 넘침은 따로 셈,
/// <c>CAST('abcd' AS VARCHAR2(3))</c> 는 자르므로 검사는 CAST 가 아니라 변수 담기(ORA-06502)로 본다.
/// </summary>
[OrandaService(typeof(IColumnConversionChecks), Dbms = Dbms.Oracle)]
internal sealed class OracleColumnConversionChecks : IColumnConversionChecks
{
    /// <summary>2차 검사가 모으는 ROWID 수(「그 행 보기」).</summary>
    internal const int MaxRowIds = 100;

    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    public Dbms Dbms => Dbms.Oracle;

    public async Task<ConversionFailures> FailingRowsAsync(
        ISession s, DbObjectRef table, ColumnDraft source, ColumnDraft target, string expression, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        if (OracleTableEditDialect.AtLeast(s.Server, 12, 2)
            && ColumnConversionDefaults.Validation(source, target, expression) is { } validation)
        {
            var where = ValidateWhere(source.Name, validation, target.IsNullable);
            var count = await ExecuteAsync(s, "tableedit.conversion_validate", [("TABLE", Qualified(table)), ("WHERE", where)], _ => { }, scalar: true, ct);
            return new ConversionFailures(Convert.ToInt64(count, CultureInfo.InvariantCulture))
            {
                ViewRowsSql = TableEditCheckSql.RowsSql(table, where),
            };
        }

        (string, string)[] args = [("TABLE", Qualified(table)), ("TYPE", OracleTableEditChecks.CastType(target))];
        var notNull = target.IsNullable ? 0 : 1;
        var scan = ValueQuery(table, expression);
        string? firstError;
        try
        {
            OracleParameter? ok = null, error = null;
            await ExecuteAsync(s, "tableedit.conversion_scan", args, c =>
            {
                c.Parameters.Add("query", OracleDbType.Varchar2, scan, ParameterDirection.Input);
                c.Parameters.Add("not_null", OracleDbType.Int32, notNull, ParameterDirection.Input);
                ok = c.Parameters.Add("ok", OracleDbType.Int32, ParameterDirection.Output);
                error = c.Parameters.Add("first_error", OracleDbType.Varchar2, 2000, null, ParameterDirection.Output);
            }, scalar: false, ct);
            if (Int(ok!.Value) == 1)
            {
                return ConversionFailures.None;
            }

            firstError = Text(error!.Value);
        }
        catch (OracleException ex) when (!ct.IsCancellationRequested)
        {
            return new ConversionFailures(1) { IsLowerBound = true, FirstError = ex.Message }; // 변환식이 맞지 않음(ORA-06550 등) — 모든 행이 실패한다
        }

        try
        {
            OracleParameter? failed = null, ids = null;
            await ExecuteAsync(s, "tableedit.conversion_rows", args, c =>
            {
                c.Parameters.Add("query", OracleDbType.Varchar2, scan + " WHERE t.ROWID = :rid", ParameterDirection.Input);
                c.Parameters.Add("not_null", OracleDbType.Int32, notNull, ParameterDirection.Input);
                c.Parameters.Add("max_ids", OracleDbType.Int32, MaxRowIds, ParameterDirection.Input);
                failed = c.Parameters.Add("failed", OracleDbType.Int32, ParameterDirection.Output);
                ids = c.Parameters.Add("row_ids", OracleDbType.Varchar2, 4000, null, ParameterDirection.Output);
            }, scalar: false, ct);
            var rowIds = (Text(ids!.Value) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var n = Int(failed!.Value) ?? 0;
            return n == 0
                ? new ConversionFailures(1) { IsLowerBound = true, FirstError = firstError } // 1차와 2차 사이에 행이 바뀜 — 1차가 본 실패를 믿는다
                : new ConversionFailures(n) { FirstError = firstError, ViewRowsSql = RowIdSql(table, rowIds) };
        }
        catch (Exception ex) when (ex is OperationCanceledException or OracleException)
        {
            // 시간 제한(또는 사용자 취소)·2차 읽기 오류 — 1차가 이미 실패를 봤으므로 「1건 이상」(⚠ 「확인 못 함」으로 내려가 실행을 막지 못하는 일이 없게).
            // 사용자 취소면 부르는 쪽 다음 읽기가 취소를 던진다.
            return new ConversionFailures(1) { IsLowerBound = true, FirstError = firstError };
        }
    }

    public Task<ConversionFailures> ConvertedCheckViolationsAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, string condition, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        return CountConvertedAsync(s, "tableedit.conversion_check_violations",
            [.. ConvertedSource(table, otherColumns, column, expression), ("CONDITION", condition.Trim())], ct);
    }

    public Task<ConversionFailures> ConvertedDuplicateKeysAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, IReadOnlyList<string> keyColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("키 열이 없습니다.", nameof(keyColumns));
        }

        var keys = keyColumns.Select(k => "t." + SqlIdentifier.QuoteAlways(k)).ToList();
        return CountConvertedAsync(s, "tableedit.conversion_duplicate_keys",
            [
                .. ConvertedSource(table, otherColumns, column, expression),
                ("KEYS", string.Join(", ", keys)),
                ("ALL_NULL", string.Join(" AND ", keys.Select(k => k + " IS NULL"))),
            ], ct);
    }

    /// <summary>변환식 값을 옛 열 이름으로 낸 원천(@COLUMNS@·@EXPRESSION@·@COL@·@TABLE@).</summary>
    private static (string, string)[] ConvertedSource(DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression)
    {
        ArgumentNullException.ThrowIfNull(otherColumns);
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return
        [
            ("TABLE", Qualified(table)),
            ("COLUMNS", string.Concat(otherColumns.Select(c => "t." + SqlIdentifier.QuoteAlways(c) + ", "))),
            ("EXPRESSION", expression.Trim()),
            ("COL", SqlIdentifier.QuoteAlways(column)),
        ];
    }

    /// <summary>건수 질의. 계산 오류(변환식·조건이 새 값에서 맞지 않음)는 「1건 이상」 + 서버 오류.</summary>
    private static async Task<ConversionFailures> CountConvertedAsync(ISession s, string id, (string, string)[] args, CancellationToken ct)
    {
        try
        {
            var count = await ExecuteAsync(s, id, args, _ => { }, scalar: true, ct);
            return new ConversionFailures(Convert.ToInt64(count, CultureInfo.InvariantCulture));
        }
        catch (OracleException ex) when (!ct.IsCancellationRequested)
        {
            return new ConversionFailures(1) { IsLowerBound = true, FirstError = ex.Message };
        }
    }

    /// <summary>
    /// 변환식 값 하나를 내는 SELECT 한 문장(바인드로 PL/SQL 블록에 넘김 — 사용자 식이 블록 문법으로 새지 않는다). 사용자 원문 앞뒤는 줄을 바꾼다(끝 <c>--</c> 주석 대비).
    /// </summary>
    internal static string ValueQuery(DbObjectRef table, string expression) =>
        "SELECT (\n" + expression.Trim() + "\n) FROM " + Qualified(table) + " t";

    public async Task<IReadOnlyList<ForeignKeyRef>> ReferencingForeignKeysAsync(ISession s, DbObjectRef table, string column, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(column);
        var rows = await QueryAsync(s, "tableedit.referencing_fks", c =>
        {
            c.Parameters.Add("owner", table.Owner);
            c.Parameters.Add("name", table.Name);
            c.Parameters.Add("column_name", column);
        }, ct);
        return [.. rows.Select(r => new ForeignKeyRef((string)r[0]!, (string)r[1]!, (string)r[2]!))];
    }

    /// <summary>
    /// <c>VALIDATE_CONVERSION</c> 조건: 값이 있고 바꿀 수 없음, NUMBER(p,s) 는 반올림 뒤 정수 자리 넘침도, 새 정의가 NOT NULL 이면 NULL 행도.
    /// </summary>
    internal static string ValidateWhere(string column, ConversionValidation validation, bool targetNullable)
    {
        var c = "t." + SqlIdentifier.QuoteAlways(column);
        var format = validation.Format is null ? string.Empty : ", " + SqlLiteral.Quote(validation.Format);
        var fails = $"VALIDATE_CONVERSION({c} AS {validation.TypeName}{format}) = 0";
        if (validation.Precision is { } p)
        {
            var scale = validation.Scale ?? 0;
            fails += $" OR CASE WHEN VALIDATE_CONVERSION({c} AS NUMBER) = 1 THEN CASE WHEN ABS(ROUND(TO_NUMBER({c}), {N(scale)})) >= POWER(10, {N(p - scale)}) THEN 1 END END = 1";
        }

        var where = $"{c} IS NOT NULL AND ({fails})";
        return targetNullable ? where : $"({where}) OR {c} IS NULL";
    }

    /// <summary>「그 행 보기」: 모은 ROWID 의 행. 없으면 null.</summary>
    internal static string? RowIdSql(DbObjectRef table, IReadOnlyList<string> rowIds) =>
        rowIds.Count == 0
            ? null
            : TableEditCheckSql.RowsSql(table, "t.ROWID IN (" + string.Join(", ", rowIds.Select(r => "CHARTOROWID(" + SqlLiteral.Quote(r) + ")")) + ")");

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Qualified(DbObjectRef table) => SqlIdentifier.QuoteAlways(table.Owner) + "." + SqlIdentifier.QuoteAlways(table.Name);

    private static int? Int(object? v) => v switch
    {
        null or DBNull => null,
        OracleDecimal d => d.IsNull ? null : d.ToInt32(),
        _ => Convert.ToInt32(v, CultureInfo.InvariantCulture),
    };

    private static string? Text(object? v) => v switch
    {
        null or DBNull => null,
        OracleString t => t.IsNull ? null : t.Value,
        _ => v.ToString(),
    };

    private static async Task<List<object?[]>> QueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await RunAsync(s, id, [], bind, async (cmd, token) =>
        {
            await using var reader = (OracleDataReader)await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, token) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }, ct);
        return rows;
    }

    private static async Task<object?> ExecuteAsync(
        ISession s, string id, (string Key, string Value)[] args, Action<OracleCommand> bind, bool scalar, CancellationToken ct)
    {
        object? result = null;
        await RunAsync(s, id, args, bind, async (cmd, token) =>
        {
            if (scalar)
            {
                result = await cmd.ExecuteScalarAsync(token);
            }
            else
            {
                await cmd.ExecuteNonQueryAsync(token);
            }
        }, ct);
        return result;
    }

    /// <summary>
    /// 카탈로그 문장 하나: <c>@PFX@</c> → <c>@KEY@</c> 채우기 → 세션 명령 잠금(D-041) → 명령. 취소는 서버 명령 취소 → <see cref="OperationCanceledException"/>
    /// (<see cref="OracleTableEditChecks"/> 와 같은 규칙).
    /// </summary>
    private static async Task RunAsync(
        ISession s, string id, (string Key, string Value)[] args, Action<OracleCommand> bind, Func<OracleCommand, CancellationToken, Task> run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var prefix = s.Server.Capabilities.CanQueryDictionary ? "dba" : "all";
        var sql = CatalogText.Fill(Catalog.Value.Get(id, s.Server).Sql.Replace("@PFX@", prefix, StringComparison.Ordinal), args);

        var gate = session.CommandGate;
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = sql;
            bind(cmd);
            await using var registration = ct.Register(static c => TryCancel((OracleCommand)c!), cmd);
            await run(cmd, ct);
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
            // 취소 신호를 보내지 못함
        }
    }
}
