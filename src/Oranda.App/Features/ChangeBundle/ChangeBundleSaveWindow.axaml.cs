using Avalonia.Controls;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>「의뢰용 스크립트 저장…」 창. 「저장…」 = 파일 고르기 → 쓰기, 잘 쓰면 닫는다.</summary>
public partial class ChangeBundleSaveWindow : Window
{
    public ChangeBundleSaveWindow()
    {
        InitializeComponent();
        SaveButton.Click += async (_, _) =>
        {
            if (DataContext is not ChangeBundleSaveViewModel vm)
            {
                return;
            }

            var path = await ChangeBundleDialogs.Current.PickSavePathAsync(vm.SuggestedFileName, default);
            if (path is not null && await vm.SaveAsync(path))
            {
                Close();
            }
        };
        CloseButton.Click += (_, _) => Close();
    }
}
