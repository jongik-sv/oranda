using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.Core.Ai.Protocol;

/// <summary>
/// 외부 연결 파일 <c>&lt;데이터 폴더&gt;/run/agents/&lt;pid&gt;.json</c> 한 개(설계 agent-cli-control §3.3, 계약 A1).
/// 앱(A4)이 외부 연결을 켤 때 쓰고(폴더 0700·파일 0600) 끊기·만료·종료 때 지운다. <c>orandactl</c>(B1)은 읽기만 한다.
/// 접속은 별칭·환경·수준만 적는다(호스트·서비스·사용자·비밀번호 없음). 토큰 원문이 들어 있으므로 <see cref="ToString"/> 은 토큰을 빼고 보인다.
/// 경로는 <see cref="Storage.AppDataLocation.EndpointFile"/>.
/// </summary>
/// <param name="V">형식 버전(<see cref="CurrentVersion"/>). 다른 값은 읽지 않는다.</param>
/// <param name="Pid">앱 프로세스 id(파일 이름과 같다).</param>
/// <param name="StartedAt">외부 연결을 켠 시각.</param>
/// <param name="AppVersion">앱 버전.</param>
/// <param name="Label">창 이름(작업 공간 이름 등). <c>--instance</c> 로 고를 때 쓴다.</param>
/// <param name="Endpoint">통로.</param>
/// <param name="Token">허가 토큰 원문(base64url 43자).</param>
/// <param name="ExpiresAt">만료 시각. null 이면 앱을 끌 때까지.</param>
/// <param name="Connections">허용 접속.</param>
public sealed record AgentEndpointFile(
    int V,
    int Pid,
    DateTimeOffset StartedAt,
    string AppVersion,
    string Label,
    AgentEndpoint Endpoint,
    string Token,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<AgentEndpointConnection> Connections)
{
    /// <summary>지금 형식 버전.</summary>
    public const int CurrentVersion = 1;

    /// <summary>만료됐는가.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && now >= at;

    /// <summary>쓰기용 문맥: 사람이 열어 볼 수 있게 한글을 이스케이프하지 않는다(파일은 0600 이고 HTML 에 넣지 않는다).</summary>
    private static readonly AgentEndpointJsonContext Writer = new(new JsonSerializerOptions(AgentEndpointJsonContext.Default.Options)
    {
        TypeInfoResolver = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>파일 글(UTF-8, 들여쓰기, snake_case, 열거형은 이름).</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Writer.AgentEndpointFile);

    /// <summary>
    /// 파일 글을 읽는다. JSON 이 틀렸거나, 버전이 다르거나, 필수 값(pid &gt; 0, 통로 종류·이름, 토큰)이 비면 null —
    /// <c>orandactl</c> 은 그런 파일을 건너뛴다(지우지 않는다).
    /// </summary>
    public static AgentEndpointFile? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        AgentEndpointFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, AgentEndpointJsonContext.Default.AgentEndpointFile);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (file is null
            || file.V != CurrentVersion
            || file.Pid <= 0
            || file.Endpoint is null
            || file.Endpoint.Kind is not (AgentEndpoint.PipeKind or AgentEndpoint.UnixKind)
            || string.IsNullOrWhiteSpace(file.Endpoint.Name)
            || string.IsNullOrWhiteSpace(file.Token))
        {
            return null;
        }

        return file with
        {
            AppVersion = file.AppVersion ?? string.Empty,
            Label = file.Label ?? string.Empty,
            Connections = file.Connections ?? [],
        };
    }

    public override string ToString() => $"AgentEndpointFile {{ Pid = {Pid}, Label = {Label}, Endpoint = {Endpoint} }}";
}

/// <summary>통로. Windows = 명명 파이프 이름(<c>\\.\pipe\</c> 뒤), macOS·Linux = 소켓 절대 경로.</summary>
/// <param name="Kind"><see cref="PipeKind"/> 또는 <see cref="UnixKind"/>.</param>
/// <param name="Name">파이프 이름 또는 소켓 절대 경로. <c>NamedPipeClientStream</c> 의 이름 인자에 그대로 넣는다.</param>
public sealed record AgentEndpoint(string Kind, string Name)
{
    public const string PipeKind = "pipe";

    public const string UnixKind = "unix";
}

/// <summary>연결 파일의 허용 접속 하나(별칭·환경·수준만).</summary>
public sealed record AgentEndpointConnection(string Alias, ConnectionEnvironment Environment, AiAccessLevel Level);

/// <summary>연결 파일 JSON 문맥. 사람이 열어 볼 수 있게 들여쓰기를 켠다(파이프 줄 틀과 따로).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AgentEndpointFile))]
internal sealed partial class AgentEndpointJsonContext : JsonSerializerContext
{
}
