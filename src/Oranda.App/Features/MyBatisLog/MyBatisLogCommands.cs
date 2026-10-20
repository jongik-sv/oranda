using Avalonia.Input.Platform;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Shell;

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

        var result = MyBatisLogPaste.Apply(tab, tab.Binds, text);
        if (result is null)
        {
            log.AddMessage(MessageKind.Warning, "클립보드에서 MyBatis 로그(Preparing/Parameters)를 찾지 못했거나 편집기가 읽기 전용입니다.");
            return;
        }

        log.AddMessage(MessageKind.Info, $"MyBatis 로그의 SQL {result.Statements}개를 넣고 바인드 {result.Binds}개를 채웠습니다.");
    }
}
