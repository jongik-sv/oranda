using Oranda.Agent.Cli;
using Oranda.Core.Ai.Protocol;

// 사용: orandactl mcp [--pipe <이름>]
// claude 가 mcp-config 로 띄운다. 파이프 이름은 앱이 --pipe 로, 실행 토큰은 환경 변수 ORANDA_RUN_TOKEN 으로 준다.
if (args.Length >= 1 && args[0] == "mcp")
{
    var pipeName = ArgumentReader.ReadOption(args, "--pipe") ?? DefaultPipe.Resolve();
    if (pipeName is null)
    {
        await Console.Error.WriteLineAsync("orandactl mcp: --pipe <이름> 이 필요합니다.");
        return 2;
    }

    var runToken = Environment.GetEnvironmentVariable(AgentRunToken.EnvironmentVariable) ?? string.Empty;
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancellation.Cancel();
    };

    await using var bridge = new PipeBridgeClient(pipeName, runToken);
    return await McpServerHost.RunAsync(bridge, cancellation.Token);
}

await Console.Error.WriteLineAsync("사용법: orandactl mcp [--pipe <이름>]");
return 2;
