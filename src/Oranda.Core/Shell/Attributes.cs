namespace Oranda.Core.Shell;

/// <summary>
/// 명령 처리기 자동 등록. <see cref="ICommandHandler"/> 구현 클래스에 단다. 한 처리기가 여러 명령을 맡으면 여러 번 단다
/// (<see cref="CommandContext.CommandId"/> 로 구분). 셸이 시작 때 어셈블리를 스캔해 모은다.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class OrandaCommandAttribute(string id, CommandCategory category) : Attribute
{
    public string Id { get; } = id;

    public CommandCategory Category { get; } = category;

    /// <summary>제목 리소스 키. null 이면 <c>Cmd.&lt;Id&gt;</c>.</summary>
    public string? Title { get; set; }

    public string? Icon { get; set; }

    public string? When { get; set; }

    public string? Enablement { get; set; }

    public string[]? Placements { get; set; }

    public string[]? Aliases { get; set; }

    public bool ShowInPalette { get; set; } = true;
}

/// <summary>
/// 도구 창·문서 종류 자동 등록. 도구 창 ViewModel 클래스에 단다. 셸이 시작 때 어셈블리를 스캔해 모은다.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OrandaToolPanelAttribute(string id, ToolPanelKind kind, DockLocation defaultLocation) : Attribute
{
    public string Id { get; } = id;

    public ToolPanelKind Kind { get; } = kind;

    public DockLocation DefaultLocation { get; } = defaultLocation;

    /// <summary>이름 리소스 키. null 이면 <c>Panel.&lt;Id&gt;</c>.</summary>
    public string? Title { get; set; }

    /// <summary>DIP. 0 이하 = 위치 기본값.</summary>
    public double DefaultSize { get; set; }

    public bool AllowMultiple { get; set; }

    public int Order { get; set; }

    public string? Icon { get; set; }

    public bool DevBuildOnly { get; set; }
}

/// <summary>상태 표시줄 구간 자동 등록. <see cref="IStatusBarItem"/> 구현 클래스에 단다.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class OrandaStatusBarItemAttribute(string id, int order) : Attribute
{
    public string Id { get; } = id;

    public int Order { get; } = order;

    /// <summary>0 = 숨기지 않음, 클수록 좁을 때 먼저 숨김.</summary>
    public int CollapseRank { get; set; }
}
