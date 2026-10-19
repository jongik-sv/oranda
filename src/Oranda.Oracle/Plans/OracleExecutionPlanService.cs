using System.Globalization;
using System.Runtime.CompilerServices;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Monitoring;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Plans;

/// <summary>
/// 실행 계획(EXPLAIN PLAN·DISPLAY_CURSOR, 위험 단계 표시). 소유: TSK-07-01.
/// <para>예상 계획: <c>EXPLAIN PLAN</c> 문은 <see cref="IExecutionService"/>(정책 관문)로 보내고, 결과는 카탈로그 <c>plan.*</c>(모두 <c>license: none</c>)로 읽는다.
/// 실제 계획: <c>DBMS_XPLAN.DISPLAY_CURSOR</c>(팩 불필요). DISPLAY_AWR·ASH 는 쓰지 않는다.
/// 팩이 필요한 SQL 모니터 보고서는 <see cref="GetSqlMonitorReportAsync"/> 가 보유 선언·서버 설정과 대조해 모자라면 보내지 않는다.</para>
/// </summary>
[OrandaService(typeof(IExecutionPlanService))]
internal sealed class OracleExecutionPlanService : IExecutionPlanService
{
    /// <summary>예상·실제 행 수가 이 배수 이상 다르면 <see cref="PlanRisk.CardinalityMisestimate"/>.</summary>
    internal const double MisestimateFactor = 10;

    /// <summary>두 값 중 큰 쪽이 이 값 미만이면 오차를 강조하지 않는다(작은 수 잡음).</summary>
    internal const long MisestimateMinRows = 10;

    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    private static readonly ConditionalWeakTable<OracleSession, SemaphoreSlim> Gates = new();

    private readonly IExecutionService _execution;

    public OracleExecutionPlanService(IExecutionService execution)
    {
        _execution = execution;
    }

    public async Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(stmt);
        if (stmt.Kind is StatementKind.PlSql or StatementKind.SqlPlus or StatementKind.TransactionControl)
        {
            throw new ArgumentException($"{stmt.Kind} 문장은 예상 계획을 볼 수 없습니다.", nameof(stmt));
        }

        var statementId = "ORANDA_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var wrapped = new SqlStatement($"EXPLAIN PLAN SET STATEMENT_ID = '{statementId}' FOR {stmt.Text}", stmt.Range, StatementKind.Other)
        {
            Verb = "EXPLAIN",
            BindNames = stmt.BindNames,
            SubstitutionNames = stmt.SubstitutionNames,
        };

        // EXPLAIN PLAN 은 값 없이 분석만 하므로 바인드는 null 로 채운다.
        var binds = stmt.BindNames.ToDictionary(n => n, _ => new BindValue(BindDataType.Varchar2, null), StringComparer.OrdinalIgnoreCase);
        var result = await _execution.ExecuteAsync(s, wrapped, binds, new ExecutionOptions { Source = HistorySource.Editor, SkipHistory = true }, ct);
        switch (result.Status)
        {
            case ExecutionStatus.Denied:
                throw new UnauthorizedAccessException(result.Policy?.Message ?? "정책에 따라 예상 계획을 만들 수 없습니다.");
            case ExecutionStatus.Cancelled:
                throw new OperationCanceledException("예상 계획 생성이 취소되었습니다.");
            case ExecutionStatus.Failed:
                throw new InvalidOperationException(result.Error switch
                {
                    { } e when e.Number == 1456 || e.Message.Contains("ORA-01456", StringComparison.Ordinal) => $"{e.Code}: 읽기 전용 접속은 서버가 EXPLAIN PLAN 의 PLAN_TABLE 기록을 막습니다. 실제 계획(DISPLAY_CURSOR)을 쓰세요. {e.Message}",
                    { } e => $"{e.Code}: {e.Message}",
                    _ => "예상 계획 생성에 실패했습니다.",
                });
        }

        try
        {
            var steps = await QueryAsync(s, "plan.explain_steps", c => AddText(c, "stmt_id", statementId), ct);
            var text = await QueryAsync(s, "plan.explain_text", c =>
            {
                AddText(c, "stmt_id", statementId);
                AddText(c, "format", "TYPICAL");
            }, ct);

            var planSteps = BuildSteps(steps.Select(r => new RawStep(
                Int(r[0]) ?? 0, Int(r[1]), Str(r[3]) ?? string.Empty, Str(r[4]), Str(r[5]), Str(r[6]),
                Long(r[7]), Long(r[8]), null, null, null, Str(r[9]), Str(r[10]))));
            return new ExecutionPlan(planSteps, JoinLines(text))
            {
                PlanHashValue = steps.Count > 0 ? Long(steps[0][11]) : null,
            };
        }
        finally
        {
            // 세션 전용 임시 테이블이라 남아도 다른 사용자에게 보이지 않지만, 쌓이지 않게 지운다.
            await CleanupAsync(s, statementId);
        }
    }

    public async Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);

        var fmt = string.IsNullOrWhiteSpace(format) ? "TYPICAL" : format.Trim();
        var rows = await QueryAsync(s, "plan.cursor_steps", c =>
        {
            AddText(c, "sql_id", cursor.SqlId);
            AddInt(c, "inst_id", cursor.InstanceId);
            AddInt(c, "child", cursor.ChildNumber);
        }, ct);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"공유 풀에서 계획을 찾을 수 없습니다(밀려났을 수 있습니다): {cursor.SqlId}");
        }

        // 자식이 여럿이면 첫 (인스턴스, 자식) 의 단계만 쓴다.
        var inst = Int(rows[0][0]);
        var child = Int(rows[0][1]);
        var first = rows.Where(r => Int(r[0]) == inst && Int(r[1]) == child).ToList();
        var text = await QueryAsync(s, "plan.cursor_text", c =>
        {
            AddText(c, "sql_id", cursor.SqlId);
            AddInt(c, "child", child);
            AddText(c, "format", fmt);
        }, ct);

        var steps = BuildSteps(first.Select(r => new RawStep(
            Int(r[2]) ?? 0, Int(r[3]), Str(r[5]) ?? string.Empty, Str(r[6]), Str(r[7]), Str(r[8]),
            Long(r[9]), Long(r[10]), Long(r[11]), Long(r[12]), Long(r[13]), Str(r[14]), Str(r[15]))));
        return new ExecutionPlan(steps, JoinLines(text))
        {
            Cursor = new CursorRef(cursor.SqlId, child, cursor.InstanceId ?? inst),
            PlanHashValue = Long(first[0][16]),
        };
    }

    /// <summary>SQL 모니터 보고서(Tuning Pack). 보유 선언·서버 설정이 모자라면 드라이버로 보내지 않고 <see cref="UnauthorizedAccessException"/>.</summary>
    internal static async Task<string> GetSqlMonitorReportAsync(ISession s, CursorRef cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(cursor);

        var rows = await QueryAsync(s, "plan.monitor_report", c => AddText(c, "sql_id", cursor.SqlId), ct);
        return rows.Count > 0 ? Str(rows[0][0]) ?? string.Empty : string.Empty;
    }

    /// <summary>원자료 → 계획 단계(Id 순). 깊이는 부모 사슬로 구하고 위험 표시를 붙인다.</summary>
    internal static IReadOnlyList<PlanStep> BuildSteps(IEnumerable<RawStep> raw)
    {
        var list = raw.OrderBy(r => r.Id).ToList();
        var depth = new Dictionary<int, int>();
        var result = new List<PlanStep>(list.Count);
        foreach (var r in list)
        {
            var d = r.ParentId is { } p && depth.TryGetValue(p, out var pd) ? pd + 1 : 0;
            depth[r.Id] = d;
            result.Add(new PlanStep(r.Id, r.ParentId, d, r.Operation)
            {
                Options = r.Options,
                ObjectOwner = r.Owner,
                ObjectName = r.Name,
                Cost = r.Cost,
                EstimatedRows = r.EstimatedRows,
                ActualRows = r.ActualRows,
                Starts = r.Starts,
                ActualTime = r.ElapsedMicros is { } us ? TimeSpan.FromTicks(us * 10) : null,
                AccessPredicates = r.Access,
                FilterPredicates = r.Filter,
                Risk = ClassifyRisk(r),
            });
        }

        return result;
    }

    /// <summary>위험 단계 판정(PLAN-03): 테이블 전체 스캔, 카테시안 조인, 예상·실제 행 수 차이(시작 횟수당 실제 행 기준).</summary>
    internal static PlanRisk ClassifyRisk(RawStep r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var risk = PlanRisk.None;
        var options = r.Options ?? string.Empty;
        if (r.Operation.Equals("TABLE ACCESS", StringComparison.OrdinalIgnoreCase) && options.Contains("FULL", StringComparison.OrdinalIgnoreCase))
        {
            risk |= PlanRisk.FullScan;
        }

        if (options.Contains("CARTESIAN", StringComparison.OrdinalIgnoreCase))
        {
            risk |= PlanRisk.Cartesian;
        }

        if (r.ActualRows is { } actual && r.EstimatedRows is { } est)
        {
            var a = (double)actual / Math.Max(r.Starts ?? 1, 1);
            var e = Math.Max(est, 1);
            var big = Math.Max(a, e);
            var ratio = big / Math.Max(Math.Min(a, e), 1);
            if (big >= MisestimateMinRows && ratio >= MisestimateFactor)
            {
                risk |= PlanRisk.CardinalityMisestimate;
            }
        }

        return risk;
    }

    private static string JoinLines(List<object?[]> rows) =>
        string.Join(Environment.NewLine, rows.Select(r => Str(r[0]) ?? string.Empty));

    private static async Task<List<object?[]>> QueryAsync(ISession s, string id, Action<OracleCommand> bind, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        var query = Catalog.Value.Get(id, s.Server);
        OracleMonitorService.EnsureLicense(s, query.Header.License);

        var gate = Gates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.InitialLOBFetchSize = -1;
            cmd.CommandText = query.Sql;
            bind(cmd);

            var rows = new List<object?[]>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
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

    private static async Task CleanupAsync(ISession s, string statementId)
    {
        if (s is not OracleSession session)
        {
            return;
        }

        var gate = Gates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.BindByName = true;
            cmd.CommandText = Catalog.Value.Get("plan.explain_cleanup", s.Server).Sql;
            AddText(cmd, "stmt_id", statementId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (OracleException)
        {
            // 정리 실패는 계획 표시에 영향이 없다.
        }
        finally
        {
            gate.Release();
        }
    }

    private static void AddText(OracleCommand c, string name, string value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Varchar2) { Value = value });

    private static void AddInt(OracleCommand c, string name, int? value) =>
        c.Parameters.Add(new OracleParameter(name, OracleDbType.Int32) { Value = value is { } v ? v : DBNull.Value });

    private static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);

    private static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static long? Long(object? v) => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    /// <summary>계획 한 줄의 원자료. 행 수·시간은 <c>last_*</c>(마지막 실행) 값.</summary>
    internal sealed record RawStep(int Id, int? ParentId, string Operation, string? Options, string? Owner, string? Name,
                                   long? Cost, long? EstimatedRows, long? ActualRows, long? Starts, long? ElapsedMicros,
                                   string? Access, string? Filter);
}
