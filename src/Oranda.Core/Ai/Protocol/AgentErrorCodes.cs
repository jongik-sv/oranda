namespace Oranda.Core.Ai.Protocol;

/// <summary>
/// 도구 결과 오류 JSON <c>{"error":"…","code":"…"}</c> 의 <c>code</c> 값 전부(계약 A1, 설계 agent-cli-control §4.1).
/// 게이트·다리·앱 도구 처리자·<c>orandactl</c> 이 같은 글자를 쓴다. <see cref="AgentCliExit.For"/> 가 종료 코드로 바꾼다.
/// 새 코드는 여기에 더하고 <see cref="AgentCliExit.For"/> 표에도 넣는다(시험이 빠진 코드를 찾는다).
/// </summary>
public static class AgentErrorCodes
{
    // ---- 앱에 닿지 못함·연결 끝남 (종료 코드 3) ----

    /// <summary>파이프·소켓에 붙지 못했다(앱이 꺼졌거나 연결 파일이 낡았다).</summary>
    public const string NotRunning = "oranda_not_running";

    /// <summary>연결 파일이 하나도 없다(외부 연결을 켠 창이 없다).</summary>
    public const string NoEndpoint = "no_endpoint";

    /// <summary>토큰을 앱이 모른다(폐기·다른 실행). 다리 <c>TokenInvalid</c>.</summary>
    public const string RunInvalid = "oranda_run_invalid";

    /// <summary>외부 허가의 기한이 지났다(연결 창에서 다시 켜거나 연장).</summary>
    public const string GrantExpired = "grant_expired";

    /// <summary>실행·허가가 끝나 더 받지 않는다(게이트).</summary>
    public const string RunRevoked = "run_revoked";

    public const string RunRequired = "run_required";

    public const string HelloRequired = "hello_required";

    public const string ShuttingDown = "oranda_shutting_down";

    public const string UnsupportedProtocol = "unsupported_protocol";

    /// <summary>다리 응답을 읽지 못한 그 밖의 통신 오류.</summary>
    public const string BridgeError = "bridge_error";

    /// <summary>
    /// CLI 의 샌드박스가 연결 파일 읽기·소켓 연결을 막았다(EPERM/EACCES, 2026-11-22 Codex·agy 실측). 앱이 꺼진 것이 아니다 —
    /// 샌드박스 밖에서 다시 부르거나 CLI 설정에서 허락해야 한다.
    /// </summary>
    public const string SandboxBlocked = "sandbox_blocked";

    // ---- 창을 고르지 못함 (종료 코드 5) ----

    /// <summary>살아 있는 연결 파일이 여럿이다. <c>--instance</c> 나 <c>ORANDA_INSTANCE</c> 로 고른다. 결과에 후보 목록(pid·label·started_at).</summary>
    public const string MultipleInstances = "multiple_instances";

    /// <summary><c>--instance</c> 로 준 pid·label 의 연결 파일이 없다.</summary>
    public const string InstanceNotFound = "instance_not_found";

    // ---- 사용법 오류 (종료 코드 2) ----

    /// <summary><c>orandactl</c> 명령줄이 틀렸다(CLI 만 쓴다).</summary>
    public const string Usage = "usage";

    public const string UnknownTool = "unknown_tool";

    public const string InvalidArguments = "invalid_arguments";

    /// <summary><c>export_result</c> 의 <c>out</c> 이 상대 경로이거나·폴더가 없거나·확장자가 <c>format</c> 과 다르다(2단계 C0).</summary>
    public const string PathInvalid = "path_invalid";

    /// <summary>
    /// <c>set_binds</c>·<c>run_tab</c> 의 <c>binds</c> 가 탭의 바인드 목록과 맞지 않는다: 탭에 없는 이름, 목록 형식(VARCHAR2·NUMBER·DATE …)에 맞지 않는 값,
    /// 값을 받지 않는 OUT·REF CURSOR 바인드, 대소문자만 다른 같은 이름. 이때 목록의 값은 하나도 바뀌지 않는다(agent-cli-set-binds 2026-11-23).
    /// </summary>
    public const string BindInvalid = "bind_invalid";

    // ---- 게이트·허가 거부 (종료 코드 4) ----

    /// <summary><c>connection</c> 이 허가(또는 실행) 밖 접속이다.</summary>
    public const string Connection = "connection";

    /// <summary>허용 접속이 여럿인데 <c>connection</c> 이 빠졌다.</summary>
    public const string ConnectionRequired = "connection_required";

    public const string Level = "level";

    public const string Environment = "environment";

    public const string Scope = "scope";

    public const string SqlIdScope = "sql_id_scope";

    public const string SqlNotSingleQuery = "sql_not_single_query";

    public const string SqlSchemaUnresolved = "sql_schema_unresolved";

    public const string SqlUnsafe = "sql_unsafe";

    public const string NoStatement = "no_statement";

    public const string ExplainPlanDisabled = "explain_plan_disabled";

    public const string LiteralMaskUnavailable = "literal_mask_unavailable";

    public const string SessionMismatch = "session_mismatch";

    public const string UnsafeResult = "unsafe_result";

    /// <summary>탭이 허가 밖 접속에 묶였다(글·결과를 주지 않는다).</summary>
    public const string Locked = "locked";

    /// <summary>읽기 전용 탭에 제안했다. <c>new_tab</c> 으로 넣는다.</summary>
    public const string ReadOnly = "read_only";

    /// <summary>운영 탭 글처럼 이 허가에서 내지 않는 내용이다.</summary>
    public const string NotAllowed = "not_allowed";

    /// <summary>한도를 넘었다(<c>new_tab</c> 1분 10개).</summary>
    public const string RateLimited = "rate_limited";

    /// <summary><c>export_result</c> 의 <c>out</c> 파일이 이미 있다. 덮어쓰지 않는다(2단계 C0).</summary>
    public const string FileExists = "file_exists";

    /// <summary><c>run_command</c>: 등급표가 「거부」로 둔 명령이거나 표에 없는 명령이다(2단계 C0).</summary>
    public const string CommandDenied = "command_denied";

    /// <summary><c>run_command</c>: 명령이 지금 문맥에서 쓸 수 없다(<c>Enablement</c> 거짓, 2단계 C0).</summary>
    public const string CommandDisabled = "command_disabled";

    // ---- 도구 실패 (종료 코드 1) ----

    public const string ToolFailed = "tool_failed";

    public const string ToolException = "tool_exception";

    public const string InternalError = "internal_error";

    public const string NotImplemented = "not_implemented";

    public const string Canceled = "canceled";

    /// <summary>다른 조회를 기다리다 시간이 지났다(다리 <c>_callGate</c>).</summary>
    public const string Busy = "busy";

    public const string ResultTooLarge = "result_too_large";

    /// <summary>탭 id 를 모르거나 활성 탭이 없다.</summary>
    public const string TabNotFound = "tab_not_found";

    /// <summary>제안 id 를 모른다(끝난 제안은 잠시 뒤 잊는다).</summary>
    public const string ProposalNotFound = "proposal_not_found";

    /// <summary>탭에 결과·오류가 없다(<c>get_result</c>·<c>get_last_error</c>).</summary>
    public const string NoResult = "no_result";

    /// <summary><c>run_command</c>: 모르는 명령 id(2단계 C0).</summary>
    public const string CommandNotFound = "command_not_found";

    /// <summary>그 탭의 세션이 실행 중이다(<c>run_tab</c>·<c>export_result</c>, 2단계 C0). 끝난 뒤 다시 부른다.</summary>
    public const string TabBusy = "tab_busy";

    /// <summary>위 목록 전부(시험·문서용).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NotRunning, NoEndpoint, RunInvalid, GrantExpired, RunRevoked, RunRequired, HelloRequired, ShuttingDown, UnsupportedProtocol, BridgeError, SandboxBlocked,
        MultipleInstances, InstanceNotFound,
        Usage, UnknownTool, InvalidArguments, PathInvalid, BindInvalid,
        Connection, ConnectionRequired, Level, Environment, Scope, SqlIdScope, SqlNotSingleQuery, SqlSchemaUnresolved, SqlUnsafe, NoStatement,
        ExplainPlanDisabled, LiteralMaskUnavailable, SessionMismatch, UnsafeResult, Locked, ReadOnly, NotAllowed, RateLimited,
        FileExists, CommandDenied, CommandDisabled,
        ToolFailed, ToolException, InternalError, NotImplemented, Canceled, Busy, ResultTooLarge, TabNotFound, ProposalNotFound, NoResult,
        CommandNotFound, TabBusy,
    ];
}

/// <summary><c>orandactl</c> 종료 코드(설계 §4.1). 제안이 거절·낡음이어도 성공(0)이고 상태는 JSON 에 있다.</summary>
public static class AgentCliExit
{
    public const int Success = 0;

    /// <summary>도구 실패(처리자 실패, 모르는 코드 포함).</summary>
    public const int ToolFailed = 1;

    public const int Usage = 2;

    /// <summary>앱에 닿지 못함·토큰 폐기·만료.</summary>
    public const int Unreachable = 3;

    /// <summary>게이트·허가 거부.</summary>
    public const int Denied = 4;

    /// <summary>창을 고르지 못함.</summary>
    public const int InstanceChoice = 5;

    /// <summary>오류 코드의 종료 코드. null·모르는 코드는 <see cref="ToolFailed"/>.</summary>
    public static int For(string? code) => code switch
    {
        null => ToolFailed,
        AgentErrorCodes.NotRunning or AgentErrorCodes.NoEndpoint or AgentErrorCodes.RunInvalid or AgentErrorCodes.GrantExpired
            or AgentErrorCodes.RunRevoked or AgentErrorCodes.RunRequired or AgentErrorCodes.HelloRequired or AgentErrorCodes.ShuttingDown
            or AgentErrorCodes.UnsupportedProtocol or AgentErrorCodes.BridgeError or AgentErrorCodes.SandboxBlocked => Unreachable,
        AgentErrorCodes.MultipleInstances or AgentErrorCodes.InstanceNotFound => InstanceChoice,
        AgentErrorCodes.Usage or AgentErrorCodes.UnknownTool or AgentErrorCodes.InvalidArguments or AgentErrorCodes.PathInvalid
            or AgentErrorCodes.BindInvalid => Usage,
        AgentErrorCodes.Connection or AgentErrorCodes.ConnectionRequired or AgentErrorCodes.Level or AgentErrorCodes.Environment
            or AgentErrorCodes.Scope or AgentErrorCodes.SqlIdScope or AgentErrorCodes.SqlNotSingleQuery or AgentErrorCodes.SqlSchemaUnresolved
            or AgentErrorCodes.SqlUnsafe or AgentErrorCodes.NoStatement or AgentErrorCodes.ExplainPlanDisabled
            or AgentErrorCodes.LiteralMaskUnavailable or AgentErrorCodes.SessionMismatch or AgentErrorCodes.UnsafeResult
            or AgentErrorCodes.Locked or AgentErrorCodes.ReadOnly or AgentErrorCodes.NotAllowed or AgentErrorCodes.RateLimited
            or AgentErrorCodes.FileExists or AgentErrorCodes.CommandDenied or AgentErrorCodes.CommandDisabled => Denied,
        _ => ToolFailed,
    };
}
