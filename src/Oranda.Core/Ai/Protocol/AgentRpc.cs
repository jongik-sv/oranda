using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Core.Ai.Protocol;

/// <summary>
/// 파이프 위 JSON-RPC 2.0 규약(연구서 §4.1, 조정 결정 2026-10-28).
/// <b>줄 단위 틀</b>: 메시지 한 개 = UTF-8 JSON 한 줄(들여쓰기·줄바꿈 없음) + <c>\n</c>, 한 줄 최대 <see cref="MaxMessageBytes"/>. 응답은 <c>id</c> 로 짝짓는다.
/// 연결의 첫 메시지는 <see cref="Methods.Hello"/> 이고, 그 전의 다른 메서드는 <see cref="ErrorCodes.HelloRequired"/> 로 거절한다.
/// 읽기·쓰기 틀은 <see cref="AgentRpcChannel"/>.
/// </summary>
public static class AgentRpc
{
    public const string Version = "2.0";

    /// <summary>현재 프로토콜 버전(<c>hello.protocol</c>). 호환이 깨지는 변경에서만 올린다.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>한 줄(메시지) 최대 바이트. 넘으면 연결을 닫는다.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>표준 JSON-RPC 오류 코드와 Oranda 가 정한 코드(-32000 대). 도구 거부는 오류가 아니라 <see cref="AgentCallToolResult.IsError"/> 로 알린다.</summary>
    public static class ErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        /// <summary>실행 토큰이 없거나 모르거나 폐기됐다.</summary>
        public const int TokenInvalid = -32001;

        /// <summary><c>oranda.hello</c> 전에 다른 메서드를 불렀다.</summary>
        public const int HelloRequired = -32002;

        /// <summary>앱이 종료 중이다.</summary>
        public const int ShuttingDown = -32003;

        /// <summary>토큰 없는 외부 연결(이번 회차는 지원하지 않는다).</summary>
        public const int RunRequired = -32004;

        /// <summary><c>hello.protocol</c> 을 앱이 지원하지 않는다.</summary>
        public const int UnsupportedProtocol = -32005;
    }

    /// <summary>메서드 이름.</summary>
    public static class Methods
    {
        /// <summary>연결 직후 첫 메시지: 프로토콜·실행 토큰·클라이언트 정보 → 실행 id·서버 버전(<see cref="AgentHelloParams"/>).</summary>
        public const string Hello = "oranda.hello";

        /// <summary>앱이 접속의 허용 수준으로 거른 도구 목록(<see cref="AgentListToolsResult"/>). <c>orandactl</c> 은 스키마를 갖지 않는다.</summary>
        public const string ListTools = "oranda.listTools";

        /// <summary>도구 호출(<see cref="AgentCallToolParams"/> → <see cref="AgentCallToolResult"/>). 거부는 <c>isError: true</c> 와 이유 글.</summary>
        public const string CallTool = "oranda.callTool";
    }

    /// <summary>메시지를 한 줄 글로(끝의 <c>\n</c> 은 붙이지 않는다).</summary>
    public static string Serialize(AgentRpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, AgentRpcJsonContext.Default.AgentRpcMessage);
    }

    /// <summary>한 줄 글을 메시지로. 틀이 잘못되면 null(호출하는 쪽이 <see cref="ErrorCodes.ParseError"/> 응답을 낸다).</summary>
    public static AgentRpcMessage? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var message = JsonSerializer.Deserialize(line, AgentRpcJsonContext.Default.AgentRpcMessage);
            return message is { Jsonrpc: Version } ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AgentRpcMessage Request(long id, string method, JsonNode? parameters = null) =>
        new() { Jsonrpc = Version, Id = id, Method = method, Params = parameters };

    public static AgentRpcMessage Success(long? id, JsonNode? result) =>
        new() { Jsonrpc = Version, Id = id, Result = result ?? new JsonObject() };

    public static AgentRpcMessage Failure(long? id, int code, string message) =>
        new() { Jsonrpc = Version, Id = id, Error = new AgentRpcError(code, message) };

    /// <summary>인자 객체를 JSON 노드로(요청 <c>Params</c>·응답 <c>Result</c> 에 넣는다).</summary>
    public static JsonNode? ToNode<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.SerializeToNode(value, info);
}

/// <summary>실행 토큰 규칙(조정 결정 2026-10-28): 32바이트 난수 base64url, 명령 인자가 아니라 환경 변수로 넘긴다.</summary>
public static class AgentRunToken
{
    /// <summary>CliDriver 가 mcp-config 의 <c>env</c> 로 <c>orandactl</c> 에 주는 환경 변수 이름. 명령 인자에 넣지 않는다(ps 에 보인다).</summary>
    public const string EnvironmentVariable = "ORANDA_RUN_TOKEN";

    /// <summary>새 토큰을 만든다(<c>claude -p</c> 기동마다, <c>--resume</c> 포함).</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// JSON-RPC 메시지 하나(요청·응답 공용). 요청은 <see cref="Method"/> 가 있고, 응답은 <see cref="Result"/> 또는 <see cref="Error"/> 가 있다.
/// <see cref="Id"/> 는 정수만 쓴다(파싱 실패 응답은 null).
/// </summary>
public sealed record AgentRpcMessage
{
    public string Jsonrpc { get; init; } = AgentRpc.Version;

    public long? Id { get; init; }

    public string? Method { get; init; }

    public JsonNode? Params { get; init; }

    public JsonNode? Result { get; init; }

    public AgentRpcError? Error { get; init; }

    [JsonIgnore]
    public bool IsRequest => Method is not null;
}

public sealed record AgentRpcError(int Code, string Message);

/// <summary><c>oranda.hello</c> 요청 인자.</summary>
/// <param name="Protocol"><see cref="AgentRpc.ProtocolVersion"/>.</param>
/// <param name="RunToken">앱이 AI 요청마다 발급한 실행 토큰(<see cref="AgentRunToken.EnvironmentVariable"/> 값).</param>
/// <param name="Client">클라이언트(MCP initialize 의 clientInfo). 감사 기록과 DBMS_APPLICATION_INFO 에 쓴다.</param>
public sealed record AgentHelloParams(int Protocol, string RunToken, AgentClientInfo Client);

public sealed record AgentClientInfo(string Name, string Version);

/// <summary><c>oranda.hello</c> 응답.</summary>
public sealed record AgentHelloResult(string RunId, string ServerVersion);

/// <summary><c>oranda.listTools</c> 응답.</summary>
public sealed record AgentListToolsResult(IReadOnlyList<AgentToolInfo> Tools);

/// <summary>MCP <c>tools/list</c> 의 도구 한 개와 같은 모양(<c>InputSchema</c> 는 JSON 객체).</summary>
public sealed record AgentToolInfo(string Name, string Description, JsonNode InputSchema)
{
    /// <summary>도구 정의에서 노출용 정보를 만든다(입력 스키마 글을 JSON 객체로 푼다).</summary>
    public static AgentToolInfo From(AgentToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(definition.Name, definition.Description, JsonNode.Parse(definition.InputSchemaJson)!);
    }
}

/// <summary><c>oranda.callTool</c> 요청 인자. 실행 id 는 hello 로 연결에 묶였으므로 싣지 않는다.</summary>
/// <param name="Name">도구 이름.</param>
/// <param name="Arguments">도구 인자(스키마의 snake_case 이름).</param>
public sealed record AgentCallToolParams(string Name, JsonNode? Arguments = null);

/// <summary>
/// <c>oranda.callTool</c> 응답. <paramref name="IsError"/> 가 참이면 게이트 거부·도구 실패이고 <paramref name="Text"/> 가
/// 이유 글(<c>{ "error": "…", "code": "…" }</c> JSON)이다. 성공이면 <paramref name="Text"/> 가 결과 JSON 글이다.
/// <paramref name="Structured"/> 는 같은 결과를 구조로 줄 필요가 있을 때만 채운다(크기를 두 배로 하지 않도록 기본은 null).
/// </summary>
public sealed record AgentCallToolResult(bool IsError, string Text, JsonNode? Structured = null);

/// <summary>프로토콜 JSON 의 소스 생성 문맥. 줄 단위 틀을 지키려고 <b>들여쓰기를 끈다</b>(<c>OrandaJsonContext</c> 는 들여쓰기가 켜져 있어 쓰면 안 된다).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AgentRpcMessage))]
[JsonSerializable(typeof(AgentHelloParams))]
[JsonSerializable(typeof(AgentHelloResult))]
[JsonSerializable(typeof(AgentListToolsResult))]
[JsonSerializable(typeof(AgentCallToolParams))]
[JsonSerializable(typeof(AgentCallToolResult))]
public sealed partial class AgentRpcJsonContext : JsonSerializerContext
{
}
