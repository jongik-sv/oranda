using Oranda.Core.Settings;

namespace Oranda.Core.Workspaces;

/// <summary>줄·열 위치(1부터).</summary>
public readonly record struct TextPosition(int Line, int Column);

/// <summary><c>workspaces/{id}.json</c>·<c>autosave/{id}.json</c> 루트(data-model 2.6).</summary>
public sealed record Workspace
{
    public int SchemaVersion { get; init; } = SchemaVersions.Workspace;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public DateTimeOffset SavedAt { get; init; }

    public Guid? ActiveTabId { get; init; }

    public IReadOnlyList<WorkspaceTab> Tabs { get; init; } = [];

    /// <summary>이 작업 공간을 연 Markdown 작업 공간 파일(.md) 또는 MyBatis 매퍼(.xml)의 절대 경로. 파일에서 열지 않았으면 null.</summary>
    public string? SourcePath { get; init; }

    public string Layout { get; init; } = "layout.json";
}

/// <summary>편집기 탭 1개. <see cref="Content"/> 는 저장 안 된 내용만.</summary>
public sealed record WorkspaceTab
{
    public Guid Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? FilePath { get; init; }

    public string? Content { get; init; }

    public bool IsDirty { get; init; }

    public string Encoding { get; init; } = "utf-8";

    public TextPosition Caret { get; init; } = new(1, 1);

    public TextPosition? SelectionStart { get; init; }

    public int ScrollLine { get; init; } = 1;

    public Guid? ConnectionProfileId { get; init; }
}
