using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Execution;

/// <summary>
/// <see cref="IExecutionDialogs"/> 의 Avalonia 구현. XAML 없이 코드로 만든다(작고 이 Task 안에서만 쓴다).
/// 모든 대화상자는 UI 스레드에서 열린다. 기본 단추는 「취소」다(Enter 로 바로 실행되지 않는다, ui-shell.md 4.3).
/// </summary>
public sealed class AvaloniaExecutionDialogs : IExecutionDialogs
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,monospace");

    private readonly Func<Window?>? _owner;

    public AvaloniaExecutionDialogs(Func<Window?>? owner = null)
    {
        _owner = owner;
    }

    private Window? Owner
        => _owner?.Invoke() ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        => OnUi(() => ConfirmCoreAsync(new ConfirmDialogModel(request)));

    public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct)
        => OnUi(() => SubstitutionsCoreAsync(names, last));

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct)
        => OnUi(() => ScriptErrorCoreAsync(stmt, failure));

    public Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct)
        => OnUi(() => UncommittedCoreAsync(null));

    public Task<UncommittedChoice> AskUncommittedAsync(string subject, CancellationToken ct)
        => OnUi(() => UncommittedCoreAsync(subject));

    public Task<bool> PreviewDmlAsync(string text, CancellationToken ct)
        => OnUi(() => PreviewCoreAsync(text));

    private static Task<T> OnUi<T>(Func<Task<T>> body)
        => Dispatcher.UIThread.CheckAccess() ? body() : Dispatcher.UIThread.InvokeAsync(body);

    // ---- 확인 ------------------------------------------------------------------------------

    private async Task<bool> ConfirmCoreAsync(ConfirmDialogModel model)
    {
        var dialog = NewWindow("실행 확인");
        var header = new Border { Padding = new Thickness(16, 10) };
        if (model.IsProduction)
        {
            // 운영: 머리 띠 빨강 + 접속 이름·사용자·서비스 크게(ui-shell.md 4.2).
            header.Bind(Border.BackgroundProperty, header.GetResourceObservable("OrandaEnvProductionBrush"));
        }
        else
        {
            header.Bind(Border.BackgroundProperty, header.GetResourceObservable("OrandaEnvTestBrush"));
        }

        var fg = model.IsProduction ? "OrandaEnvProductionForegroundBrush" : null;
        var title = new TextBlock { Text = (model.EnvironmentText.Length > 0 ? $"[{model.EnvironmentText}] " : string.Empty) + model.HeaderTitle, FontSize = 20, FontWeight = FontWeight.Bold };
        var detail = new TextBlock { Text = model.HeaderDetail, FontSize = 14 };
        if (fg is not null)
        {
            title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable(fg));
            detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable(fg));
        }

        header.Child = new StackPanel { Children = { title, detail } };

        var message = new TextBlock { Text = model.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        var sql = new TextBox
        {
            Text = model.Request.Statement.Text,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = Mono,
            MaxHeight = 160,
            MinWidth = 420,
        };
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { message, sql } };

        TextBox? retype = null;
        if (model.RetypeText is { } expected)
        {
            retype = new TextBox { PlaceholderText = expected, MinWidth = 280 };
            AutomationName(retype, "대상 이름 다시 입력");
            body.Children.Add(new TextBlock { Text = $"계속하려면 '{expected}' 을(를) 입력하세요." });
            body.Children.Add(retype);
        }

        bool accepted = false;
        var run = new Button { Content = "실행", MinWidth = 80, IsEnabled = model.CanAccept(string.Empty) };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        run.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        if (retype is not null)
        {
            retype.TextChanged += (_, _) => run.IsEnabled = model.CanAccept(retype.Text ?? string.Empty);
        }

        body.Children.Add(Buttons(run, cancel));
        dialog.Content = new StackPanel { Children = { header, body } };
        dialog.Opened += (_, _) => cancel.Focus();
        await ShowAsync(dialog).ConfigureAwait(true);
        return accepted;
    }

    // ---- 치환 변수 -------------------------------------------------------------------------

    private async Task<IReadOnlyDictionary<string, string>?> SubstitutionsCoreAsync(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last)
    {
        var dialog = NewWindow("치환 변수 입력");
        var boxes = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 8 };
        for (int i = 0; i < names.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = "&" + names[i], VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { MinWidth = 260, Text = last.TryGetValue(names[i], out var v) ? v : string.Empty };
            AutomationName(box, "치환 변수 " + names[i]);
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            boxes[names[i]] = box;
        }

        bool accepted = false;
        var ok = new Button { Content = "확인", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = Framed(grid, Buttons(ok, cancel));
        await ShowAsync(dialog).ConfigureAwait(true);
        return accepted ? boxes.ToDictionary(p => p.Key, p => p.Value.Text ?? string.Empty, StringComparer.OrdinalIgnoreCase) : null;
    }

    // ---- 스크립트 오류 ------------------------------------------------------------------------

    private async Task<ScriptErrorAction> ScriptErrorCoreAsync(SqlStatement stmt, DbError error)
    {
        var dialog = NewWindow("스크립트 오류");
        var action = ScriptErrorAction.Stop;
        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = DbErrorText.Format(error), TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
                new TextBox { Text = stmt.Text, IsReadOnly = true, AcceptsReturn = true, FontFamily = Mono, MaxHeight = 140, MinWidth = 420 },
            },
        };
        var stop = new Button { Content = "중지", MinWidth = 80, IsDefault = true };
        var next = new Button { Content = "계속", MinWidth = 80 };
        var all = new Button { Content = "모두 계속", MinWidth = 80 };
        stop.Click += (_, _) => dialog.Close();
        next.Click += (_, _) =>
        {
            action = ScriptErrorAction.Continue;
            dialog.Close();
        };
        all.Click += (_, _) =>
        {
            action = ScriptErrorAction.ContinueAll;
            dialog.Close();
        };
        dialog.Content = Framed(body, Buttons(stop, next, all));
        await ShowAsync(dialog).ConfigureAwait(true);
        return action;
    }

    // ---- 미커밋 종료 -----------------------------------------------------------------------

    private async Task<UncommittedChoice> UncommittedCoreAsync(string? subject)
    {
        var choice = UncommittedChoice.Cancel;
        var dialog = CreateUncommittedDialog(subject, c => choice = c);
        await ShowAsync(dialog).ConfigureAwait(true);
        return choice;
    }

    /// <summary>
    /// 미커밋 확인 창을 만든다(띄우지는 않는다). <paramref name="subject"/> 가 있으면 어느 탭의 세션인지 글에 넣는다(전용 세션 탭, D-017).
    /// 단추를 누르면 <paramref name="chosen"/> 을 부르고 창을 닫는다. 「취소」·창 닫기는 부르지 않는다.
    /// </summary>
    internal static Window CreateUncommittedDialog(string? subject, Action<UncommittedChoice> chosen)
    {
        var dialog = NewWindow("미커밋 트랜잭션");
        var commit = new Button { Content = "커밋", MinWidth = 80 };
        var rollback = new Button { Content = "롤백", MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        commit.Click += (_, _) =>
        {
            chosen(UncommittedChoice.Commit);
            dialog.Close();
        };
        rollback.Click += (_, _) =>
        {
            chosen(UncommittedChoice.Rollback);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var text = subject is null
            ? "커밋하지 않은 변경이 있습니다. 어떻게 할까요?"
            : UiText.Get("Execution.uncommitted.dedicated", "「{0}」 탭의 전용 세션에 커밋하지 않은 변경이 있습니다. 어떻게 할까요?")
                .Replace("{0}", subject, StringComparison.Ordinal);
        dialog.Content = Framed(
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            Buttons(commit, rollback, cancel));
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog;
    }

    // ---- DML 미리보기 ------------------------------------------------------------------------

    private async Task<bool> PreviewCoreAsync(string text)
    {
        var dialog = NewWindow("DML 미리보기");
        bool accepted = false;
        var ok = new Button { Content = "반영", MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = Mono, MinWidth = 520, MinHeight = 200, MaxHeight = 420 };
        dialog.Content = Framed(box, Buttons(ok, cancel));
        dialog.Opened += (_, _) => cancel.Focus();
        await ShowAsync(dialog).ConfigureAwait(true);
        return accepted;
    }

    // ---- 공통 ------------------------------------------------------------------------------

    private static Window NewWindow(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = true,
        MinWidth = 320,
        ShowInTaskbar = false,
    };

    private static StackPanel Framed(Control content, Control buttons)
        => new() { Margin = new Thickness(16), Spacing = 12, Children = { content, buttons } };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
        {
            panel.Children.Add(b);
        }

        return panel;
    }

    private static void AutomationName(Control c, string name) => Avalonia.Automation.AutomationProperties.SetName(c, name);

    private async Task ShowAsync(Window dialog)
    {
        if (Owner is { IsVisible: true } owner)
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return;
        }

        // 주 창이 없으면(시험·시작 직후) 독립 창으로 띄우고 닫힐 때까지 기다린다.
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        await closed.Task.ConfigureAwait(true);
    }
}
