using Oranda.Core.Execution;
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

    /// <summary>
    /// 이 작업 공간이 쓸 세션(TSK-14-01): <c>own</c> = 자기 세션(첫 실행 때 연다), <c>shared</c> = 접속의 세션을 함께 쓴다,
    /// null = 설정 「워크스페이스마다 새 세션」 을 따른다. 세션 번호는 남기지 않는다(복원 때 세션을 미리 열지 않는다).
    /// </summary>
    public string? Session { get; init; }

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

    /// <summary>
    /// 이 탭의 SQL 이 참조하는 바인드 변수의 값(참조하는 이름 모두, 값이 없으면 NULL, SQL 에 처음 나온 순서).
    /// 없으면 null. 탭을 열면 이 값으로 바인드 변수 창의 줄을 만들거나 덮어쓴다.
    /// </summary>
    public IReadOnlyList<WorkspaceBind>? Binds { get; init; }
}

/// <summary>
/// 바인드 변수 값 하나(EXEC-03 바인드 변수 창 한 줄). 작업 공간 <c>tabs[].binds</c> 와 .sql 파일 끝 바인드 블록
/// (<see cref="SqlFileBinds"/>)이 같은 모양을 쓴다. 이름이 비밀로 보여도 값 그대로 만든다.
/// </summary>
public sealed record WorkspaceBind
{
    /// <summary>콜론 없는 이름(<c>:EMPNO</c> → <c>EMPNO</c>).</summary>
    public string Name { get; init; } = string.Empty;

    public BindDataType Type { get; init; } = BindDataType.Varchar2;

    public BindDirection Direction { get; init; } = BindDirection.In;

    /// <summary>값 칸의 글자 그대로. <see cref="IsNull"/> 이면 빈 문자열.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>값이 NULL(값 칸이 빔).</summary>
    public bool IsNull { get; init; }
}
