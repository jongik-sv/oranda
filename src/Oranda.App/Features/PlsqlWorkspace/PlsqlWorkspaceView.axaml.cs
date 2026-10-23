using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Shell.Docking;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간 문서: 위에 PL/SQL 탭 줄, 아래에 고른 탭의 편집기(<see cref="PlsqlEditorView"/>). 편집기 View 는 탭마다 하나씩 만들어 두고
/// 탭을 바꿀 때 끼워 넣는다. 캐시는 이 View 인스턴스 것이다: 도킹이 문서를 옮기며 작업 공간 View 를 새로 만들면 편집기 View 도 새로 만든다
/// (한 컨트롤은 부모를 둘 가질 수 없다. ViewModel 이 View 를 들고 있으면 안 된다).
/// </summary>
public partial class PlsqlWorkspaceView : UserControl, IFocusableDocumentView
{
    private readonly Dictionary<PlsqlEditorViewModel, PlsqlEditorView> _views = [];
    private PlsqlWorkspaceViewModel? _vm;

    /// <summary>보이던 편집기가 포커스를 가진 채 빠졌다(탭 닫기 중 활성 탭이 잠깐 비었다). 다음에 보이는 탭이 포커스를 받는다.</summary>
    private bool _refocus;

    public PlsqlWorkspaceView()
    {
        InitializeComponent();
        PlsqlTabs.DoubleTapped += OnTabDoubleTapped;
        DataContextChanged += (_, _) => Bind(DataContext as PlsqlWorkspaceViewModel);

        // 도킹이 문서를 떼었다 붙이면(분할·옮기기) 떨어져 있는 동안 구독을 풀어 둔다.
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AttachedToVisualTree += (_, _) => Bind(DataContext as PlsqlWorkspaceViewModel);
    }

    /// <summary>문서로 포커스를 옮길 때(스키마 브라우저에서 두 번 누르기·Enter, 새 탭) 보이는 탭의 글자 편집 영역에 준다.</summary>
    public bool FocusContent() => EditorHost.Content is PlsqlEditorView view && view.FocusContent();

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
        }
    }

    private void Bind(PlsqlWorkspaceViewModel? vm)
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

        OnTabsChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); // 떨어져 있는 동안 빠진 탭의 View 를 버린다
        ShowTab(_vm?.ActiveTab);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlsqlWorkspaceViewModel.ActiveTab))
        {
            // 포커스가 이 작업 공간 안(탭 줄·편집기)에 있었으면(탭 고르기·Ctrl+Tab·Ctrl+W·× 단추) 새로 보이는 편집기로 옮긴다:
            // 빠진 편집기와 함께 포커스가 사라지지 않게. 스키마 브라우저 미리보기처럼 밖에서 바꾼 것은 포커스를 두고 간다.
            var tab = _vm?.ActiveTab;
            var refocus = IsKeyboardFocusWithin || _refocus;
            ShowTab(tab);
            _refocus = refocus && tab is null;
            if (refocus && tab is not null)
            {
                Dispatcher.UIThread.Post(() => FocusContent(), DispatcherPriority.Background);
            }
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

    private void ShowTab(PlsqlEditorViewModel? tab)
    {
        if (tab is null)
        {
            EditorHost.Content = null;
            return;
        }

        if (!_views.TryGetValue(tab, out var view))
        {
            view = new PlsqlEditorView { DataContext = tab };
            _views[tab] = view;
        }

        EditorHost.Content = view;

        // 줄이 넘쳐도 고른 탭 머리는 보이게(배치가 끝난 뒤에 스크롤한다).
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_vm?.ActiveTab, tab))
                {
                    PlsqlTabs.ScrollIntoView(tab);
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>탭 머리를 두 번 누르면 미리보기 탭을 고정한다.</summary>
    private void OnTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PlsqlEditorViewModel tab && _vm is not null)
        {
            _vm.PinTabCommand.Execute(tab);
            e.Handled = true;
        }
    }
}
