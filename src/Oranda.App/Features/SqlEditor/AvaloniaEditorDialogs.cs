using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// <see cref="IEditorDialogs"/> 의 Avalonia 구현. 주 창(<see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/>) 위에 모달로 띄운다.
/// 대화상자는 XAML 없이 코드로 만든다(작고 이 Task 안에서만 쓴다).
/// </summary>
public sealed class AvaloniaEditorDialogs : IEditorDialogs
{
    // 이름이 언어를 따라가야 하므로 부를 때마다 만든다(글자는 Shell 리소스 Shell.dialog.*).
    private static FilePickerFileType SqlFiles => new(UiText.Get("Shell.dialog.filter.sql", "SQL 파일")) { Patterns = ["*.sql", "*.pls", "*.pks", "*.pkb", "*.txt"] };

    private static FilePickerFileType WorkspaceFiles => new(UiText.Get("Shell.dialog.filter.workspace", "작업 공간(Markdown)")) { Patterns = ["*.md", "*.markdown"] };

    private static FilePickerFileType MyBatisFiles => new(UiText.Get("Shell.dialog.filter.mybatis", "MyBatis 매퍼")) { Patterns = ["*.xml"] };

    private static Window? Owner
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> PickOpenFileAsync(CancellationToken ct)
    {
        if (Owner?.StorageProvider is not { } sp)
        {
            return null;
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("Shell.dialog.openFile", "파일 열기"),
            AllowMultiple = false,
            FileTypeFilter = [SqlFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct)
    {
        if (Owner?.StorageProvider is not { } sp)
        {
            return null;
        }

        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiText.Get("Shell.dialog.save", "저장"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "sql",
            FileTypeChoices = [SqlFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickWorkspaceFileAsync(bool save, string suggestedName, CancellationToken ct)
    {
        if (Owner?.StorageProvider is not { } sp)
        {
            return null;
        }

        if (save)
        {
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiText.Get("Shell.dialog.saveWorkspaceFile", "작업 공간 파일로 저장"),
                SuggestedFileName = suggestedName,
                DefaultExtension = "md",
                FileTypeChoices = [WorkspaceFiles],
            }).ConfigureAwait(true);
            return file?.TryGetLocalPath();
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("Shell.dialog.openWorkspaceFile", "작업 공간 파일 열기"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiText.Get("Shell.dialog.filter.workspaceOrMapper", "작업 공간·MyBatis 매퍼")) { Patterns = ["*.md", "*.markdown", "*.xml"] }, WorkspaceFiles, MyBatisFiles],
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public Task ShowMessageAsync(string title, string message, CancellationToken ct)
        => ShowAsync(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, false, ok => ok, UiText.Get("Shell.dialog.ok", "확인"), null);

    public Task<string?> PromptAsync(string title, string initial, CancellationToken ct)
    {
        var box = new TextBox { Text = initial, MinWidth = 280 };
        return ShowAsync<string?>(title, box, null, ok => ok ? box.Text : null);
    }

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct)
    {
        var list = new ListBox { ItemsSource = items, MinWidth = 280, MaxHeight = 280, SelectedIndex = items.Count > 0 ? 0 : -1 };
        return ShowAsync<string?>(title, list, null, ok => ok ? list.SelectedItem as string : null);
    }

    public Task<bool> ConfirmAsync(string message, CancellationToken ct)
        => ShowAsync("Oranda", new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, false, ok => ok);

    public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct)
    {
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,*"), MinWidth = 640, MinHeight = 240 };
        panel.Children.Add(new TextBlock { Text = UiText.Get("Shell.dialog.current", "현재"), FontWeight = FontWeight.SemiBold });
        var right = new TextBlock { Text = UiText.Get("Shell.dialog.proposed", "제안"), FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(right, 1);
        panel.Children.Add(right);
        var mono = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        var left = new TextBox { Text = before, IsReadOnly = true, AcceptsReturn = true, FontFamily = mono };
        Grid.SetRow(left, 1);
        var proposed = new TextBox { Text = after, IsReadOnly = true, AcceptsReturn = true, FontFamily = mono };
        Grid.SetRow(proposed, 1);
        Grid.SetColumn(proposed, 1);
        panel.Children.Add(left);
        panel.Children.Add(proposed);
        return ShowAsync(title, panel, false, ok => ok, UiText.Get("Shell.dialog.apply", "적용"), UiText.Get("Shell.dialog.reject", "거절"));
    }

    private static async Task<T> ShowAsync<T>(string title, Control content, T cancelled, Func<bool, T> result, string? okText = null, string? cancelText = "")
    {
        var owner = Owner;
        if (owner is null)
        {
            return cancelled;
        }

        bool accepted = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
        };
        okText ??= UiText.Get("Shell.dialog.ok", "확인");
        cancelText = cancelText == "" ? UiText.Get("Shell.dialog.cancel", "취소") : cancelText;
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, Classes = { "accent" } };
        var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 80, IsVisible = cancelText is not null };
        ok.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                content,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        dialog.Opened += (_, _) =>
        {
            // 입력 칸이 있으면 바로 칠 수 있게 포커스를 주고 기존 글을 고른다(없으면 확인 단추).
            if (content is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
            else if (content is ListBox list)
            {
                list.Focus();
            }
            else
            {
                ok.Focus();
            }
        };
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result(accepted);
    }
}
