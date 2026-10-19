using Avalonia.Controls;

namespace Oranda.App.Shell.Settings;

/// <summary>설정 창. 내용에 맞춰 늘어나고 작업 영역보다 크면 본문만 스크롤한다.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
    }
}
