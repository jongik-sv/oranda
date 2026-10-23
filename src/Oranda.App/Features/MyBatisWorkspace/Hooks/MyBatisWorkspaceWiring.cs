using System.ComponentModel;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>MyBatis 문맥 키 세 개의 값(design.md 4.3).</summary>
/// <param name="DocActive">활성 문서가 MyBatis 작업 공간이다(<c>mybatisDocActive</c>). PL/SQL·객체 상세 같은 다른 문서가 활성이면 거짓.</param>
/// <param name="DocDirty">활성 MyBatis 세션에 저장 안 한 문장이 있다(<c>mybatisDocDirty</c>).</param>
/// <param name="StatementSelected">활성 MyBatis 세션에 고른 문장이 있다(<c>mybatisStatementSelected</c>).</param>
internal readonly record struct MyBatisContextKeys(bool DocActive, bool DocDirty, bool StatementSelected);

/// <summary>
/// MyBatis 훅을 호스트에 꽂고 MyBatis 문맥 키를 셸에 알린다(design.md 9장 「훅 해석」, TSK-01-02).
/// 문맥 키는 호스트의 활성 작업 공간이 아니라 도킹의 활성 문서로 정한다: PL/SQL·객체 상세 문서가 활성이어도 호스트의 활성 작업 공간은 마지막 SQL 작업 공간에 머문다.
/// 호스트는 DI 보다 먼저 만들어지므로(<see cref="SqlEditorHost.Current"/>) 시작 작업이 구현들을 모아 훅 자리에 넣는다.
/// 생성자는 등록된 Shell 서비스만 받는다(시작 시험 제약: 등록되지 않은 형식이 있으면 지연 어셈블리를 훑는다).
/// 훅은 만들 때 바로 꽂는다: 시작 작업은 목록을 한꺼번에 만든 뒤 차례로 await 되고, 앞쪽 작업(변경 기록·이력)이 IO 를 기다리는 사이에
/// 명령줄로 받은 매퍼 열기(같은 Background 예약)가 먼저 돈다. 그때 훅이 없으면 매퍼가 MyBatis 가 아닌 일반 작업 공간으로 열린다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class MyBatisWorkspaceWiring : IStartupTask
{
    internal const string DocActiveKey = "mybatisDocActive";
    internal const string DocDirtyKey = "mybatisDocDirty";
    internal const string StatementSelectedKey = "mybatisStatementSelected";

    private readonly IServiceProvider _services;
    private readonly ContextKeyService _keys;
    private readonly DockWorkspace _docking;
    private SqlEditorHost? _attached;

    public MyBatisWorkspaceWiring(IServiceProvider services, ContextKeyService keys, DockWorkspace docking)
    {
        _services = services;
        _keys = keys;
        _docking = docking;
        Attach(SqlEditorHost.Current);
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        if (!ReferenceEquals(_attached, SqlEditorHost.Current))
        {
            Attach(SqlEditorHost.Current); // 그사이 호스트가 바뀌었으면 새 호스트에 꽂는다(같으면 아무것도 안 한다)
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 훅 묶음을 호스트에 넣고 문맥 키를 이어 준다. 훅은 Post 없이 바로 넣는다:
    /// 작업 공간 복구(<c>SqlEditorDockWiring</c> 이 UI 스레드에 Post 한 <c>InitializeAsync</c>)가 훅 자리를 읽기 전에 서 있어야 하고,
    /// 시작 작업은 이 메서드를 부른 UI 스레드 차례에서 모두 끝난 뒤에야 Post 된 작업이 돌기 때문이다.
    /// </summary>
    internal void Attach(SqlEditorHost host)
    {
        _attached = host;
        host.MyBatisHooks = new MyBatisHooks(_services);
        new KeyFeeder(host, _keys, _docking).Start();
    }

    /// <summary>
    /// 활성 문서의 Panel(<c>DockWorkspace.ActiveDocument?.Panel</c>)에서 문맥 키 값을 계산한다.
    /// 활성 문서가 없거나, MyBatis 작업 공간이 아니거나(일반 SQL·PL/SQL·객체 상세 …) 하면 모두 거짓.
    /// </summary>
    internal static MyBatisContextKeys ComputeKeys(object? activePanel)
    {
        if (activePanel is not SqlWorkspaceViewModel { DocumentKind: SqlWorkspaceKind.MyBatis, MyBatis: { } session })
        {
            return default;
        }

        return new MyBatisContextKeys(true, session.IsDirty, session.SelectedStatementId is not null);
    }

    /// <summary>도킹의 활성 문서·호스트·세션 사건을 받아 문맥 키를 다시 계산한다(작업 공간이 바뀌면 옛 구독을 푼다).</summary>
    private sealed class KeyFeeder(SqlEditorHost host, ContextKeyService keys, DockWorkspace docking)
    {
        private SqlWorkspaceViewModel? _workspace;
        private IMyBatisWorkspaceSession? _session;

        public void Start()
        {
            docking.ActiveDocumentChanged += (_, _) => Follow();
            host.ActiveWorkspaceChanged += (_, _) => Follow();
            host.ActiveChanged += (_, _) => Push();
            Follow();
        }

        private void Follow()
        {
            Unfollow();
            _workspace = docking.ActiveDocument?.Panel as SqlWorkspaceViewModel;
            if (_workspace is not null)
            {
                _workspace.PropertyChanged += OnWorkspaceChanged;
                FollowSession();
            }

            Push();
        }

        private void Unfollow()
        {
            if (_workspace is not null)
            {
                _workspace.PropertyChanged -= OnWorkspaceChanged;
            }

            UnfollowSession();
            _workspace = null;
        }

        private void FollowSession()
        {
            _session = _workspace?.MyBatis;
            if (_session is not null)
            {
                _session.StateChanged += OnSessionChanged;
            }
        }

        private void UnfollowSession()
        {
            if (_session is not null)
            {
                _session.StateChanged -= OnSessionChanged;
                _session = null;
            }
        }

        private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SqlWorkspaceViewModel.DocumentKind))
            {
                UnfollowSession(); // 세션이 붙거나 떨어졌다
                FollowSession();
                Push();
            }
        }

        private void OnSessionChanged(object? sender, EventArgs e) => Push();

        private void Push()
        {
            var value = ComputeKeys(_workspace);
            keys.Set(DocActiveKey, value.DocActive);
            keys.Set(DocDirtyKey, value.DocDirty);
            keys.Set(StatementSelectedKey, value.StatementSelected);
        }
    }
}
