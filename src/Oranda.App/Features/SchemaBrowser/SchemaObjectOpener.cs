using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>
/// 스키마 브라우저의 객체를 「해당 편집기」로 연다(SCH-01). PL/SQL 단위(프로시저·함수·패키지·패키지 본문·트리거·타입·타입 본문)는
/// PL/SQL 작업 공간의 편집 탭(서버 소스, <see cref="PlsqlWorkspaceRouter"/>), 그 밖(테이블·뷰·머티리얼라이즈드 뷰·시퀀스·시노님·인덱스 …)은 객체 상세 문서(표·뷰는 「열」 구역이 먼저)다.
/// 종류 나누기는 DB 객체로 이동과 같은 규칙(<see cref="ObjectNavigationCommandHandler.OpenCommandFor"/>)을 쓴다.
/// <list type="bullet">
/// <item>미리보기(VS Code 방식): 미리보기 탭 하나를 돌려 쓴다. 다음 객체는 그 자리에서 새 탭으로 바뀐다(<see cref="DockWorkspace.OpenPreviewDocument"/>).
/// PL/SQL 미리보기는 작업 공간 안 탭이고, 미리보기는 둘을 합쳐 하나다: PL/SQL 미리보기를 열면 고치지 않은 객체 상세 미리보기 문서를 닫고, 반대도 같다.</item>
/// <item>미리보기를 고치기 시작하면(PL/SQL 글 고침, 데이터 탭 편집 켬) 고정 탭이 된다. 고친 내용이 남은 미리보기는 바꿔 끼우지 않는다.</item>
/// <item>같은 접속의 같은 객체(소유자·이름·유형)가 이미 열려 있으면 새로 열지 않고 그 탭을 앞으로 낸다.</item>
/// </list>
/// </summary>
public sealed class SchemaObjectOpener
{
    private readonly IServiceProvider? _services;

    public SchemaObjectOpener(IServiceProvider? services) => _services = services;

    /// <summary>시험용: 문서를 여는 도킹(기본 = 셸 도킹).</summary>
    internal DockWorkspace? Docking
    {
        get => _docking ?? (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking;
        set => _docking = value;
    }

    private DockWorkspace? _docking;

    /// <summary>객체를 여는 문서 종류: PL/SQL 단위는 PL/SQL 작업 공간, 그 밖은 객체 상세.</summary>
    public static string PanelFor(DbObjectRef target) =>
        ObjectNavigationCommandHandler.OpenCommandFor(target) == "oranda.plsql.openSource" ? PlsqlWorkspaceRouter.PanelId : ObjectDetailCommandHandler.PanelId;

    /// <summary>
    /// 객체를 연다. <paramref name="preview"/> 면 미리보기 탭, 아니면 고정 탭(이미 열린 미리보기면 그 탭을 고정한다).
    /// <paramref name="focus"/> 면 연 문서로 키보드 포커스를 옮긴다(아니면 포커스는 그대로 — 트리에서 이어서 훑어본다).
    /// 연 탭(또는 앞으로 낸 탭)을 돌려준다. PL/SQL 은 그 탭이 든 작업 공간 문서다. 셸 도킹이 없으면 null. 소스·상세 읽기 실패는 문서 안 상태 줄에 보인다.
    /// </summary>
    public async Task<PanelDocument?> OpenAsync(DbObjectRef target, ISession session, bool preview, bool focus, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        if (Docking is not { } docking)
        {
            return null;
        }

        var panelId = PanelFor(target);
        if (panelId == PlsqlWorkspaceRouter.PanelId)
        {
            return await OpenPlsqlAsync(docking, target, session, preview, focus, ct).ConfigureAwait(true);
        }

        if (FindOpen(docking, panelId, target, session) is { } open)
        {
            if (!preview)
            {
                docking.Pin(open);
            }

            Show(docking, open, focus);
            return open;
        }

        // 고친 내용이 남은 미리보기는 바꿔 끼우지 않는다: 그 탭은 고정해 두고 새 미리보기 탭을 연다.
        if (preview && docking.PreviewDocument is { } current && IsEdited(current.Panel))
        {
            docking.Pin(current);
        }

        var vm = _services is null ? new ObjectDetailViewModel() : new ObjectDetailViewModel(_services);
        var doc = Dock(docking, panelId, vm, $"{target.Owner}.{target.Name}", preview);
        if (doc is null)
        {
            return null;
        }

        if (preview)
        {
            PlsqlWorkspaceRouter.For(docking, _services).RetirePreviews(); // 미리보기는 하나: PL/SQL 미리보기 탭을 거둔다
        }

        ObjectDetailCommandHandler.CloseDataWithDocument(docking, vm);
        PinWhenEdited(docking, doc, vm.Data, nameof(vm.Data.IsEditing), () => vm.Data.IsEditing);
        Show(docking, doc, focus);
        await LoadAsync(docking, doc, token => vm.OpenAsync(session, target, showDdl: false, token), ct).ConfigureAwait(true);
        return doc;
    }

    /// <summary>
    /// PL/SQL 단위를 작업 공간 탭으로 연다. 어느 작업 공간에든 열려 있으면 그 탭을 앞으로(고정 열기면 고정한다). 미리보기면 고치지 않은 객체 상세 미리보기 문서를 닫는다
    /// (고친 것은 고정). 읽는 중에 탭이 빠지거나(다음 미리보기로 바뀜·닫음) 작업 공간이 닫히면 읽기를 멈춘다.
    /// </summary>
    private async Task<PanelDocument?> OpenPlsqlAsync(DockWorkspace docking, DbObjectRef target, ISession session, bool preview, bool focus, CancellationToken ct)
    {
        var router = PlsqlWorkspaceRouter.For(docking, _services);
        if (router.FindOpen(target, session) is { } open)
        {
            if (!preview)
            {
                open.Workspace.Pin(open.Tab);
            }

            router.Show(open.Workspace, open.Tab, focus);
            return router.DocumentOf(open.Workspace);
        }

        var vm = _services is null ? new PlsqlEditorViewModel() : new PlsqlEditorViewModel(_services);
        if (router.Place(vm, preview: preview, focus: focus) is not { } doc)
        {
            return null;
        }

        if (preview && docking.PreviewDocument is { } detail)
        {
            if (IsEdited(detail.Panel))
            {
                docking.Pin(detail);
            }
            else
            {
                docking.CloseDocument(detail);
            }
        }

        await router.LoadAsync(vm, token => vm.OpenAsync(session, target, token), ct).ConfigureAwait(true);
        return doc;
    }

    private static PanelDocument? Dock(DockWorkspace docking, string panelId, object vm, string title, bool preview) =>
        preview ? docking.OpenPreviewDocument(panelId, vm, title) : docking.OpenDocument(panelId, vm, title);

    private static void Show(DockWorkspace docking, PanelDocument doc, bool focus)
    {
        if (focus)
        {
            docking.FocusDocument(doc);
        }
        else
        {
            docking.Activate(doc);
        }
    }

    /// <summary>
    /// 내용을 읽는다. 읽는 중에 탭이 닫히면(다음 미리보기로 바뀜·사용자가 닫음) 읽기를 멈춘다 — 빠르게 훑어볼 때
    /// 지나간 객체의 조회가 세션 앞에 줄 서 있지 않게.
    /// </summary>
    private static async Task LoadAsync(DockWorkspace docking, PanelDocument doc, Func<CancellationToken, Task> load, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void OnClosed(object? sender, PanelDocument closed)
        {
            if (ReferenceEquals(closed, doc))
            {
                cts.Cancel();
            }
        }

        docking.DocumentClosed += OnClosed;
        try
        {
            await load(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            docking.DocumentClosed -= OnClosed;
        }
    }

    /// <summary>미리보기 탭에서 고치기 시작하면(글 고침·편집 켬) 고정한다. 고정된 뒤에는 더 보지 않는다.</summary>
    private static void PinWhenEdited(DockWorkspace docking, PanelDocument doc, INotifyPropertyChanged source, string property, Func<bool> edited)
    {
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != property || !edited())
            {
                return;
            }

            source.PropertyChanged -= OnChanged;
            docking.Pin(doc);
        }

        if (doc.IsPreview)
        {
            source.PropertyChanged += OnChanged;
        }
    }

    /// <summary>바꿔 끼우면 잃는 것이 있는 문서: 반영하지 않은 데이터 편집(PL/SQL 미리보기 탭의 고친 글은 작업 공간이 본다).</summary>
    private static bool IsEdited(object panel) => panel switch
    {
        ObjectDetailViewModel d => d.Data.IsEditing || d.Data.Pane.IsEditDirty,
        _ => false,
    };

    /// <summary>같은 접속(DbKey)의 같은 객체를 보이는 문서. PACKAGE 와 PACKAGE BODY 는 다른 객체다.</summary>
    private static PanelDocument? FindOpen(DockWorkspace docking, string panelId, DbObjectRef target, ISession session) =>
        docking.Documents.FirstOrDefault(d => d.Definition.Id == panelId && d.Panel is ObjectDetailViewModel o && Same(o.Target, o.Session, target, session));

    private static bool Same(DbObjectRef? open, ISession? openSession, DbObjectRef target, ISession session) =>
        open is not null
        && string.Equals(open.Owner, target.Owner, StringComparison.Ordinal)
        && string.Equals(open.Name, target.Name, StringComparison.Ordinal)
        && string.Equals(open.ObjectType, target.ObjectType, StringComparison.Ordinal)
        && (openSession is null || string.Equals(openSession.DbKey, session.DbKey, StringComparison.Ordinal));
}
