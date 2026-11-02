using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Composition;
using Oranda.Core.Settings;

namespace Oranda.Platform.Ai.Api;

/// <summary>
/// API 경로 드라이버(설계 §6.2): OpenAI 형식(<c>POST {base}/chat/completions</c>, 스트리밍) 프로필의 도구 반복을 앱 안에서 돌린다.
/// 도구는 <c>claude</c> 프로세스·파이프 없이 <see cref="IAgentGate"/> 를 직접 부르며 게이트를 건너뛰는 길은 없다
/// (인자를 읽지 못하면 게이트를 부르지 않고 오류 결과를 모델에 돌려줄 뿐이다). 설정·자격 증명 저장소는 읽지 않고 라우터가 넘긴 <see cref="AiRoute"/> 만 쓴다.
/// 사건 순서: <see cref="AiStarted"/> → (<see cref="AiTextDelta"/>·<see cref="AiToolCall"/>·<see cref="AiToolResult"/>)… → <see cref="AiUsage"/> → <see cref="AiFinal"/>.
/// 실패는 <see cref="AiError"/> 로 끝내고 취소는 <see cref="OperationCanceledException"/> 이다. 이어 말하기는 <see cref="OpenAiConversationStore"/> 가 메모리에 보관한다.
/// </summary>
[OrandaService(typeof(IApiAiDriver))]
internal sealed partial class OpenAiApiDriver : IApiAiDriver, IDisposable
{
    /// <summary>감사 기록의 client 칸(CLI 경로는 파이프 hello 가 채우지만 이 경로는 hello 가 없다).</summary>
    public const string ClientName = "oranda-api";

    private const int MaxArgumentSummaryChars = 4000;

    /// <summary>스키마에 맞지 않는 제출이 이 횟수만큼 이어지면 실행을 끝낸다.</summary>
    private const int MaxStructuredFailures = 3;

    /// <summary>모델이 구조 결과를 스키마에 맞게 내지 못해 끝난 실행의 오류 코드(메시지에 어긋난 칸의 경로가 실린다).</summary>
    public const string StructuredInvalidCode = "structured_output_invalid";

    // 도구를 쓰고 추측하지 않게 하는 짧은 시스템 프롬프트(Claude Code 는 자체 시스템 프롬프트가 있지만 이 경로에는 없다).
    private const string SystemPrompt =
        "당신은 Oracle 데이터베이스 SQL 튜닝을 돕는 도우미입니다. "
        + "객체 구조, 실행 계획, 통계 같은 사실은 주어진 도구로 확인한 뒤에 답하고, 확인하지 못한 내용은 추측하지 말고 확인하지 못했다고 밝히세요. "
        + "도구가 거부하거나 일부를 가려 준 값은 짐작해서 채우지 마세요. "
        + "결과를 제출하는 도구(StructuredOutput)가 주어지면 글로 답하지 말고 그 도구로 제출하세요.";

    private const string StructuredRetryPrompt =
        "최종 결과가 아직 제출되지 않았습니다. 글로 답하지 말고 StructuredOutput 도구를 불러 인자로 결과를 제출하세요.";

    private const string InvalidArgumentsResult =
        "{\"error\":\"도구 인자가 올바른 JSON 객체가 아닙니다. 인자를 JSON 객체로 다시 보내세요.\",\"code\":\"invalid_arguments\"}";

    private const string InterruptedNotice = "(이전 응답은 오류나 중단으로 끝나 전달되지 못했습니다.)";

    private const string StructuredAcceptedResult = "{\"ok\":true}";

    private const string SkippedResult =
        "{\"error\":\"결과가 제출되어 이 호출은 실행하지 않았습니다.\",\"code\":\"skipped\"}";

    private static readonly AiDriverCapabilities DriverCapabilities =
        new(Skills: false, Tools: true, Resume: true, LocalOnly: false, Streaming: true, Images: true);

    private static readonly HashSet<string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif",
    };

    private readonly IAgentGate _gate;
    private readonly HttpClient _http;
    private readonly OpenAiApiOptions _options;
    private readonly OpenAiConversationStore _conversations;

    /// <summary>DI 가 쓰는 생성자. 자동 리다이렉트를 끈다(키와 SQL 이 서버가 정한 다른 호스트로 넘어가지 않게).</summary>
    public OpenAiApiDriver(IAgentGate gate)
        : this(gate, CreateHandler(), new OpenAiApiOptions())
    {
    }

    internal OpenAiApiDriver(IAgentGate gate, HttpMessageHandler handler, OpenAiApiOptions options)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _conversations = new OpenAiConversationStore(_options);
    }

    public AiDriverCapabilities Capabilities => DriverCapabilities;

    public void Dispose() => _http.Dispose();

    public IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("API 경로는 라우터가 전송 대상(AiRoute)과 함께 부른다.");

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, AiRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(route);

        if (Precheck(request, route) is { } rejected)
        {
            yield return rejected;
            yield break;
        }

        var profile = route.Profile!;
        var session = request.Session!;
        var url = AiProfileUrl.RequestUrl(AiApiFormat.OpenAi, profile.BaseUrl, "chat/completions")!;

        // 파이프의 hello 가 없으므로 드라이버가 직접 채운다(비면 감사 기록의 client 칸이 빈다).
        session.Client = ClientName;

        var (lease, openError) = _conversations.Open(request.ResumeId, $"{profile.Id:N}|{AiProfileUrl.BaseFor(AiApiFormat.OpenAi, profile.BaseUrl)}", OpenAiRequestBuilder.SystemMessage(SystemPrompt));
        if (openError is not null)
        {
            yield return openError;
            yield break;
        }

        using (lease!)
        {
            yield return new AiStarted(lease!.Id);

            // 호출한 쪽이 세션을 폐기하면 진행 중인 요청도 끊는다.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.RevocationToken);
            var run = linked.Token;

            var messages = lease.Messages;
            if (lease.Interrupted)
            {
                // 앞 실행이 중간에 멈췄다: 사용자 메시지가 연달아 오면 역할 교대를 요구하는 서버 템플릿이 거부하므로 사이에 짧은 assistant 메시지를 끼운다.
                messages.Add(OpenAiRequestBuilder.AssistantMessage(InterruptedNotice));
            }

            messages.Add(OpenAiRequestBuilder.UserMessage(request.Prompt, request.Images));
            var bytes = messages.Sum(m => (long)m.Length);
            if (_conversations.Exceeds(messages.Count, bytes))
            {
                yield return OpenAiConversationStore.LimitError(resumed: !string.IsNullOrEmpty(request.ResumeId));
                yield break;
            }

            // 질문까지 먼저 확정한다: 이 실행이 오류·취소로 끝나도 같은 id 로 이어 말하면 첫 질문(SQL)을 모델이 계속 본다.
            lease.Checkpoint(messages, bytes);

            var structured = request.OutputSchema is { ValueKind: JsonValueKind.Object };
            var toolsJson = OpenAiRequestBuilder.BuildTools(_gate.ListTools(session), structured ? request.OutputSchema : null);

            var modelCalls = 0;
            var toolCalls = 0;
            long inputTokens = 0;
            long outputTokens = 0;
            long lastPromptTokens = 0;
            var usageSeen = false;
            var structuredRetried = false;
            var structuredFailures = 0;
            var lastIssues = string.Empty;

            while (true)
            {
                if (session.IsRevoked)
                {
                    yield return Revoked();
                    yield break;
                }

                // 다음 요청을 보내기 전에만 상한을 본다: 이미 받은 답과 제출된 구조 결과는 상한에 닿았다는 이유로 버리지 않는다(확정은 Commit 이 맡는다).
                if (_conversations.Exceeds(messages.Count, bytes))
                {
                    yield return OpenAiConversationStore.LimitError(resumed: true);
                    yield break;
                }

                if (modelCalls >= _options.MaxModelCalls)
                {
                    yield return new AiError($"모델 호출 횟수 상한({_options.MaxModelCalls}회)을 넘어 중단했습니다.", "turn_limit");
                    yield break;
                }

                modelCalls++;
                var body = OpenAiRequestBuilder.BuildBody(profile, messages, toolsJson);
                var (response, sendError) = await SendAsync(url, route.ApiKey, body, session, ct, run).ConfigureAwait(false);
                if (sendError is not null)
                {
                    yield return sendError;
                    yield break;
                }

                OpenAiTurn? turn = null;
                using (response)
                {
                    var (reader, readerError) = await OpenReaderAsync(response!, session, route.ApiKey, ct, run).ConfigureAwait(false);
                    if (readerError is not null)
                    {
                        yield return readerError;
                        yield break;
                    }

                    using (reader)
                    {
                        var accumulator = new OpenAiTurnAccumulator();
                        var jsonMode = IsJson(response!);
                        var finished = false;
                        while (!finished)
                        {
                            var step = await ReadStepAsync(reader!, accumulator, jsonMode, route.ApiKey, session, ct, run).ConfigureAwait(false);
                            if (step.Error is not null)
                            {
                                yield return step.Error;
                                yield break;
                            }

                            if (step.TextDelta is { Length: > 0 } delta)
                            {
                                yield return new AiTextDelta(RedactBody(delta, route.ApiKey));
                            }

                            finished = step.Done;
                        }

                        turn = accumulator.Finish();
                    }
                }

                if (turn.Usage is { } usage)
                {
                    inputTokens += usage.PromptTokens;
                    outputTokens += usage.CompletionTokens;
                    lastPromptTokens = usage.PromptTokens;
                    usageSeen = true;
                }

                if (turn.FinishReason == "length")
                {
                    yield return new AiError($"응답이 최대 출력 토큰({profile.MaxOutputTokens})에서 잘렸습니다. 프로필의 최대 출력 토큰을 늘리세요.", "output_truncated");
                    yield break;
                }

                // 오류성 끝 사유를 정상 답으로 확정하지 않는다(받은 글 조각은 이미 화면에 나갔을 수 있다).
                if (turn.FinishReason == "content_filter")
                {
                    yield return new AiError("서버가 응답을 콘텐츠 필터로 막았습니다. 질문이나 첨부를 바꿔 다시 시도하세요.", "content_filtered");
                    yield break;
                }

                if (turn.FinishReason == "error")
                {
                    yield return new AiError("서버가 응답 도중 오류로 끝냈다고 알려 왔습니다. 서버 상태를 확인하세요.", "api_error");
                    yield break;
                }

                messages.Add(turn.AssistantMessageJson);
                bytes += turn.AssistantMessageJson.Length;

                if (turn.ToolCalls.Count == 0)
                {
                    if (!structured)
                    {
                        lease.Commit(messages, bytes, lastPromptTokens > profile.MaxInputTokens);
                        if (usageSeen)
                        {
                            yield return new AiUsage(inputTokens, outputTokens);
                        }

                        yield return new AiFinal(RedactBody(turn.Text, route.ApiKey), null, lease.Id);
                        yield break;
                    }

                    // 모델이 도구를 부르지 않고 글로 끝냈다: 한 번만 다시 요구한다.
                    if (structuredRetried)
                    {
                        yield return new AiError("모델이 StructuredOutput 도구로 결과를 제출하지 않았습니다.", "structured_output_missing");
                        yield break;
                    }

                    structuredRetried = true;
                    var retry = OpenAiRequestBuilder.UserMessage(StructuredRetryPrompt, null);
                    messages.Add(retry);
                    bytes += retry.Length;
                    continue;
                }

                JsonElement? submitted = null;
                var roundFailed = false;
                foreach (var call in turn.ToolCalls)
                {
                    var hidden = structured && call.Name == OpenAiRequestBuilder.StructuredOutputTool;
                    if (submitted is not null)
                    {
                        // 결과를 이미 제출했다: 같은 메시지의 나머지 호출은 실행하지 않고 짝이 되는 도구 메시지만 채운다(이어 말하기 기록이 어긋나지 않게).
                        var skipped = OpenAiRequestBuilder.ToolMessage(call.Id, SkippedResult);
                        messages.Add(skipped);
                        bytes += skipped.Length;
                        continue;
                    }

                    if (session.IsRevoked)
                    {
                        yield return Revoked();
                        yield break;
                    }

                    // 결과 제출용 합성 도구는 실제 도구가 아니므로 도구 상한을 쓰지 않는다(모델 호출 상한이 반복을 막는다).
                    if (!hidden)
                    {
                        toolCalls++;
                        if (toolCalls > _options.MaxToolCalls)
                        {
                            yield return new AiError($"도구 호출 횟수 상한({_options.MaxToolCalls}회)을 넘어 중단했습니다.", "turn_limit");
                            yield break;
                        }
                    }

                    if (!hidden)
                    {
                        yield return new AiToolCall(call.Id, call.Name, Summarize(call.ArgumentsText));
                    }

                    string resultText;
                    AiToolJudgement judgement;
                    string? note;
                    var arguments = ParseArguments(call.ArgumentsText);
                    IReadOnlyList<string> issues = hidden && arguments is not null ? OpenAiSchemaCheck.Validate(request.OutputSchema!.Value, arguments) : [];
                    if (arguments is null)
                    {
                        // 인자를 읽지 못하면 게이트를 부르지 않는다(감사 기록에도 남기지 않는다). 모델이 고쳐 다시 부르게 오류만 돌려준다.
                        (resultText, judgement, note) = (InvalidArgumentsResult, AiToolJudgement.Failed, "invalid_arguments");
                        if (hidden)
                        {
                            (roundFailed, lastIssues) = (true, "인자가 올바른 JSON 객체가 아닙니다");
                        }
                    }
                    else if (issues.Count > 0)
                    {
                        // 스키마(필수 칸·형식, 안쪽 객체와 배열 항목까지)에 어긋난다: 받아들이지 않고 모델이 고쳐 다시 제출하게 한다(어긋난 값이 흐름까지 가면 결과를 쓸 수 없다).
                        lastIssues = string.Join("; ", issues);
                        roundFailed = true;
                        (resultText, judgement, note) = (SchemaIssuesResult(lastIssues), AiToolJudgement.Failed, "invalid_arguments");
                    }
                    else if (hidden)
                    {
                        submitted = JsonSerializer.SerializeToElement(arguments);
                        (resultText, judgement, note) = (StructuredAcceptedResult, AiToolJudgement.Allowed, null);
                    }
                    else
                    {
                        var (response2, gateError) = await CallGateAsync(session, call.Name, arguments, ct, run).ConfigureAwait(false);
                        if (gateError is not null)
                        {
                            if (!hidden)
                            {
                                // 화면의 도구 카드가 끝나지 않은 채 남지 않게 닫는다.
                                yield return new AiToolResult(call.Id, call.Name, 0, AiToolJudgement.Failed, "session_revoked");
                            }

                            yield return gateError;
                            yield break;
                        }

                        (resultText, judgement, note) = (response2!.Result.Text, response2.Judgement, response2.Note);
                    }

                    var toolMessage = OpenAiRequestBuilder.ToolMessage(call.Id, resultText);
                    messages.Add(toolMessage);
                    bytes += toolMessage.Length;
                    if (!hidden)
                    {
                        yield return new AiToolResult(call.Id, call.Name, resultText.Length, judgement, note);
                    }
                }

                structuredFailures = roundFailed ? structuredFailures + 1 : 0;
                if (submitted is null && structuredFailures >= MaxStructuredFailures)
                {
                    yield return new AiError($"모델이 StructuredOutput 을 {structuredFailures}번 연속 스키마에 맞게 제출하지 못했습니다: {lastIssues}", StructuredInvalidCode);
                    yield break;
                }

                if (submitted is null)
                {
                    // 도구 한 바퀴가 끝났다(모든 tool_calls 에 짝이 되는 tool 메시지가 있다): 여기까지는 어긋나지 않는 기록이므로 확정해 둔다.
                    lease.Checkpoint(messages, bytes);
                }

                if (submitted is { } result)
                {
                    lease.Commit(messages, bytes, lastPromptTokens > profile.MaxInputTokens);
                    if (usageSeen)
                    {
                        yield return new AiUsage(inputTokens, outputTokens);
                    }

                    // Text 는 null: 대화 창이 결과의 Summary 를 넣으므로 글을 함께 내면 두 번 보인다(CLI 와 같다).
                    yield return new AiFinal(null, result, lease.Id);
                    yield break;
                }
            }
        }
    }

    private static AiError? Precheck(AiRequest request, AiRoute route)
    {
        if (request.Session is null)
        {
            return new AiError("AI 요청에 실행 범위(Session)가 없습니다.", "session_required");
        }

        if (request.Session.IsRevoked)
        {
            return Revoked();
        }

        var profile = route.Profile;
        if (profile is null || profile.Format != AiApiFormat.OpenAi)
        {
            return new AiError("API 경로는 OpenAI 형식 프로필만 실행할 수 있습니다.", "profile_invalid");
        }

        if (AiProfileUrl.RequestUrl(AiApiFormat.OpenAi, profile.BaseUrl, "chat/completions") is null)
        {
            return new AiError($"프로필 「{profile.Name}」 의 서버 주소가 올바르지 않습니다.", "profile_invalid_url");
        }

        if (request.Images is { Count: > 0 } images)
        {
            if (profile.SupportsImages == false)
            {
                return new AiError($"프로필 「{profile.Name}」 은 이미지를 지원하지 않는 것으로 설정되어 있습니다.", "images_unsupported");
            }

            if (images.Any(i => i is null || i.Data is not { Length: > 0 } || !ImageMediaTypes.Contains(i.MediaType ?? string.Empty)))
            {
                return new AiError("첨부한 이미지의 형식을 지원하지 않습니다(png·jpeg·webp·gif).", "image_invalid");
            }
        }

        return null;
    }

    private static string SchemaIssuesResult(string issues) =>
        new JsonObject
        {
            ["error"] = $"제출한 결과가 스키마에 맞지 않습니다: {issues}. 고쳐서 StructuredOutput 을 다시 호출하세요.",
            ["code"] = "invalid_arguments",
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static AiError Revoked() =>
        new("이 AI 요청의 실행 범위가 폐기되었습니다. 새 요청으로 다시 시작하세요.", "session_revoked");

    private static string Summarize(string argumentsText) =>
        argumentsText.Length <= MaxArgumentSummaryChars ? argumentsText : argumentsText[..MaxArgumentSummaryChars] + "…";

    // 빈 인자는 인자 없는 호출이다. JSON 객체가 아니면(깨진 조각·배열·null) null 이다.
    private static JsonObject? ParseArguments(string argumentsText)
    {
        if (string.IsNullOrWhiteSpace(argumentsText))
        {
            return new JsonObject();
        }

        try
        {
            var parsed = JsonNode.Parse(argumentsText) as JsonObject;

            // 짝 없는 서로게이트 같은 글자는 다시 쓸 때(게이트·기록) 예외가 나므로 여기서 미리 써 보고 읽을 수 없는 인자로 취급한다.
            _ = parsed?.ToJsonString();

            // 중복 키는 칸을 처음 읽을 때 ArgumentException 이 나므로 여기서 미리 읽어 읽을 수 없는 인자로 취급한다.
            _ = parsed?.TryGetPropertyValue(string.Empty, out _);
            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static SocketsHttpHandler CreateHandler() =>
        new()
        {
            AllowAutoRedirect = false,

            // 쿠키 저장소를 두지 않는다: 한 프로필 서버가 준 쿠키가 같은 호스트의 다른 프로필 서버 요청에 실려 가지 않게.
            UseCookies = false,

            // 프로필 경로는 주소와 상관없이 프록시를 거치지 않는다(사용자 결정: 내부 네트워크에서 쓸 수 있으면 된다). 주소로 사내·외부를 가르지 않는다.
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
}
