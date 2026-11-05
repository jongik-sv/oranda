using System.Runtime.CompilerServices;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Completion;

/// <summary>
/// TSK-04-05 문맥 자동완성 — 명령 ID 와 편집기 입력 연결.
/// <c>.</c> 입력 자동 열기는 앱 어셈블리가 올라올 때 한 번 거는 입력 처리기(<see cref="Install"/>)로 붙인다 —
/// 편집기(TSK-04-02) 파일을 고치지 않고 모든 SQL 편집기 탭에 붙이기 위해서다. 처리기는 글자가 <c>.</c> 인지와 마지막 목록이 다 담지 못했는지만 보고 곧바로 돌아간다.
/// </summary>
public static class CompletionCommands
{
    public const string TriggerCompletion = "oranda.edit.triggerCompletion";

    public const string ParameterHints = "oranda.edit.parameterHints";

    private static int s_installed;

    [ModuleInitializer]
    internal static void Initialize() => Install();

    /// <summary>
    /// 모든 편집기의 <c>.</c> 입력을 듣는다(한 번만). <see cref="InputElement"/> 단위로 걸어 AvaloniaEdit·Language 형식을 시작 때 올리지 않는다.
    /// 편집기가 글자를 넣은 뒤 불리도록 처리된 이벤트도 받는다.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 1) == 1)
        {
            return;
        }

        InputElement.TextInputEvent.AddClassHandler<InputElement>(OnTextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.GotFocusEvent.AddClassHandler<TextArea>(OnTextAreaGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TextArea>(OnTextAreaKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// 주 창 터널 처리기가 이미 명령으로 보낸 키(실행 키·저장 등 F 키나 Ctrl·Alt·Cmd 조합)면 열려 있는 목록을 닫는다.
    /// 목록이 쓰는 키(방향·Enter·Tab·Esc·Home·End)는 수식키가 없어 여기에 걸리지 않고, 목록이 그대로 받아 항목을 넣거나 거른다.
    /// 실행 명령이 목록 위에서 쿼리를 돌리게 두면 뒤늦게 항목이 들어가는 일이 생긴다.
    /// </summary>
    private static void OnTextAreaKeyDownTunnel(TextArea sender, KeyEventArgs e)
    {
        var controller = CompletionController.Shared;
        if (e.Handled && IsCommandKey(e.Key, e.KeyModifiers))
        {
            // 명령으로 간 키는 문서·커서를 바꾸지 않으므로, 기다리던 자동 열기를 여기서 버리지 않으면 쿼리를 돌린 뒤에 목록이 뜬다.
            controller.CancelAutoOpen();
            controller.CloseCompletion();
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && controller.CurrentWindow is not null)
        {
            controller.NoteEscape(sender); // 목록이 닫힌 뒤에도 같은 단어에서는 다시 열지 않는다
        }
    }

    private static bool IsCommandKey(Key key, KeyModifiers modifiers)
        => key is >= Key.F1 and <= Key.F24 || (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;

    private static WeakReference<TextArea>? s_focusedArea;

    private static void OnTextAreaGotFocus(TextArea sender, FocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, e.Source))
        {
            s_focusedArea = new WeakReference<TextArea>(sender);
        }
    }

    /// <summary>
    /// 키보드 포커스가 지금 들어 있는 SQL 편집기(SQL 탭·PL/SQL 탭·MyBatis 문장 탭 공통). 없으면 null.
    /// 단축키는 포커스된 편집기에 창을 열어야 하므로, 활성 SQL 탭 대신 마지막으로 포커스를 받은 편집 영역을 쓴다.
    /// </summary>
    internal static (TextArea Area, SqlEditorViewModel? Tab)? FocusedEditor()
    {
        if (s_focusedArea is not { } weak || !weak.TryGetTarget(out var area) || !area.IsKeyboardFocusWithin
            || area.FindAncestorOfType<SqlEditorView>() is not { } view)
        {
            return null;
        }

        return (area, view.DataContext as SqlEditorViewModel);
    }

    private static void OnTextInput(InputElement sender, TextInputEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source))
        {
            return;
        }

        if (e.Text == ".")
        {
            CompletionController.Shared.CancelAutoOpen();
            OnDot(sender);
        }
        else if (IsNameText(e.Text))
        {
            if (CompletionController.Shared.LastResult is { IsIncomplete: true })
            {
                OnNameTyped(sender);
            }

            OnNameTypedAutoOpen(sender);
        }
        else
        {
            CompletionController.Shared.CancelAutoOpen(); // 이름이 끊기는 글자(공백·쉼표 등)
        }
    }

    /// <summary>
    /// 테이블 이름 자리에서 2자째부터 잠시 멈추면 목록을 열도록 건다. 읽기 전용 편집기·추가 커서·열 블록에서는 열지 않는다.
    /// 조건과 시간은 컨트롤러가 본다(<see cref="CompletionController.ScheduleAutoOpen"/>).
    /// </summary>
    private static void OnNameTypedAutoOpen(InputElement sender)
    {
        if (sender is not TextArea area || area.FindAncestorOfType<SqlEditorView>() is not { } view)
        {
            return;
        }

        CompletionController.Shared.ScheduleAutoOpen(area, view.DataContext as SqlEditorViewModel, () => IsAutoOpenBlocked(area, view));
    }

    private static bool IsAutoOpenBlocked(TextArea area, SqlEditorView view)
        => view.TextEditorControl.IsReadOnly || (view.DataContext as SqlEditorViewModel)?.IsReadOnly == true
            || view.Carets?.HasExtra == true || area.Selection is RectangleSelection;

    private static bool IsNameText(string? text) => !string.IsNullOrEmpty(text) && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '#');

    /// <summary>열린 목록은 그 안에서만 거른다. 다 담지 못한 목록은 지금 글자로 다시 물어 채운다.</summary>
    private static void OnNameTyped(InputElement sender)
    {
        var controller = CompletionController.Shared;
        if (sender is not TextArea area || !controller.NeedsRefill(area) || area.FindAncestorOfType<SqlEditorView>() is not { } view)
        {
            return;
        }

        var tab = view.DataContext as SqlEditorViewModel;
        Dispatcher.UIThread.Post(() => _ = controller.ShowCompletionAsync(area, tab, automatic: true));
    }

    private static void OnDot(InputElement sender)
    {
        var controller = CompletionController.Shared;
        if (!controller.AutoTriggerOnDot || sender is not TextArea area || area.FindAncestorOfType<SqlEditorView>() is not { } view)
        {
            return;
        }

        var tab = view.DataContext as SqlEditorViewModel;
        Dispatcher.UIThread.Post(() => _ = controller.ShowCompletionAsync(area, tab, automatic: true));
    }
}

/// <summary>
/// <c>oranda.edit.triggerCompletion</c>(Ctrl+Space, macOS Option+Esc)·<c>oranda.edit.parameterHints</c>(Ctrl+Shift+Space) 처리기.
/// 포커스된 편집기(PL/SQL·MyBatis 문장 탭 포함)의 편집 영역에 창을 연다. 포커스된 편집기를 모르면 활성 SQL 편집기 탭(<see cref="SqlEditorHost.Active"/>)에 연다.
/// </summary>
[OrandaCommand(CompletionCommands.TriggerCompletion, CommandCategory.Edit, Title = "자동완성 열기", When = "editorFocus",
    Placements = ["menu:edit/assist/triggerCompletion@240"])]
[OrandaCommand(CompletionCommands.ParameterHints, CommandCategory.Edit, Title = "인자 도움말", When = "editorFocus", Enablement = "hasSession",
    Placements = ["menu:edit/assist/parameterHints@241"])]
public sealed class CompletionCommandHandler : ICommandHandler
{
    private SqlEditorHost? _host;
    private CompletionController? _controller;

    // 매개변수 없는 생성자만 둔다(EditorContext 와 같은 이유: 시작 때 지연 어셈블리를 훑지 않게).

    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public CompletionController Controller
    {
        get => _controller ?? CompletionController.Shared;
        init => _controller = value;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TextArea area;
        SqlEditorViewModel? tab;
        if (CompletionCommands.FocusedEditor() is { } focused)
        {
            (area, tab) = focused;
        }
        else
        {
            tab = Host.Active;
            if (tab?.Surface is not SqlEditorView view)
            {
                return;
            }

            area = view.TextEditorControl.TextArea;
        }

        if (context.CommandId == CompletionCommands.ParameterHints)
        {
            await Controller.ShowParameterHintsAsync(area, tab, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            await Controller.ShowCompletionAsync(area, tab, automatic: false, cancellationToken).ConfigureAwait(true);
        }
    }
}
