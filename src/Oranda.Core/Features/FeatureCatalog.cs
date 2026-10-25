using Oranda.Core.Settings;

namespace Oranda.Core.Features;

/// <summary>
/// 기능 카탈로그(TRD 10.2·10.4, PRD 12.2): 기능 25개의 묶음·문구 키·StartupBound·WritesOnly·Requires, Role 별 기본 켬 목록,
/// 꼬리표 규칙 표 셋(네임스페이스 접두·id 덮어쓰기·형 이름 덮어쓰기). 데이터와 순수 계산만 둔다 — 게이트(시작 때 값·PendingRestart·변경 알림)는
/// App 의 <c>FeatureGate</c>(TSK-07-02) 몫이다. Core 는 App 형을 참조하지 않으므로 네임스페이스·형 이름은 문자열로 적는다. 소유: TSK-07-01.
/// </summary>
public static class FeatureCatalog
{
    /// <summary>네임스페이스 꼬리표의 뿌리. 접두는 이 다음부터 적는다.</summary>
    public const string AppFeaturesNamespace = "Oranda.App.Features";

    private static readonly string[] None = [];

    /// <summary>기능 25개(<see cref="FeatureIds.All"/> 순서).</summary>
    public static IReadOnlyList<FeatureDefinition> Features { get; } =
    [
        new(FeatureIds.GridEdit, FeatureGroup.DevAssist, StartupBound: false, WritesOnly: true, None),
        new(FeatureIds.LobRowView, FeatureGroup.DevAssist, false, false, None),
        new(FeatureIds.DataExport, FeatureGroup.DevAssist, false, false, None),
        new(FeatureIds.DataImport, FeatureGroup.DevAssist, false, WritesOnly: true, None),
        new(FeatureIds.DedicatedSession, FeatureGroup.DevAssist, false, false, None),
        new(FeatureIds.Flashback, FeatureGroup.DevAssist, false, false, None),
        new(FeatureIds.ChangeJournal, FeatureGroup.DevAssist, StartupBound: true, false, None),
        new(FeatureIds.TableSpec, FeatureGroup.DevAssist, false, false, None),
        new(FeatureIds.MyBatisLog, FeatureGroup.DevAssist, false, false, None),
        new(FeatureIds.PlsqlEditor, FeatureGroup.Plsql, false, WritesOnly: true, None),
        new(FeatureIds.PlsqlWorkspace, FeatureGroup.Plsql, StartupBound: true, WritesOnly: true, [FeatureIds.PlsqlEditor]),
        new(FeatureIds.PlsqlRun, FeatureGroup.Plsql, false, WritesOnly: true, None),
        new(FeatureIds.MyBatis, FeatureGroup.MyBatis, StartupBound: true, false, None),
        new(FeatureIds.PlanAdvanced, FeatureGroup.Tuning, false, false, None),
        new(FeatureIds.SqlMonitor, FeatureGroup.Tuning, false, false, None),
        new(FeatureIds.TuningAssistant, FeatureGroup.Tuning, false, false, None),
        new(FeatureIds.ChildCursors, FeatureGroup.Tuning, false, false, None),
        new(FeatureIds.TableStats, FeatureGroup.Tuning, false, false, None),
        new(FeatureIds.Trace, FeatureGroup.Tuning, false, false, None),
        new(FeatureIds.TuningReport, FeatureGroup.Tuning, false, false, None),
        new(FeatureIds.SessionMonitor, FeatureGroup.Operations, false, false, None),
        new(FeatureIds.LockMonitor, FeatureGroup.Operations, false, false, None),
        new(FeatureIds.BlockerAlert, FeatureGroup.Operations, StartupBound: true, false, None),
        new(FeatureIds.DbTools, FeatureGroup.Operations, false, false, None),
        new(FeatureIds.ColumnReorder, FeatureGroup.Operations, false, WritesOnly: true, None),
    ];

    /// <summary>Role 6개와 기본 켬 기능(PRD 12.2 의 ○ 를 그대로 옮김).</summary>
    public static IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(RoleIds.SqlDev,
        [
            FeatureIds.GridEdit, FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.DataImport, FeatureIds.DedicatedSession,
            FeatureIds.Flashback, FeatureIds.ChangeJournal, FeatureIds.TableSpec, FeatureIds.PlanAdvanced, FeatureIds.BlockerAlert,
        ]),
        new(RoleIds.PlsqlDev,
        [
            FeatureIds.GridEdit, FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.DataImport, FeatureIds.DedicatedSession,
            FeatureIds.Flashback, FeatureIds.ChangeJournal, FeatureIds.PlsqlEditor, FeatureIds.PlsqlWorkspace, FeatureIds.PlsqlRun,
            FeatureIds.PlanAdvanced, FeatureIds.BlockerAlert, FeatureIds.DbTools,
        ]),
        new(RoleIds.MyBatisDev,
        [
            FeatureIds.GridEdit, FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.DataImport, FeatureIds.ChangeJournal,
            FeatureIds.MyBatisLog, FeatureIds.MyBatis, FeatureIds.PlanAdvanced, FeatureIds.BlockerAlert,
        ]),
        new(RoleIds.Tuner,
        [
            FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.DataImport, FeatureIds.DedicatedSession, FeatureIds.PlsqlEditor,
            FeatureIds.PlanAdvanced, FeatureIds.SqlMonitor, FeatureIds.TuningAssistant, FeatureIds.ChildCursors, FeatureIds.TableStats,
            FeatureIds.Trace, FeatureIds.TuningReport, FeatureIds.SessionMonitor, FeatureIds.LockMonitor, FeatureIds.BlockerAlert,
            FeatureIds.DbTools,
        ]),
        new(RoleIds.Dba,
        [
            FeatureIds.GridEdit, FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.DataImport, FeatureIds.DedicatedSession,
            FeatureIds.Flashback, FeatureIds.ChangeJournal, FeatureIds.TableSpec, FeatureIds.PlsqlEditor, FeatureIds.PlsqlRun,
            FeatureIds.PlanAdvanced, FeatureIds.SqlMonitor, FeatureIds.ChildCursors, FeatureIds.TableStats, FeatureIds.Trace,
            FeatureIds.SessionMonitor, FeatureIds.LockMonitor, FeatureIds.BlockerAlert, FeatureIds.DbTools, FeatureIds.ColumnReorder,
        ]),
        new(RoleIds.Viewer, [FeatureIds.LobRowView, FeatureIds.DataExport, FeatureIds.TableSpec]),
    ];

    /// <summary>
    /// 네임스페이스 접두 → 기능(TRD 10.2 첫째 표). 가장 긴 일치가 이긴다(<c>Tuning.Assistant</c> 가 <c>Tuning</c> 보다 먼저).
    /// <c>PlsqlWorkspace</c> 는 <c>plsqlEditor</c> 다(편집 탭이 그 문서 패널 안에 산다, 저장·복원만 plsqlWorkspace, TRD 10.7.3).
    /// <c>Tuning</c> 뿌리(<c>TuningCommands</c>)는 표에 없어 공통이다.
    /// </summary>
    public static IReadOnlyList<FeatureNamespaceTag> NamespaceTags { get; } =
    [
        new("GridEdit", FeatureIds.GridEdit),
        new("LobRowView", FeatureIds.LobRowView),
        new("DataTransfer", FeatureIds.DataExport),
        new("DedicatedSession", FeatureIds.DedicatedSession),
        new("Flashback", FeatureIds.Flashback),
        new("ChangeJournal", FeatureIds.ChangeJournal),
        new("TableSpec", FeatureIds.TableSpec),
        new("MyBatisLog", FeatureIds.MyBatisLog),
        new("PlsqlEditor", FeatureIds.PlsqlEditor),
        new("PlsqlWorkspace", FeatureIds.PlsqlEditor),
        new("PlsqlRun", FeatureIds.PlsqlRun),
        new("MyBatisWorkspace", FeatureIds.MyBatis),
        new("SqlMonitor", FeatureIds.SqlMonitor),
        new("Tuning.Assistant", FeatureIds.TuningAssistant),
        new("Tuning.Cursors", FeatureIds.ChildCursors),
        new("Tuning.Stats", FeatureIds.TableStats),
        new("Tuning.Trace", FeatureIds.Trace),
        new("Tuning.Report", FeatureIds.TuningReport),
        new("SessionMonitor", FeatureIds.SessionMonitor),
        new("LockMonitor", FeatureIds.LockMonitor),
        new("BlockerAlert", FeatureIds.BlockerAlert),
        new("DbTools", FeatureIds.DbTools),
        new("ColumnReorder", FeatureIds.ColumnReorder),
    ];

    /// <summary>
    /// 명령 id·패널 id 덮어쓰기(TRD 10.2 둘째 표). 값이 null 이면 공통(늘 켬) — <c>oranda.plsql.openSource</c> 는 PL/SQL 을 꺼도
    /// 남아 읽기 전용 SQL 탭으로 연다(D-007).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> IdOverrides { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["oranda.tools.import"] = FeatureIds.DataImport,
        ["oranda.plan.compare"] = FeatureIds.PlanAdvanced,
        ["oranda.plan.pinForCompare"] = FeatureIds.PlanAdvanced,
        ["oranda.plan.displayCursorOptions"] = FeatureIds.PlanAdvanced,
        ["oranda.plsql.saveWorkspaceFile"] = FeatureIds.PlsqlWorkspace,
        ["oranda.plsql.addToWorkspace"] = FeatureIds.PlsqlWorkspace,
        ["oranda.plsql.openSource"] = null,
    };

    /// <summary>형 전체 이름 덮어쓰기(TRD 10.2 둘째 표). App 밖 어셈블리의 시작 작업처럼 네임스페이스로 못 잡는 항목.</summary>
    public static IReadOnlyDictionary<string, string> TypeOverrides { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Oranda.Platform.Journal.ChangeJournalRecorder"] = FeatureIds.ChangeJournal,
    };

    private static readonly Dictionary<string, FeatureDefinition> ById = Features.ToDictionary(f => f.Id, StringComparer.Ordinal);
    private static readonly Dictionary<string, RoleDefinition> RoleById = Roles.ToDictionary(r => r.Id, StringComparer.Ordinal);

    // 가장 긴 접두가 먼저 맞게 길이 내림차순.
    private static readonly FeatureNamespaceTag[] TagsLongestFirst = [.. NamespaceTags.OrderByDescending(t => t.Prefix.Length)];

    /// <summary>기능 정의. 모르는 id 면 null.</summary>
    public static FeatureDefinition? Find(string? featureId) =>
        featureId is not null && ById.TryGetValue(featureId, out var f) ? f : null;

    /// <summary>Role 정의. 모르는 id 면 null.</summary>
    public static RoleDefinition? FindRole(string? roleId) =>
        roleId is not null && RoleById.TryGetValue(roleId, out var r) ? r : null;

    public static bool IsKnownFeature(string? featureId) => Find(featureId) is not null;

    public static bool IsKnownRole(string? roleId) => FindRole(roleId) is not null;

    /// <summary>묶음 이름 문구 키(<c>FeatureGroup.&lt;Group&gt;</c>).</summary>
    public static string GroupNameKey(FeatureGroup group) => FeatureTexts.GroupNamePrefix + group;

    /// <summary>
    /// 등록 항목(명령·도구 창·상태 칸·시작 작업)이 드는 기능(TRD 10.2). null = 공통(늘 켬). 처음 맞는 규칙에서 끝난다:
    /// id 덮어쓰기 → 형 전체 이름 덮어쓰기 → 네임스페이스 가장 긴 접두.
    /// </summary>
    /// <param name="registrationId">명령 id 또는 패널 id. 상태 칸·시작 작업은 null.</param>
    /// <param name="registeredType">명령 처리기 형·도구 창 ViewModel 형·상태 칸 항목 형·시작 작업 구현 형.</param>
    public static string? FeatureFor(string? registrationId, Type registeredType)
    {
        ArgumentNullException.ThrowIfNull(registeredType);
        return FeatureFor(registrationId, registeredType.FullName, registeredType.Namespace);
    }

    /// <summary><see cref="FeatureFor(string?, Type)"/> 의 문자열판(형을 로드하지 않고 이름만 있을 때).</summary>
    public static string? FeatureFor(string? registrationId, string? typeFullName, string? typeNamespace)
    {
        if (registrationId is not null && IdOverrides.TryGetValue(registrationId, out var byId))
        {
            return byId;
        }

        if (typeFullName is not null && TypeOverrides.TryGetValue(typeFullName, out var byType))
        {
            return byType;
        }

        if (typeNamespace is null || !typeNamespace.StartsWith(AppFeaturesNamespace + ".", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var tag in TagsLongestFirst)
        {
            var ns = tag.Namespace;
            if (typeNamespace.Length == ns.Length
                ? string.Equals(typeNamespace, ns, StringComparison.Ordinal)
                : typeNamespace.StartsWith(ns + ".", StringComparison.Ordinal))
            {
                return tag.FeatureId;
            }
        }

        return null;
    }

    /// <summary>
    /// 고른 Role 들의 기본 켬 합집합(TRD 10.5-1). <paramref name="roles"/> 가 null(Role 을 고른 적 없음)이면 모든 기능,
    /// 빈 목록이면 빈 집합(공통만). 모르는 Role id 는 건너뛴다.
    /// </summary>
    public static IReadOnlySet<string> RoleDefaults(IEnumerable<string>? roles)
    {
        if (roles is null)
        {
            return new HashSet<string>(FeatureIds.All, StringComparer.Ordinal);
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in roles)
        {
            if (FindRole(id) is { } role)
            {
                set.UnionWith(role.DefaultFeatures);
            }
        }

        return set;
    }

    /// <summary>조회 전용 Role 하나만 골랐나(D-006, TRD 10.5-3). 중복은 하나로 본다. null·빈 목록은 아니다.</summary>
    public static bool IsReadOnlyRole(IEnumerable<string>? roles)
    {
        if (roles is null)
        {
            return false;
        }

        var distinct = roles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 && distinct[0] == RoleIds.Viewer;
    }

    /// <summary>
    /// 설정의 켜진 기능(TRD 10.5 의 1~4): 바탕(<see cref="RoleDefaults"/>) + Enabled − Disabled(같은 id 가 둘 다면 끔이 이김) →
    /// 조회 전용만이면 WritesOnly 끔 → Requires 못 채움 끔. 결과는 카탈로그가 아는 id 만 담는다(모르는 id 는 무시).
    /// 5단계(StartupBound 는 시작 때 값과 AND·PendingRestart)는 시작 때 값을 아는 게이트가 이 결과 위에서 한다.
    /// </summary>
    /// <param name="settings">null 이면 섹션 없음 = 모든 기능.</param>
    public static IReadOnlySet<string> Resolve(FeaturesSettings? settings)
    {
        settings ??= new FeaturesSettings();
        var on = new HashSet<string>(RoleDefaults(settings.Roles), StringComparer.Ordinal);
        on.UnionWith(settings.Enabled.Where(IsKnownFeature));
        on.ExceptWith(settings.Disabled);

        if (IsReadOnlyRole(settings.Roles))
        {
            on.ExceptWith(Features.Where(f => f.WritesOnly).Select(f => f.Id));
        }

        // Requires 사슬이 길어져도 맞게 더 바뀌지 않을 때까지 돈다.
        bool removed;
        do
        {
            removed = false;
            foreach (var f in Features)
            {
                if (on.Contains(f.Id) && f.Requires.Any(r => !on.Contains(r)))
                {
                    on.Remove(f.Id);
                    removed = true;
                }
            }
        }
        while (removed);

        return on;
    }
}
