using Oranda.Perf.Common;
using Oranda.Perf.Ui;

// 사용: Oranda.Perf.Ui <format|typing|diagnostics|completion|firstrow|cancel|memory|grid|roles|sessions> [--features <구성>] [--out <폴더>] [--samples N] [--warmup N] [--lines N] [--rows N] [--seconds N] [--label 꼬리표]
// 진짜 앱을 진짜 창으로 띄워 잰다(헤드리스 아님). DB 가 필요한 시나리오는 환경 변수 ORANDA_IT_ADMIN_PASSWORD(+ ORANDA_IT_HOST/PORT/SERVICE/ADMIN_USER)로 레인 전용 사용자를 만들고 끝나면 지운다.
Console.OutputEncoding = System.Text.Encoding.UTF8;
UiOptions options;
try
{
    options = UiOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

Func<UiContext, Task> scenario = options.Scenario switch
{
    "format" => FormatScenario.RunAsync,
    "typing" => TypingScenario.RunAsync,
    "diagnostics" => DiagnosticsScenario.RunAsync,
    "completion" => CompletionScenario.RunAsync,
    "firstrow" => FirstRowScenario.RunAsync,
    "cancel" => CancelScenario.RunAsync,
    "memory" => MemoryScenario.RunAsync,
    "grid" => GridScenario.RunAsync,
    "roles" => RolesScenarios.RunAsync,
    "sessions" => RolesScenarios.SessionsAsync,
    _ => throw new NotSupportedException(options.Scenario),
};

return UiHost.Run(options, scenario);
