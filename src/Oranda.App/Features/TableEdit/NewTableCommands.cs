using System.ComponentModel;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「새 테이블…」(소유자 노드·「테이블」 폴더)·「이 구조로 새 테이블…」(테이블 노드) 명령 처리기(테이블 편집기 설계 7.5, T8). 「✎ 새 테이블」 객체 상세 탭을 연다.
/// 대상은 명령 인자(<see cref="SchemaNode"/>·소유자 이름·<see cref="DbObjectRef"/>), 없으면 스키마 브라우저의 선택 노드. 세션은 열 순서 바꾸기와 같이 DDL 용 라우터.
/// </summary>
[OrandaCommand(NewTableId, CommandCategory.Database, When = "schemaFocus", Enablement = "ownerNodeSelected && !readOnlySession && !noImplMetadata",
    Placements = ["context:schema/detail/newTable@43"])]
[OrandaCommand(NewTableLikeId, CommandCategory.Database, When = "schemaFocus", Enablement = "tableObjectSelected && !readOnlySession && !noImplMetadata",
    Placements = ["context:schema/detail/newTable@44"])]
public sealed class NewTableCommandHandler : ICommandHandler
{
    public const string NewTableId = "oranda.schema.newTable";

    public const string NewTableLikeId = "oranda.schema.newTableLike";

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

        var vm = CreateViewModel?.Invoke() ?? (_services is null ? new ObjectDetailViewModel() : new ObjectDetailViewModel(_services));
        var owners = Owners?.Invoke() ?? schemaVm?.AllSchemaNames ?? [];
        if (!Open(vm))
        {
            return;
        }

        await vm.OpenNewTableAsync(session, owner, owners, source, ct: cancellationToken).ConfigureAwait(true);
    }

    /// <summary>탭을 연다. 탭 머리 이름은 ViewModel 제목을 따른다(실행 성공 뒤 「소유자.이름」). 닫히면 작업 저장에 알린다.</summary>
    private bool Open(ObjectDetailViewModel vm)
    {
        var title = ObjectDetailViewModel.NewTableTitle;
        if (OpenDocument is { } open)
        {
            return open(vm, title);
        }

        if (_services?.GetService(typeof(IShellUi)) is not IShellUi { Docking: { } docking } || docking.OpenDocument(ObjectDetailCommandHandler.PanelId, vm, title) is not { } doc)
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
}
