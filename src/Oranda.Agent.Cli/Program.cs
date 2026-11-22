using System.Text;
using Oranda.Agent.Cli;

// orandactl: 밖의 LLM CLI 가 셸로 부르는 하위 명령(status·tabs·tab·sql·obj·query·result·tune·call·tools)과 MCP stdio 서버(mcp).
// 앱이 띄우는 내장 실행은 지금처럼 `orandactl mcp --pipe <이름>` + 환경 변수 ORANDA_RUN_TOKEN.
// 밖에서 띄우면 데이터 폴더의 run/agents/<pid>.json 연결 파일에서 통로와 토큰을 읽는다(설계 agent-cli-control §3·§4).
// Windows 콘솔에서 한글이 깨지지 않게 UTF-8 로 둔다(출력은 CliHost 가 UTF-8 스트림으로 직접 쓴다).
try
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // 콘솔이 없는 경우(파이프로만 붙음). 스트림은 따로 UTF-8 로 연다.
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await CliApp.RunAsync(args, CliHost.Console(), cancellation.Token);
