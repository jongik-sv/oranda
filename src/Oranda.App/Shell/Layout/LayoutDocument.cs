using System.Collections.Generic;
using Oranda.Core.Settings;

namespace Oranda.App.Shell.Layout;

/// <summary>
/// <c>layout.json</c> (data-model.md 2.5). 도킹 라이브러리 형식을 그대로 저장하지 않고 이 DTO 로 바꿔 저장한다.
/// 변환·저장·복원은 셸(TSK-03-01).
/// </summary>
public sealed record LayoutDocument
{
    /// <summary>현재 <see cref="SchemaVersions.Layout"/>.</summary>
    public int SchemaVersion { get; init; } = SchemaVersions.Layout;

    /// <summary>null = 화면 가운데 기본 크기.</summary>
    public WindowPlacement? Window { get; init; }

    /// <summary>null = 기본 배치(ui-shell.md 2.1).</summary>
    public DockNode? Root { get; init; }

    /// <summary>닫아 둔 도구 창 ID.</summary>
    public IReadOnlyList<string> HiddenTools { get; init; } = [];

    /// <summary>이름 붙인 레이아웃(<c>layouts/*.json</c>)의 이름. <c>layout.json</c> 에서는 쓰지 않는다(null 이면 쓰지 않는다).</summary>
    public string? Name { get; init; }
}

/// <summary>창 위치·크기(DIP).</summary>
public sealed record WindowPlacement
{
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public WindowStateKind State { get; init; } = WindowStateKind.Normal;

    /// <summary>화면 이름. 없거나 끊긴 화면이면 주 화면 가운데.</summary>
    public string? Screen { get; init; }
}

/// <summary>도킹 트리 노드. <see cref="DockNodeKind.Documents"/> 는 트리에 정확히 1개.</summary>
public sealed record DockNode
{
    public DockNodeKind Kind { get; init; }

    /// <summary><see cref="DockNodeKind.Split"/> 만.</summary>
    public DockOrientation Orientation { get; init; } = DockOrientation.Horizontal;

    /// <summary>부모 안 비율(0~1). null = 균등.</summary>
    public double? Proportion { get; init; }

    /// <summary><see cref="DockNodeKind.Split"/> 만.</summary>
    public IReadOnlyList<DockNode> Children { get; init; } = [];

    /// <summary><see cref="DockNodeKind.ToolGroup"/> 만. 도구 창 ID.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary><see cref="DockNodeKind.ToolGroup"/> 만. null = 첫 항목.</summary>
    public string? ActiveTool { get; init; }
}

public enum WindowStateKind
{
    Normal,
    Maximized,
    FullScreen,
}

public enum DockNodeKind
{
    Split,
    ToolGroup,
    Documents,
}

/// <summary>Avalonia <c>Orientation</c> 과 이름이 겹치지 않게 따로 둔다.</summary>
public enum DockOrientation
{
    Horizontal,
    Vertical,
}
