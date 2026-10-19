using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Oranda.App.Features.Execution;

/// <summary>
/// 조회 결과 창 View. 활성 탭의 결과 영역 ViewModel 마다 View 를 한 번만 만들어 두고(약한 참조) 탭을 오갈 때 바꿔 끼운다.
/// 매번 새로 만들면 그리드 스크롤·열 폭·셀 편집 상태가 날아간다.
/// </summary>
public partial class ResultsPanelView : UserControl
{
    private static readonly ConditionalWeakTable<object, Control> Views = [];
    private ResultsPanelViewModel? _vm;

    public ResultsPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }

            _vm = DataContext as ResultsPanelViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
            }

            Show(_vm?.Pane);
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsPanelViewModel.Pane))
        {
            Show(_vm?.Pane);
        }
    }

    private void Show(object? pane)
    {
        if (pane is null)
        {
            PaneHost.Content = null;
            return;
        }

        if (!Views.TryGetValue(pane, out var view))
        {
            view = new ViewLocator().Build(pane) ?? new TextBlock { Text = pane.GetType().Name };
            view.DataContext = pane;
            Views.AddOrUpdate(pane, view);
        }

        if (!ReferenceEquals(PaneHost.Content, view))
        {
            PaneHost.Content = view;
        }
    }
}
