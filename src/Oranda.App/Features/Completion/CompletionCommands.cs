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
/// 편집기(TSK-04-02) 파일을 고치지 않고 모든 SQL 편집기 탭에 붙이기 위해서다. 처리기는 글자가 <c>.</c> 인지만 보고 곧바로 돌아간다.
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
    }

    private static void OnTextInput(InputElement sender, TextInputEventArgs e)
    {
        if (e.Text == "." && ReferenceEquals(sender, e.Source))
        {
            OnDot(sender);
        }
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
/// 활성 SQL 편집기 탭(<see cref="SqlEditorHost.Active"/>)의 편집 영역에 창을 연다.
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
        var tab = Host.Active;
        if (tab?.Surface is not SqlEditorView view)
        {
            return;
        }

        var area = view.TextEditorControl.TextArea;
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
