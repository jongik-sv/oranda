using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>가림 대화에 새 이미지를 보내기 전 확인 창. 「그대로 보내기」 면 true, 취소·닫기면 false.</summary>
public partial class AiImageWarningDialog : Window
{
    public AiImageWarningDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AiImageWarningViewModel model)
            {
                model.CloseRequested += (_, confirmed) => Close(confirmed);
            }
        };
    }

    /// <summary><paramref name="owner"/> 위에 모달로 띄운다. 창을 그냥 닫으면(결과 없음) 취소로 본다.</summary>
    public static async Task<bool> ShowAsync(Window owner, AiImageSendNotice notice)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await new AiImageWarningDialog { DataContext = new AiImageWarningViewModel(notice) }.ShowDialog<bool?>(owner).ConfigureAwait(true) == true;
    }
}
