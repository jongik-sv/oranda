using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Composition;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Ai.Profiles;

/// <summary>
/// 프로필 서버 조회(설계 §6.5): 모델 목록과 연결 테스트. 실패는 예외가 아니라 결과로 돌려주고(사용자 취소만 <see cref="OperationCanceledException"/>),
/// 결과 글에는 API 키를 넣지 않는다. 인증 헤더는 실제 실행과 같은 규칙(<see cref="AiProfileAuth"/>)을 쓴다.
/// 프록시는 쓰지 않고(<see cref="CreateHandler"/>), 키가 다른 곳으로 새지 않게 리디렉션은 따라가지 않는다(<c>x-api-key</c> 는 호스트가 바뀌어도 지워지지 않는다).
/// </summary>
[OrandaService(typeof(IAiProfileProbe))]
internal sealed class AiProfileProbe : IAiProfileProbe, IDisposable
{
    private const string AnthropicVersion = "2023-06-01";

    // 연결 테스트용 본문. 사용자 SQL·문맥은 넣지 않는다.
    private const string PingText = "ping";

    private const int TestMaxTokens = 16;

    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private const int MaxDetailChars = 200;

    private static readonly TimeSpan DefaultListTimeout = TimeSpan.FromSeconds(15);

    // 로컬 모델은 첫 요청에서 모델을 올리느라 오래 걸릴 수 있다.
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly TimeSpan _listTimeout;
    private readonly TimeSpan _testTimeout;
    private readonly Func<CancellationToken, Task<ClaudeStatus>>? _claudeStatus;

    /// <summary>claude 실행 파일 상태. Anthropic 형식 프로필은 서버가 응답해도 이 파일이 없으면 실행할 수 없다.</summary>
    internal enum ClaudeStatus
    {
        Found,
        Missing,
        BatchFile,

        /// <summary>설정을 읽지 못해 확인하지 못했다(실제 실행도 <c>settings_unreadable</c> 로 실패한다).</summary>
        SettingsUnreadable,
    }

    public AiProfileProbe(ISettingsStore settings)
        : this(
            CreateHandler(),
            ct => FindClaudeAsync(settings ?? throw new ArgumentNullException(nameof(settings)), ct))
    {
    }

    /// <summary>
    /// 운영 전송 계층. 리디렉션을 따라가지 않고(키가 다른 호스트로 가지 않게), 프록시를 쓰지 않는다(실제 실행의 프로필 경로가 프록시 변수를 넘기지 않는 것과 같다).
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10), UseProxy = false };

    /// <summary>시험용: 전송 계층·claude 확인·시간 제한을 바꿔 넣는다. 핸들러는 이 객체가 소유한다. <paramref name="claudeStatus"/> 가 null 이면 실행 파일을 확인하지 않는다.</summary>
    internal AiProfileProbe(HttpMessageHandler handler, Func<CancellationToken, Task<ClaudeStatus>>? claudeStatus = null, TimeSpan? listTimeout = null, TimeSpan? testTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _claudeStatus = claudeStatus;
        _listTimeout = listTimeout ?? DefaultListTimeout;
        _testTimeout = testTimeout ?? DefaultTestTimeout;

        // 시간 제한은 호출마다 따로 건다(취소와 시간 초과를 구분하려고 HttpClient 기본 제한은 쓰지 않는다).
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };
    }

    public void Dispose() => _http.Dispose();

    public async Task<AiModelListResult> ListModelsAsync(AiProfileProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        var key = NormalizeKey(request.ApiKey);
        if (Validate(profile) is { } invalid)
        {
            return AiModelListResult.Fail(invalid);
        }

        var url = AiProfileUrl.ModelsUrl(profile.Format, profile.BaseUrl);
        if (url is null)
        {
            return AiModelListResult.Fail(InvalidUrlMessage);
        }

        try
        {
            var reply = await SendAsync(HttpMethod.Get, url, profile, key, null, _listTimeout, ct).ConfigureAwait(false);
            if (!reply.IsSuccess)
            {
                return AiModelListResult.Fail(Describe(reply, key));
            }

            if (ParseModels(reply.Body) is not { } models)
            {
                return AiModelListResult.Fail("서버 응답에서 모델 목록을 읽지 못했습니다. 서버 주소와 API 형식을 확인하세요.");
            }

            if (profile.Preset == AiProfilePreset.Ollama)
            {
                models = await WithOllamaCapabilitiesAsync(profile, key, models, ct).ConfigureAwait(false);
            }

            return AiModelListResult.Ok(models);
        }
        catch (Exception ex) when (IsReportable(ex, ct))
        {
            ct.ThrowIfCancellationRequested(); // 처리하는 사이 사용자가 취소했다면 결과가 아니라 취소로 끝낸다
            return AiModelListResult.Fail(SecretRedactor.Redact(DescribeFailure(ex), key));
        }
    }

    public async Task<AiConnectionTestResult> TestConnectionAsync(AiProfileProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        var key = NormalizeKey(request.ApiKey);
        var clock = Stopwatch.StartNew();
        if (Validate(profile) is { } invalid)
        {
            return new AiConnectionTestResult(false, clock.Elapsed, invalid);
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            return new AiConnectionTestResult(false, clock.Elapsed, "모델 이름을 입력하세요.");
        }

        var anthropic = profile.Format == AiApiFormat.Anthropic;
        var url = AiProfileUrl.RequestUrl(profile.Format, profile.BaseUrl, anthropic ? "messages" : "chat/completions");
        if (url is null)
        {
            return new AiConnectionTestResult(false, clock.Elapsed, InvalidUrlMessage);
        }

        try
        {
            var reply = await SendAsync(HttpMethod.Post, url, profile, key, PingBody(profile.Model.Trim(), "max_tokens", stream: anthropic), _testTimeout, ct).ConfigureAwait(false);

            // 최근 OpenAI 모델은 max_tokens 대신 max_completion_tokens 만 받는다. 그 경우에만 한 번 다시 보낸다.
            if (!anthropic && reply.Status == HttpStatusCode.BadRequest && reply.Body.Contains("max_completion_tokens", StringComparison.Ordinal))
            {
                reply = await SendAsync(HttpMethod.Post, url, profile, key, PingBody(profile.Model.Trim(), "max_completion_tokens"), _testTimeout, ct).ConfigureAwait(false);
            }

            if (!reply.IsSuccess)
            {
                return new AiConnectionTestResult(false, clock.Elapsed, Describe(reply, key));
            }

            if (!IsJsonObject(reply.Body) && !(anthropic && reply.Body.Contains("event:", StringComparison.Ordinal)))
            {
                return new AiConnectionTestResult(false, clock.Elapsed, "서버 응답이 올바른 형식이 아닙니다. 서버 주소와 API 형식을 확인하세요.");
            }

            // Anthropic 형식은 claude 를 거쳐 실행한다. 서버 연결이 되어도 실행 파일이 없으면 실제로는 쓸 수 없으므로 알린다(HTTP 오류가 있으면 위에서 먼저 나간다).
            if (anthropic && _claudeStatus is not null)
            {
                var status = await _claudeStatus(ct).ConfigureAwait(false);
                if (status != ClaudeStatus.Found)
                {
                    if (status == ClaudeStatus.SettingsUnreadable)
                    {
                        return new AiConnectionTestResult(false, clock.Elapsed, "서버 연결은 됐지만 설정을 읽지 못해 claude 실행 파일을 확인하지 못했습니다.");
                    }

                    var detail = status == ClaudeStatus.BatchFile
                        ? "claude.cmd 같은 배치 파일은 지원하지 않습니다. 설정에서 claude.exe 경로를 지정하세요."
                        : "claude 실행 파일이 없습니다.";
                    return new AiConnectionTestResult(false, clock.Elapsed, $"서버 연결은 됐지만 {detail} OpenAI 형식을 쓰면 claude 없이 연결할 수 있습니다.");
                }
            }

            return new AiConnectionTestResult(true, clock.Elapsed, $"서버가 응답했습니다(모델: {profile.Model.Trim()}).");
        }
        catch (Exception ex) when (IsReportable(ex, ct))
        {
            ct.ThrowIfCancellationRequested();
            return new AiConnectionTestResult(false, clock.Elapsed, SecretRedactor.Redact(DescribeFailure(ex), key));
        }
    }

    // 설정의 실행 파일 경로(ExecutablePath)와 PATH 로 CliDriver 와 같은 규칙으로 찾는다.
    private static async Task<ClaudeStatus> FindClaudeAsync(ISettingsStore settings, CancellationToken ct)
    {
        string? configured;
        try
        {
            configured = (await settings.LoadAsync(ct).ConfigureAwait(false)).Value.Ai.ExecutablePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ClaudeStatus.SettingsUnreadable;
        }

        var found = ClaudeExecutable.Find(configured, Environment.GetEnvironmentVariable("PATH"), File.Exists);
        return found is null ? ClaudeStatus.Missing : ClaudeExecutable.IsBatchFile(found) ? ClaudeStatus.BatchFile : ClaudeStatus.Found;
    }

    private const string InvalidUrlMessage = "서버 주소가 올바르지 않습니다. http:// 또는 https:// 로 시작하는 주소를 입력하세요.";

    private static string? Validate(AiLlmProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Format is AiApiFormat.Anthropic or AiApiFormat.OpenAi ? null : "이 버전이 알지 못하는 API 형식입니다.";
    }

    private static string? NormalizeKey(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    // 사용자 취소는 그대로 올리고, 그 밖의 네트워크·형식 오류는 결과로 바꾼다.
    private static bool IsReportable(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is HttpRequestException or TimeoutException or JsonException or FormatException or IOException or InvalidOperationException;

    private static string DescribeFailure(Exception ex) => ex switch
    {
        TimeoutException => "서버가 제한 시간 안에 응답하지 않았습니다. 프로필 연결은 프록시를 쓰지 않습니다.",
        FormatException => "API 키에 사용할 수 없는 문자가 있습니다.",
        JsonException => "서버 응답을 해석하지 못했습니다.",
        HttpRequestException { InnerException: { } inner } => $"서버에 연결하지 못했습니다: {inner.Message} (프로필 연결은 프록시를 쓰지 않습니다)",
        HttpRequestException http => $"서버에 연결하지 못했습니다: {http.Message} (프로필 연결은 프록시를 쓰지 않습니다)",
        _ => $"서버 요청에 실패했습니다: {ex.Message}",
    };

    // Anthropic 형식은 claude 가 항상 스트리밍(SSE)으로 요청하므로 테스트도 같은 방식으로 보낸다. 스트리밍을 지원하지 않는 서버를 미리 알아낸다.
    private static string PingBody(string model, string limitName, bool stream = false)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            [limitName] = TestMaxTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = PingText }),
        };
        if (stream)
        {
            body["stream"] = true;
        }

        return body.ToJsonString();
    }

    private static bool IsJsonObject(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>인증·버전 헤더. 규칙은 <see cref="AiProfileAuth"/> 하나다(CLI 환경 변수 조합과 같다).</summary>
    private static void ApplyHeaders(HttpRequestMessage request, AiLlmProfile profile, string? key)
    {
        if (profile.Format == AiApiFormat.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            if (AiProfileAuth.UsesApiKeyHeader(profile))
            {
                if (key is not null)
                {
                    request.Headers.Add("x-api-key", key);
                }
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key ?? AiProfileAuth.PlaceholderToken);
            }

            return;
        }

        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private async Task<Reply> SendAsync(HttpMethod method, string url, AiLlmProfile profile, string? key, string? jsonBody, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var message = new HttpRequestMessage(method, new Uri(url));
        ApplyHeaders(message, profile, key);
        if (jsonBody is not null)
        {
            message.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            return new Reply(response.StatusCode, body, RedirectTarget(message.RequestUri!, response.Headers.Location));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private async Task<IReadOnlyList<AiModelInfo>> WithOllamaCapabilitiesAsync(AiLlmProfile profile, string? key, IReadOnlyList<AiModelInfo> models, CancellationToken ct)
    {
        // /v1/models 는 이미지 지원을 알려 주지 않는다. 원점의 /api/tags 가 모델별 capabilities 를 갖고 있다.
        // 이 조회가 실패해도 목록 자체는 유효하므로 지원 여부만 모름(null)으로 둔다.
        if (AiProfileUrl.BaseFor(AiApiFormat.OpenAi, profile.BaseUrl) is not { } baseUrl)
        {
            return models;
        }

        try
        {
            var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
            var reply = await SendAsync(HttpMethod.Get, origin + "/api/tags", profile, key, null, _listTimeout, ct).ConfigureAwait(false);
            if (!reply.IsSuccess || ParseModels(reply.Body) is not { } tags)
            {
                return models;
            }

            var byId = tags.Where(t => t.SupportsImages is not null).GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().SupportsImages, StringComparer.Ordinal);
            return [.. models.Select(m => m with { SupportsImages = m.SupportsImages ?? Lookup(byId, m.Id) })];
        }
        catch (Exception ex) when (IsReportable(ex, ct))
        {
            return models;
        }
    }

    private static bool? Lookup(Dictionary<string, bool?> byId, string id) =>
        byId.TryGetValue(id, out var exact) ? exact : byId.TryGetValue(id + ":latest", out var latest) ? latest : null;

    /// <summary>
    /// 모델 목록 본문을 읽는다. OpenAI·Anthropic 형식은 <c>data</c> 배열, Ollama 의 <c>/api/tags</c> 는 <c>models</c> 배열이다.
    /// 이미지 지원: <c>architecture.input_modalities</c> 에 image, Anthropic 의 <c>capabilities.image_input.supported</c>, Ollama 의 <c>capabilities</c> 배열에 vision.
    /// 해당 칸이 아예 없으면 null(모름). 배열이 없거나 형식이 아니면 null 목록(실패)이다.
    /// </summary>
    internal static IReadOnlyList<AiModelInfo>? ParseModels(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement array;
            if (!(root.TryGetProperty("data", out array) && array.ValueKind == JsonValueKind.Array)
                && !(root.TryGetProperty("models", out array) && array.ValueKind == JsonValueKind.Array))
            {
                return null;
            }

            var models = new List<AiModelInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && ReadId(item) is { Length: > 0 } id && seen.Add(id))
                {
                    models.Add(new AiModelInfo(id, ReadSupportsImages(item)));
                }
            }

            return models;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadId(JsonElement item)
    {
        foreach (var name in (ReadOnlySpan<string>)["id", "model", "name"])
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static bool? ReadSupportsImages(JsonElement item)
    {
        if (item.TryGetProperty("architecture", out var architecture)
            && architecture.ValueKind == JsonValueKind.Object
            && architecture.TryGetProperty("input_modalities", out var modalities)
            && modalities.ValueKind == JsonValueKind.Array)
        {
            return modalities.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && string.Equals(m.GetString(), "image", StringComparison.OrdinalIgnoreCase));
        }

        if (!item.TryGetProperty("capabilities", out var capabilities))
        {
            return null;
        }

        if (capabilities.ValueKind == JsonValueKind.Array)
        {
            return capabilities.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && string.Equals(c.GetString(), "vision", StringComparison.OrdinalIgnoreCase));
        }

        if (capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("image_input", out var imageInput)
            && imageInput.ValueKind == JsonValueKind.Object
            && imageInput.TryGetProperty("supported", out var supported)
            && supported.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return supported.GetBoolean();
        }

        return null;
    }

    // 리디렉션 대상 주소. 쿼리(토큰이 실릴 수 있다)·조각·사용자 정보는 지우고 보인다.
    private static string? RedirectTarget(Uri requested, Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        var absolute = location.IsAbsoluteUri ? location : new Uri(requested, location);
        return absolute.GetLeftPart(UriPartial.Path);
    }

    private static string Describe(Reply reply, string? key)
    {
        var code = (int)reply.Status;
        if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 서버가 입력한 키를 본문에 되돌려 줄 수 있어 본문은 싣지 않는다.
            return $"서버가 인증을 거부했습니다({code}). API 키를 확인하세요.";
        }

        if (code is >= 300 and < 400)
        {
            return reply.Location is { Length: > 0 } location
                ? $"서버가 다른 주소로 옮기라고 응답했습니다({location}). 프로필 URL 을 확인하세요."
                : $"서버가 다른 주소로 옮기라고 응답했습니다({code}). 프로필 URL 을 확인하세요.";
        }

        var head = reply.Status == HttpStatusCode.NotFound
            ? "서버에서 해당 주소나 모델을 찾지 못했습니다(404)."
            : $"서버가 오류를 돌려줬습니다({code}).";
        return ErrorDetail(reply.Body, key) is { Length: > 0 } detail ? $"{head} {detail}" : head;
    }

    // 오류 본문의 error.message(또는 error 글)만 짧게 싣는다.
    private static string? ErrorDetail(string body, string? key)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            var text = error.ValueKind switch
            {
                JsonValueKind.String => error.GetString(),
                JsonValueKind.Object when error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String => message.GetString(),
                _ => null,
            };
            // 가리기는 자르기 전에 한다: 자르는 경계가 키 중간을 지나면 일부가 남는다.
            text = SecretRedactor.Redact(text, key).ReplaceLineEndings(" ").Trim();
            return text.Length > MaxDetailChars ? text[..MaxDetailChars] + "…" : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Reply(HttpStatusCode Status, string Body, string? Location = null)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;
    }
}
