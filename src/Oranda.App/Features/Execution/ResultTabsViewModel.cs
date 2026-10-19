using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public bool HasPane => Pane is not null;

    public bool HasMessage => Pane is null;
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

    /// <summary>탭을 모두 닫는다(결과 집합 정리 포함).</summary>
    public void Clear()
    {
        foreach (var t in Tabs.ToList())
        {
            Dispose(t);
        }

        Tabs.Clear();
        Selected = null;
    }

    /// <summary>질의 결과 탭을 더한다. 결과 집합의 수명은 탭(결과 영역)이 가진다.</summary>
    public ResultTabItem AddQuery(IResultSet result, string title, SqlStatement? statement, GridDisplayOptions? display = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var pane = new SqlResultPaneViewModel();
        pane.SetResult(result, display);
        var tab = new ResultTabItem { Title = title, Pane = pane, Statement = statement };
        pane.CloseRequested += (_, _) => Close(tab);
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
}
