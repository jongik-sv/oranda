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

    public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct)
        => ShowAsync(title, BuildPreviewEditPanel(before, after, PreviewMaxSize(OwnerWorkArea())), false, ok => ok, UiText.Get("Shell.dialog.apply", "적용"), UiText.Get("Shell.dialog.reject", "거절"));

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
    /// 위에 바뀐 부분(줄 diff, 앞뒤 같은 줄은 접는다), 아래에 현재 글과 제안 글 전체를 나란히 보이는 비교 판(시험용으로 분리).
    /// 전체 글 두 칸은 SQL 강조가 걸린 읽기 전용 코드 뷰다. 크기는 <paramref name="max"/>(없으면 상한) 안이고, 넘치는 글은 칸마다 스크롤한다.
    /// </summary>
    internal static Grid BuildPreviewEditPanel(string before, string after, Size? max = null)
    {
        var limit = max ?? PreviewMaxSize(null);
        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,2*,Auto,3*"),
            MinWidth = 640,
            MinHeight = 240,
            MaxWidth = limit.Width,
            MaxHeight = limit.Height,
        };
        var lines = MapperLineDiff.Build(before, after, 1, 1);
        var (added, removed) = MapperLineDiff.Count(lines);
        var changes = new TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"{UiText.Get("Shell.dialog.changes", "바뀐 부분")}  +{added} −{removed}"),
            FontWeight = FontWeight.SemiBold,
        };
        Grid.SetColumnSpan(changes, 2);
        // diff 목록(ItemsControl)에는 스크롤이 없다: 바뀐 줄이 많아도 칸 안에서 넘기도록 감싼다.
        var diff = new ScrollViewer
        {
            Content = new MapperDiffView { Rows = MapperDiffRow.Section(null, lines, sideBySide: false) },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 2, 0, 8),
        };
        Grid.SetRow(diff, 1);
        Grid.SetColumnSpan(diff, 2);
        panel.Children.Add(changes);
        panel.Children.Add(diff);

        var current = new TextBlock { Text = UiText.Get("Shell.dialog.current", "현재"), FontWeight = FontWeight.SemiBold };
        Grid.SetRow(current, 2);
        var right = new TextBlock { Text = UiText.Get("Shell.dialog.proposed", "제안"), FontWeight = FontWeight.SemiBold };
        Grid.SetRow(right, 2);
        Grid.SetColumn(right, 1);
        panel.Children.Add(current);
        panel.Children.Add(right);
        var left = new SqlCodeView { Text = before };
        Grid.SetRow(left, 3);
        var proposed = new SqlCodeView { Text = after };
        Grid.SetRow(proposed, 3);
        Grid.SetColumn(proposed, 1);
        panel.Children.Add(left);
        panel.Children.Add(proposed);
        return panel;
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
        dialog.Content = DialogShell.Framed(content, DialogShell.Buttons(ok, cancel));
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
