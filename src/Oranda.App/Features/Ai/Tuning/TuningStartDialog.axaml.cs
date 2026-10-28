using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>AI 튜닝 시작 확인 창. 「튜닝 시작」 이면 선택을, 취소이거나 시작할 수 없으면 null 을 돌려준다.</summary>
public partial class TuningStartDialog : Window
{
    public TuningStartDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close(null);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TuningStartViewModel model)
            {
                model.StartRequested += (_, _) => Close(model.Choice);
            }
        };
    }

    /// <summary><paramref name="owner"/> 위에 모달로 띄운다.</summary>
    public static Task<TuningStartChoice?> ShowAsync(Window owner, TuningStartViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new TuningStartDialog { DataContext = model }.ShowDialog<TuningStartChoice?>(owner);
    }
}
