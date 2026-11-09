using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Catalog;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Monitoring;
using Oranda.Core.Monitoring.Providers;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Monitoring;

/// <summary>
/// Oracle 세션·Lock·SQL 모니터: DBMS 공통 본문 <see cref="CatalogMonitorService"/> 에 Oracle 질의 포트(ODP.NET)와 고리를 붙여 등록한다.
/// 잠긴 행 값 읽기(LOB 머리)는 .Locks.cs. 소유: TSK-09-01·TSK-05-02·TSK-09-02.
/// <para>Kill = <c>ALTER SYSTEM KILL SESSION</c>(RAC 면 <c>,@inst</c>), 트레이스 = <c>DBMS_MONITOR</c>, 서버 오류 = <see cref="OracleException"/>,
/// 「볼 수 없음」 = <see cref="OracleErrorClassifier.IsNoAccess"/>(ORA-00942·01031·01039 등).</para>
/// </summary>
[OrandaService(typeof(IMonitorService), Dbms = Dbms.Oracle)]
internal sealed partial class OracleMonitorService : CatalogMonitorService
{
    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    public OracleMonitorService(IExecutionService execution)
        : base(execution)
    {
    }

    /// <summary><c>ALTER SYSTEM KILL SESSION 'sid,serial#[,@inst]' [IMMEDIATE]</c>. 위험 명령 확인 대상(재입력 이름 = 세션 식별).</summary>
    internal static SqlStatement KillStatement(SessionKey target, bool immediate, bool rac)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"{target.Sid},{target.Serial}");
        if (rac)
        {
            id += string.Create(CultureInfo.InvariantCulture, $",@{target.InstanceId}");
        }

        return new SqlStatement($"ALTER SYSTEM KILL SESSION '{id}'{(immediate ? " IMMEDIATE" : string.Empty)}", default, StatementKind.Other)
        {
            Verb = "ALTER SYSTEM",
            DangerousTarget = id,
        };
    }

    /// <summary><c>DBMS_MONITOR.SESSION_TRACE_ENABLE/DISABLE</c> 블록. 값은 숫자·불리언뿐이라 글자로 넣는다.</summary>
    internal static SqlStatement TraceStatement(SessionKey target, bool enable, TraceOptions options)
    {
        var sid = target.Sid.ToString(CultureInfo.InvariantCulture);
        var serial = target.Serial.ToString(CultureInfo.InvariantCulture);
        var text = enable
            ? $"BEGIN DBMS_MONITOR.SESSION_TRACE_ENABLE(session_id => {sid}, serial_num => {serial}, waits => {Bool(options.Waits)}, binds => {Bool(options.Binds)}); END;"
            : $"BEGIN DBMS_MONITOR.SESSION_TRACE_DISABLE(session_id => {sid}, serial_num => {serial}); END;";
        return new SqlStatement(text, default, StatementKind.PlSql) { Verb = "BEGIN" };

        static string Bool(bool b) => b ? "TRUE" : "FALSE";
    }

    /// <summary>권한·뷰 없음(ORA-00942·01031·01039). 이때는 빈 결과로 돌린다.</summary>
    internal static bool IsAccessError(OracleException ex) => OracleErrorClassifier.IsNoAccess(ex);

    /// <summary>카탈로그 <c>session.*</c>·<c>lock.*</c>·<c>sql.*</c> 쿼리를 모니터 세션 연결로 읽는다. 세션 명령 잠금(D-041)으로 한 번에 하나만 보낸다.</summary>
    protected override Task<CatalogRows> QueryAsync(ISession s, string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options, CancellationToken ct) =>
        OracleCatalogQuery.QueryAsync(Catalog, s, id, binds, options, ct);

    protected override bool IsServerError(Exception ex) => ex is OracleException;

    protected override bool IsNoAccess(Exception ex) => ex is OracleException oracle && IsAccessError(oracle);

    /// <summary>11.2 처럼 판이 없는 id(lock.deadlocks: v$diag_alert_ext 없음)는 거짓.</summary>
    protected override bool HasQuery(ISession monitor, string id)
    {
        try
        {
            Catalog.Value.Get(id, monitor.Server);
            return true;
        }
        catch (CatalogQueryNotFoundException)
        {
            return false;
        }
    }

    protected override SqlStatement KillStatement(ISession monitor, SessionKey target, bool immediate) =>
        KillStatement(target, immediate, monitor.Server.IsRac);

    protected override SqlStatement? TraceStatement(ISession monitor, SessionKey target, bool enable, TraceOptions options) =>
        TraceStatement(target, enable, options);

    protected override string? LockedRowSelect(ISession monitor, string owner, string table) => LockedRowSelect(owner, table);
}
