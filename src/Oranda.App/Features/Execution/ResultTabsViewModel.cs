using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.ResultGrid;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.Execution;

/// <summary>결과 탭 하나: 질의 결과(그리드)이거나 글 한 덩이(DML 영향 행·오류·DDL 완료).</summary>
public sealed partial class ResultTabItem : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>질의 결과를 보이는 영역(TSK-06-03). 글 탭이면 null.</summary>
    public SqlResultPaneViewModel? Pane { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool IsError { get; init; }

    /// <summary>이 탭을 만든 문장.</summary>
    public SqlStatement? Statement { get; init; }

    /// <summary>편집 모드로 열린 결과의 편집 상태(F9). 편집 모드가 아니면 null.</summary>
    public Oranda.App.Features.GridEdit.GridEditSession? EditSession { get; set; }

    /// <summary>
    /// 고정한 결과 탭(GRID-06, <c>oranda.grid.pinResultTab</c>). 다음 실행이 지우지 않고 결과를 새 탭에 연다.
    /// 탭 머리에 핀이 보이고, 핀을 누르거나 명령을 다시 쓰면 풀린다. 「결과 탭 닫기」로는 닫힌다.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    public bool HasPane => Pane is not null;

    public bool HasMessage => Pane is null;

    /// <summary>결과 탭 고정을 바꾼다(<c>oranda.grid.pinResultTab</c>).</summary>
    public void TogglePin() => IsPinned = !IsPinned;

    /// <summary>탭 머리의 핀 단추: 고정을 푼다.</summary>
    [RelayCommand]
    private void Unpin() => IsPinned = false;
}

/// <summary>
/// SQL 편집기 문서 아래 결과 영역(<c>SqlEditorViewModel.ResultPane</c> 에 꽂는다). 결과 탭 여러 개를 품고,
/// 맨 위 띠에 접속 라벨(운영/테스트·읽기 전용)을 보인다 — 편집기 문서 안쪽의 안전장치 표시 자리.
/// </summary>
public sealed partial class ResultTabsViewModel : ViewModelBase
{
    public ResultTabsViewModel(ConnectionProfile? profile = null)
    {
        Profile = profile;
    }

    public ConnectionProfile? Profile { get; private set; }

    public ObservableCollection<ResultTabItem> Tabs { get; } = [];

    [ObservableProperty]
    public partial ResultTabItem? Selected { get; set; }

    /// <summary>띠 글. 접속이 없으면 비어 있다.</summary>
    public string Banner
    {
        get
        {
            if (Profile is not { } p)
            {
                return string.Empty;
            }

            var env = p.Environment switch
            {
                ConnectionEnvironment.Production => "운영",
                ConnectionEnvironment.Test => "테스트",
                _ => string.Empty,
            };
            var parts = new List<string>();
            if (env.Length > 0)
            {
                parts.Add(env);
            }

            parts.Add(p.Name);
            if (p.ReadOnly)
            {
                parts.Add("읽기 전용");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasBanner => Banner.Length > 0 && (Profile is { Environment: not ConnectionEnvironment.Development } || Profile is { ReadOnly: true });

    public bool IsProduction => Profile?.Environment == ConnectionEnvironment.Production;

    public void SetProfile(ConnectionProfile? profile)
    {
        Profile = profile;
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Banner));
        OnPropertyChanged(nameof(HasBanner));
        OnPropertyChanged(nameof(IsProduction));
    }

    /// <summary>
    /// 고정하지 않은 탭을 모두 닫는다(결과 집합 정리 포함). 고정한 탭(<see cref="ResultTabItem.IsPinned"/>)은 남는다 —
    /// 실행이 결과를 바꿀 때 이것을 부르므로, 고정한 결과는 바뀌지 않고 다음 결과는 새 탭에 열린다.
    /// </summary>
    public void Clear()
    {
        foreach (var t in Tabs.Where(t => !t.IsPinned).ToList())
        {
            Tabs.Remove(t);
            Dispose(t);
        }

        Selected = Tabs.Count == 0 ? null : Tabs[^1];
    }

    /// <summary>
    /// 새 결과 탭 번호: 남은 탭 수 + 1. 고정한 탭이 이미 그 번호(「결과 N」)를 쓰고 있으면 쓰지 않은 다음 번호.
    /// </summary>
    public int NextOrdinal()
    {
        var n = Tabs.Count + 1;
        while (Tabs.Any(t => UsesOrdinal(t.Title, n)))
        {
            n++;
        }

        return n;
    }


    /// <summary>질의 결과 탭을 더한다. 결과 집합의 수명은 탭(결과 영역)이 가진다.</summary>
    public ResultTabItem AddQuery(IResultSet result, string title, SqlStatement? statement, GridDisplayOptions? display = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var pane = new SqlResultPaneViewModel { Statement = statement };
        pane.SetResult(result, display);
        var tab = new ResultTabItem { Title = title, Pane = pane, Statement = statement };
        pane.CloseRequested += (_, _) => Close(tab);
        pane.PinRequested += (_, _) => tab.TogglePin();
        pane.FilterApplied += (_, _) => LeaveEditMode(tab);
        Add(tab);
        return tab;
    }

    public ResultTabItem AddMessage(string title, string message, bool isError, SqlStatement? statement = null)
    {
        var tab = new ResultTabItem { Title = title, Message = message, IsError = isError, Statement = statement };
        Add(tab);
        return tab;
    }

    public void Close(ResultTabItem tab)
    {
        int i = Tabs.IndexOf(tab);
        if (i < 0)
        {
            return;
        }

        Tabs.RemoveAt(i);
        Dispose(tab);
        if (ReferenceEquals(Selected, tab))
        {
            Selected = Tabs.Count == 0 ? null : Tabs[Math.Min(i, Tabs.Count - 1)];
        }
    }

    private void Add(ResultTabItem tab)
    {
        Tabs.Add(tab);
        Selected = tab;
    }

    private static void Dispose(ResultTabItem tab) => tab.Pane?.Clear();

    /// <summary>거르기로 결과를 바꾸면 편집 모드(F9)는 풀린다: 탭의 편집 상태와 「(편집)」 표시를 지운다.</summary>
    private static void LeaveEditMode(ResultTabItem tab)
    {
        if (tab.EditSession is null)
        {
            return;
        }

        tab.EditSession = null;
        const string editing = " (편집)";
        if (tab.Title.EndsWith(editing, StringComparison.Ordinal))
        {
            tab.Title = tab.Title[..^editing.Length];
        }
    }

    private static bool UsesOrdinal(string title, int n)
    {
        var name = "결과 " + n.ToString(CultureInfo.InvariantCulture);
        return title == name || title.StartsWith(name + " ", StringComparison.Ordinal);
    }
}
