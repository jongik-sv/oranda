using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>항목 옆 배지 하나(<see cref="IMyBatisStatementBadgeProvider"/> 결과). 색은 View 가 <see cref="Kind"/> 별 테마 브러시로 그린다.</summary>
public sealed record MyBatisStatementBadgeView(string Text, string? ToolTip, MyBatisBadgeKind Kind)
{
    public bool IsInfo => Kind == MyBatisBadgeKind.Info;

    public bool IsWarning => Kind == MyBatisBadgeKind.Warning;

    public bool IsError => Kind == MyBatisBadgeKind.Error;

    /// <summary>좁은 상태 칸에 그리는 짧은 글(「조합 오류 3」 → 「3」).</summary>
    public string ShortText => Text[(Text.LastIndexOf(' ') + 1)..];
}

/// <summary>
/// 「문장」 창 트리 노드 하나: 종류별 묶음 머리(<see cref="IsGroup"/>)이거나 세션 요소 하나(<see cref="Entry"/>).
/// 요소 노드는 요소 객체로 짝짓는다(id 는 겹치거나 없을 수 있다). 표시 글은 ViewModel 이 채우고, 값이 바뀔 때만 알림이 나간다.
/// </summary>
public sealed partial class MyBatisStatementNode : ObservableObject
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _modifiedText = string.Empty;

    [ObservableProperty]
    private string _newText = string.Empty;

    [ObservableProperty]
    private string _usageText = string.Empty;

    [ObservableProperty]
    private string _keepText = string.Empty;

    [ObservableProperty]
    private string _ambiguousText = string.Empty;

    [ObservableProperty]
    private string? _toolTipText;

    [ObservableProperty]
    private double _rowOpacity = 1;

    [ObservableProperty]
    private IReadOnlyList<MyBatisStatementBadgeView> _badges = [];

    /// <summary>상태 칸의 「쓰는 곳」 수(sql 조각만, 값이 있을 때만 그린다).</summary>
    [ObservableProperty]
    private string _usageNumber = string.Empty;

    /// <summary>상태 칸 툴팁: 수정·새 쿼리·편집 불가·조합 오류·쓰는 곳의 뜻.</summary>
    [ObservableProperty]
    private string? _statusToolTip;

    /// <summary>id 칸에서 강조할 검색어(거르지 않으면 빈 글).</summary>
    [ObservableProperty]
    private string _highlight = string.Empty;

    internal MyBatisStatementNode(string title, MyBatisStatementKind kind, MyBatisStatementEntry? entry)
    {
        _title = title;
        Kind = kind;
        Entry = entry;
    }

    public MyBatisStatementKind Kind { get; }

    /// <summary>세션의 요소. 묶음 머리면 null.</summary>
    public MyBatisStatementEntry? Entry { get; }

    public bool IsGroup => Entry is null;

    /// <summary>격자 「종류」 칸의 짧은 배지 글(SEL·INS·UPD·DEL·SQL·MAP).</summary>
    public string KindText => Kind switch
    {
        MyBatisStatementKind.Select => "SEL",
        MyBatisStatementKind.Insert => "INS",
        MyBatisStatementKind.Update => "UPD",
        MyBatisStatementKind.Delete => "DEL",
        MyBatisStatementKind.Sql => "SQL",
        MyBatisStatementKind.ResultMap => "MAP",
        _ => "···",
    };

    /// <summary>요소 노드의 지금 id(id 바꾸기 뒤의 id). id 가 없는 요소와 묶음 머리는 null.</summary>
    public string? StatementId => Entry?.Id;

    public ObservableCollection<MyBatisStatementNode> Children { get; } = [];

    /// <summary>배지를 마지막으로 물어본 id(id 가 바뀌면 다시 묻는다).</summary>
    internal string? BadgedId { get; set; }

    /// <summary>묶음을 접고 펼칠 때 ViewModel 이 기억하도록 알린다.</summary>
    internal Action<MyBatisStatementNode>? ExpandedChangedCallback { get; set; }

    /// <summary>묶음 머리 앞의 접힘 표시.</summary>
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandGlyph));
        ExpandedChangedCallback?.Invoke(this);
    }
}
