using System.ComponentModel;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「새 테이블 ▸」 명령 처리기(테이블 편집기 설계 7.5·18.7, T8·T16d): 「새 테이블…」(소유자 노드·「테이블」 폴더)·「이 구조로 새 테이블…」(테이블 노드)은
/// 「✎ 새 테이블」 객체 상세 탭을 열고, 「만들던 새 테이블…」(소유자 노드)은 남겨 둔 것·끝난 앱이 남긴 것 목록에서 골라 다시 연다(지우기도, TE-43).
/// 대상은 명령 인자(<see cref="SchemaNode"/>·소유자 이름·<see cref="DbObjectRef"/>), 없으면 스키마 브라우저의 선택 노드.
/// </summary>
[OrandaCommand(NewTableId, CommandCategory.Database, When = "schemaFocus", Enablement = "ownerNodeSelected && !readOnlySession && !noImplMetadata",
    Placements = ["context:schema/detail/newTable@43"])]
[OrandaCommand(NewTableLikeId, CommandCategory.Database, When = "schemaFocus", Enablement = "tableObjectSelected && !readOnlySession && !noImplMetadata",
    Placements = ["context:schema/detail/newTable@44"])]
[OrandaCommand(ResumeId, CommandCategory.Database, When = "schemaFocus", Enablement = "ownerNodeSelected && !readOnlySession && !noImplMetadata",
    Placements = ["context:schema/detail/newTable@45"])]
public sealed class NewTableCommandHandler : ICommandHandler
{
    public const string NewTableId = "oranda.schema.newTable";

    public const string NewTableLikeId = "oranda.schema.newTableLike";

    /// <summary>「만들던 새 테이블…」(설계 18.7, TE-43).</summary>
    public const string ResumeId = "oranda.schema.resumeNewTable";

    private readonly IServiceProvider? _services;

    public NewTableCommandHandler()
    {
    }

    public NewTableCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 지금 세션.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    /// <summary>시험용: 소유자 칸 목록(기본 = 스키마 브라우저의 스키마).</summary>
    internal Func<IReadOnlyList<string>>? Owners { get; set; }

    /// <summary>시험용: 탭 ViewModel 만들기.</summary>
    internal Func<ObjectDetailViewModel>? CreateViewModel { get; set; }

    /// <summary>시험용: 문서 탭 열기(기본 = 셸 도킹).</summary>
    internal Func<ObjectDetailViewModel, string, bool>? OpenDocument { get; set; }

    /// <summary>시험용: 작업 저장(기본 = 서비스의 <see cref="TableDraftHook"/>).</summary>
    internal TableDraftHook? DraftHook { get; set; }

    /// <summary>시험용: 「만들던 새 테이블…」 고르기 창(기본 = <see cref="TableDraftPickerWindow"/>). 고른 저장분, 닫으면 null.</summary>
    internal Func<TableDraftPickerViewModel, Task<TableDraftEntry?>>? ShowPicker { get; set; }

    /// <summary>시험용: 알림(기본 = 편집기 대화상자 알림).</summary>
    internal Func<string, Task>? Notify { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null && OpenDocument is null)
        {
            return;
        }

        var schemaVm = ObjectDetailCommandHandler.FindSchemaBrowser(_services);
        var node = context.Parameter as SchemaNode ?? (context.Parameter is null ? schemaVm?.SelectedNode : null);
        string? owner;
        DbObjectRef? source = null;
        if (context.CommandId == NewTableLikeId)
        {
            source = context.Parameter is DbObjectRef r ? r : SchemaBrowserViewModel.IsTableObject(node) ? node!.Ref : null;
            if (source is not { ObjectType: "TABLE" })
            {
                return;
            }

            owner = source.Owner;
        }
        else
        {
            owner = context.Parameter as string ?? (SchemaBrowserViewModel.IsOwnerNode(node) ? node!.Owner : null);
        }

        // 탭이 읽는 세션은 객체 상세와 같다(스키마 브라우저 세션). 실행만 RunTableEditAsync 가 DDL 용 라우터로 보낸다.
        var session = SessionProvider is { } provide ? provide() : schemaVm?.CurrentSession ?? ConnectionManager.SessionRouter.CurrentOf(_services);
        if (string.IsNullOrEmpty(owner) || session is null)
        {
            return;
        }

        var owners = Owners?.Invoke() ?? schemaVm?.AllSchemaNames ?? [];
        if (context.CommandId == ResumeId)
        {
            await ResumeAsync(session, owner, owners, cancellationToken).ConfigureAwait(true);
            return;
        }

        var vm = NewViewModel();
        if (!NewTableLauncher.OpenTab(_services, vm, OpenDocument))
        {
            return;
        }

        await vm.OpenNewTableAsync(session, owner, owners, source, ct: cancellationToken).ConfigureAwait(true);
    }

    private ObjectDetailViewModel NewViewModel() =>
        CreateViewModel?.Invoke() ?? (_services is null ? new ObjectDetailViewModel() : new ObjectDetailViewModel(_services));

    /// <summary>「만들던 새 테이블…」: 이 접속·소유자의 남겨 둔 것·끝난 앱이 남긴 것을 보이고, 고른 것을 다시 연다(지우기는 창 안에서).</summary>
    private async Task ResumeAsync(ISession session, string owner, IReadOnlyList<string> owners, CancellationToken ct)
    {
        var notify = Notify ?? (message => SqlEditor.SqlEditorHost.Current.Dialogs.ShowMessageAsync(
            TableEditText.T("만들던 새 테이블", "Resume New Table"), message, CancellationToken.None));
        if ((DraftHook ?? _services?.GetService(typeof(INewTableDraftHook)) as TableDraftHook) is not { } hook)
        {
            await notify(TableEditText.T("작업 저장을 쓸 수 없습니다", "Saved drafts are not available")).ConfigureAwait(true);
            return;
        }

        var entries = await hook.Library.ListForOwnerAsync(session.Profile.Name, owner, ct).ConfigureAwait(true);
        if (entries.Count == 0)
        {
            await notify(TableEditText.T($"{owner} 에 남겨 둔 새 테이블이 없습니다", $"There is no kept new table for {owner}")).ConfigureAwait(true);
            return;
        }

        var picker = new TableDraftPickerViewModel(owner, entries, (entry, token) => hook.Library.DeleteAsync(entry, token));
        if (await (ShowPicker ?? TableDraftPickerWindow.ShowAsync)(picker).ConfigureAwait(true) is not { } chosen)
        {
            return;
        }

        if (await hook.Library.GetAsync(chosen.Id, ct).ConfigureAwait(true) is not { } stored)
        {
            await notify(TableEditText.T("그 작업을 읽지 못했습니다(지워졌거나 깨졌습니다)", "Could not read that draft (deleted or damaged)")).ConfigureAwait(true);
            return;
        }

        if (!await NewTableLauncher.OpenStoredAsync(_services, hook, session, stored, owners, NewViewModel, OpenDocument, ct).ConfigureAwait(true))
        {
            await notify(TableEditText.T("다른 탭이나 다른 Oranda 가 먼저 열어 열지 않았습니다", "Another tab or Oranda window opened it first")).ConfigureAwait(true);
        }
    }
}

/// <summary>「✎ 새 테이블」 탭 열기(새 탭·되살린 저장분). 탭 머리 이름은 ViewModel 제목을 따르고, 닫히면 작업 저장 연결을 놓는다.</summary>
internal static class NewTableLauncher
{
    /// <summary>문서 탭을 연다(<paramref name="openOverride"/> = 시험용). 열었으면 true.</summary>
    internal static bool OpenTab(IServiceProvider? services, ObjectDetailViewModel vm, Func<ObjectDetailViewModel, string, bool>? openOverride)
    {
        var title = ObjectDetailViewModel.NewTableTitle;
        if (openOverride is { } open)
        {
            return open(vm, title);
        }

        if (services?.GetService(typeof(IShellUi)) is not IShellUi { Docking: { } docking } || docking.OpenDocument(ObjectDetailCommandHandler.PanelId, vm, title) is not { } doc)
        {
            return false;
        }

        ObjectDetailCommandHandler.CloseDataWithDocument(docking, vm);
        void OnTitle(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ObjectDetailViewModel.Title))
            {
                doc.Title = vm.Title;
            }
        }

        void OnClosed(object? sender, PanelDocument closed)
        {
            if (ReferenceEquals(closed, doc))
            {
                vm.PropertyChanged -= OnTitle;
                docking.DocumentClosed -= OnClosed;
                vm.EndNewTableDraft();
            }
        }

        vm.PropertyChanged += OnTitle;
        docking.DocumentClosed += OnClosed;
        return true;
    }

    /// <summary>
    /// 저장분을 탭으로 다시 연다(작업 공간 복원·「만들던 새 테이블…」): 먼저 이 인스턴스가 쥐고(<see cref="TableDraftHook.ResumeAsync"/>), 탭을 열어 그 모델로 시작한다.
    /// 그 사이 다른 탭·다른 앱이 쥐었거나 지워졌으면 열지 않고 false.
    /// </summary>
    internal static async Task<bool> OpenStoredAsync(IServiceProvider? services, TableDraftHook hook, ISession session, StoredTableDraft stored,
                                                     IReadOnlyList<string> owners, Func<ObjectDetailViewModel> create,
                                                     Func<ObjectDetailViewModel, string, bool>? openOverride, CancellationToken ct)
    {
        if (await hook.ResumeAsync(stored, ct).ConfigureAwait(true) is not { } resumed)
        {
            return false;
        }

        var vm = create();
        if (!OpenTab(services, vm, openOverride))
        {
            await resumed.Link.FinishAsync(NewTableDraftEnd.Kept, ct).ConfigureAwait(true); // 탭을 못 열었다: 놓는다(목록에 다시 보인다)
            return false;
        }

        return await vm.OpenNewTableAsync(session, stored.Draft.Owner, owners, restore: stored.Draft, resumed: resumed, ct: ct).ConfigureAwait(true);
    }
}
