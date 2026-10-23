using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.Core.Composition;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Parameters;

/// <summary>
/// 문장 계산 상태(<see cref="MyBatisStatementEval"/>)를 (세션, 문장 id)마다 하나씩 두고, 활성 문장을 따라간다(TSK-03-02).
/// 활성 문장 = 활성 작업 공간이 MyBatis 문서일 때 그 세션의 <see cref="IMyBatisWorkspaceSession.SelectedStatementId"/>
/// (「문장」 창에서 고른 문장 또는 활성 문장 탭). 두 도구 창·실행 명령이 <see cref="Active"/> 를 본다.
/// 계산기·시나리오 저장소는 처음 쓸 때 해석한다(시작 때 Oranda.Language 를 로드하지 않게, design.md 9장). UI 스레드에서 쓴다.
/// </summary>
[OrandaService(typeof(MyBatisEvalHub))]
public sealed class MyBatisEvalHub
{
    private readonly ConditionalWeakTable<IMyBatisWorkspaceSession, Dictionary<string, MyBatisStatementEval>> _states = [];
    private readonly Lazy<IMyBatisEvaluator?> _evaluator;
    private readonly Lazy<IMyBatisScenarioStore?> _store;
    private SqlEditorHost? _host;
    private bool _attached;
    private SqlWorkspaceViewModel? _workspace;
    private IMyBatisWorkspaceSession? _session;
    private MyBatisStatementEval? _active;
    private IDisposable? _activeTrack;

    /// <summary>셸(DI)이 쓰는 생성자.</summary>
    public MyBatisEvalHub(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _evaluator = new(() =>
        {
            // 계산기는 지연 어셈블리(Oranda.Language)에 있고 목록형 해석은 그것을 깨우지 않으므로, 처음 계산할 때 한 번 훑는다.
            (services as ServiceContainer)?.ScanDeferred();
            return Last<IMyBatisEvaluator>(services);
        });
        _store = new(() => Subscribe(Last<IMyBatisScenarioStore>(services)));
    }

    /// <summary>
    /// 시험용 서비스 공급자: 계산기·저장소를 직접 넣는다(<c>new MyBatisEvalHub(MyBatisEvalHub.Services(…))</c>).
    /// 생성자를 하나만 두는 까닭: 셸의 생성자 고르기(<c>ServiceContainer.Create</c>)가 매개변수 많은 생성자를 먼저 보므로,
    /// 계산기를 받는 생성자가 있으면 그 형식을 풀려고 지연 어셈블리를 훑는다.
    /// </summary>
    internal static IServiceProvider Services(IMyBatisEvaluator? evaluator, IMyBatisScenarioStore? store) =>
        new MyBatisServiceMap()
            .With<IEnumerable<IMyBatisEvaluator>>(evaluator is null ? [] : [evaluator])
            .With<IEnumerable<IMyBatisScenarioStore>>(store is null ? [] : [store]);

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>.</summary>
    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    /// <summary>값·원문 고침 뒤 다시 계산까지 기다리는 시간. 기본 300 ms.</summary>
    public TimeSpan Debounce { get; init; } = MyBatisStatementEval.DefaultDebounce;

    /// <summary>UI 스레드로 옮기기(저장소 사건). 시험은 바로 부른다.</summary>
    internal Action<Action> Post { get; init; } = a => Dispatcher.UIThread.Post(a);

    public IMyBatisEvaluator? Evaluator => _evaluator.Value;

    public IMyBatisScenarioStore? Store => _store.Value;

    /// <summary>활성 문장의 계산 상태. MyBatis 문서가 아니거나 고른 문장이 없으면 null.</summary>
    public MyBatisStatementEval? Active
    {
        get
        {
            EnsureAttached();
            return _active;
        }
    }

    /// <summary><see cref="Active"/> 가 다른 문장으로 바뀌었다.</summary>
    public event EventHandler? ActiveChanged;

    /// <summary>(세션, 문장 id)의 계산 상태. 없으면 만든다(처음 쓸 때 <see cref="MyBatisStatementEval.EnsureLoadedAsync"/> 를 부른다).</summary>
    public MyBatisStatementEval For(IMyBatisWorkspaceSession session, string statementId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(statementId);
        var map = _states.GetValue(session, _ => new Dictionary<string, MyBatisStatementEval>(StringComparer.Ordinal));
        if (!map.TryGetValue(statementId, out var state))
        {
            state = new MyBatisStatementEval(this, session, statementId);
            map[statementId] = state;
        }

        return state;
    }

    /// <summary>탭이 MyBatis 문장 탭이면 그 문장의 계산 상태, 아니면 null.</summary>
    public MyBatisStatementEval? ForTab(SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return Host.WorkspaceOf(tab)?.MyBatis is { } session && session.StatementIdOf(tab) is { } id ? For(session, id) : null;
    }

    /// <summary>활성 문장을 다시 정한다(호스트·세션 사건이 부른다. 시험도 부른다).</summary>
    public void Refresh()
    {
        EnsureAttached();
        Follow();
    }

    private void EnsureAttached()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Host.ActiveWorkspaceChanged += OnHostChanged;
        Host.ActiveChanged += OnHostChanged;
        Follow();
    }

    private void OnHostChanged(object? sender, EventArgs e) => Follow();

    private void OnWorkspaceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis))
        {
            Follow();
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) => Follow();

    private void Follow()
    {
        var workspace = Host.ActiveWorkspace;
        if (!ReferenceEquals(workspace, _workspace))
        {
            if (_workspace is not null)
            {
                _workspace.PropertyChanged -= OnWorkspaceChanged;
            }

            _workspace = workspace;
            if (workspace is not null)
            {
                workspace.PropertyChanged += OnWorkspaceChanged;
            }
        }

        var session = workspace?.MyBatis;
        if (!ReferenceEquals(session, _session))
        {
            if (_session is not null)
            {
                _session.StateChanged -= OnSessionStateChanged;
            }

            _session = session;
            if (session is not null)
            {
                session.StateChanged += OnSessionStateChanged;
            }
        }

        var next = session?.SelectedStatementId is { Length: > 0 } id ? For(session, id) : null;
        if (ReferenceEquals(next, _active))
        {
            return;
        }

        _activeTrack?.Dispose();
        _activeTrack = null;
        _active = next;
        if (next is not null)
        {
            // 활성 탭이 그 문장 탭이면 글 고침을 따라 다시 계산한다(편집기 꾸밈도 따로 붙는다).
            if (Host.Active is { } tab && ReferenceEquals(Host.WorkspaceOf(tab), workspace) && session!.StatementIdOf(tab) == next.StatementId)
            {
                _activeTrack = next.Track(tab);
            }

            _ = next.EnsureLoadedAsync();
        }

        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    private IMyBatisScenarioStore? Subscribe(IMyBatisScenarioStore? store)
    {
        if (store is not null)
        {
            store.Changed += (_, e) => Post(() => OnStoreChanged(e));
        }

        return store;
    }

    private void OnStoreChanged(MyBatisScenarioChangedEventArgs e)
    {
        // 같은 문장(경로 + id)의 상태만 다시 읽는다. 경로 비교는 저장소가 정규화한 것과 세션 경로를 같은 규칙으로 맞춘다.
        foreach (var (session, map) in _states)
        {
            if (!SamePath(session.MapperPath, e.MapperPath) || !map.TryGetValue(e.StatementId, out var state))
            {
                continue;
            }

            _ = state.ReloadScenariosAsync(selectId: null);
        }
    }

    private static bool SamePath(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(a), Normalize(b), comparison);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // 단일 형식을 물으면 등록이 없을 때 컨테이너가 지연 어셈블리를 훑는다(MyBatisHooks 주석). 늘 IEnumerable 로 묻는다.
    private static T? Last<T>(IServiceProvider services)
        where T : class => services.GetService(typeof(IEnumerable<T>)) is IEnumerable<T> all ? all.LastOrDefault() : null;
}
