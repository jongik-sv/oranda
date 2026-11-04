using Oranda.App.Features.DataTransfer;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Help;

/// <summary>도움말 &gt; 사용자 문서(F1): 지금 포커스가 있는 화면에 해당하는 매뉴얼 절을 기본 브라우저로 연다. 화면을 모르면 매뉴얼 첫 쪽을 연다.</summary>
[OrandaCommand("oranda.help.docs", CommandCategory.Help, Placements = ["menu:help/docs@870"])]
public sealed class UserDocsCommandHandler : ICommandHandler
{
    /// <summary>주소를 기본 브라우저로 연다. 시험이 바꿔 끼운다.</summary>
    public Func<string, bool> OpenUrl { get; set; } = FileLauncher.Open;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var slot = (context.Keys as ContextKeyService)?.FocusSlot;
        var url = ManualCatalog.UrlFor(ManualCatalog.PathFor(slot));
        if (!OpenUrl(url))
        {
            System.Diagnostics.Trace.TraceWarning("사용자 문서를 기본 브라우저로 열지 못했다: " + url);
        }

        return Task.CompletedTask;
    }
}
