using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Connection;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>접속 트리의 한 줄: 폴더(그룹) 또는 접속.</summary>
public sealed partial class ConnectionTreeNode : ObservableObject
{
    /// <summary>폴더면 접혀 있는지, 접속이면 편집 중인 값이 저장되지 않았는지를 화면이 그린다.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isModified;

    /// <summary>폴더 이름을 고치는 중(트리 줄이 글자 입력칸으로 바뀐다).</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>폴더 이름 입력칸의 글자.</summary>
    [ObservableProperty]
    private string _editText = string.Empty;

    /// <summary>끌어 놓을 대상으로 강조 중인 폴더 줄.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public ConnectionTreeNode(string title, ConnectionProfile? profile = null, string? path = null)
    {
        Title = title;
        Profile = profile;
        Path = path ?? string.Empty;
    }

    /// <summary>폴더면 이름(경로의 마지막 단계), 접속이면 접속 이름.</summary>
    public string Title { get; }

    /// <summary>폴더면 전체 경로(「부모/자식」), 접속이면 빈 글자.</summary>
    public string Path { get; }

    /// <summary>폴더면 하위 폴더까지 포함한 접속 수(트리를 채운 뒤 정한다).</summary>
    public int ProfileCount { get; set; }

    /// <summary>접속이면 그 프로필, 폴더면 null.</summary>
    public ConnectionProfile? Profile { get; }

    public ObservableCollection<ConnectionTreeNode> Children { get; } = [];

    public bool IsFolder => Profile is null;

    /// <summary>환경 칩 글자(색만으로 구분하지 않는다, ui-shell.md 4.1). 폴더면 빈 글자.</summary>
    public string EnvironmentLabel => Profile?.Environment switch
    {
        ConnectionEnvironment.Production => "운영",
        ConnectionEnvironment.Test => "테스트",
        ConnectionEnvironment.Development => "개발",
        _ => string.Empty,
    };

    /// <summary>환경 칩 바탕색(<c>#RRGGBB</c>). 운영은 항상 빨강, 나머지는 사용자 색 또는 환경 기본색.</summary>
    public string EnvironmentColor => Profile is null ? "#00000000" : LabelColorRules.Effective(Profile.Environment, Profile.Color);

    public bool HasChip => Profile is not null;

    /// <summary>화면 낭독기용 이름(운영은 「운영 접속」).</summary>
    public string AccessibleName => Profile is null
        ? Title
        : Profile.Environment == ConnectionEnvironment.Production ? $"{Title}, 운영 접속" : $"{Title}, {EnvironmentLabel}";

    public string Subtitle => Profile is null ? $"({ProfileCount})" : $"{Profile.User}@{Profile.DataSource}";

    /// <summary>마지막 접속 시각(짧게). 접속한 적이 없거나 폴더면 빈 글자.</summary>
    public string LastConnectedText => Profile?.LastConnectedAt is { } at ? FormatWhen(at, DateTimeOffset.Now) : string.Empty;

    /// <summary>마지막 접속 시각 전체(풍선 도움말).</summary>
    public string? LastConnectedTip => Profile?.LastConnectedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>오늘이면 <c>HH:mm</c>, 올해면 <c>MM-dd</c>, 그 전이면 <c>yyyy-MM-dd</c>(목록 폭이 좁아서 짧게 쓴다).</summary>
    public static string FormatWhen(DateTimeOffset when, DateTimeOffset now)
    {
        var local = when.ToOffset(now.Offset);
        var format = local.Date == now.Date ? "HH:mm" : local.Year == now.Year ? "MM-dd" : "yyyy-MM-dd";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }
}
