using System.Data.Common;
using System.Globalization;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Results;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 열 형식 변경 자동 변환의 Tibero 검사 읽기(설계 18.9, T17b). Tibero 에는 <c>VALIDATE_CONVERSION</c> 이 없어(실측) 「변환 안 되는 행」은 늘 행 단위 시험 질의다:
/// PL/SQL 1차(모든 행을 새 형식 모음에 담아 봄 — 실패 없으면 0) → 실패하면 2차(행마다 담아 세고 앞 100행 ROWID). 2차가 시간 제한·오류로 끝나면 1차가 본 실패로 「1건 이상」.
/// 사용자 변환식은 바인드한 SELECT 한 문장(<see cref="ValueQuery"/>)으로만 블록에 들어간다(동적 SQL — 블록 문법으로 새지 않음). 블록 결과는 OUT 바인드로 받는다
/// (DBMS_OUTPUT 거두기와 같은 <see cref="TiberoBindBuilder"/> 경로). 취소는 서버 명령 취소 → <see cref="OperationCanceledException"/>(블록이 삼키지 않게 다시 던짐).
/// CHECK·PK·UK 를 변환한 값으로 세는 질의는 Oracle 카탈로그 <c>tableedit.conversion_*</c> 와 같은 꼴이다(Tibero 검사 읽기는 문장을 코드에 둠 — <see cref="TiberoTableEditChecks"/>).
/// 실측 = <c>docs/design/tibero-table-edit-probe.md</c> 「변환 검사」 장.
/// </summary>
[OrandaService(typeof(IColumnConversionChecks), Dbms = Dbms.Tibero)]
internal sealed partial class TiberoColumnConversionChecks : IColumnConversionChecks
{
    /// <summary>2차 검사가 모으는 ROWID 수(「그 행 보기」).</summary>
    internal const int MaxRowIds = 100;

    /// <summary>
    /// 블록 안 <c>WHEN OTHERS</c> 가 삼키지 않고 다시 던질 SQLCODE: 사용자 취소(드라이버 <c>Cancel</c> → TBR-12040)와 Oracle 호환 ORA-01013.
    /// </summary>
    internal const string CancelCodes = "-12040, -1013";

    /// <summary>
    /// 1차: 모든 행의 변환식 값을 새 형식 모음에 500행씩 담아 본다. 바인드 query·not_null / 출력 ok(1 = 모두 됨)·first_error.
    /// </summary>
    internal const string ScanBlock = """
        DECLARE
          TYPE value_list IS TABLE OF @TYPE@;
          vs value_list;
          c SYS_REFCURSOR;
          q VARCHAR2(32767) := :query;
          nn PLS_INTEGER := :not_null;
        BEGIN
          :ok := 1;
          OPEN c FOR q;
          LOOP
            FETCH c BULK COLLECT INTO vs LIMIT 500;
            IF nn = 1 THEN
              FOR i IN 1 .. vs.COUNT LOOP
                IF vs(i) IS NULL THEN
                  RAISE VALUE_ERROR;
                END IF;
              END LOOP;
            END IF;
            EXIT WHEN vs.COUNT < 500;
          END LOOP;
          CLOSE c;
        EXCEPTION
          WHEN OTHERS THEN
            IF SQLCODE IN (@CANCEL@) THEN
              RAISE;
            END IF;
            IF c%ISOPEN THEN
              CLOSE c;
            END IF;
            :ok := 0;
            :first_error := SUBSTR(SQLERRM, 1, 2000);
        END;
        """;

    /// <summary>
    /// 2차: 행마다 변환식 값(끝에 <c>ROWID = :rid</c>)을 새 형식 변수에 담아 실패한 행을 센다. 바인드 query·not_null·max_ids / 출력 failed·row_ids.
    /// </summary>
    internal const string RowsBlock = """
        DECLARE
          v @TYPE@;
          n PLS_INTEGER := 0;
          ids VARCHAR2(4000);
          q VARCHAR2(32767) := :query;
          nn PLS_INTEGER := :not_null;
          mx PLS_INTEGER := :max_ids;
        BEGIN
          FOR r IN (SELECT ROWID AS rid FROM @TABLE@) LOOP
            BEGIN
              EXECUTE IMMEDIATE q INTO v USING r.rid;
              IF nn = 1 AND v IS NULL THEN
                RAISE VALUE_ERROR;
              END IF;
            EXCEPTION
              WHEN OTHERS THEN
                IF SQLCODE IN (@CANCEL@) THEN
                  RAISE;
                END IF;
                n := n + 1;
                IF n <= mx AND NVL(LENGTHB(ids), 0) + 40 < 4000 THEN
                  ids := ids || ROWIDTOCHAR(r.rid) || ',';
                END IF;
            END;
          END LOOP;
          :failed := n;
          :row_ids := RTRIM(ids, ',');
        END;
        """;

    public Dbms Dbms => Dbms.Tibero;

    public async Task<ConversionFailures> FailingRowsAsync(
        ISession s, DbObjectRef table, ColumnDraft source, ColumnDraft target, string expression, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var type = CheckedType(TiberoTableEditChecks.TypeText(target));
        var notNull = target.IsNullable ? 0m : 1m;
        var scan = ValueQuery(table, expression);
        string? firstError;
        try
        {
            var outs = await BlockAsync(s, Fill(ScanBlock, table, type),
                [
                    ("query", new BindValue(BindDataType.Varchar2, scan)),
                    ("not_null", new BindValue(BindDataType.Number, notNull)),
                    ("ok", new BindValue(BindDataType.Number, null, BindDirection.Out)),
                    ("first_error", new BindValue(BindDataType.Varchar2, null, BindDirection.Out) { Size = 2000 }),
                ], ct).ConfigureAwait(false);
            if (Int(outs["ok"]) == 1)
            {
                return ConversionFailures.None;
            }

            firstError = TiberoSessionQuery.Str(outs["first_error"]);
            if (IsCancelError(firstError))
            {
                // 취소가 블록의 다른 오류 처리에 걸려 삼켜졌다 — 실패로 세지 않고 취소로 돌린다(⚠ 「확인 못 함」, 실행을 막지 않음).
                throw new OperationCanceledException("변환 검사 블록이 취소됐다: " + firstError, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && TiberoErrorClassifier.IsDriverException(ex) && s.State != SessionState.Broken)
        {
            return new ConversionFailures(1) { IsLowerBound = true, FirstError = ex.Message }; // 새 형식이 맞지 않음 등 블록 자체가 실패 — 모든 행이 실패한다
        }

        try
        {
            var outs = await BlockAsync(s, Fill(RowsBlock, table, type),
                [
                    ("query", new BindValue(BindDataType.Varchar2, scan + " WHERE t.ROWID = :rid")),
                    ("not_null", new BindValue(BindDataType.Number, notNull)),
                    ("max_ids", new BindValue(BindDataType.Number, (decimal)MaxRowIds)),
                    ("failed", new BindValue(BindDataType.Number, null, BindDirection.Out)),
                    ("row_ids", new BindValue(BindDataType.Varchar2, null, BindDirection.Out) { Size = 4000 }),
                ], ct).ConfigureAwait(false);
            var rowIds = (TiberoSessionQuery.Str(outs["row_ids"]) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var n = Int(outs["failed"]) ?? 0;
            return n == 0
                ? new ConversionFailures(1) { IsLowerBound = true, FirstError = firstError } // 1차와 2차 사이에 행이 바뀜 — 1차가 본 실패를 믿는다
                : new ConversionFailures(n) { FirstError = firstError, ViewRowsSql = RowIdSql(table, rowIds) };
        }
        catch (Exception ex) when (ex is OperationCanceledException || (TiberoErrorClassifier.IsDriverException(ex) && s.State != SessionState.Broken))
        {
            // 시간 제한(또는 사용자 취소)·2차 읽기 오류 — 1차가 이미 실패를 봤으므로 「1건 이상」. 사용자 취소면 부르는 쪽 다음 읽기가 취소를 던진다.
            return new ConversionFailures(1) { IsLowerBound = true, FirstError = firstError };
        }
    }

    public Task<ConversionFailures> ConvertedCheckViolationsAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, string condition, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        var source = ConvertedSource(table, otherColumns, column, expression);
        return CountConvertedAsync(s, $"SELECT COUNT(*) FROM {source} t WHERE NOT (\n{condition.Trim()}\n)", ct);
    }

    public Task<ConversionFailures> ConvertedDuplicateKeysAsync(
        ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, IReadOnlyList<string> keyColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("키 열이 없습니다.", nameof(keyColumns));
        }

        var source = ConvertedSource(table, otherColumns, column, expression);
        var keys = keyColumns.Select(k => "t." + SqlIdentifier.QuoteAlways(k)).ToList();
        var allNull = string.Join(" AND ", keys.Select(k => k + " IS NULL"));
        return CountConvertedAsync(s,
            $"SELECT NVL(SUM(n), 0) FROM (SELECT COUNT(*) AS n FROM {source} t WHERE NOT ({allNull}) GROUP BY {string.Join(", ", keys)} HAVING COUNT(*) > 1)",
            ct);
    }

    public async Task<IReadOnlyList<ForeignKeyRef>> ReferencingForeignKeysAsync(ISession s, DbObjectRef table, string column, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrEmpty(column);
        var pfx = TiberoSessionQuery.ViewPrefix(s);
        var sql = $"""
            SELECT DISTINCT c.owner, c.table_name, c.constraint_name
              FROM {pfx}_constraints c
              JOIN {pfx}_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name
              JOIN {pfx}_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name
             WHERE c.constraint_type = 'R'
               AND p.constraint_type IN ('P', 'U')
               AND p.owner = :owner
               AND p.table_name = :name
               AND pc.column_name = :column_name
             ORDER BY c.owner, c.table_name, c.constraint_name
            """;
        var result = await QueryAsync(s, sql, ct, ("owner", table.Owner), ("name", table.Name), ("column_name", column)).ConfigureAwait(false);
        return [.. result.Rows.Select(r => new ForeignKeyRef(TiberoSessionQuery.Str(r[0])!, TiberoSessionQuery.Str(r[1])!, TiberoSessionQuery.Str(r[2])!))];
    }

    /// <summary>
    /// 변환식 값 하나를 내는 SELECT 한 문장(바인드로 블록에 넘김). 사용자 원문 앞뒤는 줄을 바꾼다(끝 <c>--</c> 주석 대비).
    /// </summary>
    internal static string ValueQuery(DbObjectRef table, string expression) =>
        "SELECT (\n" + expression.Trim() + "\n) FROM " + Qualified(table) + " t";

    /// <summary>「그 행 보기」: 모은 ROWID 의 행. 없으면 null.</summary>
    internal static string? RowIdSql(DbObjectRef table, IReadOnlyList<string> rowIds) =>
        rowIds.Count == 0
            ? null
            : TableEditCheckSql.RowsSql(table, "t.ROWID IN (" + string.Join(", ", rowIds.Select(r => "CHARTOROWID(" + SqlLiteral.Quote(r) + ")")) + ")");

    internal static string Fill(string block, DbObjectRef table, string type) =>
        block.Replace("@TABLE@", Qualified(table), StringComparison.Ordinal)
             .Replace("@TYPE@", type, StringComparison.Ordinal)
             .Replace("@CANCEL@", CancelCodes, StringComparison.Ordinal);

    /// <summary>변환식 값을 옛 열 이름으로 낸 원천(다른 열 + 변환식 AS 열).</summary>
    private static string ConvertedSource(DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression)
    {
        ArgumentNullException.ThrowIfNull(otherColumns);
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var others = string.Concat(otherColumns.Select(c => "t." + SqlIdentifier.QuoteAlways(c) + ", "));
        return $"(SELECT {others}(\n{expression.Trim()}\n) AS {SqlIdentifier.QuoteAlways(column)} FROM {Qualified(table)} t)";
    }

    /// <summary>건수 질의. 계산 오류(변환식·조건이 새 값에서 맞지 않음)는 「1건 이상」 + 서버 오류.</summary>
    private static async Task<ConversionFailures> CountConvertedAsync(ISession s, string sql, CancellationToken ct)
    {
        try
        {
            var result = await QueryAsync(s, sql, ct).ConfigureAwait(false);
            var count = result.Rows.Count == 0 ? 0 : TiberoSessionQuery.Long(result.Rows[0][0]) ?? 0;
            return new ConversionFailures(count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && TiberoErrorClassifier.IsDriverException(ex) && s.State != SessionState.Broken)
        {
            return new ConversionFailures(1) { IsLowerBound = true, FirstError = ex.Message };
        }
    }

    /// <summary>블록이 돌려준 첫 오류가 사용자 취소인가(ODP/TBR-12040, ORA-01013).</summary>
    internal static bool IsCancelError(string? error) =>
        error is not null && (error.Contains("12040", StringComparison.Ordinal) || error.Contains("-01013", StringComparison.Ordinal) || error.Contains("-1013", StringComparison.Ordinal));

    /// <summary>
    /// 블록 DECLARE 에 그대로 들어가는 형식 글은 형식 표기 글자(영숫자·<c>_</c>·괄호·쉼표·공백·<c>*</c>)만 받는다 — 검사 읽기는 정책 관문 밖이라
    /// 자유 입력이 PL/SQL 로 새지 않게. 아니면 <see cref="ArgumentException"/>(부르는 쪽이 ⚠ 「확인 못 함」).
    /// </summary>
    internal static string CheckedType(string type) =>
        TypePattern().IsMatch(type) ? type : throw new ArgumentException("형식 표기가 아니다: " + type, nameof(type));

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_(), *]*\z")]
    private static partial System.Text.RegularExpressions.Regex TypePattern();

    private static string Qualified(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return SqlIdentifier.QuoteAlways(table.Owner) + "." + SqlIdentifier.QuoteAlways(table.Name);
    }

    private static int? Int(object? v) => v switch
    {
        null => null,
        string t when decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (int)d,
        string => null,
        _ => Convert.ToInt32(v, CultureInfo.InvariantCulture),
    };

    /// <summary>세션 읽기 경로로 질의. 취소가 오면 서버 명령을 취소하고 <see cref="OperationCanceledException"/>(<see cref="TiberoTableEditChecks"/> 와 같은 규칙).</summary>
    private static async Task<TiberoSessionQuery.Table> QueryAsync(ISession s, string sql, CancellationToken ct, params (string Name, object? Value)[] binds)
    {
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
            throw new OperationCanceledException("변환 검사 질의가 시간 제한·취소로 멈췄다.", ex, ct);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// PL/SQL 블록 하나를 OUT 바인드와 함께 보낸다(세션 명령 잠금 D-041, 드라이버 동기 호출은 스레드 풀). 돌려주는 값 = OUT 바인드 이름 → 값.
    /// 취소가 오면 서버 명령을 취소하고 <see cref="OperationCanceledException"/>. 끊김이면 세션을 Broken 으로 돌린다.
    /// </summary>
    private static async Task<Dictionary<string, object?>> BlockAsync(
        ISession s, string sql, IReadOnlyList<(string Name, BindValue Bind)> binds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var session = s as TiberoSession ?? throw new ArgumentException("TiberoSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없습니다.");
        ct.ThrowIfCancellationRequested();
        await session.CommandGate.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                await using var cmd = session.CreateCommand(sql);
                var outs = new List<(string Name, DbParameter Parameter, BindValue Bind)>();
                foreach (var (name, bind) in binds)
                {
                    var p = TiberoBindBuilder.Create(driver, cmd, name, bind);
                    cmd.Parameters.Add(p);
                    if (bind.Direction != BindDirection.In)
                    {
                        outs.Add((name, p, bind));
                    }
                }

                await using var registration = ct.Register(static state => TryCancel((DbCommand)state!), cmd);
                ct.ThrowIfCancellationRequested(); // 등록 전에 이미 취소됐으면 Cancel 이 듣지 않는다
                await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                return outs.ToDictionary(o => o.Name, o => TiberoValues.ReadOut(driver, o.Parameter, o.Bind), StringComparer.Ordinal);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (session.ReportFailure(ex))
        {
            throw; // 끊김(세션 Broken) — 취소로 바꾸지 않는다
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("변환 검사 블록이 시간 제한·취소로 멈췄다.", ex, ct);
        }
        finally
        {
            session.CommandGate.Release();
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
}
