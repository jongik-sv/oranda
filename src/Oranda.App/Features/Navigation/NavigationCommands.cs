using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Settings;
using Oranda.Core.Formatting;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// TSK-04-06 정의로 이동·객체 정보·참조 찾기·뒤로·앞으로 명령 처리기(commands.md 7장). 한 클래스가 <see cref="CommandContext.CommandId"/> 로 나눠 처리한다.
/// 「객체 정보 보기」는 커서 아래가 테이블·뷰면 테이블 레이아웃 창(<see cref="TableLayoutWindow"/>, TSK-13-32), 아니면 요약 팝업을 연다.
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
                        // 테이블 레이아웃 창의 쉼표 위치 = 설정 「SQL 포맷」의 목록 쉼표(TSK-13-38).
                        var comma = info.Layout is null ? CommaPosition.Trailing : await CommaAsync(cancellationToken).ConfigureAwait(true);
                        Show(tab, info, comma);
                    }

                    break;
                }

            case "oranda.nav.findReferences":
                {
                    var refs = NavigationService.FindReferences(tab, tab.CaretOffset);
                    if (refs is not null)
                    {
                        Show(tab, refs.Summary, CommaPosition.Trailing);
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

    /// <summary>시험용: 쉼표 위치를 바꾼다(기본 = 설정의 기본 포매터 프로필).</summary>
    internal Func<CancellationToken, Task<CommaPosition>>? CommaProvider { get; set; }

    /// <summary>테이블 레이아웃 창을 열 때 받은 옵션(시험이 본다).</summary>
    internal TableLayoutOptions? LastLayoutOptions { get; private set; }

    private async Task<CommaPosition> CommaAsync(CancellationToken ct)
    {
        if (CommaProvider is not null)
        {
            return await CommaProvider(ct).ConfigureAwait(true);
        }

        try
        {
            var o = await FormatterIds.ActiveOptionsAsync(
                _services?.GetService(typeof(ISettingsStore)) as ISettingsStore,
                _services?.GetService(typeof(IFormatterProfileStore)) as IFormatterProfileStore,
                ct).ConfigureAwait(true);
            return o.CommaPosition;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CommaPosition.Trailing; // 프로필을 읽지 못해도 창은 연다.
        }
    }

    private void Show(SqlEditorViewModel tab, ObjectInfo info, CommaPosition comma)
    {
        if (Presenter is not null)
        {
            Presenter(tab, info);
            return;
        }

        if (info.Layout is { } layout)
        {
            TableLayoutWindow.Show(NewSession(tab, layout, comma));
            return;
        }

        ObjectInfoPopup.Show(tab, info);
    }

    // 확인란 4개는 설정에서 읽고(설정이 없는 시험은 기본값), 바뀌면 바로 저장해 다음 팝업에도 유지한다.
    private TableLayoutSession NewSession(SqlEditorViewModel tab, TableLayoutModel layout, CommaPosition comma)
    {
        var settings = _services?.GetService(typeof(SettingsService)) as SettingsService;
        var options = TableLayoutOptions.From(settings?.Current.TableLayout ?? new TableLayoutSettings()) with { Comma = comma };
        LastLayoutOptions = options;
        return new TableLayoutSession(tab, layout, options, settings is null ? null : s => _ = SaveAsync(settings, s));
    }

    private static async Task SaveAsync(SettingsService settings, TableLayoutSettings value)
    {
        try
        {
            await settings.UpdateAsync(s => s with { TableLayout = value with { AuditColumns = s.TableLayout.AuditColumns } }).ConfigureAwait(true); // 감사 목록은 설정 창에서만 바꾼다
        }
        catch (IOException)
        {
            // 저장하지 못해도 이번 창의 옵션은 그대로 쓴다.
        }
    }
}

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="NavigationCommandHandler"/>.</summary>
public static class NavigationCommands
{
}
