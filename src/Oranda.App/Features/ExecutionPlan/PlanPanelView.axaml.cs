using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 실행 계획 창 View. 활성 탭의 계획 모델(<see cref="PlanViewModel"/>) 마다 <see cref="PlanView"/> 를 한 번만 만들어 두고(약한 참조)
/// 탭을 오갈 때 바꿔 끼운다. 매번 새로 만들면 고른 줄·스크롤·접힘 같은 화면 상태가 날아간다.
/// </summary>
public partial class PlanPanelView : UserControl
{
    private static readonly ConditionalWeakTable<PlanViewModel, PlanView> Views = [];
    private PlanPanelViewModel? _vm;

    public PlanPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }

            _vm = DataContext as PlanPanelViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
            }

            Show(_vm?.Active);
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanPanelViewModel.Active))
        {
            Show(_vm?.Active);
        }
    }

    private void Show(PlanViewModel? plan)
    {
        if (plan is null)
        {
            PlanHost.Content = null;
            return;
        }

        if (!Views.TryGetValue(plan, out var view))
        {
            view = new PlanView { DataContext = plan };
            Views.AddOrUpdate(plan, view);
        }

        if (!ReferenceEquals(PlanHost.Content, view))
        {
            // 도킹이 창을 다시 만들면 옛 창이 떨어지기 전에 새 창이 같은 View 를 원한다: 한 컨트롤은 부모를 둘 가질 수 없으니 옛 자리에서 먼저 뺀다.
            if (view.Parent is ContentControl { } owner && !ReferenceEquals(owner, PlanHost))
            {
                owner.Content = null;
            }

            PlanHost.Content = view;
        }
    }
}
