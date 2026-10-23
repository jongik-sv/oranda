using Avalonia.Input.Platform;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Language;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Run;

/// <summary>
/// 시나리오 실행·실행 계획·펼친 SQL 복사 명령 4개의 처리기(docs/oranda1/design.md 4.1). 특성(ID·활성 조건·자리)은 TSK-01-02 가 동결했다.
/// 대상은 활성 문장의 계산 상태(<see cref="MyBatisEvalHub.Active"/>)다. 실행 직전에 예약된 다시 계산을 끝내고(<see cref="MyBatisStatementEval.Flush"/>) 그 결과를 쓴다.
/// 계산 못 한 식이 남아 있어도 실행은 한다(덮어쓰기는 사용자 몫). 실행은 <see cref="MyBatisRunner"/> 가 기존 실행 경로(정책 관문 포함)로 넘긴다.
/// </summary>
[OrandaCommand("oranda.mybatis.runScenario", CommandCategory.Sql, Icon = "run", When = "mybatisDocFocus", Enablement = "mybatisDocActive && mybatisStatementSelected && (hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy", Placements = ["toolbar:mybatis/run@10", "menu:sql/mybatisRun@15"])]
[OrandaCommand("oranda.mybatis.explainScenario", CommandCategory.Sql, Icon = "explainPlan", Enablement = "mybatisDocActive && mybatisStatementSelected && (hasSession || dedicatedSession || tabConnectionMissing) && !sessionBusy", Placements = ["toolbar:mybatis/explain@20", "menu:sql/mybatisExplain@52"])]
[OrandaCommand("oranda.mybatis.copyExpanded", CommandCategory.Edit, Icon = "copy", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["toolbar:mybatis/copy@50", "context:mybatisExpanded/copy@10"])]
[OrandaCommand("oranda.mybatis.copyExpandedWithValues", CommandCategory.Edit, Icon = "copy", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["toolbar:mybatis/copy@51", "context:mybatisExpanded/copyValues@20"])]
public sealed class MyBatisRunCommands : ICommandHandler
{
    // 계산기·언어 서비스 같은 Language 형은 필드로 두지 않고 쓸 때 컨테이너에서 꺼낸다(시작 시험 제약, OutlineViewModel 주석 참고).
    private readonly ServiceContainer? _container;

    public MyBatisRunCommands()
    {
    }

    public MyBatisRunCommands(ServiceContainer container)
    {
        _container = container;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container?.GetService<MyBatisEvalHub>() is not { } hub || hub.Active is not { } eval)
        {
            return;
        }

        var log = ExecutionLog.For(_container.GetService<IEventBus>());
        eval.Flush();
        if (eval.Result is not { } result)
        {
            log.AddMessage(MessageKind.Warning, eval.Error is { } error ? "펼친 SQL 을 만들지 못했습니다: " + error : "펼친 SQL 이 아직 없습니다.");
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.mybatis.copyExpanded":
                await CopyAsync(result.Sql, "펼친 SQL", log).ConfigureAwait(true);
                break;
            case "oranda.mybatis.copyExpandedWithValues":
                {
                    var binds = MyBatisRunBinds.Build(result.Binds, out var error, eval.RowTypeOf);
                    if (binds is null)
                    {
                        log.AddMessage(MessageKind.Error, error ?? "바인드 값을 읽을 수 없습니다.");
                        return;
                    }

                    await CopyAsync(MyBatisRunText.WithValues(result.Sql, binds), "바인드 값을 넣은 SQL", log).ConfigureAwait(true);
                    break;
                }

            case "oranda.mybatis.runScenario":
                if (TabOf(eval, hub) is { } runTab)
                {
                    await Runner().RunAsync(runTab, result, eval.RowTypeOf, cancellationToken).ConfigureAwait(true);
                }

                break;
            case "oranda.mybatis.explainScenario":
                if (TabOf(eval, hub) is { } planTab)
                {
                    await ExplainAsync(planTab, result, cancellationToken).ConfigureAwait(true);
                }

                break;
        }
    }

    private MyBatisRunner Runner() =>
        new(ExecutionCommands.Resolve(_container!), _container!.GetRequiredService<ILanguageService>());

    private static SqlEditorViewModel? TabOf(MyBatisStatementEval eval, MyBatisEvalHub hub) =>
        MyBatisRunner.TabOf(eval, hub.Host);

    /// <summary>같은 실행 문장으로 예상 계획을 낸다(접속 확인·세션은 실행과 같고, 문장은 실행하지 않는다).</summary>
    private async Task ExplainAsync(SqlEditorViewModel tab, EvalResult result, CancellationToken ct)
    {
        var controller = ExecutionCommands.Resolve(_container!);
        if (Runner().Prepare(result) is not { } stmt || !await controller.EnsureConnectionAsync(tab, ct).ConfigureAwait(true))
        {
            return;
        }

        if (controller.SessionFor(tab) is not { } session)
        {
            controller.Log.AddMessage(MessageKind.Warning, "접속이 없습니다. 접속한 뒤 실행 계획을 보세요.");
            return;
        }

        if (controller.State.IsBusy(session.Id) || _container!.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        shell.Docking.ShowTool(ExecutionPlanCommands.PanelId);
        if (await FindPanelAsync().ConfigureAwait(true) is { } plan)
        {
            await plan.ExplainAsync(session, stmt, ct).ConfigureAwait(true);
        }
    }

    // 창이 막 열렸으면 모델이 아직 없을 수 있어 몇 번 기다린다(ExecutionPlanCommandHandler 와 같은 방식).
    private static async Task<PlanViewModel?> FindPanelAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            if (PlanViewModel.Current is { } vm)
            {
                return vm;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        return PlanViewModel.Current;
    }

    private async Task CopyAsync(string text, string what, ExecutionLog log)
    {
        var clipboard = _container!.GetService<ShellController>()?.MainWindow?.Clipboard;
        if (clipboard is null)
        {
            log.AddMessage(MessageKind.Warning, "클립보드를 쓸 수 없어 복사하지 못했습니다.");
            return;
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(true);
        log.AddMessage(MessageKind.Info, what + "을(를) 복사했습니다.");
    }
}
