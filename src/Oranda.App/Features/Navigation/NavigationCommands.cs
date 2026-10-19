using Oranda.App.Features.SqlEditor;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// TSK-04-06 정의로 이동·객체 정보·참조 찾기·뒤로·앞으로 명령 처리기(commands.md 7장). 한 클래스가 <see cref="CommandContext.CommandId"/> 로 나눠 처리한다.
/// 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle·ANTLR 어셈블리를 올리지 않기 위해). 편집기 생성자는 건드리지 않는다.
/// </summary>
[OrandaCommand("oranda.nav.goToDefinition", CommandCategory.Navigate, When = "editorFocus", Enablement = "hasSession",
    Placements = ["menu:navigate/goToDefinition@20", "context:editor/goToDefinition@300"])]
[OrandaCommand("oranda.nav.describeObject", CommandCategory.Navigate, When = "editorFocus", Enablement = "hasSession",
    Placements = ["menu:navigate/describeObject@30", "context:editor/describeObject@310"])]
[OrandaCommand("oranda.nav.findReferences", CommandCategory.Navigate, When = "editorFocus",
    Placements = ["menu:navigate/findReferences@40", "context:editor/findReferences@320"])]
[OrandaCommand("oranda.nav.back", CommandCategory.Navigate, When = "editorFocus", Enablement = "canNavigateBack",
    Placements = ["menu:navigate/back@50"])]
[OrandaCommand("oranda.nav.forward", CommandCategory.Navigate, When = "editorFocus", Enablement = "canNavigateForward",
    Placements = ["menu:navigate/forward@60"])]
public sealed class NavigationCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;
    private SqlEditorHost? _host;

    public NavigationCommandHandler()
    {
    }

    public NavigationCommandHandler(IServiceProvider services) => _services = services;

    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    /// <summary>시험용: 서비스를 바꾼다(기본 = <see cref="NavigationService.GetOrCreate"/>).</summary>
    internal NavigationService? Service { get; set; }

    /// <summary>시험용: 팝업 대신 받는다.</summary>
    internal Action<SqlEditorViewModel, ObjectInfo>? Presenter { get; set; }

    /// <summary>시험용: 상태 메시지 받는 곳.</summary>
    internal Action<string>? Notify { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var svc = Service ?? NavigationService.GetOrCreate(_services);
        var tab = Host.Active;
        if (tab is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.nav.goToDefinition":
                {
                    var r = await svc.GoToDefinitionAsync(tab, tab.CaretOffset, cancellationToken).ConfigureAwait(true);
                    Notify?.Invoke(r.Message);
                    break;
                }

            case "oranda.nav.describeObject":
                {
                    var info = await svc.DescribeAsync(tab, tab.CaretOffset, cancellationToken).ConfigureAwait(true);
                    if (info is not null)
                    {
                        Show(tab, info);
                    }

                    break;
                }

            case "oranda.nav.findReferences":
                {
                    var refs = NavigationService.FindReferences(tab, tab.CaretOffset);
                    if (refs is not null)
                    {
                        Show(tab, refs.Summary);
                    }

                    break;
                }

            case "oranda.nav.back":
                svc.Back();
                break;
            case "oranda.nav.forward":
                svc.Forward();
                break;
            default:
                break;
        }
    }

    private void Show(SqlEditorViewModel tab, ObjectInfo info)
    {
        if (Presenter is not null)
        {
            Presenter(tab, info);
            return;
        }

        ObjectInfoPopup.Show(tab, info);
    }
}

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="NavigationCommandHandler"/>.</summary>
public static class NavigationCommands
{
}
