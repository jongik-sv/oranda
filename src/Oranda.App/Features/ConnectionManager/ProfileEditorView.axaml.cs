using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>프로필 편집기 화면(접속 관리자 오른쪽). 모델은 <see cref="ProfileEditorViewModel"/>.</summary>
public partial class ProfileEditorView : UserControl
{
    public ProfileEditorView()
    {
        InitializeComponent();
        PickFolder = PickWithStorageAsync;
        TnsAdminBrowseButton.Click += async (_, _) =>
        {
            if (DataContext is ProfileEditorViewModel vm && await PickFolder("TNS 디렉터리", vm.TnsAdmin) is { } path)
            {
                vm.TnsAdmin = path;
            }
        };
        WalletBrowseButton.Click += async (_, _) =>
        {
            if (DataContext is ProfileEditorViewModel vm && await PickFolder("지갑 폴더", vm.WalletLocation) is { } path)
            {
                vm.WalletLocation = path;
            }
        };
    }

    /// <summary>폴더 선택 창(제목, 지금 칸 값 → 고른 폴더 경로, 취소면 null). 시험에서 바꿔 끼운다.</summary>
    public Func<string, string, Task<string?>> PickFolder { get; set; }

    private async Task<string?> PickWithStorageAsync(string title, string current)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current.Trim()))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(current.Trim());
        }

        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
