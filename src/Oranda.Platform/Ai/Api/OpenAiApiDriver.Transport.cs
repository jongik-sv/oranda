using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Platform.Ai.Profiles;

namespace Oranda.Platform.Ai.Api;

// 전송 계층: 요청 보내기, 스트림 읽기, 게이트 호출, 오류 변환. yield 는 catch 가 있는 try 안에서 쓸 수 없으므로,
// 실패할 수 있는 await 는 모두 여기서 잡아 AiError 로 돌려주고 반복기는 결과만 본다(CliDriver 의 TryStart 와 같은 방식).
internal sealed partial class OpenAiApiDriver
{
    private const int MaxErrorExcerptChars = 300;

    private readonly record struct ReadStep(string? TextDelta, bool Done, AiError? Error);

    [GeneratedRegex(@"Bearer\s+[^\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    /// <summary>글에서 API 키와 <c>Bearer …</c> 값을 가린다. 서버가 오류 본문에 요청 헤더를 되돌려 주는 경우가 있다.</summary>
    internal static string Redact(string text, string? apiKey)
    {
        return BearerPattern().Replace(SecretRedactor.Redact(text, apiKey), "Bearer ***");
    }

    /// <summary>답변 본문용: 키 값만 가린다. 짧은 자리채움 키(none·test)나 Bearer 글은 일반 글을 망가뜨리므로 건드리지 않는다.</summary>
    internal static string RedactBody(string text, string? apiKey) =>
        apiKey is { Length: >= 8 } ? SecretRedactor.Redact(text, apiKey) : text;

    private static bool IsJson(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    // 취소의 원인이 호출한 쪽의 취소가 아닐 때(세션 폐기·무응답 시간 초과) 오류로 바꾼다.
    private AiError CancelError(AgentSession session) =>
        session.IsRevoked
            ? Revoked()
            : new AiError($"서버에서 {FormatTimeout(_options.ResponseTimeout)} 넘게 응답이 없어 중단했습니다. 서버가 다른 요청을 처리 중인지 확인하세요.", "api_timeout");

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds >= 120 ? $"{(int)timeout.TotalMinutes}분" : $"{Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds))}초";

    private async Task<(HttpResponseMessage? Response, AiError? Error)> SendAsync(
        string url, string? apiKey, byte[] body, AgentSession session, CancellationToken ct, CancellationToken run)
    {
        // 헤더에 실을 수 없는 문자(줄바꿈 등)가 키에 들어 있으면 보내기 전에 오류로 닫는다. 키 값은 메시지에 넣지 않는다.
        if (!string.IsNullOrEmpty(apiKey) && apiKey.Any(c => char.IsControl(c) || c > '~'))
        {
            return (null, KeyInvalid());
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url)) { Version = HttpVersion.Version11 };
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        // 머리글이 올 때까지의 제한. 본문을 읽는 동안의 제한은 줄마다 따로 건다(ReadStepAsync).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run);
        timeout.CancelAfter(_options.ResponseTimeout);
        HttpResponseMessage? response = null;
        var handedOver = false;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                handedOver = true;
                return (response, null);
            }

            var httpError = await HttpErrorAsync(response, apiKey, ct, timeout.Token).ConfigureAwait(false);
            return (null, session.IsRevoked ? Revoked() : httpError);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !run.IsCancellationRequested && !timeout.IsCancellationRequested)
        {
            // 우리 취소·무응답 제한이 아닌 취소는 연결 시간 초과(SocketsHttpHandler.ConnectTimeout)다.
            return (null, new AiError("서버에 연결하지 못했습니다: 연결 시간이 초과되었습니다. 서버 주소와 네트워크를 확인하세요.", "api_connect_failed"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, CancelError(session));
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            return (null, new AiError($"서버에 연결하지 못했습니다: {Redact(ex.Message, apiKey)}", "api_connect_failed"));
        }
        catch (FormatException)
        {
            return (null, KeyInvalid());
        }
        finally
        {
            if (!handedOver)
            {
                response?.Dispose();
            }
        }
    }

    private const int MaxErrorBodyBytes = 64 * 1024;

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken timeout)
    {
        await using var stream = await content.ReadAsStreamAsync(timeout).ConfigureAwait(false);
        var buffer = new byte[MaxErrorBodyBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), timeout).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, filled);
    }

    private static AiError KeyInvalid() =>
        new("API 키에 헤더로 보낼 수 없는 문자가 들어 있습니다. 프로필 관리에서 키를 다시 입력하세요.", "api_key_invalid");

    private static async Task<AiError> HttpErrorAsync(HttpResponseMessage response, string? apiKey, CancellationToken ct, CancellationToken timeout)
    {
        var excerpt = string.Empty;
        try
        {
            // 본문의 charset 선언은 믿지 않는다(모르는 이름·등록 안 된 코드 페이지면 ReadAsStringAsync 가 예외를 낸다). 오류 글은 UTF-8 로 읽는다.
            // 본문은 앞부분만 읽는다(끝없이 큰 오류 본문이 메모리와 정규식을 쓰지 못하게).
            var raw = await ReadCappedAsync(response.Content, timeout).ConfigureAwait(false);
            excerpt = Clip(Redact(ExtractErrorMessage(raw), apiKey));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 본문을 끝까지 받지 못해도 상태 코드만으로 오류를 알린다.
        }

        var status = (int)response.StatusCode;
        var detail = excerpt.Length > 0 ? $": {excerpt}" : string.Empty;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AiError($"서버가 인증을 거부했습니다(HTTP {status}). 프로필의 API 키를 확인하세요{detail}", "api_auth_failed"),
            HttpStatusCode.TooManyRequests =>
                new AiError($"서버가 요청이 너무 많다고 응답했습니다(HTTP 429). 잠시 뒤에 다시 시도하세요{detail}", "api_rate_limited"),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
                new AiError($"서버가 다른 주소로 옮기라고 응답했습니다({RedirectTarget(response, apiKey)}). 프로필 URL 을 확인하세요.", "api_redirect"),
            _ => new AiError($"서버가 오류를 응답했습니다(HTTP {status}){detail}", "api_http_error"),
        };
    }

    // 이동 대상 주소를 보여 주되 쿼리 값은 지운다(토큰이 쿼리에 실려 있을 수 있다). 따라가지는 않는다.
    private static string RedirectTarget(HttpResponseMessage response, string? apiKey)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return $"HTTP {(int)response.StatusCode}";
        }

        var text = location.IsAbsoluteUri ? location.GetLeftPart(UriPartial.Path) : location.OriginalString.Split('?', '#')[0];
        var query = location.IsAbsoluteUri ? location.Query : (location.OriginalString.Contains('?', StringComparison.Ordinal) ? "?" + location.OriginalString.Split('?', 2)[1].Split('#')[0] : string.Empty);
        if (query.Length > 1)
        {
            var keys = query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Contains('=', StringComparison.Ordinal) ? pair.Split('=', 2)[0] + "=***" : "***");
            text += "?" + string.Join('&', keys);
        }

        // 주소의 사용자 정보(user:password@)는 지운다. 절대 주소와 //host 꼴 모두.
        text = UserInfoPattern().Replace(text, "$1");
        return Redact(text, apiKey);
    }

    [GeneratedRegex(@"^((?:[A-Za-z][A-Za-z0-9+.\-]*:)??[/\\]*)[^/\\?#]*@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    // OpenAI 형식의 {"error":{"message":…}}, {"error":"…"}, {"message":…}, {"detail":…} 에서 사람이 읽을 글을 뽑고, 아니면 본문 앞부분을 쓴다.
    private static string ExtractErrorMessage(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return error.GetString() ?? string.Empty;
                    }

                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.String)
                    {
                        return nested.GetString() ?? string.Empty;
                    }
                }

                foreach (var name in new[] { "message", "detail" })
                {
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // JSON 이 아니면 본문 글을 그대로 쓴다.
        }

        return raw;
    }

    private static string Clip(string text)
    {
        // 정규식에 큰 글을 넘기지 않는다: 자를 길이의 몇 배까지만 본다.
        if (text.Length > MaxErrorExcerptChars * 4)
        {
            text = text[..(MaxErrorExcerptChars * 4)];
        }

        var single = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
        return single.Length <= MaxErrorExcerptChars ? single : single[..MaxErrorExcerptChars] + "…";
    }

    private async Task<(StreamReader? Reader, AiError? Error)> OpenReaderAsync(
        HttpResponseMessage response, AgentSession session, string? apiKey, CancellationToken ct, CancellationToken run)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(run).ConfigureAwait(false);
            return (new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, CancelError(session));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return (null, new AiError($"서버 응답을 읽지 못했습니다: {Redact(ex.Message, apiKey)}", "api_connection_lost"));
        }
    }

    /// <summary>
    /// 스트림에서 한 줄을 읽어 반영한다. 줄마다 무응답 제한을 새로 건다. <c>data:</c> 가 아닌 줄(주석 <c>:</c>·<c>event:</c>)은 건너뛰되 읽은 것으로 친다.
    /// 끝 표시 <c>[DONE]</c> 이 없이 연결이 닫혀도 <c>finish_reason</c> 를 받았으면 정상이다(HTTP/1.0 서버는 연결을 닫아 본문을 끝낸다).
    /// </summary>
    private async Task<ReadStep> ReadStepAsync(
        StreamReader reader, OpenAiTurnAccumulator accumulator, bool jsonMode, string? apiKey, AgentSession session, CancellationToken ct, CancellationToken run)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(run);
        idle.CancelAfter(_options.ResponseTimeout);
        string? line;
        try
        {
            // 서버가 stream 을 무시하고 JSON 한 덩어리로 답하면 본문 전체를 한 번에 읽는다.
            line = jsonMode ? await reader.ReadToEndAsync(idle.Token).ConfigureAwait(false) : await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReadStep(null, false, CancelError(session));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return new ReadStep(null, false, new AiError($"서버와의 연결이 끊겼습니다: {Redact(ex.Message, apiKey)}", "api_connection_lost"));
        }

        if (line is null)
        {
            return accumulator.HasFinishReason
                ? new ReadStep(null, true, null)
                : new ReadStep(null, false, new AiError("서버 응답이 끝나기 전에 연결이 닫혔습니다.", "api_bad_response"));
        }

        var payload = line.Trim();
        if (!jsonMode)
        {
            if (payload.Length == 0 || payload[0] == ':')
            {
                return default;
            }

            if (payload.StartsWith("data:", StringComparison.Ordinal))
            {
                payload = payload[5..].Trim();
            }
            else if (payload[0] != '{')
            {
                // event:·id:·retry: 같은 SSE 필드.
                return default;
            }

            if (payload.Length == 0)
            {
                return default;
            }

            if (payload == "[DONE]")
            {
                return new ReadStep(null, true, null);
            }
        }

        OpenAiChunkResult result;
        try
        {
            using var document = JsonDocument.Parse(payload);
            result = accumulator.Apply(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new ReadStep(null, false, new AiError("서버 응답을 해석하지 못했습니다. OpenAI 형식(chat/completions) 서버인지 확인하세요.", "api_bad_response"));
        }

        if (accumulator.Size > _options.MaxBytes)
        {
            return new ReadStep(null, false, new AiError("서버 응답이 너무 커서 중단했습니다.", "api_bad_response"));
        }

        if (result.Error is not null)
        {
            return new ReadStep(null, false, new AiError($"서버가 오류를 알려 왔습니다: {Clip(Redact(result.Error, apiKey))}", "api_error"));
        }

        return new ReadStep(result.TextDelta, jsonMode, null);
    }

    private async Task<(AgentGateResponse? Response, AiError? Error)> CallGateAsync(
        AgentSession session, string name, JsonNode arguments, CancellationToken ct, CancellationToken run)
    {
        try
        {
            return (await _gate.CallToolAsync(session, name, arguments, run).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (session.IsRevoked)
        {
            return (null, Revoked());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 게이트 안의 시간 초과(취소 예외)와 그 밖의 실패는 도구 실패다. 예외 메시지에는 DB·내부 정보가 섞일 수 있어 모델에도 화면에도 내지 않는다.
            return (new AgentGateResponse(
                new AgentCallToolResult(true, "{\"error\":\"도구를 실행하는 중 내부 오류가 발생했습니다.\",\"code\":\"tool_failed\"}"),
                AiToolJudgement.Failed,
                "tool_failed"), null);
        }
    }
}
