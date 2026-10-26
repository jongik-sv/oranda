using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Execution;

/// <summary>미커밋 상태로 끝낼 때의 선택(SAFE: 종료 시 확인).</summary>
public enum UncommittedChoice
{
    Commit,
    Rollback,
    Cancel,

    /// <summary>트랜잭션을 이전 접속에 그대로 두고 탭의 접속만 바꾼다(접속 콤보, TSK-12-04). 그 묻기에서만 나온다.</summary>
    Keep,
}

/// <summary>접속 안 됨 탭을 실행할 때의 선택(CONN-06).</summary>
public enum MissingConnectionChoice
{
    /// <summary>탭에 묶인 접속으로 다시 접속한다.</summary>
    Reconnect,

    /// <summary>탭을 지금 접속으로 옮긴다.</summary>
    UseCurrent,

    Cancel,
}

/// <summary>실행 확인 대화상자에 필요한 것. 접속은 머리 띠(접속 이름·사용자·서비스)에 크게 보인다.</summary>
public sealed record ConfirmRequest(SqlStatement Statement, PolicyDecision Decision, ConnectionProfile? Profile);

/// <summary>
/// 실행 중 사용자에게 묻는 대화상자 모음. 화면 구현은 <see cref="AvaloniaExecutionDialogs"/>, 시험은 가짜를 넣는다.
/// 어느 스레드에서 불러도 된다(구현이 UI 스레드로 옮긴다).
/// </summary>
public interface IExecutionDialogs
{
    /// <summary>운영·WHERE 없는 DML·위험 명령 확인. true = 진행.</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct);

    /// <summary>치환 변수 값 입력. <paramref name="last"/> 는 마지막으로 넣은 값. null = 취소.</summary>
    Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(
        IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct);

    Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct);

    Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct);

    /// <summary>전용 세션 탭(D-017)의 미커밋 확인. <paramref name="subject"/> = 탭 이름. 기본은 탭 이름 없이 묻는다.</summary>
    Task<UncommittedChoice> AskUncommittedAsync(string subject, CancellationToken ct) => AskUncommittedAsync(ct);

    /// <summary>
    /// 접속 하나의 미커밋 확인(접속 끊기·앱 종료 때 접속마다, CONN-06). <paramref name="connection"/> = 「번호 - 사용자@이름」.
    /// 기본은 접속 이름 없이 묻는다.
    /// </summary>
    Task<UncommittedChoice> AskConnectionUncommittedAsync(string connection, CancellationToken ct) => AskUncommittedAsync(ct);

    /// <summary>
    /// 접속 콤보로 탭의 접속을 바꾸기 전, 이전 접속(<paramref name="connection"/> = 「번호 - 사용자@이름」)의 미커밋 확인(TSK-12-04):
    /// 커밋 / 롤백 / 그대로 두고 바꾸기(<see cref="UncommittedChoice.Keep"/>) / 취소. 기본은 「그대로 두고 바꾸기」 없이 접속 이름으로 묻는다.
    /// </summary>
    Task<UncommittedChoice> AskSwitchUncommittedAsync(string connection, CancellationToken ct) => AskConnectionUncommittedAsync(connection, ct);

    /// <summary>
    /// 탭에 묶인 접속이 열려 있지 않은데 실행하려 한다(CONN-06): 다시 접속 / 지금 접속(<paramref name="current"/>)으로 바꾸기 / 취소.
    /// <paramref name="profile"/> = 탭에 묶인 접속 이름, <paramref name="current"/> = 지금 접속 이름(없으면 null, 그 단추를 감춘다).
    /// 기본은 취소(실행하지 않는다).
    /// </summary>
    Task<MissingConnectionChoice> AskMissingConnectionAsync(string profile, string? current, CancellationToken ct) =>
        Task.FromResult(MissingConnectionChoice.Cancel);

    /// <summary>DML 미리보기(그리드 반영 전, GRID-05). true = 반영.</summary>
    Task<bool> PreviewDmlAsync(string text, CancellationToken ct);

    /// <summary>
    /// 반영 동작을 밝히는 DML 미리보기. <paramref name="commit"/> = 반영 뒤 바로 커밋하는 동작(제목·주 단추 글자가 갈린다).
    /// 기본은 동작을 밝히지 않는 <see cref="PreviewDmlAsync(string, CancellationToken)"/> 로 넘긴다.
    /// </summary>
    Task<bool> PreviewDmlAsync(string text, bool commit, CancellationToken ct) => PreviewDmlAsync(text, ct);
}

/// <summary>실행 확인 대화상자의 판단 부분(화면 없이 시험한다).</summary>
public sealed class ConfirmDialogModel(ConfirmRequest request)
{
    public ConfirmRequest Request { get; } = request;

    public bool IsProduction => Request.Profile?.Environment == ConnectionEnvironment.Production;

    /// <summary>다시 입력해야 하는 이름. 없으면 null.</summary>
    public string? RetypeText => string.IsNullOrWhiteSpace(Request.Decision.RetypeText) ? null : Request.Decision.RetypeText;

    public string Message => Request.Decision.Message ?? "이 문장을 실행합니다.";

    /// <summary>머리 띠 첫 줄: 접속 이름.</summary>
    public string HeaderTitle => Request.Profile?.Name ?? "접속 없음";

    /// <summary>머리 띠 둘째 줄: 사용자@서비스.</summary>
    public string HeaderDetail => Request.Profile is { } p ? $"{p.User}@{p.DataSource}" : string.Empty;

    /// <summary>환경 칩 글.</summary>
    public string EnvironmentText => Request.Profile?.Environment switch
    {
        ConnectionEnvironment.Production => "운영",
        ConnectionEnvironment.Test => "테스트",
        ConnectionEnvironment.Development => "개발",
        _ => string.Empty,
    };

    /// <summary>실행 단추를 켤 수 있는지. 이름 재입력이 필요하면 같은 이름(대소문자 무시)을 쳐야 한다.</summary>
    public bool CanAccept(string typed) =>
        RetypeText is not { } expected || string.Equals(typed?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
}
