using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

// 소유: TSK-03-10. TNS 디렉터리·지갑 폴더 칸 옆 「찾아보기…」 단추(폴더 선택 창) 시험.

/// <summary>「찾아보기…」 로 고른 폴더가 칸에 들어가고(별칭 제안까지), 취소하면 칸이 그대로다.</summary>
public sealed class ProfileEditorFolderPickerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oranda-pick-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Browse_buttons_fill_tns_and_wallet_folders_and_cancel_keeps_the_value()
    {
        var tns = Path.Combine(_dir, "tns");
        Directory.CreateDirectory(tns);
        File.WriteAllText(Path.Combine(tns, "tnsnames.ora"), "ONLY = (DESCRIPTION = (ADDRESS = (HOST = h1)(PORT = 1521)) (CONNECT_DATA = (SERVICE_NAME = s1)))");

        await HeadlessHost.Run(async () =>
        {
            var vm = ProfileEditorViewModel.ForNew();
            var view = new ProfileEditorView { DataContext = vm };
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            string? answer = tns;
            var asked = new List<string>();
            view.PickFolder = (title, _) =>
            {
                asked.Add(title);
                return Task.FromResult<string?>(answer);
            };

            Click(view, "TnsAdminBrowseButton");
            await HeadlessHost.WaitUntil(() => vm.TnsAdmin == tns);
            Assert.Equal(ConnectionMode.TnsAlias, vm.Mode);
            Assert.Equal(["ONLY"], vm.TnsAliases);
            Assert.Equal("ONLY", vm.TnsAlias);

            Click(view, "WalletBrowseButton");
            await HeadlessHost.WaitUntil(() => vm.WalletLocation == tns);

            answer = null; // 선택 창 취소
            Click(view, "TnsAdminBrowseButton");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(tns, vm.TnsAdmin);
            Assert.Equal(["TNS 디렉터리", "지갑 폴더", "TNS 디렉터리"], asked);
            window.Close();
        });
    }

    private static void Click(ProfileEditorView view, string name) =>
        view.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
