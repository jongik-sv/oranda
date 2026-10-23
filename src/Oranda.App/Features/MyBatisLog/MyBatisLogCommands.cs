using Avalonia.Input.Platform;
using Oranda.App.Features.Execution;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.LogPaste;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisLog;

/// <summary>
/// 「MyBatis 로그 붙여 넣기」 명령 처리기(TSK-13-03, <c>oranda.edit.pasteMyBatisLog</c>). 클립보드 글을 읽어 <see cref="MyBatisLogPaste"/> 에 넘긴다.
/// 기본 단축키는 없다(편집 메뉴·명령 팔레트).
/// </summary>
[OrandaCommand("oranda.edit.pasteMyBatisLog", CommandCategory.Edit, When = "editorFocus", Enablement = "sqlDocActive",
    Placements = ["menu:edit/pasteMyBatisLog@55"])]
public sealed class MyBatisLogCommandHandler(ServiceContainer container) : ICommandHandler
{
    private SqlEditorHost? _host;

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Host.Active is not { } tab)
        {
            return;
        }

        var bus = container.GetService<IEventBus>();
        var log = ExecutionLog.For(bus);
        var clipboard = container.GetService<ShellController>()?.MainWindow?.Clipboard;
        var text = clipboard is null ? null : await clipboard.TryGetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text))
        {
            log.AddMessage(MessageKind.Warning, "클립보드에 MyBatis 로그가 없습니다.");
            return;
        }

        // MyBatis 문서에서는 logger 로 문장을 찾아 열고 값을 시나리오로 저장한다(MB-09). 일반 SQL 탭은 아래 지금 동작 그대로다.
        if (Host.WorkspaceOf(tab)?.MyBatis is { } session
            && container.GetService<IMyBatisEvaluator>() is { } evaluator
            && container.GetService<IMyBatisScenarioStore>() is { } store)
        {
            await PasteIntoMyBatisAsync(log, text, session, evaluator, store, cancellationToken).ConfigureAwait(true);
            return;
        }

        var result = MyBatisLogPaste.Apply(tab, tab.Binds, text);
        if (result is null)
        {
            log.AddMessage(MessageKind.Warning, "클립보드에서 MyBatis 로그(Preparing/Parameters)를 찾지 못했거나 편집기가 읽기 전용입니다.");
            return;
        }

        log.AddMessage(MessageKind.Info, $"MyBatis 로그의 SQL {result.Statements}개를 넣고 바인드 {result.Binds}개를 채웠습니다.");
    }

    private async Task PasteIntoMyBatisAsync(
        ExecutionLog log, string text, IMyBatisWorkspaceSession session, IMyBatisEvaluator evaluator, IMyBatisScenarioStore store, CancellationToken ct)
    {
        var flow = new MyBatisLogPasteFlow(Host, evaluator, store, Host.Dialogs);
        MyBatisLogFlowResult? result;
        try
        {
            result = await flow.RunAsync(text, session, ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.AddMessage(MessageKind.Error, "MyBatis 로그 붙여 넣기에 실패했습니다: " + e.Message);
            return;
        }

        if (result is null)
        {
            log.AddMessage(MessageKind.Warning, "클립보드에서 MyBatis 로그(Preparing/Parameters)를 찾지 못했습니다.");
            return;
        }

        if (result.Cancelled)
        {
            log.AddMessage(MessageKind.Info, "후보 고르기를 취소해 MyBatis 로그 붙여 넣기를 멈췄습니다.");
            return;
        }

        foreach (var o in result.Opened)
        {
            log.AddMessage(MessageKind.Info, $"MyBatis 로그로 문장 {o.StatementId} 을 열고 시나리오 「{o.ScenarioName}」 을 저장했습니다.");
        }

        if (result.Fallback > 0)
        {
            log.AddMessage(MessageKind.Warning, $"문장을 찾거나 값을 맞추지 못한 로그 {result.Fallback}개는 일반 SQL 탭으로 붙여 넣었습니다.");
        }
    }
}
