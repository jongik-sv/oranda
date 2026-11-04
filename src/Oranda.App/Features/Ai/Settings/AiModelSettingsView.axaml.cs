using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oranda.App.Features.Ai.Settings;

public partial class AiModelSettingsView : UserControl
{
    private AiModelSettingsPage? _page;

    public AiModelSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_page is not null)
        {
            _page.ProfileManagerRequested -= OnManagerRequested;
        }

        _page = DataContext as AiModelSettingsPage;
        if (_page is not null)
        {
            _page.ProfileManagerRequested += OnManagerRequested;
        }
    }

    private void OnManagerRequested(object? sender, ProfileManagerRequestedEventArgs e) => _ = OpenManagerAsync(e);

    /// <summary>프로필 관리 창을 이 설정 창 위에 모달로 연다. 창이 닫히면 도는 서버 조회도 멈춘다.</summary>
    internal async Task OpenManagerAsync(ProfileManagerRequestedEventArgs request)
    {
        if (_page is not { } page || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var english = page.Services?.Text?.Language == "en";
        // 확인 창은 관리 창 위에 띄운다(설정 창 위에 띄우면 관리 창과 형제가 되어 관리 창이 닫힌 뒤에도 남을 수 있다).
        AiProfileManagerWindow? managerWindow = null;
        var manager = page.CreateManager(
            request,
            message => AiConfirmDialog.ShowAsync(managerWindow ?? owner, message, english ? "OK" : "확인", english ? "Cancel" : "취소"));
        if (manager is null)
        {
            return;
        }

        using (manager)
        {
            managerWindow = new AiProfileManagerWindow { DataContext = manager };
            await managerWindow.ShowDialog(owner).ConfigureAwait(true);
        }
    }

    // 기타 CLI 줄의 「사용」 단추.
    private void OnUseCustomCliClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AiCustomCliRow row } && _page is not null)
        {
            _page.UseCustomCliRowCommand.Execute(row);
        }
    }

    // 기타 CLI 줄의 「삭제」 단추: 확인을 받은 뒤 지운다.
    private void OnDeleteCustomCliClick(object? sender, RoutedEventArgs e) => _ = DeleteCustomCliAsync(sender);

    private async Task DeleteCustomCliAsync(object? sender)
    {
        if (sender is not Control { DataContext: AiCustomCliRow row } || _page is not { } page || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var english = page.Services?.Text?.Language == "en";
        if (await AiConfirmDialog.ShowAsync(owner, page.DeleteCustomCliConfirmText(row), english ? "OK" : "확인", english ? "Cancel" : "취소").ConfigureAwait(true))
        {
            page.RemoveCustomCli(row);
        }
    }

    // 드롭다운 줄 클릭: 줄의 데이터(AiProfileChoice)를 페이지에 넘긴다.
    private void OnChoiceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AiProfileChoice choice } && _page is not null)
        {
            _page.Choose(choice);
        }
    }
}
