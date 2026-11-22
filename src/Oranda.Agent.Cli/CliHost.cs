using System.Reflection;
using System.Text;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli;

/// <summary>
/// <c>orandactl</c> 이 바깥과 닿는 곳 전부(콘솔·파일·환경 변수·프로세스·시계·통로). 시험은 가짜를 넣는다.
/// </summary>
internal sealed class CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>stdin 전부(UTF-8).</summary>
    public required Func<string> ReadStdin { get; init; }

    /// <summary>파일 전부(UTF-8, BOM 은 읽어 버린다).</summary>
    public required Func<string, string> ReadFile { get; init; }

    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    public required AppOs Os { get; init; }

    public required Func<int, bool> IsProcessAlive { get; init; }

    public required Func<DateTimeOffset> Now { get; init; }

    /// <summary>정해진 통로로 실제 클라이언트를 만든다.</summary>
    public required Func<BridgeTarget, IBridgeClient> CreateClient { get; init; }

    /// <summary>MCP stdio 서버를 돌린다(서버 이름 = 둘째 인자).</summary>
    public required Func<IBridgeClient, string, CancellationToken, Task<int>> RunMcp { get; init; }

    /// <summary>실제 프로세스용.</summary>
    public static CliHost Console()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var stdout = new StreamWriter(System.Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        var stderr = new StreamWriter(System.Console.OpenStandardError(), utf8) { AutoFlush = true };
        return new CliHost
        {
            Out = stdout,
            Error = stderr,
            ReadStdin = () =>
            {
                using var reader = new StreamReader(System.Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            },
            ReadFile = path => File.ReadAllText(path, utf8),
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            Os = AppDataLocation.CurrentOs,
            IsProcessAlive = EndpointLocator.IsProcessAlive,
            Now = () => DateTimeOffset.Now,
            CreateClient = target => new PipeBridgeClient(target.PipeName, target.Token),
            RunMcp = McpServerHost.RunAsync,
        };
    }

    /// <summary>이 프로그램 버전.</summary>
    public static string Version { get; } =
        typeof(CliHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";

    /// <summary>
    /// hello 에 낼 클라이언트 이름(상태 표시줄 띠·감사에 보인다). <c>ORANDA_CLIENT</c> 가 있으면 그 값,
    /// Claude Code 안이면(<c>CLAUDECODE=1</c>) <c>Claude Code</c>, 아니면 <c>orandactl</c>.
    /// </summary>
    public AgentClientInfo Client()
    {
        var name = GetEnvironmentVariable("ORANDA_CLIENT");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = GetEnvironmentVariable("CLAUDECODE") == "1" ? "Claude Code" : "orandactl";
        }

        return new AgentClientInfo(name.Trim(), Version);
    }

    /// <summary>데이터 폴더의 연결 파일 찾기.</summary>
    public EndpointLocator Locator() =>
        new(AppDataLocation.Resolve(GetEnvironmentVariable, Os), IsProcessAlive, Now);
}
