namespace Oranda.Core.Shell;

/// <summary><see cref="Tool"/> = 도킹 도구 창, <see cref="Document"/> = 문서 영역 탭 (ui-shell.md 2.2).</summary>
public enum ToolPanelKind
{
    Tool,
    Document,
}

/// <summary>도구 창 기본 위치 (ui-shell.md 2.1). 값 순서가 탑재 순서다.</summary>
public enum DockLocation
{
    Document,
    Left,
    Bottom,
    Right,
}

/// <summary>도구 창·문서 종류 하나의 등록 정보 (ui-shell.md 2.2 규칙).</summary>
public sealed record ToolPanelDefinition
{
    /// <summary><c>oranda.panel.&lt;camelCase&gt;</c>. 레이아웃 파일에 저장되므로 바꾸지 않는다.</summary>
    public required string Id { get; init; }

    /// <summary>이름 리소스 키. 기본 <c>Panel.&lt;Id&gt;</c>.</summary>
    public required string Title { get; init; }

    public required ToolPanelKind Kind { get; init; }

    public required DockLocation DefaultLocation { get; init; }

    /// <summary>ViewModel 형식. View 는 이름 규칙(<c>…ViewModel</c> → <c>…View</c>)으로 찾는다.</summary>
    public required Type ViewModelType { get; init; }

    /// <summary>기본 폭(왼쪽·오른쪽) 또는 높이(아래), DIP. null = 위치 기본값(왼쪽 280, 오른쪽 300, 아래 220).</summary>
    public double? DefaultSize { get; init; }

    /// <summary>여러 개 열기 허용(문서 종류).</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>같은 위치 안 탭 순서. 작을수록 앞.</summary>
    public int Order { get; init; }

    public string? Icon { get; init; }

    /// <summary>개발 빌드에서만 등록(확장 시험 패널).</summary>
    public bool DevBuildOnly { get; init; }
}

/// <summary>
/// 도구 창 레지스트리 (TRD 4.10·4.11). 셸은 이 목록으로 도킹 배치·보기 메뉴를 만든다.
/// 기능 Task 는 <see cref="OrandaToolPanelAttribute"/> 로 등록하고 셸 파일을 고치지 않는다.
/// </summary>
public interface IToolPanelRegistry
{
    /// <summary>탑재 순서: 위치(<see cref="DockLocation"/> 값 순) → <see cref="ToolPanelDefinition.Order"/> → ID.</summary>
    IReadOnlyList<ToolPanelDefinition> Panels { get; }

    ToolPanelDefinition? Find(string id);

    /// <summary>실행 중 등록(확장 패널). 반환값을 Dispose 하면 빠진다. ID 가 겹치면 <see cref="InvalidOperationException"/>.</summary>
    IDisposable Register(ToolPanelDefinition definition);

    event EventHandler? PanelsChanged;
}
