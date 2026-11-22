using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Text;

namespace Oranda.App.Features.Ai.External.Editor;

/// <summary>
/// 밖의 에이전트에게 탭 글을 줄지 정한다(허가의 접속 목록·운영 규칙). 편집기 도구는 이것만 보고 <c>locked</c> 를 가른다.
/// 계약(허가 모델)이 들어오면 허가를 이 모양으로 감싼다.
/// </summary>
public interface IExternalTabAccess
{
    /// <summary>
    /// 그 접속(없으면 null = 접속 없는 탭)의 탭 글을 읽어도 되는가. 거짓이면 그 탭은 <c>locked</c>: 목록에는 제목만 나오고 읽기·제안·저장은 거부한다.
    /// </summary>
    bool CanReadText(Guid? connectionProfileId);

    /// <summary>그 접속에서 바인드 값까지 줄 수 있는가(행 값 수준, 운영은 거짓). 기본 = 주지 않음.</summary>
    bool CanReadBindValues(Guid? connectionProfileId) => false;

    /// <summary>허가 밖 접속에 묶인 탭인가(<c>list_tabs</c> 의 <c>locked</c>: 제목만 보인다). 기본 = 글을 못 읽으면 잠김.</summary>
    bool IsLocked(Guid? connectionProfileId) => !CanReadText(connectionProfileId);

    /// <summary>글을 못 읽을 때 거부 코드와 영어 글. 기본 = <c>locked</c>.</summary>
    (string Code, string Message) Refusal(Guid? connectionProfileId) => (ExternalEditorErrors.Locked, ExternalEditorText.Locked);
}

/// <summary>
/// 외부 허가(<see cref="AgentGrantView"/>)로 탭 접근을 정한다(계약 A1 §2·§6): 글 = <see cref="AgentGrantView.CanReadTabText"/>(운영·수준 Off·허가 밖 거부),
/// 잠김 = <see cref="AgentGrantView.IsLocked"/>, 바인드 값 = 글을 읽을 수 있고 수준이 행 값(QueryRows) 이상.
/// </summary>
public sealed class GrantTabAccess(AgentGrantView grant) : IExternalTabAccess
{
    public AgentGrantView Grant { get; } = grant ?? throw new ArgumentNullException(nameof(grant));

    public bool CanReadText(Guid? connectionProfileId) => Grant.CanReadTabText(connectionProfileId);

    public bool CanReadBindValues(Guid? connectionProfileId) =>
        CanReadText(connectionProfileId) && Grant.LevelFor(connectionProfileId) >= AiAccessLevel.QueryRows;

    public bool IsLocked(Guid? connectionProfileId) => Grant.IsLocked(connectionProfileId);

    public (string Code, string Message) Refusal(Guid? connectionProfileId)
    {
        if (connectionProfileId is null)
        {
            return (AgentErrorCodes.NotAllowed, ExternalEditorText.UnboundText);
        }

        if (Grant.FindConnection(connectionProfileId.Value) is not { } connection)
        {
            return (AgentErrorCodes.Locked, ExternalEditorText.Locked);
        }

        return connection.Environment == ConnectionEnvironment.Production
            ? (AgentErrorCodes.Environment, ExternalEditorText.ProductionText)
            : (AgentErrorCodes.Level, ExternalEditorText.LevelOff);
    }
}

/// <summary>모든 탭을 열어 두는 허가(시험·앱 안 쓰임용).</summary>
public sealed class OpenTabAccess : IExternalTabAccess
{
    public static OpenTabAccess Instance { get; } = new();

    public bool CanReadText(Guid? connectionProfileId) => true;
}

/// <summary>편집기 도구의 거부 코드(계약 <see cref="AgentErrorCodes"/> 값).</summary>
public static class ExternalEditorErrors
{
    public const string TabNotFound = AgentErrorCodes.TabNotFound;

    /// <summary>활성 탭이 없다(코드는 탭 없음과 같다).</summary>
    public const string NoActiveTab = AgentErrorCodes.TabNotFound;

    public const string Locked = AgentErrorCodes.Locked;

    public const string ReadOnly = AgentErrorCodes.ReadOnly;

    public const string RateLimited = AgentErrorCodes.RateLimited;

    public const string InvalidRange = AgentErrorCodes.InvalidArguments;

    public const string ProposalNotFound = AgentErrorCodes.ProposalNotFound;
}

/// <summary>편집기 도구 한 번의 결과: 값 또는 거부(코드·글).</summary>
public sealed record ExternalEditorResult<T>
{
    public T? Value { get; }

    public string? Error { get; }

    public string? Message { get; }

    public bool IsOk => Error is null;

    internal ExternalEditorResult(T? value, string? error, string? message)
    {
        Value = value;
        Error = error;
        Message = message;
    }
}

/// <summary><see cref="ExternalEditorResult{T}"/> 만들기.</summary>
public static class ExternalEditorResult
{
    public static ExternalEditorResult<T> Ok<T>(T value) => new(value, null, null);

    public static ExternalEditorResult<T> Fail<T>(string error, string message) => new(default, error, message);
}

/// <summary><c>list_tabs</c> 한 줄. 잠긴 탭은 식별자·제목·<see cref="Locked"/> 만 채운다.</summary>
public sealed record ExternalTabSummary(Guid TabId, string Title, bool Locked)
{
    public string? Workspace { get; init; }

    public string? Kind { get; init; }

    public Guid? ConnectionProfileId { get; init; }

    public bool? Dirty { get; init; }

    public bool? ReadOnly { get; init; }

    public long? Version { get; init; }

    public bool Active { get; init; }

    /// <summary>파일 이름만(폴더 경로는 뺀다).</summary>
    public string? FileName { get; init; }
}

/// <summary><c>get_tab</c> 이 읽을 범위.</summary>
public enum ExternalTabScope
{
    Document,
    Selection,
    Statement,
}

/// <summary>글 안의 자리: 0 부터 센 위치와 1 부터 센 줄·열.</summary>
public sealed record ExternalCaret(int Offset, int Line, int Col);

/// <summary>선택: 한 덩어리 범위. 열 블록·여러 커서면 캐럿 자리(길이 0)와 <see cref="Block"/> = 참.</summary>
public sealed record ExternalSelection(int Start, int Length, bool Block);

/// <summary>커서 문장의 범위와 종류(<c>query</c>·<c>dml</c>·<c>ddl</c>·<c>plsql</c> 등).</summary>
public sealed record ExternalStatement(int Start, int Length, string Kind);

/// <summary><c>get_tab</c> 의 결과.</summary>
public sealed record ExternalTabContent(Guid TabId, long Version, string Text, bool Truncated, TextRange Range)
{
    public required ExternalCaret Caret { get; init; }

    public required ExternalSelection Selection { get; init; }

    public ExternalStatement? Statement { get; init; }

    public Guid? ConnectionProfileId { get; init; }

    public IReadOnlyList<string> Binds { get; init; } = [];
}

/// <summary><c>new_tab</c> 의 결과.</summary>
public sealed record ExternalNewTab(Guid TabId, long Version, string Title);

/// <summary><c>propose_edit</c> 가 바꿀 범위의 종류.</summary>
public enum ExternalRangeKind
{
    Document,
    Selection,
    Statement,
    Caret,

    /// <summary>시작·길이를 직접 준다.</summary>
    Explicit,
}

/// <summary><c>propose_edit</c> 의 범위(종류 + 직접 줄 때 시작·길이).</summary>
public sealed record ExternalRange(ExternalRangeKind Kind, int Start = 0, int Length = 0)
{
    public static ExternalRange Document { get; } = new(ExternalRangeKind.Document);

    public static ExternalRange Selection { get; } = new(ExternalRangeKind.Selection);

    public static ExternalRange Statement { get; } = new(ExternalRangeKind.Statement);

    public static ExternalRange Caret { get; } = new(ExternalRangeKind.Caret);

    public static ExternalRange Of(int start, int length) => new(ExternalRangeKind.Explicit, start, length);
}

/// <summary>제안 하나의 요청.</summary>
/// <param name="TabId">바꿀 탭.</param>
/// <param name="BaseVersion">에이전트가 읽은 탭의 판. 지금 판과 다르면 바로 <c>stale</c>.</param>
/// <param name="Range">바꿀 범위.</param>
/// <param name="NewText">넣을 글.</param>
public sealed record ExternalProposalRequest(Guid TabId, long BaseVersion, ExternalRange Range, string NewText)
{
    /// <summary>띠·비교 창에 보일 제목.</summary>
    public string? Title { get; init; }

    /// <summary>띠에 보일 까닭 한 줄.</summary>
    public string? Reason { get; init; }

    /// <summary>제안한 클라이언트 이름(hello 의 client). 없으면 「외부 에이전트」.</summary>
    public string? Client { get; init; }
}

/// <summary>제안의 상태(계약 글자 = 소문자 이름).</summary>
public enum ExternalProposalStatus
{
    /// <summary>띠에 있거나 줄 서 있다.</summary>
    Pending,

    /// <summary>사람이 제안 그대로 넣었다.</summary>
    Accepted,

    /// <summary>사람이 비교 창에서 고쳐 넣었다.</summary>
    Modified,

    /// <summary>사람이 버렸거나 탭이 닫혔다.</summary>
    Rejected,

    /// <summary>탭 글이 기준 판과 달라졌다(다시 읽고 다시 제안).</summary>
    Stale,

    /// <summary>기다린 시간 안에 사람이 정하지 않았다. 제안은 띠에 남는다(<c>get_proposal</c> 로 다시 본다).</summary>
    Timeout,
}

/// <summary><c>propose_edit</c>·<c>get_proposal</c> 의 결과.</summary>
public sealed record ExternalProposalView(string ProposalId, ExternalProposalStatus Status)
{
    /// <summary>끝난 뒤 탭의 판(넣었거나 어긋났을 때).</summary>
    public long? Version { get; init; }

    /// <summary>넣은 최종 글(받아들임·고침일 때).</summary>
    public string? FinalText { get; init; }

    /// <summary>계약 글자(<c>pending</c>·<c>accepted</c> …).</summary>
    public string StatusText => Status.ToString().ToLowerInvariant();
}

/// <summary><c>save_tab</c> 의 결과 상태.</summary>
public enum ExternalSaveStatus
{
    Saved,
    Rejected,
    Timeout,
}

/// <summary><c>save_tab</c> 의 결과.</summary>
public sealed record ExternalSaveResult(ExternalSaveStatus Status, string? FileName = null)
{
    public string StatusText => Status.ToString().ToLowerInvariant();
}

/// <summary><c>get_binds</c> 한 줄. 값은 허가가 허락하고 비밀 이름이 아닐 때만.</summary>
public sealed record ExternalBind(string Name, string? Type, bool HasValue, string? Value = null);
