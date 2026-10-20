using Oranda.App.Features.SqlEditor;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Layout;

/// <summary>
/// 이름 붙인 레이아웃(Toad Desktop Layout·PL/SQL Developer 레이아웃 저장): 지금 도구 창 배치를 이름으로 저장하고,
/// 저장한 배치로 바꾸고, 지운다. 문서(작업 공간 등)는 그대로 두고 도구 창 배치만 바뀐다. 「레이아웃 초기화」 옆에 둔다.
/// </summary>
[OrandaCommand("oranda.window.saveLayoutAs", CommandCategory.Window, Placements = ["menu:window/saveLayoutAs@320"])]
[OrandaCommand("oranda.window.applyLayout", CommandCategory.Window, Placements = ["menu:window/applyLayout@330"])]
[OrandaCommand("oranda.window.deleteLayout", CommandCategory.Window, Placements = ["menu:window/deleteLayout@340"])]
public sealed class NamedLayoutCommandHandler(IShellUi ui, NamedLayoutStore store) : ICommandHandler
{
    /// <summary>이름 묻기·고르기 대화상자. 시험은 가짜로 바꾼다.</summary>
    public IEditorDialogs Dialogs { get; init; } = new AvaloniaEditorDialogs();

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.CommandId)
        {
            case "oranda.window.saveLayoutAs":
                var name = context.Parameter as string
                    ?? await Dialogs.PromptAsync("레이아웃 이름", "내 레이아웃", cancellationToken).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    await store.SaveAsync(name, ui.Docking.Capture(), cancellationToken).ConfigureAwait(true);
                }

                break;
            case "oranda.window.applyLayout":
                if (await ChooseAsync("적용할 레이아웃", context, cancellationToken).ConfigureAwait(true) is { } apply
                    && await store.LoadAsync(apply, cancellationToken).ConfigureAwait(true) is { } layout)
                {
                    ui.Docking.Apply(layout);
                }

                break;
            case "oranda.window.deleteLayout":
                if (await ChooseAsync("지울 레이아웃", context, cancellationToken).ConfigureAwait(true) is { } remove)
                {
                    await store.DeleteAsync(remove, cancellationToken).ConfigureAwait(true);
                }

                break;
        }
    }

    private async Task<string?> ChooseAsync(string title, CommandContext context, CancellationToken ct)
    {
        if (context.Parameter is string given)
        {
            return given;
        }

        var names = await store.ListNamesAsync(ct).ConfigureAwait(true);
        if (names.Count == 0)
        {
            await Dialogs.ShowMessageAsync(title, "저장한 레이아웃이 없습니다. 「창 → 레이아웃 이름 붙여 저장」으로 먼저 저장하세요.", ct).ConfigureAwait(true);
            return null;
        }

        return await Dialogs.ChooseAsync(title, names, ct).ConfigureAwait(true);
    }
}
