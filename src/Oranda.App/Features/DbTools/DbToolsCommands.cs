using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.DbTools;

/// <summary>
/// TSK-07-04 객체 관리·검색·DB 정보·DDL 일괄 추출 명령 처리기 (commands.md 7장 「데이터베이스」).
/// 문서 도구 창을 열고 지금 작업 세션으로 채운다. 변경 동작(컴파일·Purge·Flashback)은 <see cref="ObjectSearchViewModel"/> 이
/// 실행 엔진(정책 관문)으로 보낸다. 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaCommand("oranda.schema.compileInvalid", CommandCategory.Database, Enablement = "hasSession && !readOnlySession",
    Placements = ["menu:database/compileInvalid@1020", "context:schema/compileInvalid@50"])]
[OrandaCommand("oranda.schema.recyclebinPurge", CommandCategory.Database, When = "schemaFocus", Enablement = "recyclebinNodeSelected && !readOnlySession",
    Placements = ["context:schema/recyclebin@60"])]
[OrandaCommand("oranda.schema.recyclebinFlashback", CommandCategory.Database, When = "schemaFocus", Enablement = "recyclebinNodeSelected && !readOnlySession",
    Placements = ["context:schema/recyclebin@61"])]
[OrandaCommand("oranda.schema.objectSearch", CommandCategory.Database, Enablement = "hasSession",
    Placements = ["menu:database/objectSearch@1000"])]
[OrandaCommand("oranda.db.info", CommandCategory.Database, Enablement = "hasSession",
    Icon = "database", Placements = ["menu:database/dbInfo@1010", "toolbar:main/dbInfo@530"])]
[OrandaCommand("oranda.db.ddlExtract", CommandCategory.Database, Enablement = "hasSession",
    Placements = ["menu:database/ddlExtract@1030"])]
public sealed class DbToolsCommandHandler : ICommandHandler
{
    public const string DbInfoPanelId = "oranda.panel.dbInfo";
    public const string ObjectSearchPanelId = "oranda.panel.objectSearch";
    public const string DdlExtractPanelId = "oranda.panel.ddlExtract";

    private readonly IServiceProvider? _services;

    public DbToolsCommandHandler()
    {
    }

    public DbToolsCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 문서 탭을 여는 방법(panelId, 새 ViewModel, 제목 → 실제 화면의 ViewModel)을 바꾼다. 기본 = 셸 도킹의 <c>OpenDocument</c>.</summary>
    internal Func<string, ToolPanelViewModelBase, string, ToolPanelViewModelBase?>? OpenDocument { get; set; }

    /// <summary>시험용: 지금 세션을 바꾼다.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    /// <summary>시험용: 지금 활성 문서의 ViewModel 을 바꾼다.</summary>
    internal Func<ToolPanelViewModelBase?>? ActivePanelProvider { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null && OpenDocument is null)
        {
            return;
        }

        var session = SessionProvider?.Invoke()
            ?? ConnectionManager.SessionRouter.CurrentOf(_services);
        if (session is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.db.info":
                await ShowDbInfoAsync(session, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.schema.objectSearch":
                (Open(ObjectSearchPanelId, NewObjectSearch(), "객체 검색") as ObjectSearchViewModel)?.Attach(session);
                break;
            case "oranda.schema.compileInvalid":
                await CompileInvalidAsync(WriteSession(session), context.Parameter as string, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.schema.recyclebinPurge":
            case "oranda.schema.recyclebinFlashback":
                await ChangeRecycleBinAsync(WriteSession(session), context, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.db.ddlExtract":
                (Open(DdlExtractPanelId, NewDdlExtract(), "DDL 일괄 추출") as DdlExtractViewModel)?.Attach(session);
                break;
        }
    }

    /// <summary>
    /// DDL(ALTER … COMPILE·PURGE·FLASHBACK — 암묵 커밋)을 보내는 명령의 세션: 활성 SQL 작업 공간이 같은 접속을 쓰면 그 작업 공간의 세션(TSK-14-08).
    /// 지금 접속(프로필의 첫 세션)이 다른 작업 공간의 미커밋을 들고 있어도 확정하지 않는다. 시험이 세션을 정했으면 그대로.
    /// </summary>
    private ISession WriteSession(ISession session) =>
        SessionProvider is null ? ConnectionManager.SessionRouter.ForTool(_services, session) ?? session : session;

    private async Task ShowDbInfoAsync(ISession session, CancellationToken ct)
    {
        if (Open(DbInfoPanelId, NewDbInfo(), "DB 정보") is DbInfoViewModel vm)
        {
            await vm.LoadAsync(session, ct).ConfigureAwait(true);
        }
    }

    private async Task CompileInvalidAsync(ISession session, string? owner, CancellationToken ct)
    {
        if (Open(ObjectSearchPanelId, NewObjectSearch(), "객체 검색") is not ObjectSearchViewModel vm)
        {
            return;
        }

        vm.Attach(session);
        if (!string.IsNullOrWhiteSpace(owner))
        {
            vm.Owner = owner;
        }

        await vm.CompileInvalidAsync(ct).ConfigureAwait(true);
    }

    private async Task ChangeRecycleBinAsync(ISession session, CommandContext context, CancellationToken ct)
    {
        var vm = (ActivePanelProvider?.Invoke() ?? ActivePanel()) as ObjectSearchViewModel;
        vm ??= Open(ObjectSearchPanelId, NewObjectSearch(), "객체 검색") as ObjectSearchViewModel;
        if (vm is null)
        {
            return;
        }

        vm.Attach(session);
        var item = context.Parameter switch
        {
            RecycleBinItem i => i,
            RecycleBinRow r => r.Item,
            _ => vm.SelectedRecycle?.Item,
        };
        if (item is null)
        {
            // 대상이 없으면 휴지통 목록만 보여 준다(항목을 고른 뒤 다시 실행).
            await vm.LoadRecycleBinAsync(ct).ConfigureAwait(true);
            return;
        }

        if (context.CommandId == "oranda.schema.recyclebinPurge")
        {
            await vm.PurgeAsync(item, ct).ConfigureAwait(true);
        }
        else
        {
            await vm.FlashbackAsync(item, ct).ConfigureAwait(true);
        }
    }

    private DbInfoViewModel NewDbInfo() => _services is null ? new DbInfoViewModel() : new DbInfoViewModel(_services);

    private ObjectSearchViewModel NewObjectSearch() => _services is null ? new ObjectSearchViewModel() : new ObjectSearchViewModel(_services);

    private DdlExtractViewModel NewDdlExtract() => _services is null ? new DdlExtractViewModel() : new DdlExtractViewModel(_services);

    /// <summary>문서 탭을 열고(이미 열려 있으면 그 탭) 화면이 쓰는 ViewModel 을 돌려준다.</summary>
    private ToolPanelViewModelBase? Open(string panelId, ToolPanelViewModelBase vm, string title)
    {
        if (OpenDocument is not null)
        {
            return OpenDocument(panelId, vm, title);
        }

        return (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.OpenDocument(panelId, vm, title)?.Panel as ToolPanelViewModelBase;
    }

    private ToolPanelViewModelBase? ActivePanel()
        => (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ActiveDocument?.Panel as ToolPanelViewModelBase;
}

/// <summary>자리 표시 클래스(ui-contract 소유 파일 규약). 실제 처리기는 <see cref="DbToolsCommandHandler"/>.</summary>
public static class DbToolsCommands
{
}
