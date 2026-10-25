using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Core;
using Oranda.App.Features.Localization;
using Oranda.App.Features.Navigation;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Features;
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
/// <item>PL/SQL 편집기 기능이 꺼져 있으면(D-007) PL/SQL 단위는 서버 소스를 일반 SQL 탭에 읽기 전용으로 연다(<see cref="OpenReadOnlySourceAsync"/>).
/// DB 객체로 이동·객체 검색·<c>oranda.plsql.openSource</c> 처리기도 이 분기를 부른다(정의로 이동은 원래 읽기 전용 SQL 탭이다).</item>
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

    /// <summary>시험용: SQL 편집기 호스트(기본 = <see cref="SqlEditorHost.Current"/>).</summary>
    internal SqlEditorHost? Host { get; set; }

    /// <summary>시험용: 안내 받는 곳(기본 = 스키마 브라우저 상태 줄).</summary>
    internal Action<string>? Notify { get; set; }

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
        if (panelId == PlsqlWorkspaceRouter.PanelId && IsPlsqlEditorOff(_services))
        {
            var tab = await OpenReadOnlySourceAsync(_services, target, session, line: 0, preview, Notify, ct, Host).ConfigureAwait(true);
            if (tab is null || DocumentOf(docking, tab) is not { } sqlDoc)
            {
                return null;
            }

            Show(docking, sqlDoc, focus);
            return sqlDoc;
        }

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

    // ---- PL/SQL 편집기 끔: 읽기 전용 소스 (D-007) --------------------------------------------

    // 읽기 전용 소스 탭 표식: 어느 접속의 어느 객체인지, 아직 미리보기(다음 미리보기가 바꿔 쓴다)인지.
    private static readonly ConditionalWeakTable<SqlEditorViewModel, ReadOnlySourceMark> ReadOnlyTabs = new();

    /// <summary>PL/SQL 편집기 기능이 꺼졌나(게이트가 없으면 켬 = 지금 동작).</summary>
    public static bool IsPlsqlEditorOff(IServiceProvider? services) =>
        services?.GetService(typeof(IFeatureGate)) is IFeatureGate gate && !gate.IsEnabled(FeatureIds.PlsqlEditor);

    /// <summary>읽기 전용 소스 탭 제목: 「[읽기 전용] OWNER.NAME」(본문은 뒤에 BODY — 명세와 본문은 다른 탭이다).</summary>
    public static string ReadOnlyTitle(DbObjectRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = $"{target.Owner}.{target.Name}" + (target.ObjectType.EndsWith(" BODY", StringComparison.Ordinal) ? " BODY" : string.Empty);
        return UiText.Get(FeatureTexts.ReadOnlyTabTitle, "[읽기 전용] {0}").Replace("{0}", name, StringComparison.Ordinal);
    }

    /// <summary>탭이 PL/SQL 끔 읽기 전용 소스 탭이면 그 객체(아니면 null).</summary>
    internal static DbObjectRef? ReadOnlySourceOf(SqlEditorViewModel tab) =>
        ReadOnlyTabs.TryGetValue(tab, out var mark) ? mark.Target : null;

    /// <summary>
    /// PL/SQL 편집기가 꺼졌을 때의 「소스 열기」(D-007): 서버 소스(메타데이터 공급자)를 일반 SQL 탭에 넣고 읽기 전용으로 둔다(제목 <see cref="ReadOnlyTitle"/>).
    /// 같은 접속의 같은 객체 탭이 있으면 새로 읽지 않고 그 탭을 앞으로 낸다(고정 열기면 미리보기 표식을 뗀다). 미리보기면 지난 읽기 전용 미리보기 탭을 그 자리에서 바꿔 쓴다.
    /// <paramref name="line"/> 이 1 이상이면 그 서버 줄(<c>all_source.line</c>)로 커서를 옮긴다. 소스를 읽지 못하면 탭을 열지 않고 이유를 알린 뒤 null.
    /// 안내는 <paramref name="notify"/>(없으면 상태 표시줄 알림과 스키마 브라우저 상태 줄)로 보낸다.
    /// </summary>
    internal static async Task<SqlEditorViewModel?> OpenReadOnlySourceAsync(IServiceProvider? services, DbObjectRef target, ISession session, int line, bool preview,
        Action<string>? notify, CancellationToken ct, SqlEditorHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        host ??= SqlEditorHost.Current;
        var tab = host.Tabs.FirstOrDefault(t => ReadOnlyTabs.TryGetValue(t, out var mark) && mark.Is(target, session));
        if (tab is null)
        {
            if (services?.GetService(typeof(IMetadataProvider)) is not IMetadataProvider meta)
            {
                return null;
            }

            string source;
            try
            {
                source = await meta.GetSourceAsync(session, target, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Tell(services, notify, "소스를 열지 못했다: " + ex.Message.ReplaceLineEndings(" "), failure: true);
                return null;
            }

            tab = Place(host, target, session, source, preview);
        }
        else if (!preview && ReadOnlyTabs.TryGetValue(tab, out var shown))
        {
            shown.Preview = false;
        }

        host.Active = tab;
        if (line > 0)
        {
            tab.GoToLine(line);
        }

        Tell(services, notify, UiText.Get(FeatureTexts.PlsqlOffOpenedReadOnly, "PL/SQL 편집기 기능이 꺼져 있어 읽기 전용으로 열었습니다"), failure: false);
        return tab;
    }

    /// <summary>새 읽기 전용 탭을 열거나, 미리보기면 지난 읽기 전용 미리보기 탭을 그 자리에서 바꿔 쓴다.</summary>
    private static SqlEditorViewModel Place(SqlEditorHost host, DbObjectRef target, ISession session, string source, bool preview)
    {
        var mark = new ReadOnlySourceMark(target, session.DbKey) { Preview = preview };
        var tab = preview ? host.Tabs.FirstOrDefault(t => ReadOnlyTabs.TryGetValue(t, out var m) && m.Preview) : null;
        if (tab is not null)
        {
            tab.LoadText(source);
            tab.Select(0, 0);
        }
        else
        {
            tab = host.NewDocument(source);
        }

        ReadOnlyTabs.AddOrUpdate(tab, mark);
        tab.Title = ReadOnlyTitle(target);
        tab.IsReadOnly = true;
        tab.ConnectionProfileId = session.Profile.Id;
        tab.IsDirty = false;
        return tab;
    }

    /// <summary>탭이 든 SQL 작업 공간 문서(도킹에 아직 없으면 null).</summary>
    private static PanelDocument? DocumentOf(DockWorkspace docking, SqlEditorViewModel tab) =>
        docking.Documents.FirstOrDefault(d => d.Panel is SqlWorkspaceViewModel ws && ws.Tabs.Contains(tab));

    /// <summary>안내를 보낸다: 받는 곳을 주었으면 거기로, 아니면 상태 표시줄 알림(잠깐 보임)과 스키마 브라우저 상태 줄.</summary>
    private static void Tell(IServiceProvider? services, Action<string>? notify, string message, bool failure)
    {
        if (notify is not null)
        {
            notify(message);
            return;
        }

        GridEdit.SaveFeedback.Show(new GridEdit.SaveNotice(message, failure ? GridEdit.SaveFeedbackKind.Failure : GridEdit.SaveFeedbackKind.Success));
        NotifySchemaBrowser(services, message);
    }

    /// <summary>스키마 브라우저 상태 줄에 안내를 쓴다(도구 창이 아직 없으면 아무것도 안 한다).</summary>
    internal static void NotifySchemaBrowser(IServiceProvider? services, string message)
    {
        if (services?.GetService(typeof(IShellUi)) is not IShellUi { Docking.Layout: { } root })
        {
            return;
        }

        var stack = new Stack<IDockable>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is PanelTool { Definition.Id: "oranda.panel.schemaBrowser", IsPanelCreated: true, Panel: SchemaBrowserViewModel vm })
            {
                vm.StatusText = message;
                return;
            }

            if (d is IDock dock && dock.VisibleDockables is { } kids)
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }
    }

    /// <summary>읽기 전용 소스 탭 표식.</summary>
    private sealed class ReadOnlySourceMark(DbObjectRef target, string dbKey)
    {
        public DbObjectRef Target { get; } = target;

        public bool Preview { get; set; }

        public bool Is(DbObjectRef other, ISession session) =>
            Same(Target, null, other, session) && string.Equals(dbKey, session.DbKey, StringComparison.Ordinal);
    }

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
