using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// SQL 작업 공간 문서: 위에 작업 공간의 SQL 탭 줄, 아래에 고른 탭의 편집기. 편집기 View 는 탭마다 하나씩 만들어 두고
/// 탭을 바꿀 때 끼워 넣는다(View 를 새로 만들면 접기·스크롤 위치가 사라진다). 캐시는 이 View 인스턴스 것이다:
/// 도킹이 문서를 옮기며 작업 공간 View 를 새로 만들면 편집기 View 도 새로 만든다(한 컨트롤은 부모를 둘 가질 수 없다).
/// </summary>
public partial class SqlWorkspaceView : UserControl
{
    private readonly Dictionary<SqlEditorViewModel, SqlEditorView> _views = [];
    private SqlWorkspaceViewModel? _vm;

    public SqlWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as SqlWorkspaceViewModel);

        // 도킹이 문서를 떼었다 붙이면(분할·옮기기) 떨어져 있는 동안 구독을 풀어 둔다.
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AttachedToVisualTree += (_, _) => Bind(DataContext as SqlWorkspaceViewModel);
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
        }
    }

    private void Bind(SqlWorkspaceViewModel? vm)
    {
        Unsubscribe();
        if (!ReferenceEquals(_vm, vm))
        {
            _views.Clear(); // 다른 작업 공간이 들어왔다. 같은 작업 공간이 다시 붙은 것이면 편집기 View 를 그대로 쓴다.
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged += OnTabsChanged;
        }

        ShowTab(_vm?.ActiveTab);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.ActiveTab))
        {
            ShowTab(_vm?.ActiveTab);
        }
    }

    /// <summary>닫힌 탭의 편집기 View 는 버린다(작업 공간이 오래 열려 있어도 쌓이지 않게).</summary>
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var gone in _views.Keys.Where(t => _vm is null || !_vm.Tabs.Contains(t)).ToList())
        {
            _views.Remove(gone);
        }
    }

    private void ShowTab(SqlEditorViewModel? tab)
    {
        if (tab is null)
        {
            EditorHost.Content = null;
            return;
        }

        if (!_views.TryGetValue(tab, out var view))
        {
            view = new SqlEditorView { DataContext = tab };
            _views[tab] = view;
        }

        EditorHost.Content = view;

        // 탭을 고르거나 새로 열면 바로 칠 수 있게 편집기로 포커스를 옮긴다(보고 있는 작업 공간일 때만).
        if (_vm is { } vm && ReferenceEquals(vm.Host.ActiveWorkspace, vm))
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(EditorHost.Content, view) && TopLevel.GetTopLevel(view) is not null)
                    {
                        view.FocusEditor();
                    }
                },
                DispatcherPriority.Background);
        }
    }
}
