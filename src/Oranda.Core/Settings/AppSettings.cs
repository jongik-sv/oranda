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

    public string LastWorkspaceId { get; init; } = "default";
}
