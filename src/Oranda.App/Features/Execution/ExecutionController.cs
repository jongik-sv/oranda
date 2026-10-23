using System.Globalization;
using System.Text;
using Oranda.App.Features.Binds;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

/// <summary>실행 범위(commands.md 「SQL」).</summary>
public enum RunMode
{
    /// <summary>선택이 있으면 선택, 없으면 커서 문장.</summary>
    Statement,

    /// <summary>선택 영역(문장이 여럿이면 스크립트로).</summary>
    Selection,

    /// <summary>문서 전체.</summary>
    Script,

    /// <summary>커서 문장부터 끝까지.</summary>
    FromCursor,

    /// <summary>커서 문장 하나를 실행하고 커서를 다음 문장으로 옮긴다.</summary>
    Step,

    /// <summary>커서(선택)를 품은 가장 안쪽 괄호 하위 쿼리. 하위 쿼리 밖이면 <see cref="Statement"/> 와 같다.</summary>
    Subquery,
}

/// <summary>
/// 컨트롤러가 쓰는 바깥 의존. 서비스는 전부 호출 때 해석한다(<see cref="Func{TResult}"/>) — 편집기 생성과 시작 경로가
/// Oracle·Language 어셈블리를 깨우지 않도록(D-006, 04-02 decisions).
/// </summary>
public sealed class ExecutionDeps
{
    public required ExecutionState State { get; init; }

    public required ExecutionLog Log { get; init; }

    public required IExecutionDialogs Dialogs { get; init; }

    /// <summary>공유 작업 세션(접속 관리자의 활성 세션). 전용 세션 탭은 <see cref="TabSession"/> 이 먼저다.</summary>
    public required Func<ISession?> Session { get; init; }

    /// <summary>탭의 세션(전용 세션 D-017·탭에 묶인 접속 CONN-06). null 이고 <see cref="MissingConnection"/> 도 null 이면 공유 작업 세션을 쓴다.</summary>
    public Func<SqlEditorViewModel, ISession?> TabSession { get; init; } = _ => null;

    /// <summary>탭에 묶인 접속이 열려 있지 않으면 그 프로필 ID(접속 안 됨: 공유 작업 세션으로 대신하지 않는다).</summary>
    public Func<SqlEditorViewModel, Guid?> MissingConnection { get; init; } = _ => null;

    /// <summary>열린 작업 세션(접속, 연 순서). 앱 종료 미커밋 확인이 접속마다 묻는다. null 이면 <see cref="Session"/> 하나.</summary>
    public Func<IReadOnlyList<ISession>>? WorkSessions { get; init; }

    /// <summary>대화상자에 보일 접속 이름(「번호 - 사용자@이름」).</summary>
    public Func<ISession, string> ConnectionLabel { get; init; } = s => $"{s.Profile.User}@{s.Profile.Name}";

    /// <summary>프로필 이름(접속 안 됨 탭 묻기, 저장된 프로필을 아직 안 읽었으면 읽는다). 모르면 null.</summary>
    public Func<Guid, Task<string?>> ProfileName { get; init; } = _ => Task.FromResult<string?>(null);

    /// <summary>프로필로 다시 접속한다(비밀번호를 물을 수 있다). 연 세션, 못 열었거나 사용자가 취소하면 null.</summary>
    public Func<Guid, CancellationToken, Task<ISession?>> Reconnect { get; init; } = (_, _) => Task.FromResult<ISession?>(null);

    public required Func<IExecutionService> Execution { get; init; }

    public required Func<ILanguageService> Language { get; init; }

    public Func<SqlEditorHost> Host { get; init; } = () => SqlEditorHost.Current;

    public Func<IRowEditor?> RowEditor { get; init; } = () => null;

    public Func<IMetadataProvider?> Metadata { get; init; } = () => null;

    /// <summary>도구 창 탭을 앞으로 가져온다(패널 ID). 실행하면 「조회 결과」 창을 보인다.</summary>
    public Action<string> ActivatePanel { get; init; } = _ => { };

    /// <summary>도구 창 보이기/숨기기(패널 ID).</summary>
    public Action<string> TogglePanel { get; init; } = _ => { };

    /// <summary>바인드 변수 목록(「바인드 변수」 창과 같은 것). 기본값은 버스 없는 공용 목록.</summary>
    public BindVariableStore Binds { get; init; } = BindVariableStore.For(null);
}

/// <summary>
/// SQL 실행 화면의 중심(TSK-06-02). 편집기에서 실행 범위를 정하고(문장 나누기는 <see cref="ILanguageService"/>),
/// 바인드·치환 변수를 받고, <see cref="IExecutionService"/> 로 실행하고, 결과를 결과 영역(<see cref="ResultTabsViewModel"/> →
/// <c>SqlResultPaneViewModel</c>)·메시지 창·상태에 반영한다. DB 호출은 항상 스레드 풀에서 한다(UI 스레드 금지).
/// </summary>
public sealed partial class ExecutionController : IDisposable
{
    private readonly ExecutionDeps _d;
    private readonly Dictionary<Guid, CancellationTokenSource> _runs = []; // 세션 ID → 실행 중 취소 토큰(전용 세션 탭은 따로 돈다)
    private readonly HashSet<Guid> _lastCancelled = [];
    /// <summary>결과·오류를 보이는 아래 도구 창(<see cref="ResultsPanelViewModel"/>).</summary>
    public const string ResultsPanelId = "oranda.panel.results";

    /// <summary>바인드 변수 창(<see cref="BindVariablesViewModel"/>).</summary>
    public const string BindsPanelId = "oranda.panel.binds";

    public ExecutionController(ExecutionDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _d = deps;

        // 그리드 편집(TSK-06-04)이 비워 둔 연결 지점을 채운다.
        GridEditCommands.Rerun = RerunForEditAsync;
        GridEditCommands.ConfirmPreview = (text, commit, ct) => _d.Dialogs.PreviewDmlAsync(text, commit, ct);
        GridEditCommands.Report = ReportGridApply;
    }

    public ExecutionState State => _d.State;

    public ExecutionLog Log => _d.Log;

    private SqlEditorHost Host => _d.Host();

    /// <summary>
    /// 탭이 쓰는 세션: 탭의 세션(전용 세션·묶인 접속)이 있으면 그것, 없으면 공유 작업 세션. 묶인 접속이 열려 있지 않은 탭은 null(접속 안 됨).
    /// 탭이 null 이면 공유 작업 세션.
    /// </summary>
    public ISession? SessionFor(SqlEditorViewModel? tab) =>
        tab is null ? _d.Session() : _d.TabSession(tab) ?? (_d.MissingConnection(tab) is null ? _d.Session() : null);

    /// <summary>
    /// 지금 실행 중인 문장의 세션 프로필(실행 흐름마다). 실행 확인 대화상자의 머리 띠가 활성 탭이 아니라 실행하는 세션의 접속을 보이게 한다.
    /// </summary>
    public static ConnectionProfile? RunningProfile => s_runningProfile.Value;

    private static readonly AsyncLocal<ConnectionProfile?> s_runningProfile = new();

    /// <summary>
    /// 상태 표시줄이 보여 주는 세션(<see cref="ExecutionState.FocusedDedicated"/>)과 같은 세션: SQL 작업 공간이 아닌 문서가 활성이면
    /// 활성 SQL 탭이 전용 세션 탭이어도 공유 작업 세션이다. 탭을 고르지 않는 커밋·롤백·취소 명령이 쓴다.
    /// </summary>
    private ISession? FocusedSession()
    {
        if (State.FocusedNothing)
        {
            return null; // 활성 SQL 탭의 접속이 닫혔다: 다른 접속을 커밋하지 않는다
        }

        // 보이는 세션이 활성 탭의 세션일 때만 그것(SQL 작업 공간이 아닌 문서가 활성이면 보이는 세션은 지금 접속이다).
        var tab = SessionFor(Host.Active);
        return tab is not null && State.FocusedSessionId == tab.Id ? tab : _d.Session();
    }

    // ---- 오류 문장으로 이동 --------------------------------------------------------------------

    /// <summary>마지막 오류가 난 자리로 편집기 커서를 옮긴다(<c>oranda.exec.goToError</c>).</summary>
    public bool GoToError()
    {
        if (State.LastErrorStatement is not { } stmt)
        {
            return false;
        }

        var host = Host;
        var tab = (State.LastErrorTab is { } id ? host.Tabs.FirstOrDefault(t => t.TabId == id) : null) ?? host.Active;
        if (tab is null || stmt.Range.Length == 0)
        {
            return false;
        }

        int offset = Math.Min(stmt.Range.Start + Math.Max(0, State.LastError?.Offset ?? 0), tab.Document.TextLength);
        host.Active = tab;
        tab.Select(offset, 0);
        tab.Surface?.ScrollToLine(tab.PositionOf(offset).Line);
        tab.Surface?.FocusEditor();
        return true;
    }

    public void Dispose()
    {
        foreach (var cts in _runs.Values.ToList())
        {
            cts.Cancel();
        }
    }
}
