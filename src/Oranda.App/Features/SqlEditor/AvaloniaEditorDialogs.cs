using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// <see cref="IEditorDialogs"/> 의 Avalonia 구현. 주 창(<see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/>) 위에 모달로 띄운다.
/// 대화상자는 XAML 없이 코드로 만든다(작고 이 Task 안에서만 쓴다).
/// </summary>
public sealed class AvaloniaEditorDialogs : IEditorDialogs
{
    // 이름이 언어를 따라가야 하므로 부를 때마다 만든다(글자는 Shell 리소스 Shell.dialog.*).
    private static FilePickerFileType SqlFiles => new(UiText.Get("Shell.dialog.filter.sql", "SQL 파일")) { Patterns = ["*.sql", "*.pls", "*.pks", "*.pkb", "*.plb", "*.prc", "*.fnc", "*.trg", "*.txt"] };

    // Oranda 작업 공간은 이름.workspace.md(D-028). macOS 피커는 확장자 하나만 알아서 UTI 는 Markdown 으로 준다.
    private static FilePickerFileType WorkspaceFiles => new(UiText.Get("Shell.dialog.filter.workspace", "Oranda 작업 공간 (*.workspace.md)"))
    {
        Patterns = ["*" + SqlEditor.WorkspaceFiles.Extension, "*.plsql-workspace.md"],
        AppleUniformTypeIdentifiers = ["net.daringfireball.markdown"],
    };

    private static FilePickerFileType MarkdownFiles => new(UiText.Get("Shell.dialog.filter.markdown", "Markdown 파일 (*.md)")) { Patterns = ["*.md", "*.markdown"] };

    private static FilePickerFileType MyBatisFiles => new(UiText.Get("Shell.dialog.filter.mybatis", "MyBatis 매퍼")) { Patterns = ["*.xml"] };

    private static FilePickerFileType SqdFiles => new(UiText.Get("Shell.dialog.filter.sqd", "Orange 작업 공간 (*.sqd)")) { Patterns = ["*.sqd"] };

    private static Window? Owner => DialogShell.MainWindow;

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
            FileTypeFilter = [SqlFiles, WorkspaceFiles, SqdFiles, FilePickerFileTypes.All],
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
                FileTypeChoices = [WorkspaceFiles, MarkdownFiles],
            }).ConfigureAwait(true);
            return SaveFileNames.CollapseDoubled(file?.TryGetLocalPath(), suggestedName, "md");
        }

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("Shell.dialog.openWorkspaceFile", "작업 공간 파일 열기"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiText.Get("Shell.dialog.filter.workspaceOrMapper", "작업 공간·MyBatis 매퍼·Orange 작업 공간")) { Patterns = ["*.md", "*.markdown", "*.xml", "*.sqd"] }, WorkspaceFiles, MarkdownFiles, MyBatisFiles, SqdFiles],
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
        dialog.Content = DialogShell.Framed(
            new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = tabName, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                    new TextBlock { Text = "변경 내용을 저장할까요?", TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                },
            },
            DialogShell.Buttons(save, dontSave, cancel));
        dialog.Opened += (_, _) => save.Focus();
        return dialog;
    }

    public async Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct)
        => await PreviewEditTextAsync(title, before, after, ct).ConfigureAwait(true) is not null;

    public Task<string?> PreviewEditTextAsync(string title, string before, string after, CancellationToken ct)
    {
        var panel = BuildPreviewEditPanel(before, after, PreviewMaxSize(OwnerWorkArea()));
        return ShowAsync(title, panel, null, ok => ok ? panel.ProposedText : null, UiText.Get("Shell.dialog.apply", "적용"), UiText.Get("Shell.dialog.reject", "거절"), fill: true);
    }

    public Task ShowComparisonAsync(string title, string before, string after, CancellationToken ct)
    {
        var labels = (UiText.Get("Shell.dialog.original", "원본"), UiText.Get("Shell.dialog.appliedProposal", "적용한 제안"));
        var panel = new PreviewEditPanel(before, after, PreviewMaxSize(OwnerWorkArea()), labels, readOnly: true);
        return ShowAsync(title, panel, false, ok => ok, UiText.Get("Shell.dialog.close", "닫기"), null, fill: true);
    }

    /// <summary>
    /// 비교 판의 최대 크기 상한. 창이 내용 크기를 따르므로(<see cref="SizeToContent.WidthAndHeight"/>) 막지 않으면 긴 문장에서 창이 화면만큼 커져
    /// 적용·거절 단추가 화면 밖으로 밀려난다.
    /// </summary>
    internal const double PreviewMaxWidth = 1100;

    internal const double PreviewMaxHeight = 560;

    /// <summary>비교 판의 최대 크기: 상한과 화면 작업 영역의 80%(너비)·70%(높이) 중 작은 쪽. 작업 영역을 모르면 상한.</summary>
    internal static Size PreviewMaxSize(Size? workArea) => workArea is { } area
        ? new Size(Math.Min(PreviewMaxWidth, area.Width * 8 / 10), Math.Min(PreviewMaxHeight, area.Height * 7 / 10)) // 0.7 을 곱하면 476 이 475.999… 가 된다
        : new Size(PreviewMaxWidth, PreviewMaxHeight);

    /// <summary>주 창이 있는 화면의 작업 영역(작업 표시줄을 뺀 영역, DIP). 모르면 null.</summary>
    private static Size? OwnerWorkArea() =>
        Owner is { } owner && owner.Screens.ScreenFromWindow(owner) is { } screen ? screen.WorkingArea.Size.ToSize(screen.Scaling) : null;

    /// <summary>
    /// 탭 둘로 된 비교 판(시험용으로 분리): 「바뀐 부분 비교」(왼쪽 기존·오른쪽 변경 줄 diff)와 「현재 / 제안」(SQL 강조가 걸린 글 전체 두 칸, 제안 칸은 고칠 수 있음).
    /// 크기는 <paramref name="max"/>(없으면 상한) 안에서 글 길이로 정해 고정하고, 넘치는 글은 칸마다 스크롤한다.
    /// </summary>
    internal static PreviewEditPanel BuildPreviewEditPanel(string before, string after, Size? max = null)
        => new(before, after, max ?? PreviewMaxSize(null));

    private static async Task<T> ShowAsync<T>(string title, Control content, T cancelled, Func<bool, T> result, string? okText = null, string? cancelText = "", bool fill = false)
    {
        var owner = Owner;
        if (owner is null)
        {
            return cancelled;
        }

        bool accepted = false;
        var dialog = BuildDialog(title, content, okText, cancelText, () => accepted = true, fill);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result(accepted);
    }

    /// <summary>
    /// 대화상자 창. <paramref name="fill"/> 이면 본문이 창의 남은 높이를 채우는 틀을 쓴다(세로 StackPanel 인 <see cref="DialogShell.Framed"/> 는 본문을 자기 크기로만 두어,
    /// 스크롤하는 칸이 든 본문은 창 크기를 정하지 못한다).
    /// </summary>
    internal static Window BuildDialog(string title, Control content, string? okText, string? cancelText, Action onAccept, bool fill = false)
    {
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
            onAccept();
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = DialogShell.Buttons(ok, cancel);
        if (fill)
        {
            Grid.SetRow(buttons, 1);
            dialog.Content = new Grid { Margin = new Thickness(16), RowSpacing = 12, RowDefinitions = new RowDefinitions("*,Auto"), Children = { content, buttons } };
        }
        else
        {
            dialog.Content = DialogShell.Framed(content, buttons);
        }

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
        return dialog;
    }
}
