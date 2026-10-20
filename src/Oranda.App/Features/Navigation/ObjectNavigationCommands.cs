using Dock.Model.Core;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Extensibility;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Language.Navigation;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// DB 객체 빠른 이동 명령 처리기(commands.md 7장 「탐색」).
/// <list type="bullet">
/// <item><c>oranda.nav.quickOpenObject</c> — 이름 일부로 객체를 찾아 연다(DataGrip 「Go to Database Object」, SQL Developer 「Find DB Object」).</item>
/// <item><c>oranda.schema.reveal</c> — 객체를 스키마 브라우저 트리에서 찾아 고른다(DataGrip 「Select in Database Tree」).
/// 대상은 명령 인자 → 활성 문서(객체 상세·PL/SQL 편집기)의 객체 → SQL 편집기 커서 아래 이름 순으로 정한다.</item>
/// </list>
/// 서비스는 실행할 때 꺼낸다(시작 경로에서 Oracle·ANTLR 어셈블리를 올리지 않기 위해). 편집기 파일은 건드리지 않고 <see cref="IEditorContext"/> 로만 읽는다.
/// </summary>
[OrandaCommand(QuickOpenId, CommandCategory.Navigate, Enablement = "hasSession",
    Placements = ["menu:navigate/quickOpenObject@100"])]
[OrandaCommand(RevealId, CommandCategory.Navigate, Enablement = "hasSession",
    Placements = ["menu:navigate/revealInSchema@110", "context:editor/revealInSchema@330"])]
public sealed class ObjectNavigationCommandHandler : ICommandHandler
{
    public const string QuickOpenId = "oranda.nav.quickOpenObject";

    public const string RevealId = "oranda.schema.reveal";

    private const string SchemaPanelId = "oranda.panel.schemaBrowser";

    private const string OpenSourceId = "oranda.plsql.openSource";

    private const string OpenDetailId = "oranda.schema.openDetail";

    private static QuickOpenObjectWindow? s_window;

    private readonly IServiceProvider? _services;

    public ObjectNavigationCommandHandler()
    {
    }

    public ObjectNavigationCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>열려 있는 DB 객체로 이동 창(없으면 null). 한 번에 하나만 연다.</summary>
    internal static QuickOpenObjectWindow? OpenWindow => s_window;

    /// <summary>시험용: 지금 세션(기본 = 편집기 접속의 작업 세션 → 스키마 브라우저의 세션 → 마지막 작업 세션).</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    /// <summary>시험용: 메타데이터 제공자(기본 = 서비스 컨테이너).</summary>
    internal IMetadataProvider? Metadata { get; set; }

    /// <summary>시험용: 편집기 문맥(기본 = 서비스 컨테이너).</summary>
    internal IEditorContext? Editor { get; set; }

    /// <summary>시험용: 활성 문서의 ViewModel(기본 = 셸 도킹의 활성 문서).</summary>
    internal Func<object?>? ActiveDocumentProvider { get; set; }

    /// <summary>시험용: 스키마 브라우저를 보이고 그 ViewModel 을 돌려주는 방법.</summary>
    internal Func<SchemaBrowserViewModel?>? BrowserProvider { get; set; }

    /// <summary>시험용: 창 대신 ViewModel 을 받는다.</summary>
    internal Action<QuickOpenObjectViewModel>? Presenter { get; set; }

    /// <summary>시험용: 객체를 여는 방법(기본 = 객체 상세 / PL/SQL 소스 편집기).</summary>
    internal Func<DbObjectRef, ISession, CancellationToken, Task>? OpenObject { get; set; }

    /// <summary>시험용: 알림 받는 곳.</summary>
    internal Action<string>? Notify { get; set; }

    private IMetadataProvider? Meta => Metadata ?? Get<IMetadataProvider>();

    private IEditorContext? EditorContext => Editor ?? Get<IEditorContext>();

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.CommandId)
        {
            case QuickOpenId:
                ShowQuickOpen();
                break;
            case RevealId:
                {
                    var session = ResolveSession();
                    var (target, inSession) = await ResolveTargetAsync(context.Parameter, session, cancellationToken).ConfigureAwait(true);
                    if (target is null)
                    {
                        Notify?.Invoke("스키마 브라우저에서 찾을 객체가 없다");
                        return;
                    }

                    await RevealAsync(target, inSession, cancellationToken).ConfigureAwait(true);
                    break;
                }
        }
    }

    // ---- DB 객체로 이동 ----------------------------------------------------------------------

    private void ShowQuickOpen()
    {
        if (Presenter is null && s_window is not null)
        {
            s_window.Activate();
            return;
        }

        var session = ResolveSession();
        var meta = Meta;
        if (session is null || meta is null)
        {
            Notify?.Invoke("접속이 있어야 DB 객체를 찾는다");
            return;
        }

        var vm = new QuickOpenObjectViewModel(
            ct => Task.Run(() => meta.ListObjectsAsync(session, new ObjectQuery(), ct), ct),
            (target, ct) => OpenAsync(target, session, ct),
            (target, ct) => RevealAsync(target, session, ct))
        {
            CurrentUser = session.Profile.User,
            Query = Prefill(),
        };

        if (Presenter is not null)
        {
            Presenter(vm);
        }
        else
        {
            var window = new QuickOpenObjectWindow { DataContext = vm };
            window.Closed += (_, _) =>
            {
                s_window = null;
                vm.Dispose();
            };
            s_window = window;
            if (Get<ShellController>()?.MainWindow is { IsVisible: true } owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }

        _ = vm.LoadAsync();
    }

    /// <summary>편집기에서 한 줄 이름을 골라 두었으면 그 글로 시작한다(전부 선택돼 있어 바로 고쳐 칠 수 있다).</summary>
    private string Prefill()
    {
        if (EditorContext?.GetActiveDocument() is not { } doc || doc.Selection.Length is <= 0 or > 128
            || doc.Selection.Start < 0 || doc.Selection.Start + doc.Selection.Length > doc.Text.Length)
        {
            return string.Empty;
        }

        var text = doc.Text.Substring(doc.Selection.Start, doc.Selection.Length).Trim();
        return text.Contains('\n', StringComparison.Ordinal) ? string.Empty : text;
    }

    /// <summary>표·뷰 등은 객체 상세, PL/SQL 단위는 PL/SQL 편집기의 소스로 연다.</summary>
    internal Task OpenAsync(DbObjectRef target, ISession session, CancellationToken ct)
    {
        if (OpenObject is not null)
        {
            return OpenObject(target, session, ct);
        }

        if (_services is null)
        {
            return Task.CompletedTask;
        }

        // 두 명령의 활성 조건(트리 노드 선택)은 여기서 뜻이 없으므로 레지스트리를 거치지 않고 처리기를 바로 부른다.
        var command = OpenCommandFor(target);
        var context = new CommandContext(command, AlwaysKeys.Instance, target);
        return command == OpenSourceId
            ? new PlsqlEditorCommandHandler(_services) { SessionProvider = () => session }.ExecuteAsync(context, ct)
            : new ObjectDetailCommandHandler(_services) { SessionProvider = () => session }.ExecuteAsync(context, ct);
    }

    /// <summary>객체를 여는 명령: PL/SQL 단위(패키지·프로시저·함수·트리거·타입)는 소스, 그 밖은 객체 상세.</summary>
    internal static string OpenCommandFor(DbObjectRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return PlsqlSourceText.SourceTypes.Contains(target.ObjectType) ? OpenSourceId : OpenDetailId;
    }

    // ---- 스키마 브라우저에서 찾기 --------------------------------------------------------------

    /// <summary>스키마 브라우저 도구 창을 보이고 그 트리에서 객체를 고른다. 찾으면 true.</summary>
    internal async Task<bool> RevealAsync(DbObjectRef target, ISession? session, CancellationToken ct)
    {
        var browser = BrowserProvider is not null ? BrowserProvider() : ShowBrowser();
        return browser is not null && await browser.RevealAsync(target, session, ct).ConfigureAwait(true);
    }

    private SchemaBrowserViewModel? ShowBrowser()
    {
        if (Get<IShellUi>() is not { } shell)
        {
            return null;
        }

        // 이미 앞에 보이는 탭이면 배치를 다시 짓지 않는다(ShowTool 은 배치 전체를 다시 지어 트리 화면도 새로 만든다).
        if (FindTool(shell) is not { Owner: IDock { ActiveDockable: { } active } } tool || !ReferenceEquals(active, tool))
        {
            shell.Docking.ShowTool(SchemaPanelId);
        }

        // 아직 그려지지 않은 도구 창이어도 ViewModel 을 만든다(트리는 RevealAsync 가 짓는다).
        return FindTool(shell)?.Panel as SchemaBrowserViewModel;
    }

    /// <summary>만들어진 스키마 브라우저 ViewModel(도구 창이 없거나 아직 안 그려졌으면 null).</summary>
    private static SchemaBrowserViewModel? FindBrowser(IShellUi shell) =>
        FindTool(shell) is { IsPanelCreated: true } tool ? tool.Panel as SchemaBrowserViewModel : null;

    /// <summary>도킹 트리에서 스키마 브라우저 도구 창을 찾는다.</summary>
    private static PanelTool? FindTool(IShellUi shell)
    {
        if (shell.Docking.Layout is not { } root)
        {
            return null;
        }

        var stack = new Stack<IDockable>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is PanelTool { Definition.Id: SchemaPanelId } tool)
            {
                return tool;
            }

            if (d is IDock dock && dock.VisibleDockables is { } kids)
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 찾을 객체와 그 접속. 객체 상세·PL/SQL 문서의 객체는 지금 트리의 접속에서 찾고(세션 null),
    /// 편집기 이름은 그 편집기의 접속에서 찾는다.
    /// </summary>
    private async Task<(DbObjectRef? Target, ISession? Session)> ResolveTargetAsync(object? parameter, ISession? session, CancellationToken ct)
    {
        if (FromDocument(parameter) is { } given)
        {
            return (given, null);
        }

        var active = ActiveDocumentProvider is not null ? ActiveDocumentProvider() : Get<IShellUi>()?.Docking.ActiveDocument?.Panel;
        if (FromDocument(active) is { } shown)
        {
            return (shown, null);
        }

        // 그 밖의 문서(결과 모니터 등)가 활성이면 뒤에 숨은 SQL 편집기의 커서를 쓰지 않는다.
        if (active is not null and not SqlEditorViewModel)
        {
            return (null, null);
        }

        var meta = Meta;
        if (EditorContext?.GetActiveDocument() is not { } doc || session is null || meta is null)
        {
            return (null, null);
        }

        var offset = Math.Clamp(doc.CaretOffset, 0, doc.Text.Length);
        var sym = Get<ISymbolLocator>()?.GetSymbolAt(doc.Text, offset) ?? SymbolAnalyzer.GetSymbolAt(doc.Text, offset);
        if (sym is null)
        {
            return (null, null);
        }

        var (obj, _) = await NavigationService.ResolveAsync(meta, session, sym, ct).ConfigureAwait(true);
        return (obj, obj is null ? null : session);
    }

    private static DbObjectRef? FromDocument(object? value) => value switch
    {
        DbObjectRef r => r,
        SchemaNode { Ref: { } r } => r,
        ObjectDetailViewModel { Target: { } t } => t,
        PlsqlEditorViewModel { Target: { } t } => t,
        _ => null,
    };

    // ---- 공통 ------------------------------------------------------------------------------

    private ISession? ResolveSession()
    {
        if (SessionProvider is not null)
        {
            return SessionProvider();
        }

        var sessions = Get<ISessionManager>()?.Sessions;
        if (sessions is null)
        {
            return null;
        }

        var work = sessions.Where(s => s.Purpose == SessionPurpose.Work && s.State != SessionState.Closed).ToList();
        var profileId = EditorContext?.GetActiveDocument()?.ConnectionProfileId;
        var browser = Get<IShellUi>() is { } shell ? FindBrowser(shell) : null;
        return (profileId is { } id ? work.LastOrDefault(s => s.Profile.Id == id) : null)
            ?? (browser?.CurrentSession is { State: not SessionState.Closed } shown ? shown : null)
            ?? work.LastOrDefault();
    }

    private T? Get<T>()
        where T : class => _services?.GetService(typeof(T)) as T;

    private sealed class AlwaysKeys : IContextKeys
    {
        public static readonly AlwaysKeys Instance = new();

        public bool Evaluate(string? expression) => true;
    }
}
