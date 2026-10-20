using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Language.Snippets;

namespace Oranda.App.Features.Templates;

/// <summary>템플릿 창 목록의 한 행(TreeView 두 단: 종류 묶음 → 항목). View 의 TreeViewItem 이 <see cref="IsExpanded"/> 를 묶는다.</summary>
public abstract partial class TemplateRow : ObservableObject
{
    /// <summary>행 앞 아이콘(<c>ShellIcons</c> 키).</summary>
    public abstract string IconKey { get; }

    /// <summary>펼침 상태. 항목 행은 펼칠 것이 없어 늘 false 다.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>종류마다 아이콘(거르기 단추·묶음 머리·항목 행이 같은 것을 쓴다).</summary>
    public static string IconOf(SnippetKind kind, bool isUser = false) => kind switch
    {
        SnippetKind.Snippet => isUser ? "docEdit" : "newPlsql",
        SnippetKind.Hint => "explainPlan",
        SnippetKind.Function => "objFunction",
        _ => "column",
    };
}

/// <summary>종류 묶음 머리(「조각 25」). 접으면 항목을 숨긴다.</summary>
public sealed partial class TemplateGroupRow : TemplateRow
{
    public TemplateGroupRow(SnippetKind kind)
    {
        Kind = kind;
        IsExpanded = true;
    }

    public SnippetKind Kind { get; }

    public override string IconKey => IconOf(Kind);

    /// <summary>종류 이름(화면 언어).</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>찾기·거르기를 지난 항목 수.</summary>
    [ObservableProperty]
    public partial int Count { get; set; }

    public ObservableCollection<TemplateItemRow> Items { get; } = [];
}

/// <summary>항목 한 줄: 아이콘, 줄임말(고정폭), 짧은 설명(흐리게). 도움말 풍선에 이름과 설명 전체. 찾는 낱말은 강조한다.</summary>
public sealed class TemplateItemRow(Snippet snippet, string summary, string toolTip, string highlight = "") : TemplateRow
{
    public Snippet Snippet { get; } = snippet;

    public string Prefix => Snippet.Prefix;

    /// <summary>줄임말 옆에 보일 짧은 글(<see cref="SnippetLibrary.Summary"/>).</summary>
    public string Summary { get; } = summary;

    public string ToolTip { get; } = toolTip;

    /// <summary>강조할 찾기 글(<see cref="MatchHighlight"/>).</summary>
    public string Highlight { get; } = highlight;

    public override string IconKey => IconOf(Snippet.Kind, Snippet.IsUser);
}
