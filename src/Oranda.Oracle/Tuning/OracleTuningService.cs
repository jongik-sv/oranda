using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Core.Tuning.Providers;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.SqlPlus;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Oracle.Tuning;

/// <summary>
/// Oracle 튜닝 조회(PRD 11장, TSK-06-02): DBMS 공통 본문 <see cref="CatalogTuningService"/> 에 Oracle 질의 포트(ODP.NET)와 고리를 붙여 등록한다.
/// 세션 통계 = <c>v$mystat</c>, 트레이스 본문 = <c>v$diag_trace_file_contents</c>(12.2+), 직전 계획 해시 = <c>v$session</c>(tuning.prev_cursor),
/// 읽기 전용 트랜잭션 = <c>BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;</c>.
/// </summary>
[OrandaService(typeof(ITuningService), Dbms = Dbms.Oracle)]
internal sealed class OracleTuningService : CatalogTuningService
{
    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    public OracleTuningService(IExecutionService execution, IExecutionPlanService plans, ILanguageService language)
        : base(execution, plans, language, OracleSessionStatistics.Source)
    {
    }

    /// <summary>트레이스 본문을 SQL 로 못 읽는 이유(12.2 미만). 읽을 수 있으면 null.</summary>
    internal static string? TraceUnavailableReason(ServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var parts = server.Version.Split('.');
        var major = server.MajorVersion;
        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        if (major > 12 || (major == 12 && minor >= 2))
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"서버 {server.Version} 에서는 트레이스 본문을 SQL 로 읽을 수 없습니다(v$diag_trace_file_contents 는 12.2 이후). 서버의 파일을 직접 여세요.");
    }

    /// <summary>카탈로그 <c>tuning.*</c> 를 세션 연결로 읽는다(열 이름 포함). 팩이 모자라면 드라이버로 보내지 않는다.</summary>
    protected override async Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return await OracleCatalogQuery.QueryAsync(Catalog, s, id, binds, options, ct);
    }

    protected override bool IsNoAccess(Exception ex) => ex is OracleException oracle && OracleErrorClassifier.IsNoAccess(oracle);

    protected override string? TraceUnavailableReason(ISession s) => TraceUnavailableReason(s.Server);

    protected override async Task<long?> PreviousPlanHashAsync(ISession s, CancellationToken ct)
    {
        try
        {
            var rows = (await QueryAsync(s, "tuning.prev_cursor", CatalogBinds.None, TuningQueryOptions, ct)).Rows;
            return rows.Count == 0 ? null : Long(rows[0][2]);
        }
        catch (OracleException)
        {
            return null; // v$session 을 볼 권한이 없으면 계획 해시는 모른다.
        }
    }

    /// <summary>
    /// 읽기 전용 트랜잭션을 연다. SET TRANSACTION 은 트랜잭션의 첫 문장이어야 하므로 앞의 열린 트랜잭션(EXPLAIN PLAN 의 PLAN_TABLE 행 등)을 먼저 되돌린다.
    /// 되돌리는 것은 에이전트가 쓰는 모니터 세션에서만 한다(사용자 작업 세션의 미커밋 작업을 지우지 않게, 그 밖의 세션은 거부).
    /// </summary>
    protected override async Task BeginReadOnlyAsync(ISession s, CancellationToken ct)
    {
        var session = s as OracleSession ?? throw new ArgumentException("OracleSession 이 아닌 세션은 쓸 수 없습니다.", nameof(s));
        if (session.Purpose != SessionPurpose.Monitor)
        {
            throw new InvalidOperationException("읽기 전용 벤치마크는 에이전트(모니터) 세션에서만 실행합니다.");
        }

        var gate = session.CommandGate;
        await gate.EnterAsync(ct);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = "BEGIN ROLLBACK; SET TRANSACTION READ ONLY; END;";
            await cmd.ExecuteNonQueryAsync(ct);
            session.SetPending(false); // 앞의 미커밋 표시는 방금 되돌렸다
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
}
