using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 외부 허가(밖의 LLM CLI)의 도구 관문(설계 agent-cli-control §3.2, A3). 다리(<c>AgentBridgeHost</c>)가 hello 로 토큰을 허가로 바꾼 연결에서 부른다.
/// Host Db 도구는 접속을 고른 뒤(<c>connection</c> 필수, 허용 접속이 하나면 생략 가능) 그 접속의 하위 세션으로 기존 관문을 그대로 지나고,
/// Host App·Lang 도구는 DB 세션 없이 <see cref="IAppToolHandler"/> 로 간다(같은 인자 정리·수준·감사·결과 한도).
/// </summary>
public interface IAgentExternalGate
{
    /// <summary>이 허가에서 쓸 수 있는 도구(<c>oranda.listTools</c>).</summary>
    IReadOnlyList<AgentToolInfo> ListTools(AgentGrantView grant);

    /// <summary>DB 를 쓰지 않는 도구다(App·Lang). 다리는 이 도구를 DB 조회 대기열(<c>_callGate</c>) 밖에서 처리한다(§8.4).</summary>
    bool RunsWithoutDatabase(string toolName);

    Task<AgentGateResponse> CallToolAsync(AgentGrantView grant, string? client, string toolName, JsonNode? arguments, CancellationToken ct);

    /// <summary>
    /// 외부 도구 호출이 끝날 때마다(거부 포함, Db·App·Lang 모두) 한 번. 상태 표시줄 「최근 호출」 의 원천이다.
    /// Db 도구는 <see cref="IAgentGate.ToolCompleted"/> 도 함께 난다(실행 id = 허가 id:접속).
    /// </summary>
    event EventHandler<AgentExternalToolActivity>? ExternalToolCompleted;
}

/// <summary><see cref="IAgentExternalGate.ExternalToolCompleted"/> 인자.</summary>
/// <param name="GrantId">허가 id.</param>
/// <param name="Client">그 연결의 클라이언트 이름(hello 의 <c>client.name</c>).</param>
/// <param name="Connection">도구가 쓴 접속 별칭. 접속 없는 도구는 null.</param>
/// <param name="Sequence">외부 호출 순번(앱 전체에서 늘어난다).</param>
/// <param name="ToolName">도구 이름.</param>
/// <param name="Judgement">판단.</param>
/// <param name="Size">돌려준 글자 수.</param>
/// <param name="Note">사유 메모(거부 코드·<c>masked</c> 등).</param>
/// <param name="At">끝난 시각(UTC).</param>
public sealed record AgentExternalToolActivity(
    string GrantId,
    string? Client,
    string? Connection,
    long Sequence,
    string ToolName,
    AiToolJudgement Judgement,
    int Size,
    string? Note,
    DateTimeOffset At);

public sealed partial class AgentGate
{
    private readonly IAgentGrantSessions? _grants;
    private readonly IServiceProvider? _services;
    private Dictionary<string, IAppToolHandler>? _appHandlers;
    private long _externalSequence;

    public event EventHandler<AgentExternalToolActivity>? ExternalToolCompleted;

    /// <summary>시험용: 앱 도구 처리자를 직접 넣는다(없으면 서비스에서 처음 쓸 때 찾는다).</summary>
    public IEnumerable<IAppToolHandler>? AppHandlers
    {
        init => _appHandlers = value is null ? null : MapAppHandlers(value);
    }

    public IReadOnlyList<AgentToolInfo> ListTools(AgentGrantView grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var tools = new List<AgentToolInfo>();
        foreach (var tool in AppTools.External)
        {
            if (Usable(grant, tool))
            {
                tools.Add(AgentToolInfo.From(tool));
            }
        }

        return tools;
    }

    public bool RunsWithoutDatabase(string toolName) => AppTools.Find(toolName) is { Host: not AgentToolHost.Db };

    public async Task<AgentGateResponse> CallToolAsync(AgentGrantView grant, string? client, string toolName, JsonNode? arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var requested = toolName ?? string.Empty;
        var shown = Clip(requested, AuditToolMaxChars);
        var started = _time.GetTimestamp();
        var state = new CallState();
        AgentGrantConnection? connection = null;
        GateOutcome outcome;
        try
        {
            // 연결에 묶인 사본은 오래됐을 수 있다. 지금 값을 다시 본다(끊기·만료·접속 설정 변경).
            var current = _grants is null ? grant : _grants.Find(grant.GrantId);
            if (current is null)
            {
                outcome = Deny(AgentErrorCodes.RunRevoked, "This external connection has ended; turn it on again in Oranda.");
            }
            else
            {
                var tool = AppTools.Find(requested);
                if (tool is null)
                {
                    outcome = Deny(AgentErrorCodes.UnknownTool, "Unknown tool.");
                }
                else if (tool.Host == AgentToolHost.Db)
                {
                    return await CallDbToolAsync(current, client, tool, arguments, shown, started, ct).ConfigureAwait(false);
                }
                else
                {
                    (outcome, connection) = await EvaluateAppAsync(current, client, tool, arguments, state, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishExternalAsync(grant, client, connection, requested, shown, state, Fail(AgentErrorCodes.Canceled, "The tool call was canceled."), _time.GetElapsedTime(started))
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            outcome = Fail(AgentErrorCodes.InternalError, ex.GetType().Name);
        }

        return await FinishExternalAsync(grant, client, connection, requested, shown, state, outcome, _time.GetElapsedTime(started)).ConfigureAwait(false);
    }

    /// <summary>
    /// Db 도구: 접속을 고르고 그 접속의 하위 세션으로 기존 관문을 지난다(수준·범위·가림·감사는 기존 그대로).
    /// 접속을 못 고르면 여기서 거부하고 감사 한 줄을 남긴다.
    /// </summary>
    private async Task<AgentGateResponse> CallDbToolAsync(
        AgentGrantView grant, string? client, AgentToolDefinition tool, JsonNode? arguments, string shown, long started, CancellationToken ct)
    {
        var alias = arguments is JsonObject obj && obj[AgentFields.Connection] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        if (arguments is not (null or JsonObject))
        {
            return await FinishExternalAsync(grant, client, null, tool.Name, shown, new CallState(), Deny(AgentErrorCodes.InvalidArguments, "The tool arguments must be a JSON object."), _time.GetElapsedTime(started))
                .ConfigureAwait(false);
        }

        var connection = grant.ResolveConnection(alias, out var code);
        if (connection is null)
        {
            var outcome = code == AgentErrorCodes.ConnectionRequired
                ? Deny(code, "Pass connection: more than one connection is allowed. Call list_connections.")
                : Deny(AgentErrorCodes.Connection, "This connection is not allowed for this external connection. Call list_connections.");
            return await FinishExternalAsync(grant, client, null, tool.Name, shown, new CallState(), outcome, _time.GetElapsedTime(started)).ConfigureAwait(false);
        }

        var session = _grants?.SessionFor(grant.GrantId, connection.ProfileId, client);
        if (session is null)
        {
            return await FinishExternalAsync(grant, client, connection, tool.Name, shown, new CallState(), Deny(AgentErrorCodes.RunRevoked, "This external connection has ended; turn it on again in Oranda."), _time.GetElapsedTime(started))
                .ConfigureAwait(false);
        }

        // 생략한 connection 을 채워 하위 세션의 접속 검사와 감사 요약이 같은 값을 보게 한다.
        var args = (JsonObject?)arguments?.DeepClone() ?? [];
        args[AgentFields.Connection] = connection.Alias;
        var response = await CallToolAsync(session, tool.Name, args, ct).ConfigureAwait(false);
        var reason = response.Result.IsError ? CodeOf(response.Result.Text) : null;
        RaiseExternal(grant, client, connection, shown, response.Judgement, response.Result.Text.Length, response.Note ?? reason);
        return response;
    }

    private async Task<(GateOutcome Outcome, AgentGrantConnection? Connection)> EvaluateAppAsync(
        AgentGrantView grant, string? client, AgentToolDefinition tool, JsonNode? arguments, CallState state, CancellationToken ct)
    {
        if (CleanArguments(tool, arguments, state.Clean) is { } invalid)
        {
            return (invalid, null);
        }

        // connection 인자가 있는 도구: 값이 있으면 허가 안 별칭이어야 하고, 없으면 허용 접속이 하나일 때만 그 접속이다.
        AgentGrantConnection? connection = null;
        var declares = DeclaresConnection(tool);
        if (declares)
        {
            TryGetString(state.Clean, AgentFields.Connection, out var alias);
            connection = grant.ResolveConnection(alias, out var code);
            if (connection is null && (code == AgentErrorCodes.Connection || NeedsConnection(tool)))
            {
                return (code == AgentErrorCodes.ConnectionRequired
                    ? Deny(code, "Pass connection: more than one connection is allowed. Call list_connections.")
                    : Deny(AgentErrorCodes.Connection, "This connection is not allowed for this external connection. Call list_connections."), null);
            }
        }

        if (connection is not null)
        {
            if (connection.Level == AiAccessLevel.Off)
            {
                return (Deny(AgentErrorCodes.Level, "AI access is turned off for this connection."), connection);
            }

            if (connection.Level < tool.MinimumLevel)
            {
                return (Deny(AgentErrorCodes.Level, $"The access level of this connection ({connection.Level}) does not allow this tool."), connection);
            }

            // 운영 갈래(D5, E-22): 실행 도구(DevTestOnly = query)는 허가 정책이 그 환경의 실행을 허용할 때만. 내장 AI 경로(AgentGate.cs)는 그대로(E-23).
            if (tool.DevTestOnly && !AgentGrantPolicy.ExecutesIn(connection.Environment))
            {
                return (Deny(AgentErrorCodes.Environment, "This tool is not available on production connections."), connection);
            }

            // 「로컬 모델」 허가가 이 접속을 덮으면(UsesLocalModel) 감사 인자 요약에 data=local_ai 를 남긴다(가리지 않고 나간 호출).
            state.LocalAi = AgentGrantPolicy.UsesLocalModel(grant, connection.Environment);
        }

        var handlers = AppHandlerMap();
        if (!handlers.TryGetValue(tool.Name, out var handler))
        {
            return (Fail(AgentErrorCodes.NotImplemented, "This tool is not implemented yet."), connection);
        }

        AgentToolOutput? output;
        try
        {
            output = await handler.ExecuteAsync(new AppToolInvocation(grant, client ?? string.Empty, state.Clean, connection), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 앱 도구 예외 글에는 파일 경로·탭 글이 섞일 수 있다. 형 이름만 보인다.
            return (Fail(AgentErrorCodes.ToolException, ex.GetType().Name + ": " + FailedGenericText), connection);
        }

        if (output is null)
        {
            return (Fail(AgentErrorCodes.ToolFailed, "The tool returned no result."), connection);
        }

        // 감사에 적을 접속: 호출이 정한 접속, 없으면 처리자가 알린 접속(탭이 접속을 정하는 도구). 가림은 호출이 정한 접속에서만 한다(아래).
        var audited = connection ?? Reported(grant, output, state);
        if (output.IsError)
        {
            var code = output.ErrorCode ?? AgentErrorCodes.ToolFailed;
            var text = output.AppWritten ? SanitizeError(output.ErrorMessage!) : FailedGenericText;
            return (AgentCliExit.For(code) == AgentCliExit.Denied ? Deny(code, text) : Fail(code, text), audited);
        }

        var result = output.Result?.DeepClone() ?? new JsonObject();
        var removed = 0;
        if (connection is not null)
        {
            // 접속이 정해진 호출은 값 수준으로 행 값 칸을 가린다. 탭이 접속을 정하는 도구는 처리자가 판단했다.
            // 값 수준 = AiAccessPolicy.DataLevel(수준, 로컬 모델): 클라우드 모델 운영(Query)은 행 값을 지우고, 로컬 모델이면 행 값까지 보인다(D4·D5).
            var dataLevel = AiAccessPolicy.DataLevel(connection.Level, state.LocalAi);
            removed = MaskRowValues(ref result, tool.Name, dataLevel, state.Clean, state.LocalAi);
            if (dataLevel < AiAccessLevel.QueryRows && !IsMaskableShape(result))
            {
                return (Deny(AgentErrorCodes.UnsafeResult, "The tool result has a shape whose row values cannot be masked; it was withheld."), connection);
            }
        }

        var serialized = Serialize(result);
        if (serialized.Length > MaxResultChars)
        {
            return (Fail(
                AgentErrorCodes.ResultTooLarge,
                $"The result is too large ({serialized.Length} characters, limit {MaxResultChars}); narrow the request, for example a smaller range or limit."), audited);
        }

        return (new GateOutcome(
            new AgentCallToolResult(false, serialized),
            removed > 0 ? AiToolJudgement.Masked : AiToolJudgement.Allowed,
            WithNotes(removed > 0 ? "masked" : null, output)), audited);
    }

    /// <summary>
    /// 처리자가 알린 접속(<see cref="AgentToolOutput.ProfileId"/>)을 허가 안 접속으로 바꾼다. 허가 안이면 그 접속의 「로컬 모델」 여부를 감사 요약(<c>data=local_ai</c>)에 싣는다.
    /// 허가 밖 프로필은 믿지 않는다 — 그 접속으로 적지 않고(null) <c>connection=outside_grant</c> 만 남긴다.
    /// </summary>
    private static AgentGrantConnection? Reported(AgentGrantView grant, AgentToolOutput output, CallState state)
    {
        if (output.ProfileId is not { } profileId)
        {
            return null;
        }

        if (grant.FindConnection(profileId) is not { } reported)
        {
            state.ReportedOutsideGrant = true;
            return null;
        }

        state.LocalAi = AgentGrantPolicy.UsesLocalModel(grant, reported.Environment);
        return reported;
    }

    /// <summary>
    /// 성공 호출의 감사 사유에 처리자 글을 붙인다(G5, 계획서 §3.2): 행 수(<see cref="AgentToolOutput.RowCount"/>, <c>query</c>·<c>run_tab</c>·<c>export_result</c>)는
    /// <c>rows=N</c>, 그다음 <see cref="AgentToolOutput.AuditNote"/>(예: <c>grade=confirm,status=confirmed</c>). 쉼표로 잇는다(<c>masked, rows=N</c>). 둘 다 없으면 사유 그대로.
    /// 운영 외부 호출은 판단과 관계없이 모두 감사에 남고(<see cref="FinishExternalAsync"/>), 내보낸 경로는 인자 요약(<c>out=</c>)에 남는다.
    /// </summary>
    internal static string? WithNotes(string? reason, AgentToolOutput output)
    {
        var parts = new List<string>(3);
        if (reason is not null)
        {
            parts.Add(reason);
        }

        if (output.RowCount is { } rows)
        {
            parts.Add(AgentToolOutput.RowsNote(rows));
        }

        if (!string.IsNullOrWhiteSpace(output.AuditNote))
        {
            parts.Add(output.AuditNote.Trim());
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>외부 호출의 감사 한 줄과 사건. 실행 id 는 <c>허가 id[:접속]</c>.</summary>
    private async Task<AgentGateResponse> FinishExternalAsync(
        AgentGrantView grant, string? client, AgentGrantConnection? connection, string toolName, string shownName, CallState state, GateOutcome outcome, TimeSpan elapsed)
    {
        var note = outcome.Reason;
        var runId = connection is null ? grant.GrantId : grant.GrantId + ":" + connection.Alias;
        try
        {
            var entry = new AgentAuditEntry
            {
                OccurredAt = _time.GetUtcNow(),
                ProfileId = connection?.ProfileId,
                ProfileName = connection?.Alias,
                Client = client,
                RunId = runId,
                Tool = shownName,
                Arguments = AppTools.Find(toolName) is null ? null : SummarizeArguments(state),
                AccessLevel = connection?.Level ?? AiAccessLevel.Off,
                Verdict = outcome.Judgement,
                Reason = outcome.Reason,
                ResultChars = outcome.Result.Text.Length,
                ElapsedMs = (long)elapsed.TotalMilliseconds,
            };
            using var timeout = new CancellationTokenSource(AuditTimeout, _time);
            await _audit.AppendAsync(entry, timeout.Token).AsTask().WaitAsync(AuditTimeout, _time).ConfigureAwait(false);
        }
        catch (Exception)
        {
            note = note is null ? "audit_failed" : note + ", audit_failed";
        }

        try
        {
            ToolCompleted?.Invoke(this, new AgentToolActivityEventArgs(runId, Interlocked.Increment(ref _externalSequence), shownName, outcome.Judgement, outcome.Result.Text.Length, note));
        }
        catch (Exception)
        {
            // 화면 쪽 구독자의 오류가 도구 결과를 막지 않는다.
        }

        RaiseExternal(grant, client, connection, shownName, outcome.Judgement, outcome.Result.Text.Length, note);
        return new AgentGateResponse(outcome.Result, outcome.Judgement, note);
    }

    private void RaiseExternal(AgentGrantView grant, string? client, AgentGrantConnection? connection, string tool, AiToolJudgement judgement, int size, string? note)
    {
        try
        {
            ExternalToolCompleted?.Invoke(this, new AgentExternalToolActivity(
                grant.GrantId, client, connection?.Alias, Interlocked.Increment(ref _externalSequence), tool, judgement, size, note, _time.GetUtcNow()));
        }
        catch (Exception)
        {
            // 화면 쪽 구독자의 오류가 도구 결과를 막지 않는다.
        }
    }

    /// <summary>오류 결과 글의 <c>code</c>(사건 메모용). 못 읽으면 null.</summary>
    private static string? CodeOf(string text)
    {
        try
        {
            return JsonNode.Parse(text)?["code"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 목록에 보일 도구: 수준이 필요 없는 앱 도구는 언제나, 수준이 필요한 도구는 그 수준 이상인 허용 접속이 하나라도 있을 때
    /// (운영 불가 도구는 운영이 아닌 접속, EXPLAIN PLAN 도구는 그 설정이 켜진 접속이어야 한다).
    /// </summary>
    private static bool Usable(AgentGrantView grant, AgentToolDefinition tool)
    {
        if (tool.Host != AgentToolHost.Db && tool.MinimumLevel == AiAccessLevel.Off)
        {
            return true;
        }

        foreach (var connection in grant.Connections)
        {
            if (connection.Level != AiAccessLevel.Off
                && connection.Level >= tool.MinimumLevel
                && !(tool.DevTestOnly && !AgentGrantPolicy.ExecutesIn(connection.Environment))
                && !(tool.RequiresExplainPlanSetting && !connection.ExplainPlanAllowed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DeclaresConnection(AgentToolDefinition tool) =>
        Schemas.GetOrAdd(tool.Name, _ => ToolSchema.Parse(tool.InputSchemaJson)).ByName.ContainsKey(AgentFields.Connection);

    /// <summary>접속 없이는 할 수 없는 앱 도구(트리에서 객체 고르기). 나머지 앱·언어 도구의 connection 은 고르기용이라 빠져도 된다.</summary>
    private static bool NeedsConnection(AgentToolDefinition tool) => tool.Name == AppToolNames.RevealObject;

    private Dictionary<string, IAppToolHandler> AppHandlerMap()
    {
        if (_appHandlers is { } map)
        {
            return map;
        }

        var found = _services?.GetService(typeof(IEnumerable<IAppToolHandler>)) as IEnumerable<IAppToolHandler> ?? [];
        map = MapAppHandlers(found);
        Interlocked.CompareExchange(ref _appHandlers, map, null);
        return _appHandlers!;
    }

    /// <summary>같은 도구 이름의 처리자가 둘이면 조립 오류다.</summary>
    private static Dictionary<string, IAppToolHandler> MapAppHandlers(IEnumerable<IAppToolHandler> handlers)
    {
        var map = new Dictionary<string, IAppToolHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!map.TryAdd(handler.ToolName, handler))
            {
                throw new ArgumentException($"Two app tool handlers are registered for tool '{handler.ToolName}'.", nameof(handlers));
            }
        }

        return map;
    }
}
