using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Execution;

/// <summary>
/// <c>oranda.panel.results</c> — 조회 결과 창. 활성 SQL 편집기 탭의 결과 영역(<see cref="SqlEditorViewModel.ResultPane"/>,
/// 결과·오류·스크립트 요약 탭)을 아래 도구 창 묶음(DBMS 출력·메시지 옆)에 보인다. 편집기는 세로를 다 쓰고, 결과는 탭으로 오간다.
/// 결과는 여전히 탭마다 따로 있다: 활성 탭이 바뀌면 그 탭의 결과로 바꿔 보인다.
/// </summary>
[OrandaToolPanel("oranda.panel.results", ToolPanelKind.Tool, DockLocation.Bottom, Order = 5)]
public sealed partial class ResultsPanelViewModel : ToolPanelViewModelBase, IDisposable
{
    private SqlEditorHost? _host;
    private SqlEditorViewModel? _tab;

    // 생성자는 매개변수 없는 이것 하나만 둔다: 컨테이너는 비공개 생성자까지 살피므로, 등록 안 된 형식(SqlEditorHost)을
    // 받는 생성자가 있으면 그 형식을 찾느라 지연 어셈블리를 훑는다(시작 시험이 막는다). 시험은 ForHost 로 호스트를 바꾼다.
    public ResultsPanelViewModel()
    {
        Attach(SqlEditorHost.Current); // 창은 처음 보일 때 만들어진다: 그 전에 실행한 결과도 바로 보인다
    }

    /// <summary>시험용: 지정한 편집기 호스트를 따라간다.</summary>
    internal static ResultsPanelViewModel ForHost(SqlEditorHost host)
    {
        var vm = new ResultsPanelViewModel();
        vm.Attach(host);
        return vm;
    }

    /// <summary>지금 보이는 결과 영역(없으면 null).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPane))]
    private object? _pane;

    public bool HasPane => Pane is not null;

    public void Dispose()
    {
        Attach(null);
    }

    private void Attach(SqlEditorHost? host)
    {
        if (_host is not null)
        {
            _host.ActiveChanged -= OnActiveChanged;
        }

        _host = host;
        if (_host is not null)
        {
            _host.ActiveChanged += OnActiveChanged;
        }

        Follow(_host?.Active);
    }

    private void OnActiveChanged(object? sender, EventArgs e) => Follow(_host?.Active);

    private void Follow(SqlEditorViewModel? tab)
    {
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _tab = tab;
        if (_tab is not null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        Pane = _tab?.ResultPane;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlEditorViewModel.ResultPane))
        {
            Pane = _tab?.ResultPane;
        }
    }
}
