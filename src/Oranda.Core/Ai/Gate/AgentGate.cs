using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 도구 호출 관문의 구현(설계 §5.1~§5.4). 판단 순서: 폐기 → 알려진 도구 → 인자 정리 → 접속 → 허용 수준 → 접속 설정 →
/// DB 세션 → 범위(객체·SQL·sql_id) → 처리자 → 결과 가림 → 크기 → sql_id 기억 → 감사 기록·사건.
/// 거부는 예외가 아니라 <c>IsError</c> 결과 <c>{"error":…,"code":…}</c> 로 돌려주고, 어떤 경로로 끝나든 감사 한 줄과 <see cref="ToolCompleted"/> 한 번을 낸다.
/// 한 세션의 호출은 한 번에 하나씩 처리한다(같은 DB 세션을 겹쳐 쓰지 않는다). 세션 폐기는 진행 중인 호출도 멈추고, 처리자에서 돌아온 결과를 버린다.
/// </summary>
public sealed partial class AgentGate : IAgentGate
{
    /// <summary>결과 글 최대 길이. 넘으면 처리자 결과를 버린다(LLM 문맥과 파이프 한 줄 한도를 지키려는 안전장치).</summary>
    public const int MaxResultChars = 80_000;

    private const int AuditArgumentsMaxChars = 400;
    private const int AuditValueMaxChars = 100;
    private const int AuditToolMaxChars = 200;
    private const int ExceptionMessageMaxChars = 200;
    private const int ErrorTextMaxChars = 300;
    private const int LogTextMaxChars = 2_000;

    /// <summary>U+2028·U+2029(U+0085 는 제어 문자에 든다). 줄바꿈으로 읽히는 유니코드 문자.</summary>
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private const string FailedGenericText = "The tool failed; details are available in the Oranda log.";

    /// <summary>
    /// 한글·SQL 의 따옴표 같은 글자를 \uXXXX 로 바꾸지 않는 인코더(글자 수가 늘어 80,000자 한도가 의미를 잃는 것을 막는다).
    /// 출력은 파이프 JSON 이고 HTML 이 아니므로 <c>&lt;</c>·<c>&amp;</c> 를 이스케이프하지 않아도 된다.
    /// </summary>
    private static readonly JsonWriterOptions CompactWriter = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAgentSessionSource _sessions;
    private readonly IStatementReferenceExtractor _extractor;
    private readonly Dictionary<string, IAgentToolHandler> _handlers;
    private readonly IAgentAuditStore _audit;
    private readonly TimeProvider _time;
    private readonly ISqlLiteralMasker? _masker;
    private readonly ConditionalWeakTable<AgentSession, ScopeHolder> _scopes = new();
    private readonly IUserFunctionSafetyChecker? _userFunctions;

    public AgentGate(
        IAgentSessionSource sessions,
        IStatementReferenceExtractor extractor,
        IEnumerable<IAgentToolHandler> handlers,
        IAgentAuditStore audit,
        TimeProvider? time = null,
        ISqlLiteralMasker? masker = null,
        IUserFunctionSafetyChecker? userFunctions = null)
    {
        _userFunctions = userFunctions;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        ArgumentNullException.ThrowIfNull(handlers);
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? TimeProvider.System;
        _masker = masker;

        // 같은 도구 이름의 처리자가 둘이면 어느 것이 도는지 모호하다. 조립 오류이므로 시작할 때 알린다.
        var map = new Dictionary<string, IAgentToolHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.ToolName, handler))
            {
                throw new ArgumentException($"Two handlers are registered for tool '{handler.ToolName}'.", nameof(handlers));
            }
        }

        _handlers = map;
    }

    /// <summary>
    /// 감사 저장에 주는 시간 제한(기본 5초). 저장소가 멈춰도 도구 결과는 이 시간 안에 돌아가고 메모에 <c>audit_failed</c> 가 붙는다.
    /// 호출자의 취소 토큰은 감사 저장에 쓰지 않는다(늦은 취소가 감사 한 줄을 지우지 못한다).
    /// </summary>
    public TimeSpan AuditTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

    public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsRevoked)
        {
            return [];
        }

        var level = session.Level;
        var production = session.Profile.Environment == ConnectionEnvironment.Production;
        var explainPlan = AiAccessPolicy.ExplainPlanEnabled(session.Profile);
        var tools = new List<AgentToolInfo>();
        foreach (var tool in AgentTools.All)
        {
            if (level == AiAccessLevel.Off
                || tool.MinimumLevel > level
                || (tool.DevTestOnly && production)
                || (tool.RequiresExplainPlanSetting && !explainPlan))
            {
                continue;
            }

            tools.Add(AgentToolInfo.From(tool));
        }

        return tools;
    }

    public async Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var requested = toolName ?? string.Empty;
        var shown = Clip(requested, AuditToolMaxChars);
        var sequence = session.NextSequence();
        var started = _time.GetTimestamp();
        var level = session.Level;
        var state = new CallState();

        // 호출자의 취소와 세션 폐기를 한 토큰으로 묶어 처리자·DB 세션 획득에 넘긴다.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.RevocationToken);

        GateOutcome outcome;
        try
        {
            outcome = await RunSerializedAsync(session, level, requested, arguments, state, ct, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 호출자의 취소는 그대로 전파하되, 이미 DB 에 나간 호출이 있을 수 있으므로 감사 한 줄과 사건은 남긴다.
            await FinishAsync(session, level, requested, shown, sequence, state, Fail("canceled", "The tool call was canceled."), _time.GetElapsedTime(started))
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // 분석기·DB 세션 선택 같은 협력 객체의 실패도 예외로 새지 않게 한다(감사 한 줄은 남아야 한다).
            outcome = ex is OperationCanceledException && session.IsRevoked
                ? Deny("run_revoked", "This run has ended and no longer accepts tool calls.")
                : Fail("internal_error", ExceptionText(ex, level, session.Run.Literals));
        }

        return await FinishAsync(session, level, requested, shown, sequence, state, outcome, _time.GetElapsedTime(started)).ConfigureAwait(false);
    }

    /// <summary>세션당 한 번에 한 호출만 처리한다. 기다리는 동안 폐기되면 거부하고, 호출자가 취소하면 취소 예외를 낸다.</summary>
    private async Task<GateOutcome> RunSerializedAsync(
        AgentSession session, AiAccessLevel level, string toolName, JsonNode? arguments, CallState state, CancellationToken ct, CancellationToken token)
    {
        try
        {
            await session.CallGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Deny("run_revoked", "This run has ended and no longer accepts tool calls.");
        }

        try
        {
            return await EvaluateAsync(session, level, toolName, arguments, state, ct, token).ConfigureAwait(false);
        }
        finally
        {
            session.CallGate.Release();
        }
    }

    /// <summary>감사 한 줄을 저장하고 <see cref="ToolCompleted"/> 를 낸다. 어떤 실패도 밖으로 내지 않는다(저장 실패는 메모에 <c>audit_failed</c> 를 덧붙인다).</summary>
    private async Task<AgentGateResponse> FinishAsync(
        AgentSession session, AiAccessLevel level, string toolName, string shownName, long sequence, CallState state, GateOutcome outcome, TimeSpan elapsed)
    {
        var note = outcome.Reason;
        try
        {
            var entry = BuildAuditEntry(session, level, toolName, shownName, state, outcome, elapsed);
            using var timeout = new CancellationTokenSource(AuditTimeout, _time);

            // 저장소가 토큰을 무시하고 멈춰도 시간 제한 안에 돌아온다.
            await _audit.AppendAsync(entry, timeout.Token).AsTask().WaitAsync(AuditTimeout, _time).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 감사 저장 실패가 조회를 막지 않는다(저장소 계약). 화면 사건의 메모에만 남긴다.
            note = note is null ? "audit_failed" : note + ", audit_failed";
        }

        try
        {
            ToolCompleted?.Invoke(this, new AgentToolActivityEventArgs(session.RunId, sequence, shownName, outcome.Judgement, outcome.Result.Text.Length, note));
        }
        catch (Exception)
        {
            // 화면 쪽 구독자의 오류가 도구 결과를 막지 않는다.
        }

        return new AgentGateResponse(outcome.Result, outcome.Judgement, note);
    }

    private async ValueTask<GateOutcome> EvaluateAsync(
        AgentSession session, AiAccessLevel level, string toolName, JsonNode? arguments, CallState state, CancellationToken ct, CancellationToken token)
    {
        if (session.IsRevoked)
        {
            return Deny("run_revoked", "This run has ended and no longer accepts tool calls.");
        }

        var tool = AgentTools.Find(toolName);
        if (tool is null)
        {
            return Deny("unknown_tool", "Unknown tool.");
        }

        // 리터럴 가림이 켜져 있는가는 첫 거부보다 먼저 정한다(어느 거부든 감사 요약에 literals=masked 가 남도록).
        var literals = session.Run.Literals;
        state.LiteralMasking = literals is not null && _masker is not null;

        if (CleanArguments(tool, arguments, state.Clean) is { } invalid)
        {
            return invalid;
        }

        // 스키마를 지난 문자열 인자는 모두 문자열이어야 한다. 읽지 못하면 검사를 건너뛰지 않고 거부한다.
        foreach (var name in StringArguments)
        {
            if (state.Clean.TryGetPropertyValue(name, out var node) && !(node is JsonValue v && v.TryGetValue<string>(out _)))
            {
                return Deny("invalid_arguments", $"The argument '{name}' must be a string.");
            }
        }

        if (TryGetString(state.Clean, AgentFields.Connection, out var connection)
            && !string.Equals(connection, session.Profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Deny("connection", "The connection argument must be the connection of the current request.");
        }

        if (level == AiAccessLevel.Off)
        {
            return Deny("level", "AI access is turned off for this connection.");
        }

        if (level < tool.MinimumLevel)
        {
            return Deny("level", $"The access level of this connection ({level}) does not allow this tool.");
        }

        var production = session.Profile.Environment == ConnectionEnvironment.Production;
        if (tool.DevTestOnly && production)
        {
            return Deny("environment", "This tool is not available on production connections.");
        }

        var explainPlanEnabled = AiAccessPolicy.ExplainPlanEnabled(session.Profile);
        if (tool.RequiresExplainPlanSetting && !explainPlanEnabled)
        {
            return Deny("explain_plan_disabled", "EXPLAIN PLAN is not allowed for this connection.");
        }

        // 리터럴 가림(설계 ai-tuning-2): 사전이 있으면 가림 켜짐이고 운영 접속은 항상 필요하다. 필요한데 사전이나 마스커가 없으면
        // SQL 을 다루는 도구를 막는다(닫힌 쪽으로 실패). 켜져 있으면 인자의 자리표시를 원래 글로 되돌린 뒤 범위 검사를 한다.
        if (TouchesSql(tool.Name) && (production || session.Run.MaskLiteralsRequired || literals is not null))
        {
            if (!state.LiteralMasking)
            {
                return Deny("literal_mask_unavailable", "Literal masking is required for this request but is not available; SQL tools are disabled.");
            }

            if (RestoreLiterals(state, literals!) is { } unknown)
            {
                return unknown;
            }
        }

        var db = await _sessions.AcquireDbSessionAsync(session, token).ConfigureAwait(false);

        // 다른 접속의 DB 세션이 돌아오면 이 실행의 범위 검사가 의미를 잃는다.
        if (db.Profile.Id != Guid.Empty && session.Profile.Id != Guid.Empty && db.Profile.Id != session.Profile.Id)
        {
            return Deny("session_mismatch", "The database session does not belong to the connection of this request.");
        }

        if (await CheckScopeAsync(session, db, tool, state, token).ConfigureAwait(false) is { } outOfScope)
        {
            return outOfScope;
        }

        if (!_handlers.TryGetValue(tool.Name, out var handler))
        {
            return Fail("not_implemented", "This tool is not implemented yet.");
        }

        var invocation = new AgentToolInvocation(
            session,
            db,
            level,
            state.Clean,
            state.Resolved,
            ExplainPlanAllowed: level >= AiAccessLevel.Plan && explainPlanEnabled,
            MayExecuteSql: level >= AiAccessLevel.Query && state.StatementsChecked,
            ReadOnlyExecution: state.ReadOnlyExecution,
            Substitutions: state.FirstSubstitution is null ? null : [.. state.Substitutions]);

        AgentToolOutput output;
        try
        {
            output = await handler.ExecuteAsync(invocation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 호출자의 취소가 아닌 취소(세션 폐기·처리자 안의 시간 초과)는 실패로 본다.
            if (ex is OperationCanceledException && session.IsRevoked)
            {
                return Deny("run_revoked", "This run has ended and no longer accepts tool calls.");
            }

            LogFailure(session, tool.Name, ex.GetType().Name, ex.Message);
            return FailWithKind("tool_exception", ExceptionText(ex, level, session.Run.Literals), ex.Message);
        }

        // 처리자가 도는 동안 폐기됐으면 결과를 쓰지 않는다(sql_id 도 기억하지 않는다).
        if (session.IsRevoked)
        {
            return Deny("run_revoked", "This run has ended and no longer accepts tool calls.");
        }

        if (output is null)
        {
            return Fail("tool_failed", "The tool returned no result.");
        }

        if (output.IsError)
        {
            LogFailure(session, tool.Name, output.ErrorMessage!, output.LogDetail);

            // 앱이 쓴 거부 글(도구 상자의 거부)은 어느 수준에서든 정화해 보인다: 모델이 무엇을 고칠지 알아야 같은 실수를 되풀이하지 않는다.
            var shownError = output.AppWritten
                ? SanitizeError(session.Run.Literals?.Redact(output.ErrorMessage!) ?? output.ErrorMessage!)
                : HandlerErrorText(output.ErrorMessage!, level, session.Run.Literals);
            return FailWithKind("tool_failed", shownError, output.ErrorMessage!);
        }

        // 처리자가 캐시 노드를 돌려줄 수 있으므로 복사본에서 가린다.
        var result = output.Result?.DeepClone() ?? new JsonObject();
        var removed = MaskRowValues(ref result, tool.Name, level, state.Clean);

        // 결과의 SQL 글·술어 칸(AgentFields.SqlTextFields)에서 리터럴을 가린다. 가릴 수 없는 모양의 결과는 내지 않는다.
        if (state.LiteralMasking)
        {
            if (!IsMaskableShape(result))
            {
                return Deny("unsafe_result", "The tool result has a shape whose SQL text cannot be masked; it was withheld.");
            }

            MaskSqlTextFields(ref result, literals!);
        }

        // 가릴 수 없는 모양의 결과(문자열·숫자 루트, JSON 글이 든 문자열)는 행 값이 숨어 있을 수 있어 내지 않는다.
        if (level < AiAccessLevel.QueryRows && !IsMaskableShape(result))
        {
            return Deny("unsafe_result", "The tool result has a shape whose row values cannot be masked; it was withheld.");
        }

        var text = Serialize(result);
        if (text.Length > MaxResultChars)
        {
            return Fail(
                "result_too_large",
                $"The result is too large ({text.Length} characters, limit {MaxResultChars}); narrow the request, for example one table, fewer columns or a smaller budget_tokens.");
        }

        foreach (var sqlId in output.SqlIds)
        {
            session.AddSqlId(sqlId);
        }

        // 대체한 결과는 성공이다. 화면 카드에는 대체했다는 것과 첫 사유를 남긴다(substituted: user_function_autonomous).
        var reason = state.FirstSubstitution is { } substituted ? "substituted: " + substituted : null;
        return new GateOutcome(
            new AgentCallToolResult(false, text),
            removed > 0 ? AiToolJudgement.Masked : AiToolJudgement.Allowed,
            removed > 0 ? (reason is null ? "masked" : "masked, " + reason) : reason);
    }

    private AgentAuditEntry BuildAuditEntry(
        AgentSession session, AiAccessLevel level, string toolName, string shownName, CallState state, GateOutcome outcome, TimeSpan elapsed) =>
        new()
        {
            OccurredAt = _time.GetUtcNow(),
            ProfileId = session.Profile.Id,
            ProfileName = session.Profile.Name,
            Client = session.Client,
            RunId = session.RunId,
            Tool = shownName,
            Arguments = AgentTools.Find(toolName) is null ? null : SummarizeArguments(state),
            AccessLevel = level,
            Verdict = outcome.Judgement,
            Reason = outcome.Reason,
            ResultChars = outcome.Result.Text.Length,
            ElapsedMs = (long)elapsed.TotalMilliseconds,
        };

    private static GateOutcome Deny(string code, string message) => Reject(AiToolJudgement.Denied, code, message);

    private static GateOutcome Fail(string code, string message) => Reject(AiToolJudgement.Failed, code, message);

    /// <summary>
    /// 처리자 실패(<paramref name="rawMessage"/> 는 처리자 오류 글 또는 예외 메시지). 감사 사유에 오류 종류로 DB 오류 번호만 덧붙인다
    /// (<c>tool_failed: ORA-00942</c>). 번호가 없으면 사유는 코드뿐이다. 원문은 감사 기록에 넣지 않는다.
    /// </summary>
    private static GateOutcome FailWithKind(string code, string message, string rawMessage)
    {
        var failed = Fail(code, message);
        var ora = OraNumber().Match(rawMessage);
        return ora.Success ? failed with { Reason = $"{code}: {ora.Value}" } : failed;
    }

    private static GateOutcome Reject(AiToolJudgement judgement, string code, string message)
    {
        var body = new JsonObject { ["error"] = message, ["code"] = code };
        return new GateOutcome(new AgentCallToolResult(true, Serialize(body)), judgement, code);
    }

    /// <summary>
    /// 예외를 LLM 에 보일 글로. 행 값을 줄 수 없는 수준(<see cref="AiAccessLevel.QueryRows"/> 미만)에서는 형 이름만,
    /// 그 위에서는 형 이름과 정화한 메시지 앞부분.
    /// </summary>
    private static string ExceptionText(Exception ex, AiAccessLevel level, LiteralVault? vault = null)
    {
        var name = ex.GetType().Name;
        if (level >= AiAccessLevel.QueryRows)
        {
            // 정화·자르기 전에 사전의 값을 먼저 가린다(정화가 줄바꿈을 공백으로 바꾸고 300자로 자르면 값이 더는 맞지 않는다).
            return SanitizeError(name + ": " + (vault?.Redact(ex.Message) ?? ex.Message));
        }

        // 메시지에 오류 번호가 있으면 형 이름과 함께 남긴다(번호만으로는 값·서버 정보가 새지 않는다).
        // 예외를 감싼 형(InvalidOperationException 등)도 번호는 같이 보여야 LLM 이 다른 도구로 돌아갈 수 있다.
        var ora = OraNumber().Match(ex.Message);
        return ora.Success ? name + ": " + ora.Value : name;
    }

    /// <summary>
    /// 처리자의 오류 글을 LLM 에 보일 글로. 행 값을 줄 수 없는 수준에서는 DB 오류 번호(있으면)와 고정 문장뿐이다
    /// (오류 글에 끼어드는 값·서버 정보를 막는다). 그 위에서는 정화한 글.
    /// </summary>
    private static string HandlerErrorText(string message, AiAccessLevel level, LiteralVault? vault = null)
    {
        if (level >= AiAccessLevel.QueryRows)
        {
            return SanitizeError(vault?.Redact(message) ?? message);
        }

        var ora = OraNumber().Match(message);
        return ora.Success ? ora.Value + ": " + FailedGenericText : FailedGenericText;
    }

    /// <summary>
    /// 처리자 실패를 Oranda 로그(<see cref="Trace"/>: 앱이 데이터 폴더 <c>logs</c> 에 쓴다)에 남긴다. 감사 기록에는 오류 번호만 남으므로 원인은 여기서만 볼 수 있다.
    /// <paramref name="message"/> 는 처리자의 오류 글, <paramref name="detail"/> 은 원인 예외의 원문(없으면 null)이다.
    /// 바인드 값(긴 값부터) → 사전의 리터럴 순으로 가리고, 원문에 남은 따옴표 글(사전이 없을 때의 리터럴 등)은 통째로 지운다.
    /// 글자로 확실히 맞출 수 없는 바인드 값(날짜·이진 등)이 있으면 원문 대신 오류 번호만 남긴다. 로그 쓰기 실패는 도구 결과를 막지 않는다.
    /// </summary>
    private static void LogFailure(AgentSession session, string tool, string message, string? detail)
    {
        try
        {
            var forms = new List<string>();
            var unsure = false;
            foreach (var bind in session.Run.Binds.Values)
            {
                unsure |= !TryAddBindForms(bind, forms);
            }

            forms = [.. forms.Distinct(StringComparer.Ordinal).OrderByDescending(f => f.Length)];
            var text = Redact(message, session.Run.Literals, forms);
            if (detail is not null)
            {
                var shownDetail = unsure
                    ? string.Join(", ", OraNumber().Matches(detail).Select(m => m.Value).Distinct(StringComparer.Ordinal))
                    : QuotedText().Replace(Redact(detail, session.Run.Literals, forms), "'[removed]'");
                text += " / " + (shownDetail.Length > 0 ? shownDetail : "[detail withheld]");
            }

            Trace.TraceWarning("AI 도구 실패 ({0}, run {1}): {2}", tool, session.RunId, Clip(text, LogTextMaxChars));
        }
        catch (Exception)
        {
            // 로그 수신자의 오류가 도구 결과를 막지 않는다.
        }
    }

    /// <summary>바인드 값을 먼저 가리고(사전이 따옴표 안을 바꾸면 값이 더는 맞지 않는다) 사전의 리터럴을 가린다.</summary>
    private static string Redact(string text, LiteralVault? vault, IReadOnlyList<string> bindForms)
    {
        foreach (var form in bindForms)
        {
            // 앞뒤가 영문·숫자가 아닌 자리만 바꾼다(값 1 이 다른 숫자를 망가뜨리지 않게, 한글이 바로 붙은 값도 맞게). 오류 번호의 숫자는 그대로 둔다.
            var pattern = @"(?<![A-Za-z0-9_])(?<!ORA-)" + Regex.Escape(form) + @"(?![A-Za-z0-9_])";
            text = Regex.Replace(text, pattern, "[bind]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        return vault?.Redact(text) ?? text;
    }

    /// <summary>바인드 값이 글에 나타날 수 있는 모양을 더한다. 문자열·정수·실수만 확실히 맞출 수 있다(그 밖이면 false).</summary>
    private static bool TryAddBindForms(BindValue bind, List<string> forms)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        switch (bind.Value)
        {
            case null:
                return true;
            case string text:
                if (text.Length > 0)
                {
                    forms.Add(text);
                }

                return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                forms.Add(((IFormattable)bind.Value).ToString(null, invariant));
                return true;
            case decimal or double or float:
                var number = ((IFormattable)bind.Value).ToString(null, invariant);
                forms.Add(number);
                // Oracle 은 1 보다 작은 수를 앞의 0 없이 쓴다(.5, -.5).
                if (number.StartsWith("0.", StringComparison.Ordinal) || number.StartsWith("-0.", StringComparison.Ordinal))
                {
                    forms.Add(number.Replace("0.", ".", StringComparison.Ordinal));
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>작은따옴표로 감싼 글(한 줄, 400자까지).</summary>
    [GeneratedRegex(@"'[^'\r\n]{0,400}'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedText();

    /// <summary>오류 글 정화: <c>host:port</c>·IP·경로를 지우고 줄바꿈을 없애며 300자로 줄인다.</summary>
    private static string SanitizeError(string text)
    {
        // 먼저 정화하고 나중에 자른다(자르면서 호스트 이름 같은 것이 반쯤 남는 일이 없게). 입력은 정규식 비용을 위해 넉넉히만 제한한다.
        var cleaned = text.Length > 4_000 ? text[..4_000] : text;
        cleaned = TnsDescriptor().Replace(cleaned, "[removed]");
        cleaned = ListenerHostPort().Replace(cleaned, "[removed]");
        cleaned = DrivePath().Replace(cleaned, "[removed]");
        cleaned = UncPath().Replace(cleaned, "[removed]");
        cleaned = UnixPath().Replace(cleaned, "[removed]");
        cleaned = IpAndPort().Replace(cleaned, "[removed]");
        cleaned = HostPort().Replace(cleaned, "[removed]");
        cleaned = IpAddress().Replace(cleaned, "[removed]");
        cleaned = ServiceName().Replace(cleaned, "$1 [removed] $2");
        return Clip(cleaned, ErrorTextMaxChars);
    }

    /// <summary>TNS 접속 기술자 조각: <c>(HOST=…)</c>·<c>(PORT=…)</c>·<c>(SERVICE_NAME=…)</c> 등.</summary>
    [GeneratedRegex(@"\(\s*(?:HOST|PORT|SERVICE_NAME|SID|SERVER|INSTANCE_NAME)\s*=[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TnsDescriptor();

    /// <summary>리스너 오류 문장: <c>No listener at host X port N</c>.</summary>
    [GeneratedRegex(@"\bhost\s+\S+\s+port\s+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListenerHostPort();

    /// <summary>Unix 절대 경로: <c>/</c> 로 시작하고 <c>/</c> 가 두 번 이상.</summary>
    [GeneratedRegex(@"(?<![\w:./])/[^\s/'""()]+(?:/[^\s'""()]*)+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}:\d{2,5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpAndPort();

    /// <summary><c>Service X is not registered</c> 의 서비스 이름.</summary>
    [GeneratedRegex(@"(\bService)\s+\S+\s+(is not registered)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"ORA-\d{5}", RegexOptions.CultureInvariant)]
    private static partial Regex OraNumber();

    [GeneratedRegex(@"[A-Za-z]:[\\/][^\s'""]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"\\\\[^\s'""]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();

    /// <summary>글자로 시작하는 호스트 이름과 포트(시각 <c>09:30</c> 같은 숫자 쌍은 지우지 않는다).</summary>
    [GeneratedRegex(@"\b[A-Za-z][\w.\-]*:\d{2,5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HostPort();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddress();

    private static string Serialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CompactWriter))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>제어 문자·줄바꿈(U+2028·U+2029·U+0085 포함)을 공백으로 바꾸고 앞 <paramref name="max"/> 글자만 남긴다.</summary>
    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clipped = text.Length > max ? text[..max] : text;
        var builder = new StringBuilder(clipped.Length);
        foreach (var c in clipped)
        {
            builder.Append(char.IsControl(c) || c == LineSeparator || c == ParagraphSeparator ? ' ' : c);
        }

        return builder.ToString();
    }

    private static bool TryGetString(JsonObject obj, string key, out string value)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>문자열이어야 하는 선언 인자.</summary>
    private static readonly string[] StringArguments =
    [
        AgentFields.Connection, AgentFields.ObjectName, AgentFields.Table, AgentFields.Column, AgentFields.SqlId, AgentFields.Sql,
    ];

    /// <summary>한 번 호출 동안 모으는 값: 정리된 인자와 해석된 객체(감사 기록의 인자 요약이 쓴다).</summary>
    private sealed class CallState
    {
        public JsonObject Clean { get; } = new();

        public Dictionary<string, AgentObjectRef> Resolved { get; } = new(StringComparer.Ordinal);

        /// <summary>이 호출에 리터럴 가림이 켜져 있다(사전과 마스커가 모두 있다). 감사 기록의 인자 요약에 <c>literals=masked</c> 로 남는다.</summary>
        public bool LiteralMasking { get; set; }

        /// <summary>되돌리기 전, LLM 이 보낸 <c>sql</c> 인자(가린 글)의 길이. 감사 요약이 쓴다. 되돌리지 않았으면 null.</summary>
        public int? MaskedSqlLength { get; set; }

        /// <summary><c>sql</c>·<c>variants[].sql</c> 에 쓰인 자리표시 수. 되돌리지 않았으면 null.</summary>
        public int? PlaceholderCount { get; set; }

        /// <summary>처리자가 실행·계획에 쓸 문장(<c>sql</c>·<c>variants</c>·대상 문장)을 게이트가 검사했는가.</summary>
        public bool StatementsChecked { get; set; }

        /// <summary>검사한 문장(들)에 든 사용자 함수·패키지 함수 후보 수(감사 요약의 <c>user_functions=N</c>).</summary>
        public int UserFunctionCount { get; set; }

        /// <summary>실행할 문장에 사용자 함수가 있고 확인을 통과했다(또는 사용자가 위험 함수 실행을 켰다): 처리자는 읽기 전용 트랜잭션 안에서만 실행한다.</summary>
        public bool ReadOnlyExecution { get; set; }

        /// <summary><c>variants</c> 순서대로, 실행하지 않고 커서 실측으로 대체할 변형의 사유 코드(실행하면 null).</summary>
        public List<string?> Substitutions { get; } = [];

        public string? FirstSubstitution => Substitutions.FirstOrDefault(x => x is not null);
    }

    /// <summary>판단 하나: 돌려줄 결과, 화면 판단, 사유 코드(Allowed 면 null, Masked 면 <c>masked</c>).</summary>
    private sealed record GateOutcome(AgentCallToolResult Result, AiToolJudgement Judgement, string? Reason);
}
