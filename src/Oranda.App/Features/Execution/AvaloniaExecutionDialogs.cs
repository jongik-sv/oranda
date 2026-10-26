using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Appearance;
using Oranda.App.Shell.Controls;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Execution;

/// <summary>
/// <see cref="IExecutionDialogs"/> 의 Avalonia 구현. XAML 없이 코드로 만든다(작고 이 Task 안에서만 쓴다).
/// 모든 대화상자는 UI 스레드에서 열린다. 기본 단추는 「취소」다(Enter 로 바로 실행되지 않는다, ui-shell.md 4.3).
/// </summary>
public sealed class AvaloniaExecutionDialogs : IExecutionDialogs
{
    private static readonly FontFamily Mono = MonoFonts.Code;

    private readonly Func<Window?>? _owner;

    public AvaloniaExecutionDialogs(Func<Window?>? owner = null)
    {
        _owner = owner;
    }

    private Window? Owner
        => _owner?.Invoke() ?? DialogShell.MainWindow;

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

    public Task<UncommittedChoice> AskConnectionUncommittedAsync(string connection, CancellationToken ct)
        => OnUi(() => UncommittedCoreAsync(null, connection));

    public Task<UncommittedChoice> AskSwitchUncommittedAsync(string connection, CancellationToken ct)
        => OnUi(() => UncommittedCoreAsync(null, connection, keep: true));

    public Task<MissingConnectionChoice> AskMissingConnectionAsync(string profile, string? current, CancellationToken ct)
        => OnUi(() => MissingConnectionCoreAsync(profile, current));

    public Task<bool> ConfirmSharedTransactionAsync(string connection, IReadOnlyList<string> workspaces, bool commit, CancellationToken ct)
        => OnUi(() => SharedTransactionCoreAsync(connection, workspaces, commit));

    public Task<bool> PreviewDmlAsync(string text, CancellationToken ct)
        => OnUi(() => PreviewCoreAsync(text, commit: false));

    public Task<bool> PreviewDmlAsync(string text, bool commit, CancellationToken ct)
        => OnUi(() => PreviewCoreAsync(text, commit));

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
        var sql = new SqlCodeView { Text = model.Request.Statement.Text, MaxHeight = 160, MinWidth = 420 };
        DialogShell.AutomationName(sql, "실행할 문장");
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { message, sql } };

        TextBox? retype = null;
        if (model.RetypeText is { } expected)
        {
            retype = new TextBox { PlaceholderText = expected, MinWidth = 280 };
            DialogShell.AutomationName(retype, "대상 이름 다시 입력");
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

        body.Children.Add(DialogShell.Buttons(run, cancel));
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
            DialogShell.AutomationName(box, "치환 변수 " + names[i]);
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
        dialog.Content = DialogShell.Framed(grid, DialogShell.Buttons(ok, cancel));
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
                new SqlCodeView { Text = stmt.Text, MaxHeight = 140, MinWidth = 420 },
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
        dialog.Content = DialogShell.Framed(body, DialogShell.Buttons(stop, next, all));
        await ShowAsync(dialog).ConfigureAwait(true);
        return action;
    }

    // ---- 미커밋 종료 -----------------------------------------------------------------------

    private async Task<UncommittedChoice> UncommittedCoreAsync(string? subject, string? connection = null, bool keep = false)
    {
        var choice = UncommittedChoice.Cancel;
        var dialog = CreateUncommittedDialog(subject, c => choice = c, connection, keep);
        await ShowAsync(dialog).ConfigureAwait(true);
        return choice;
    }

    /// <summary>
    /// 미커밋 확인 창을 만든다(띄우지는 않는다). <paramref name="subject"/> 가 있으면 어느 탭의 세션인지 글에 넣는다(전용 세션 탭, D-017).
    /// <paramref name="connection"/> 이 있으면 어느 접속인지 글에 넣는다(접속 끊기·앱 종료 때 접속마다, CONN-06).
    /// <paramref name="keep"/> 이면 접속 콤보로 탭의 접속을 바꾸기 전 확인이다(TSK-12-04): 글이 「이전 접속」 을 밝히고 「그대로 두고 바꾸기」 단추가 더 있다.
    /// 단추를 누르면 <paramref name="chosen"/> 을 부르고 창을 닫는다. 「취소」·창 닫기는 부르지 않는다.
    /// </summary>
    internal static Window CreateUncommittedDialog(string? subject, Action<UncommittedChoice> chosen, string? connection = null, bool keep = false)
    {
        var dialog = NewWindow("미커밋 트랜잭션");
        var commit = new Button { Content = "커밋", MinWidth = 80 };
        var rollback = new Button { Content = "롤백", MinWidth = 80 };
        var leave = new Button { Content = "그대로 두고 바꾸기", MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        leave.Click += (_, _) =>
        {
            chosen(UncommittedChoice.Keep);
            dialog.Close();
        };
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
        var text = keep && connection is not null
            ? UiText.Get("Execution.uncommitted.switch", "이전 접속 「{0}」 에 커밋하지 않은 변경이 있습니다. 접속을 바꾸기 전에 어떻게 할까요?")
                .Replace("{0}", connection, StringComparison.Ordinal)
            : connection is not null
            ? UiText.Get("Execution.uncommitted.connection", "접속 「{0}」 에 커밋하지 않은 변경이 있습니다. 어떻게 할까요?")
                .Replace("{0}", connection, StringComparison.Ordinal)
            : subject is null
            ? "커밋하지 않은 변경이 있습니다. 어떻게 할까요?"
            : UiText.Get("Execution.uncommitted.dedicated", "「{0}」 탭의 전용 세션에 커밋하지 않은 변경이 있습니다. 어떻게 할까요?")
                .Replace("{0}", subject, StringComparison.Ordinal);
        dialog.Content = DialogShell.Framed(
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            keep ? DialogShell.Buttons(commit, rollback, leave, cancel) : DialogShell.Buttons(commit, rollback, cancel));
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog;
    }

    // ---- 공유 세션 커밋·롤백 -------------------------------------------------------------------

    private async Task<bool> SharedTransactionCoreAsync(string connection, IReadOnlyList<string> workspaces, bool commit)
    {
        var accepted = false;
        var dialog = CreateSharedTransactionDialog(connection, workspaces, commit, () => accepted = true);
        await ShowAsync(dialog).ConfigureAwait(true);
        return accepted;
    }

    /// <summary>
    /// 여러 작업 공간이 함께 쓰는 세션의 커밋·롤백 확인 창을 만든다(띄우지는 않는다, TSK-14-10): 함께 확정(되돌림)되는 작업 공간 이름을 보이고
    /// 커밋(롤백) / 취소. 주 단추를 누르면 <paramref name="accepted"/> 를 부르고 닫는다. 기본 단추는 「취소」.
    /// </summary>
    internal static Window CreateSharedTransactionDialog(string connection, IReadOnlyList<string> workspaces, bool commit, Action accepted)
    {
        var dialog = NewWindow(UiText.Get("Execution.shared.title", "공유 세션"));
        var ok = new Button { Content = commit ? "커밋" : "롤백", MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted();
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var text = (commit
                ? UiText.Get("Execution.shared.commit", "「{0}」 은(는) 워크스페이스 {1} 이(가) 함께 쓰는 세션입니다. 커밋하면 이 워크스페이스들의 변경이 모두 확정됩니다. 커밋할까요?")
                : UiText.Get("Execution.shared.rollback", "「{0}」 은(는) 워크스페이스 {1} 이(가) 함께 쓰는 세션입니다. 롤백하면 이 워크스페이스들의 변경이 모두 되돌려집니다. 롤백할까요?"))
            .Replace("{0}", connection, StringComparison.Ordinal)
            .Replace("{1}", string.Join(", ", workspaces.Select(w => $"「{w}」")), StringComparison.Ordinal);
        dialog.Content = DialogShell.Framed(
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            DialogShell.Buttons(ok, cancel));
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog;
    }

    // ---- 접속 안 됨 탭 실행 ----------------------------------------------------------------------

    private async Task<MissingConnectionChoice> MissingConnectionCoreAsync(string profile, string? current)
    {
        var choice = MissingConnectionChoice.Cancel;
        var dialog = CreateMissingConnectionDialog(profile, current, c => choice = c);
        await ShowAsync(dialog).ConfigureAwait(true);
        return choice;
    }

    /// <summary>
    /// 접속 안 됨 탭 실행 확인 창을 만든다(띄우지는 않는다): 다시 접속 / 지금 접속으로 바꾸기(<paramref name="current"/> 가 있을 때만) / 취소.
    /// 단추를 누르면 <paramref name="chosen"/> 을 부르고 창을 닫는다. 「취소」·창 닫기는 부르지 않는다.
    /// </summary>
    internal static Window CreateMissingConnectionDialog(string profile, string? current, Action<MissingConnectionChoice> chosen)
    {
        var dialog = NewWindow("접속 안 됨");
        var reconnect = new Button { Content = "다시 접속", MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        reconnect.Click += (_, _) =>
        {
            chosen(MissingConnectionChoice.Reconnect);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new List<Button> { reconnect };
        if (current is not null)
        {
            // 다른 DB 에서 실행하게 되므로 바꿀 접속 이름을 단추에 밝힌다.
            var useCurrent = new Button { Content = $"「{current}」 로 바꾸기" };
            useCurrent.Click += (_, _) =>
            {
                chosen(MissingConnectionChoice.UseCurrent);
                dialog.Close();
            };
            buttons.Add(useCurrent);
        }

        buttons.Add(cancel);
        var text = UiText.Get("Execution.missing.message", "이 탭의 접속 「{0}」 이(가) 열려 있지 않습니다. 어떻게 할까요?")
            .Replace("{0}", profile, StringComparison.Ordinal);
        dialog.Content = DialogShell.Framed(
            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            DialogShell.Buttons([.. buttons]));
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog;
    }

    // ---- DML 미리보기 ------------------------------------------------------------------------

    private async Task<bool> PreviewCoreAsync(string text, bool commit)
    {
        // 어느 동작인지 제목·주 단추·안내 글로 밝힌다(툴바 단추가 아이콘뿐이라 확인 창에서 구분해야 한다).
        var action = commit ? "반영 후 커밋" : "반영(커밋 안 함)";
        var dialog = NewWindow("DML 미리보기 · " + action);
        bool accepted = false;
        var ok = new Button { Content = action };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var box = new SqlCodeView { Text = text, MinWidth = 520, MinHeight = 200, MaxHeight = 420 };
        DialogShell.AutomationName(box, "DML 미리보기");
        var hint = new TextBlock
        {
            Text = commit ? "아래 DML 을 실행한 뒤 바로 커밋합니다." : "아래 DML 만 실행하고 커밋하지 않습니다. 커밋 또는 롤백을 해야 확정됩니다.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        };
        dialog.Content = DialogShell.Framed(new StackPanel { Spacing = 8, Children = { hint, box } }, DialogShell.Buttons(ok, cancel));
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

    private Task ShowAsync(Window dialog) => DialogShell.ShowAsync(dialog, Owner);
}
