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

    private static FilePickerFileType SqdFiles => new(UiText.Get("Shell.dialog.filter.sqd", "Orange 작업 공간 (*.sqd)")) { Patterns = ["*.sqd"] };

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
            DefaultExtension = Path.HasExtension(suggestedName) ? null : "sql", // 이름에 확장자가 있으면 macOS 가 또 붙이지 않게 한다
            FileTypeChoices = [SqlFiles, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return SaveFileNames.CollapseDoubled(file?.TryGetLocalPath(), suggestedName, "sql");
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
                DefaultExtension = Path.HasExtension(suggestedName) ? null : "md",
                FileTypeChoices = [WorkspaceFiles],
            }).ConfigureAwait(true);
            return SaveFileNames.CollapseDoubled(file?.TryGetLocalPath(), suggestedName, "md");
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("Shell.dialog.openWorkspaceFile", "작업 공간 파일 열기"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiText.Get("Shell.dialog.filter.workspaceOrMapper", "작업 공간·MyBatis 매퍼·Orange 작업 공간")) { Patterns = ["*.md", "*.markdown", "*.xml", "*.sqd"] }, WorkspaceFiles, MyBatisFiles, SqdFiles],
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

    public async Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct)
    {
        var owner = Owner;
        if (owner is null)
        {
            return SaveChoice.Cancel;
        }

        // 창의 닫기 단추도 취소다.
        var answer = SaveChoice.Cancel;
        var dialog = BuildSaveChangesDialog(tabName, choice => answer = choice);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return answer;
    }

    /// <summary>
    /// 저장 확인 대화상자를 만든다(보여 주지는 않는다). 단추를 누르면 <paramref name="onAnswer"/> 를 부르고 창을 닫는다.
    /// Enter = 저장(기본 단추), Esc = 취소. 시험·렌더 점검이 창만 따로 띄울 수 있게 나눠 두었다.
    /// </summary>
    public static Window BuildSaveChangesDialog(string tabName, Action<SaveChoice> onAnswer)
    {
        ArgumentNullException.ThrowIfNull(onAnswer);
        var save = new Button { Content = "저장", IsDefault = true, MinWidth = 80, Classes = { "accent" } };
        var dontSave = new Button { Content = "저장 안 함", MinWidth = 80 };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 80 };
        var dialog = new Window
        {
            Title = "저장하지 않은 변경",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            MinWidth = 320,
        };
        save.Click += (_, _) =>
        {
            onAnswer(SaveChoice.Save);
            dialog.Close();
        };
        dontSave.Click += (_, _) =>
        {
            onAnswer(SaveChoice.DontSave);
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            onAnswer(SaveChoice.Cancel);
            dialog.Close();
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = tabName, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                        new TextBlock { Text = "변경 내용을 저장할까요?", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                    },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { save, dontSave, cancel } },
            },
        };
        dialog.Opened += (_, _) => save.Focus();
        return dialog;
    }

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
