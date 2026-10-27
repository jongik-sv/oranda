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

    /// <summary>
    /// 닫을 수 있는 탭(외래키 따라가기·플래시백으로 연 결과). 탭 머리에 닫기(×) 단추와 문맥 메뉴 「결과 탭 닫기」가 생긴다.
    /// 원래 질의 결과 탭은 닫을 수 없다(다음 실행이 바꾼다).
    /// </summary>
    public bool IsClosable { get; init; }

    /// <summary>탭을 품은 결과 영역이 닫기 단추를 이어 주는 자리.</summary>
    internal Action<ResultTabItem>? CloseHandler { get; set; }

    public bool HasPane => Pane is not null;

    public bool HasMessage => Pane is null;

    /// <summary>결과 탭 고정을 바꾼다(<c>oranda.grid.pinResultTab</c>).</summary>
    public void TogglePin() => IsPinned = !IsPinned;

    /// <summary>탭 머리의 닫기(×) 단추·문맥 메뉴: 이 탭을 닫고 왼쪽 이웃 탭을 보인다.</summary>
    [RelayCommand]
    private void CloseTab()
    {
        if (IsClosable)
        {
            CloseHandler?.Invoke(this);
        }
    }

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
        _runBar.PropertyChanged += OnRunBarChanged;
    }

    public ConnectionProfile? Profile { get; private set; }

    public ObservableCollection<ResultTabItem> Tabs { get; } = [];

    private readonly DelayedBusyFlag _runBar = new();
    private bool _isRunning;

    /// <summary>
    /// 이 결과 영역의 문장이 실행 중이다(첫 페치 전까지 포함). 컨트롤러가 켜고 끈다 — 끝·오류·취소 모두에서 꺼진다.
    /// 결과 영역 위쪽 가장자리의 얇은 막대가 이것을 따른다(<see cref="IsRunBarVisible"/>).
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                if (value)
                {
                    Run.Begin();
                }
                else
                {
                    Run.End();
                }

                _runBar.Set(value);
            }
        }
    }

    /// <summary>결과 영역 위 「실행 중 · 경과 n초」 띠의 표지(TSK-14-13). <see cref="IsRunning"/> 을 따른다.</summary>
    public Oranda.App.Shell.RunMarker Run { get; } = new();

    /// <summary>실행 중 막대를 보인다(<see cref="RunBarDelay"/> 넘게 이어질 때만 — 짧은 쿼리는 깜빡이지 않는다).</summary>
    public bool IsRunBarVisible => _runBar.Visible;

    /// <summary>막대가 보이기까지의 지연(기본 150ms, 시험이 줄인다).</summary>
    public TimeSpan RunBarDelay
    {
        get => _runBar.Delay;
        set => _runBar.Delay = value;
    }

    private void OnRunBarChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(IsRunBarVisible));

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
    /// 고정하지 않은 오류 탭(실패·거부·접속 없음 안내 등)만 닫는다. 새 문장을 실행하기 시작할 때 부른다 —
    /// 실행 중에 지난 실행의 오류가 그대로 보여 계속 오류가 나는 것처럼 보이지 않게. 정상 결과·고정한 탭은 건드리지 않는다.
    /// </summary>
    public void ClearErrors()
    {
        var errors = Tabs.Where(t => t.IsError && !t.IsPinned).ToList();
        if (errors.Count == 0)
        {
            return;
        }

        foreach (var t in errors)
        {
            Close(t);
        }
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
    public ResultTabItem AddQuery(IResultSet result, string title, SqlStatement? statement, GridDisplayOptions? display = null, bool closable = false,
                                  IReadOnlyDictionary<string, BindValue>? runBinds = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var pane = new SqlResultPaneViewModel { Statement = statement, RunBinds = runBinds };
        pane.SetResult(result, display);
        var tab = new ResultTabItem { Title = title, Pane = pane, Statement = statement, IsClosable = closable };
        tab.CloseHandler = CloseToLeft;
        pane.CloseRequested += (_, _) => Close(tab);
        pane.PinRequested += (_, _) => tab.TogglePin();
        pane.FilterApplied += (_, _) => LeaveEditMode(tab);
        pane.RelatedRowsOpened += (_, related) => AddRelated(related, pane);
        Add(tab);
        return tab;
    }

    /// <summary>
    /// 외래키 따라가기(GRID-11)로 받은 부모·자식 행 결과를 새 탭에 연다(「부모: DEPT」·「자식: EMP(FK_EMP_DEPT)」).
    /// 그 문장이 새 결과 영역의 문장이 되고, 문장의 바인드 값은 새 결과 영역이 가진다 — 거르기 줄·전체 행 수 세기가 그 위에서 그대로 된다.
    /// </summary>
    public ResultTabItem AddRelated(RelatedRowsResult related, SqlResultPaneViewModel? source = null)
    {
        ArgumentNullException.ThrowIfNull(related);
        var tab = AddQuery(related.Result, related.Title, related.Statement, source?.Model?.Display, closable: true);
        tab.Pane!.UseStatementBinds(related.Binds);
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

    /// <summary>닫기 단추로 닫으면 활성 탭은 왼쪽 이웃(없으면 새 첫 탭)이 된다.</summary>
    private void CloseToLeft(ResultTabItem tab)
    {
        int i = Tabs.IndexOf(tab);
        bool wasSelected = ReferenceEquals(Selected, tab);
        Close(tab);
        if (wasSelected && Tabs.Count > 0)
        {
            Selected = Tabs[Math.Max(i - 1, 0)];
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
