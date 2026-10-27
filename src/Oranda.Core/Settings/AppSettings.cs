using Oranda.Core.Execution;

namespace Oranda.Core.Settings;

public enum Theme
{
    System,
    Light,
    Dark,
}

/// <summary><c>settings.json</c> 루트(data-model 2.1). 섹션 이름(camelCase)은 <c>SettingsChanged.Section</c> 값과 같다.</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = SchemaVersions.Settings;

    public UiSettings Ui { get; init; } = new();

    public EditorSettings Editor { get; init; } = new();

    public string KeymapId { get; init; } = "default";

    public string FormatterProfileId { get; init; } = "oranda-default";

    public ExecutionSettings Execution { get; init; } = new();

    public GridSettings Grid { get; init; } = new();

    public HistorySettings History { get; init; } = new();

    public MetadataCacheSettings MetadataCache { get; init; } = new();

    public MonitorSettings Monitor { get; init; } = new();

    public WorkspaceSettings Workspace { get; init; } = new();

    public SchemaBrowserSettings SchemaBrowser { get; init; } = new();

    public UpdateSettings Update { get; init; } = new();

    /// <summary>테이블 레이아웃 팝업(객체 정보 보기)의 붙여 넣기 옵션(TSK-13-32).</summary>
    public TableLayoutSettings TableLayout { get; init; } = new();

    /// <summary>MyBatis 작업 공간(docs/oranda1, TSK-03-03).</summary>
    public MyBatisSettings MyBatis { get; init; } = new();

    /// <summary>Role·기능 구성(PRD 12, TSK-07-01). 키가 없는 옛 파일 = 모든 기능 켬. 새 섹션이라 스키마 버전은 그대로.</summary>
    public FeaturesSettings Features { get => _features; init => _features = value ?? new(); }

    // "features": null 인 손으로 고친 파일도 기본값으로 받는다.
    private readonly FeaturesSettings _features = new();
}

/// <summary>MyBatis 작업 공간 설정. 설정 창에는 없고 「바뀌는 부분」 창의 확인란이 바꾼다.</summary>
public sealed record MyBatisSettings
{
    /// <summary>매퍼에 저장할 때마다 「바뀌는 부분」 창을 먼저 보인다. 끄면 검사가 통과할 때 바로 쓴다.</summary>
    public bool ShowSaveDiffEveryTime { get; init; } = true;

    /// <summary>쿼리 탭 안 「펼친 SQL」 분할 방향(TSK-03-10). false = 세로 분할(편집기 오른쪽, 기본), true = 가로 분할(편집기 아래).</summary>
    public bool ExpandedHorizontal { get; init; }

    /// <summary>쿼리 탭 안에 「펼친 SQL」 을 보인다(분할 머리 줄의 숨김 토글).</summary>
    public bool ShowExpanded { get; init; } = true;

    /// <summary>원문 편집기가 차지하는 비율(0.15~0.85, 기본 0.5). 분할선을 끌어 놓으면 바뀐다.</summary>
    public double ExpandedRatio { get; init; } = 0.5;

    /// <summary>원문 편집기와 펼친 SQL 의 세로 스크롤을 서로 맞춘다(분할 단추 줄의 「스크롤 맞춤」 토글, 기본 켬).</summary>
    public bool SyncScroll { get; init; } = true;
}

/// <summary>테이블 레이아웃 팝업의 확인란 4개. 다음에 연 팝업에도 유지한다. 별칭은 표마다 달라 저장하지 않는다.</summary>
public sealed record TableLayoutSettings
{
    public bool InsertComma { get; init; }

    public bool LowerCase { get; init; }

    public bool ShowPk { get; init; }

    public bool ShowComment { get; init; }

    /// <summary>「감사 컬럼 빼기」 확인란(TSK-13-40). 켜면 <see cref="AuditColumns"/> 에 맞는 컬럼을 표에서 숨긴다.</summary>
    public bool HideAudit { get; init; }

    /// <summary>감사 컬럼 이름 목록(쉼표·줄로 나눔, 와일드카드 *·? 허용, 대소문자 무시). 설정 창 편집기 탭에서 정한다. 기본 비움.</summary>
    public string AuditColumns { get; init; } = string.Empty;
}

public sealed record UiSettings
{
    public Theme Theme { get; init; } = Theme.System;

    /// <summary><c>system</c> | <c>ko</c> | <c>en</c>.</summary>
    public string Language { get; init; } = "system";

    /// <summary>화면 글자 배율(0.8~2.5, 기본 1.0). 메뉴·대화상자·패널 글자가 함께 커진다(TSK-10-02).</summary>
    public double UiScale { get; init; } = 1.0;

    /// <summary>고대비 색 사용(OS 고대비 설정이 켜져 있으면 이 값과 상관없이 고대비로 그린다, TSK-10-02).</summary>
    public bool HighContrast { get; init; }
}

public sealed record EditorSettings
{
    /// <summary>null = 플랫폼 기본 고정폭.</summary>
    public string? FontFamily { get; init; }

    public double FontSize { get; init; } = 13;

    public int TabSize { get; init; } = 4;

    /// <summary>실행 전에 흔한 SQL 실수를 경고 밑줄로 알린다(SQL 점검 lint, TSK-13-04).</summary>
    public bool LintEnabled { get; init; } = true;
}

public sealed record ExecutionSettings
{
    public bool AutoCommit { get; init; }

    public bool DbmsOutput { get; init; } = true;

    public ScriptErrorMode ScriptErrorMode { get; init; } = ScriptErrorMode.Ask;

    public bool SplitOnBlankLine { get; init; }

    /// <summary>실제 계획(Ctrl+Shift+E)에 A-Rows 가 없으면 묻지 않고 <c>GATHER_PLAN_STATISTICS</c> 힌트 사본으로 다시 실행한다(TSK-06-04).</summary>
    public bool GatherPlanStatisticsHint { get; init; }
}

public sealed record GridSettings
{
    public int PageSize { get; init; } = 500;

    public int MemoryLimitMb { get; init; } = 200;

    public int InitialLobFetchSize { get; init; } = 4000;

    /// <summary>결과 그리드 글꼴. null 이면 기본 고정폭 글꼴 목록.</summary>
    public string? FontFamily { get; init; }

    /// <summary>결과 그리드 글자 크기(pt, 8~32). 화면 글자 배율을 곱하지 않는다.</summary>
    public double FontSize { get; init; } = 13;

    /// <summary>
    /// 머리글 정렬을 이 PC 에서 바로 하는 행 수의 문턱(<see cref="DefaultLocalSortMaxRows"/>). 이 수에 닿으면 「서버에서 ORDER BY 로 다시 조회」 등을 묻는다.
    /// </summary>
    public int LocalSortMaxRows { get; init; } = DefaultLocalSortMaxRows;

    public const int DefaultLocalSortMaxRows = 100_000;

    /// <summary>
    /// 결과 그리드 「플래시백 ▸ 이 행 변경 이력」(변경 추적하기, TSK-13-44)을 쓴다. 끄면 그 명령만 메뉴·우클릭·팔레트·단축키에서 숨는다.
    /// </summary>
    public bool FlashbackTrackEnabled { get; init; } = true;

    /// <summary>변경 추적 질의가 읽는 기간: 최근 N일(<see cref="FlashbackTrackDays"/>) 또는 가능한 전체 기간.</summary>
    public FlashbackTrackRange FlashbackTrackRange { get; init; } = FlashbackTrackRange.RecentDays;

    /// <summary>「최근 N일」 의 N(소수 허용, <see cref="MinFlashbackTrackDays"/>~<see cref="MaxFlashbackTrackDays"/>).</summary>
    public double FlashbackTrackDays { get; init; } = DefaultFlashbackTrackDays;

    public const double DefaultFlashbackTrackDays = 1.5;

    public const double MinFlashbackTrackDays = 0.01;

    public const double MaxFlashbackTrackDays = 365;
}

/// <summary>변경 추적 질의 기간(<c>grid.flashbackTrackRange</c>).</summary>
public enum FlashbackTrackRange
{
    /// <summary><c>VERSIONS BETWEEN TIMESTAMP SYSDATE - N AND MAXVALUE</c>.</summary>
    RecentDays,

    /// <summary><c>VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE</c>(DB 에 따라 ORA-08180).</summary>
    All,
}

public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;

    public bool SaveBindValues { get; init; }

    public int MaxEntries { get; init; } = 50000;

    public int MaxAgeDays { get; init; } = 180;
}

public sealed record MetadataCacheSettings
{
    public int RetentionDays { get; init; } = 30;
}

public sealed record MonitorSettings
{
    /// <summary>2~60.</summary>
    public int RefreshSeconds { get; init; } = 5;

    /// <summary>Lock 모니터 이력·임계치 알림(oranda1 TSK-05-04). 선택 필드라 스키마 버전은 그대로.</summary>
    public LockAlertSettings LockAlert { get; init; } = new();
}

/// <summary>Lock 모니터 「알림 설정」 플라이아웃 값. 화면이 범위를 맞춘다(대기 1~86400 초, 대기자 1~1000 명, 보관 5~720 분).</summary>
public sealed record LockAlertSettings
{
    /// <summary>임계치 알림 켜기(기본 켬).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>가장 오래 기다린 세션이 이 초 이상이면 알림.</summary>
    public int WaitSeconds { get; init; } = 120;

    /// <summary>한 보유자 뒤 대기자가 이 수 이상이면 알림.</summary>
    public int Waiters { get; init; } = 5;

    /// <summary>알림 때 보유자·대기자 SQL 스냅샷을 앱 데이터 폴더 lock-history 에 텍스트로 남긴다.</summary>
    public bool SaveSnapshot { get; init; } = true;

    /// <summary>Lock 모니터 탭이 뒤에 있어도 창 오른쪽 아래 알림을 띄운다.</summary>
    public bool NotifyWhenHidden { get; init; } = true;

    /// <summary>이력 띠 보관 분(앱 메모리).</summary>
    public int HistoryMinutes { get; init; } = 30;

    /// <summary>이력 점을 lock-history 폴더 CSV 로도 남긴다(기본 끔).</summary>
    public bool SaveHistoryFile { get; init; }
}

public sealed record WorkspaceSettings
{
    /// <summary>1~10.</summary>
    public int AutosaveSeconds { get; init; } = 10;

    public bool RestoreOnStartup { get; init; } = true;

    /// <summary>마지막으로 보던(활성) 작업 공간.</summary>
    public string LastWorkspaceId { get; init; } = "default";

    /// <summary>종료할 때 열려 있던 작업 공간들(열린 순서). 다음 시작 때 모두 다시 연다.</summary>
    public IReadOnlyList<string> OpenWorkspaceIds { get; init; } = ["default"];

    /// <summary>
    /// 워크스페이스마다 새 세션(TSK-14-01, 기본 켬): 같은 DB 를 쓰는 작업 공간이 여럿이면 첫 실행 때 각자 세션을 열어 서로 기다리지 않는다.
    /// 끄면 지금처럼 접속의 세션 하나를 함께 쓴다. 작업 공간마다 고른 값(작업 공간 파일)이 있으면 그것이 먼저다.
    /// </summary>
    public bool SessionPerWorkspace { get; init; } = true;

    // 목록은 참조가 아니라 내용으로 비교한다(설정 구역별 변경 알림이 같은 값을 「바뀜」으로 보지 않게).
    public bool Equals(WorkspaceSettings? other) =>
        other is not null
        && AutosaveSeconds == other.AutosaveSeconds
        && RestoreOnStartup == other.RestoreOnStartup
        && SessionPerWorkspace == other.SessionPerWorkspace
        && LastWorkspaceId == other.LastWorkspaceId
        && OpenWorkspaceIds.SequenceEqual(other.OpenWorkspaceIds);

    public override int GetHashCode() => HashCode.Combine(AutosaveSeconds, RestoreOnStartup, LastWorkspaceId, OpenWorkspaceIds.Count);
}

public sealed record SchemaBrowserSettings
{
    /// <summary>객체를 한 번 누르면(방향키로 고르고 잠깐 멈추면) 미리보기 문서로 연다. 끄면 두 번 누르기·Enter 로만 연다.</summary>
    public bool SingleClickPreview { get; init; } = true;

    /// <summary>이름 붙여 저장한 트리 필터(오렌지 Schema Browser Filter, TSK-13-10). 모든 접속이 같이 쓴다.</summary>
    public IReadOnlyList<SchemaBrowserFilter> Filters { get => _filters; init => _filters = value ?? []; }

    /// <summary>접속 프로필 id → 그 접속에서 마지막으로 고른 필터 id. 없으면 필터 없음.</summary>
    public IReadOnlyDictionary<string, string> ActiveFilters { get => _activeFilters; init => _activeFilters = value ?? EmptyMap; }

    // 이 키가 생기기 전의 settings.json(구역은 있고 키는 없음)은 소스 생성 직렬화기가 null 을 넣으므로 빈 값으로 받는다.
    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>();
    private readonly IReadOnlyList<SchemaBrowserFilter> _filters = [];
    private readonly IReadOnlyDictionary<string, string> _activeFilters = EmptyMap;

    // 목록·사전은 참조가 아니라 내용으로 비교한다(설정 구역별 변경 알림이 같은 값을 「바뀜」으로 보지 않게).
    public bool Equals(SchemaBrowserSettings? other) =>
        other is not null
        && SingleClickPreview == other.SingleClickPreview
        && Filters.SequenceEqual(other.Filters)
        && ContentEquality.SameEntries(ActiveFilters, other.ActiveFilters);

    public override int GetHashCode() => HashCode.Combine(SingleClickPreview, Filters.Count, ActiveFilters.Count);
}

/// <summary>저장 필터의 스키마 범위(오렌지 필터 Schema 탭).</summary>
public enum SchemaFilterScope
{
    /// <summary>모든 스키마.</summary>
    All,

    /// <summary>접속한 사용자의 스키마만.</summary>
    LogonSchema,

    /// <summary>객체를 하나 이상 가진 스키마만.</summary>
    SchemasWithObjects,

    /// <summary><see cref="SchemaBrowserFilter.Schemas"/> 에 고른 스키마만.</summary>
    Selected,

    /// <summary><see cref="SchemaBrowserFilter.SchemaPattern"/> 정규식에 맞는 스키마만(대소문자 무시, 일부 일치).</summary>
    Pattern,
}

/// <summary>스키마 브라우저 저장 필터 하나. 스키마 범위 · 숨길 객체 유형 · 유형별 이름 패턴 · 패턴 옵션.</summary>
public sealed record SchemaBrowserFilter
{
    /// <summary>바뀌지 않는 식별자(접속별 선택이 이름 바꾸기에 끊기지 않게).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "";

    public SchemaFilterScope Scope { get; init; } = SchemaFilterScope.All;

    /// <summary><see cref="SchemaFilterScope.Selected"/> 일 때 보일 스키마.</summary>
    public IReadOnlyList<string> Schemas { get => _schemas; init => _schemas = value ?? []; }

    /// <summary><see cref="SchemaFilterScope.Pattern"/> 일 때 스키마 이름에 거는 .NET 정규식. 옛 설정 파일에는 없어 null.</summary>
    public string? SchemaPattern { get; init; }

    /// <summary>트리에서 숨길 객체 유형(ALL_OBJECTS.OBJECT_TYPE). 목록에 없는 새 유형은 보인다.</summary>
    public IReadOnlyList<string> HiddenTypes { get => _hiddenTypes; init => _hiddenTypes = value ?? []; }

    /// <summary>객체 유형 → 이름 패턴(<c>*</c>·<c>%</c> 와일드카드). 없거나 비면 모두.</summary>
    public IReadOnlyDictionary<string, string> NamePatterns { get => _namePatterns; init => _namePatterns = value ?? new Dictionary<string, string>(); }

    /// <summary><c>_</c> 를 한 글자 와일드카드로 본다. 끄면 글자 그대로.</summary>
    public bool UnderscoreIsWildcard { get; init; }

    /// <summary>패턴에 와일드카드가 없으면 앞뒤에 <c>%</c> 를 붙여 포함 검색한다. 끄면 이름 전체가 같아야 한다.</summary>
    public bool WrapWithPercent { get; init; } = true;

    // 키가 빠진 JSON 에서 직렬화기가 넣는 null 은 빈 값으로 받는다.
    private readonly IReadOnlyList<string> _schemas = [];
    private readonly IReadOnlyList<string> _hiddenTypes = [];
    private readonly IReadOnlyDictionary<string, string> _namePatterns = new Dictionary<string, string>();

    public bool Equals(SchemaBrowserFilter? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Scope == other.Scope
        && Schemas.SequenceEqual(other.Schemas)
        && string.Equals(SchemaPattern, other.SchemaPattern, StringComparison.Ordinal)
        && HiddenTypes.SequenceEqual(other.HiddenTypes)
        && ContentEquality.SameEntries(NamePatterns, other.NamePatterns)
        && UnderscoreIsWildcard == other.UnderscoreIsWildcard
        && WrapWithPercent == other.WrapWithPercent;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Scope, Schemas.Count, HiddenTypes.Count, NamePatterns.Count);
}

internal static class ContentEquality
{
    public static bool SameEntries(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
}

/// <summary>자동 업데이트(TSK-10-01). 확인은 원격 전송 동의 대상이 아니라서 끄는 길만 둔다(PRD 「원격 전송(업데이트 확인 제외)」).</summary>
public sealed record UpdateSettings
{
    /// <summary>메인 창이 뜬 뒤 백그라운드로 새 버전을 확인한다(24시간에 한 번).</summary>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary>새 버전을 찾으면 백그라운드로 내려받는다. 끄면 「있음」만 알린다.</summary>
    public bool AutoDownload { get; init; } = true;

    /// <summary>피드 주소: http(s) 주소, 폴더 경로(UNC 포함), <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c>. null·빈 값 = 빌드 때 넣은 기본값.</summary>
    public string? FeedUrl { get; init; }

    /// <summary>시작 확인에서 알리지 않을 버전(「이 버전 건너뛰기」).</summary>
    public string? SkippedVersion { get; init; }

    /// <summary>마지막으로 확인을 끝낸 시각(UTC). 시작 확인의 24시간 규칙에 쓴다.</summary>
    public DateTimeOffset? LastCheckedUtc { get; init; }
}
