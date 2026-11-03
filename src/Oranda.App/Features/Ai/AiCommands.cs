using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai;

/// <summary>
/// 「AI 튜닝」 명령(툴바 나눔 단추의 본체와 ▾ 자식, 튜닝 메뉴). 「이 문장 튜닝」 만 동작한다.
/// 해설·설명·오류 고치기는 각 명령 파일(<c>Tuning/Commands/*</c>)의 <c>Enablement</c> 상수를 활성 식으로 쓰고, 켜기 전에는 늘 거짓인 키(<c>aiNotYet</c>)다.
/// 명령을 켜는 항목은 이 파일이 아니라 자기 명령 파일의 상수만 바꾼다.
/// 설정에서 AI 모델을 고르지 않았거나(<c>aiConfigured</c>) 접속의 AI 허용 수준이 「끔」 이면(<c>aiAccessOn</c>) 비활성이고, 이유는 메뉴에 나온다.
/// 이 형은 <c>Features.Ai</c> 네임스페이스라 aiTuning 기능이 꺼져 있으면 명령 자체가 보이지 않는다.
/// </summary>
[OrandaCommand(AiCommands.Tune, CommandCategory.Sql, Title = "AI 튜닝", Icon = "aiTune", When = "sqlDocFocus",
    Enablement = AiCommands.TuneEnablement, Placements = ["toolbar:main/aiTune@420", "menu:tuning/aiTune@900"])]
[OrandaCommand(AiCommands.TuneStatement, CommandCategory.Sql, Title = "이 문장 튜닝", When = "sqlDocFocus",
    Enablement = AiCommands.TuneEnablement, Placements = ["toolbar:main/aiTune/tuneStatement@10"])]
[OrandaCommand(AiCommands.ExplainPlan, CommandCategory.Sql, Title = "실행 계획 해설", When = "sqlDocFocus",
    Enablement = ExplainPlanCommand.Enablement, Placements = ["toolbar:main/aiTune/explainPlan@20"])]
[OrandaCommand(AiCommands.ExplainSql, CommandCategory.Sql, Title = "SQL 설명", When = "sqlDocFocus",
    Enablement = ExplainSqlCommand.Enablement, Placements = ["toolbar:main/aiTune/explainSql@30"])]
[OrandaCommand(AiCommands.FixError, CommandCategory.Sql, Title = "오류 고치기", When = "sqlDocFocus",
    Enablement = FixErrorCommand.Enablement, Placements = ["toolbar:main/aiTune/fixError@40"])]
public sealed class AiCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public AiCommandHandler()
    {
    }

    public AiCommandHandler(IServiceProvider services) => _services = services;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null)
        {
            return Task.CompletedTask;
        }

        var launcher = AiTuningLauncher.For(_services);
        return context.CommandId switch
        {
            AiCommands.Tune or AiCommands.TuneStatement => launcher.RunAsync(cancellationToken),
            AiCommands.ExplainSql => launcher.RunCommandAsync(ExplainSqlCommand.Instance, cancellationToken),
            AiCommands.ExplainPlan => launcher.RunCommandAsync(ExplainPlanCommand.Instance, cancellationToken),
            AiCommands.FixError => launcher.RunCommandAsync(FixErrorCommand.Instance, cancellationToken),
            _ => Task.CompletedTask,
        };
    }
}

/// <summary>AI 명령 id 와 활성 식.</summary>
public static class AiCommands
{
    public const string Tune = "oranda.ai.tune";

    public const string TuneStatement = "oranda.ai.tuneStatement";

    public const string ExplainPlan = "oranda.ai.explainPlan";

    public const string ExplainSql = "oranda.ai.explainSql";

    public const string FixError = "oranda.ai.fixError";

    /// <summary>튜닝을 시작할 수 있는 조건(실행 명령과 같은 세션 조건에 AI 설정·허용 수준을 더했다).</summary>
    public const string TuneEnablement = "aiConfigured && aiAccessOn && (hasSession || dedicatedSession) && !mybatisDocActive";
}
