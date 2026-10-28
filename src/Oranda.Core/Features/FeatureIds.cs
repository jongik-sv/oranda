namespace Oranda.Core.Features;

/// <summary>
/// 기능 id 26개(PRD 12.2, D-002). 설정 <c>features.enabled</c>·<c>disabled</c> 와 문맥 키 <c>feature.&lt;id&gt;</c> 에 그대로 쓰이므로
/// 한 번 내보낸 값은 바꾸지 않는다. 소유: TSK-07-01(계약).
/// </summary>
public static class FeatureIds
{
    // 개발 보조
    public const string GridEdit = "gridEdit";
    public const string LobRowView = "lobRowView";
    public const string DataExport = "dataExport";
    public const string DataImport = "dataImport";
    public const string DedicatedSession = "dedicatedSession";
    public const string Flashback = "flashback";
    public const string ChangeJournal = "changeJournal";
    public const string TableSpec = "tableSpec";
    public const string MyBatisLog = "myBatisLog";

    // PL/SQL
    public const string PlsqlEditor = "plsqlEditor";
    public const string PlsqlWorkspace = "plsqlWorkspace";
    public const string PlsqlRun = "plsqlRun";

    // MyBatis
    public const string MyBatis = "myBatis";

    // 튜닝
    public const string PlanAdvanced = "planAdvanced";
    public const string SqlMonitor = "sqlMonitor";
    public const string TuningAssistant = "tuningAssistant";
    public const string ChildCursors = "childCursors";
    public const string TableStats = "tableStats";
    public const string Trace = "trace";
    public const string TuningReport = "tuningReport";

    /// <summary>AI 튜닝(실험). 직접 켜는 기능(<see cref="FeatureDefinition.OptIn"/>).</summary>
    public const string AiTuning = "aiTuning";

    // 운영
    public const string SessionMonitor = "sessionMonitor";
    public const string LockMonitor = "lockMonitor";
    public const string BlockerAlert = "blockerAlert";
    public const string DbTools = "dbTools";
    public const string ColumnReorder = "columnReorder";

    /// <summary>문맥 키 머리. 기능 id 를 붙여 <c>feature.myBatis</c> 처럼 쓴다(TSK-07-02 가 값을 넣는다).</summary>
    public const string ContextKeyPrefix = "feature.";

    /// <summary>모든 기능 id(PRD 12.2 표 순서).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        GridEdit, LobRowView, DataExport, DataImport, DedicatedSession, Flashback, ChangeJournal, TableSpec, MyBatisLog,
        PlsqlEditor, PlsqlWorkspace, PlsqlRun,
        MyBatis,
        PlanAdvanced, SqlMonitor, TuningAssistant, ChildCursors, TableStats, Trace, TuningReport, AiTuning,
        SessionMonitor, LockMonitor, BlockerAlert, DbTools, ColumnReorder,
    ];

    /// <summary>기능의 문맥 키 이름(<c>feature.&lt;id&gt;</c>).</summary>
    public static string ContextKey(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        return ContextKeyPrefix + featureId;
    }
}

/// <summary>Role id 6개(PRD 12.1). 설정 <c>features.roles</c> 에 저장되므로 바꾸지 않는다.</summary>
public static class RoleIds
{
    /// <summary>R1 SQL 개발자.</summary>
    public const string SqlDev = "sqlDev";

    /// <summary>R2 PL/SQL 개발자.</summary>
    public const string PlsqlDev = "plsqlDev";

    /// <summary>R3 MyBatis 개발자.</summary>
    public const string MyBatisDev = "myBatisDev";

    /// <summary>R4 DB 튜너.</summary>
    public const string Tuner = "tuner";

    /// <summary>R5 운영 DBA.</summary>
    public const string Dba = "dba";

    /// <summary>R6 조회 전용. 이것 하나만 고르면 실행을 막는다(D-006).</summary>
    public const string Viewer = "viewer";

    /// <summary>모든 Role id(PRD 12.1 표 순서).</summary>
    public static IReadOnlyList<string> All { get; } = [SqlDev, PlsqlDev, MyBatisDev, Tuner, Dba, Viewer];
}
