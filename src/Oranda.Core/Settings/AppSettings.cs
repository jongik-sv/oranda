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

    // 목록은 참조가 아니라 내용으로 비교한다(설정 구역별 변경 알림이 같은 값을 「바뀜」으로 보지 않게).
    public bool Equals(WorkspaceSettings? other) =>
        other is not null
        && AutosaveSeconds == other.AutosaveSeconds
        && RestoreOnStartup == other.RestoreOnStartup
        && LastWorkspaceId == other.LastWorkspaceId
        && OpenWorkspaceIds.SequenceEqual(other.OpenWorkspaceIds);

    public override int GetHashCode() => HashCode.Combine(AutosaveSeconds, RestoreOnStartup, LastWorkspaceId, OpenWorkspaceIds.Count);
}

public sealed record SchemaBrowserSettings
{
    /// <summary>객체를 한 번 누르면(방향키로 고르고 잠깐 멈추면) 미리보기 문서로 연다. 끄면 두 번 누르기·Enter 로만 연다.</summary>
    public bool SingleClickPreview { get; init; } = true;
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
