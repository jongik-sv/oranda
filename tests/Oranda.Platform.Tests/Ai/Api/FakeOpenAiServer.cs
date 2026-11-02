using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Oranda.Platform.Tests.Ai.Api;

/// <summary>한 번의 요청: 주소·본문·Authorization 값.</summary>
internal sealed record RecordedRequest(Uri Uri, string Body, string? Authorization, string? Accept)
{
    public JsonNode Json => JsonNode.Parse(Body)!;

    public JsonArray Messages => Json["messages"]!.AsArray();
}

/// <summary>
/// OpenAI 형식 서버 가짜. 요청을 기록하고 미리 넣어 둔 응답을 차례로 돌려준다(없으면 시험이 잘못 짠 것이므로 예외).
/// 조각 모양은 사내 서버·OpenAI 의 실제 스트림을 따른다: <c>choices[0].delta</c>, 끝 조각의 <c>finish_reason</c>, 별도 <c>usage</c> 조각, <c>data: [DONE]</c>.
/// </summary>
internal sealed class FakeOpenAiServer : HttpMessageHandler
{
    private readonly Queue<Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public void Enqueue(Func<RecordedRequest, HttpResponseMessage> response) => _responses.Enqueue((request, _) => Task.FromResult(response(request)));

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue((_, _) => Task.FromResult(response));

    /// <summary>응답을 만들기까지 기다릴 수 있는 응답(취소·머리글 무응답 시험).</summary>
    public void EnqueueAsync(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> response) => _responses.Enqueue(response);

    /// <summary>머리글은 바로 오고 본문은 <paramref name="prefix"/> 만 준 뒤 멈추는 응답.</summary>
    public void EnqueueHanging(params string[] chunks) =>
        Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangingStream(Encoding.UTF8.GetBytes(string.Concat(chunks.Select(c => $"data: {c}\n\n"))))),
        });

    /// <summary>스트리밍 응답 한 번. 마지막에 <c>[DONE]</c> 을 붙인다.</summary>
    public void EnqueueSse(params string[] chunks) => Enqueue(Sse(chunks));

    public static HttpResponseMessage Sse(IEnumerable<string> chunks, bool done = true)
    {
        var body = new StringBuilder();
        foreach (var chunk in chunks)
        {
            body.Append("data: ").Append(chunk).Append("\n\n");
        }

        if (done)
        {
            body.Append("data: [DONE]\n\n");
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var recorded = new RecordedRequest(request.RequestUri!, body, request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString());
        Requests.Add(recorded);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("시험이 준비하지 않은 요청입니다: " + recorded.Uri);
        }

        return await _responses.Dequeue()(recorded, cancellationToken).ConfigureAwait(false);
    }

    // 조각 만들기.
    public static string Chunk(JsonObject delta, string? finishReason = null)
    {
        var choice = new JsonObject { ["index"] = 0, ["delta"] = delta };
        if (finishReason is not null)
        {
            choice["finish_reason"] = finishReason;
        }

        return new JsonObject { ["choices"] = new JsonArray(choice) }.ToJsonString();
    }

    public static string Text(string text) => Chunk(new JsonObject { ["content"] = text });

    public static string Finish(string reason = "stop") => Chunk([], reason);

    public static string Usage(int prompt, int completion) =>
        new JsonObject
        {
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion },
        }.ToJsonString();

    /// <summary>도구 호출 조각. 인자는 스트림에서처럼 조각난 문자열 값이다.</summary>
    public static string ToolCall(int index, string? id, string? name, string? arguments)
    {
        var call = new JsonObject { ["index"] = index };
        if (id is not null)
        {
            call["id"] = id;
            call["type"] = "function";
        }

        var function = new JsonObject();
        if (name is not null)
        {
            function["name"] = name;
        }

        if (arguments is not null)
        {
            function["arguments"] = arguments;
        }

        call["function"] = function;
        return Chunk(new JsonObject { ["tool_calls"] = new JsonArray(call) });
    }

    /// <summary>한 번에 도구 하나를 부르고 끝나는 응답(조각 3개 + 끝 + usage).</summary>
    public static string[] ToolCallTurn(string id, string name, string arguments, int prompt = 10, int completion = 5) =>
    [
        ToolCall(0, id, name, string.Empty),
        ToolCall(0, null, null, arguments),
        Finish("tool_calls"),
        Usage(prompt, completion),
    ];

    public static string[] TextTurn(string text, int prompt = 10, int completion = 5) =>
        [Text(text), Finish(), Usage(prompt, completion)];
}

/// <summary>앞에 준 바이트를 읽게 한 뒤 취소될 때까지 응답이 멈춘 스트림(서버가 말없이 붙잡고 있는 상황).</summary>
internal sealed class HangingStream(byte[] prefix) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < prefix.Length)
        {
            var count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
