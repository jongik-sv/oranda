using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Sessions;

/// <summary>
/// 「세션」 도구 창의 한 줄(세션 하나). 세션이 같으면 같은 행 객체를 갱신해 쓰므로(<see cref="Update"/>) 새로 고쳐도 선택·열린 메뉴가 유지된다.
/// 글자는 모두 여기서 만든다(보기는 바인딩만).
/// </summary>
public sealed partial class SessionRow(ISession session) : ObservableObject
{
    public ISession Session { get; } = session;

    /// <summary>접속 이름 「번호 - 사용자@이름」, 같은 DB 에 더 연 세션이면 뒤에 「 (2)」.</summary>
    [ObservableProperty]
    public partial string Label { get; private set; } = string.Empty;

    /// <summary>둘째 줄: 쓰는 방식 · 연결된 워크스페이스 수 · 상태.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    /// <summary>세션 모니터와 같은 번호(접속 때 서버에서 읽어 둔 SID). 모르면 0.</summary>
    [ObservableProperty]
    public partial long Sid { get; private set; }

    [ObservableProperty]
    public partial string ToolTip { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsBroken { get; private set; }

    /// <summary>커밋하지 않은 변경이 있다.</summary>
    [ObservableProperty]
    public partial bool IsPending { get; private set; }

    /// <summary>이 세션을 쓰는(묶인) 작업 공간 수.</summary>
    [ObservableProperty]
    public partial int WorkspaceCount { get; private set; }

    /// <summary>세션이 접속(콤보 번호의 기본 세션)인지. 같은 DB 에 더 연 세션이면 false.</summary>
    [ObservableProperty]
    public partial bool IsPrimary { get; private set; }

    partial void OnSidChanged(long value) => OnPropertyChanged(nameof(SidText));

    public void Update(SessionRouter router, ExecutionState state)
    {
        var s = Session;
        var workspaces = router.WorkspacesOn(s);
        var shared = workspaces.Count > 1 || workspaces.Any(w => router.ModeOf(w) == WorkspaceSessionMode.Shared);
        IsBroken = router.IsBroken(s);
        IsRunning = !IsBroken && state.IsRunning(s.Id);
        IsPending = !IsBroken && (state.IsTxnPending(s.Id) || s.HasPendingTransaction);
        WorkspaceCount = workspaces.Count;
        Sid = s.Server.SessionId;
        Label = router.LabelOf(s);
        IsPrimary = router.SessionsOf(s.Profile.Id) is [var first, ..] && ReferenceEquals(first, s);

        var mode = workspaces.Count == 0
            ? UiText.Get("Sessions.mode.unused", "미사용")
            : shared ? UiText.Get("Sessions.mode.shared", "공유") : UiText.Get("Sessions.mode.own", "전용");
        var status = IsBroken ? UiText.Get("Sessions.status.broken", "끊김")
            : IsRunning ? UiText.Get("Sessions.status.running", "실행 중")
            : UiText.Get("Sessions.status.idle", "대기");
        if (IsPending)
        {
            status += " · " + UiText.Get("Sessions.status.pending", "미커밋");
        }

        Detail = $"{mode} · {UiText.Format("Sessions.workspaces", "워크스페이스 {0}개", workspaces.Count)} · {status}";
        ToolTip = SidText + " · " + UiText.Get("Sessions.sidHint", "세션 모니터와 같은 번호") + Environment.NewLine
            + string.Join(", ", workspaces.Select(w => w.Name));
    }

    /// <summary>「SID 241」. 모르면 「SID ?」(식별자라 번역하지 않는다).</summary>
    public string SidText => "SID " + (Sid > 0 ? Sid.ToString(CultureInfo.InvariantCulture) : "?");
}
