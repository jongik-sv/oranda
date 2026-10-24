using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Execution;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.Tuning.Trace;

/// <summary>
/// TSK-06-04 「트레이스 보기」·「옵티마이저 트레이스(10053)」(commands.md 「튜닝」 계획 보강 300~).
/// 트레이스 보기는 매개변수로 <see cref="SessionKey"/> 를 받으면 그 세션(세션 모니터), 없으면 내 세션의 파일을 연다.
/// 10053 은 편집기 지금 문장의 사본을 파싱만 시킨 뒤 트레이스 보기로 연다(<see cref="OptimizerTraceRunner"/>).
/// </summary>
[OrandaCommand("oranda.tuning.optimizerTrace", CommandCategory.Sql, When = "sqlDocFocus", Enablement = "hasSession && !sessionBusy",
    Icon = "bug", Placements = ["menu:tuning/optimizerTrace@310"])]
[OrandaCommand("oranda.tuning.traceViewer", CommandCategory.Sql, Enablement = "hasSession",
    Icon = "console", Placements = ["menu:tuning/traceViewer@320"])]
public sealed class TraceCommandHandler : ICommandHandler
{
    public const string TraceViewerId = "oranda.tuning.traceViewer";
    public const string OptimizerTraceId = "oranda.tuning.optimizerTrace";

    private readonly ServiceContainer? _container;

    public TraceCommandHandler()
    {
    }

    public TraceCommandHandler(ServiceContainer container) => _container = container;

    /// <summary>시험용: 문서 탭 열기(panelId, 새 ViewModel, 제목 → 화면의 ViewModel).</summary>
    internal Func<string, ToolPanelViewModelBase, string, ToolPanelViewModelBase?>? OpenDocument { get; set; }

    /// <summary>시험용: 지금 세션.</summary>
    internal Func<ISession?>? SessionProvider { get; set; }

    /// <summary>시험용: 10053 실행을 바꾼다.</summary>
    internal Func<SqlStatement, CancellationToken, Task<OptimizerTraceOutcome>>? OptimizerTraceOverride { get; set; }

    /// <summary>시험용: 편집기 지금 문장.</summary>
    internal Func<SqlStatement?>? StatementProvider { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null && OpenDocument is null)
        {
            return;
        }

        var session = CurrentSession();
        if (session is null)
        {
            return;
        }

        if (Open() is not TraceViewerViewModel vm)
        {
            return;
        }

        vm.OptimizerTrace = ct => RunOptimizerTraceAsync(ct);
        if (context.CommandId == OptimizerTraceId)
        {
            await vm.RunOptimizerTraceAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        var target = context.Parameter is SessionKey key ? key : default;
        await vm.LoadAsync(session, target, ct: cancellationToken).ConfigureAwait(true);
    }

    private Task<OptimizerTraceOutcome> RunOptimizerTraceAsync(CancellationToken ct)
    {
        var stmt = StatementProvider?.Invoke() ?? _container?.GetService<IEditorContext>()?.GetCurrentStatement()
            ?? throw new InvalidOperationException("10053 을 남길 문장이 없습니다. 편집기에서 문장 안에 커서를 두세요.");
        if (OptimizerTraceOverride is { } run)
        {
            return run(stmt, ct);
        }

        var container = _container ?? throw new InvalidOperationException("서비스가 없습니다.");
        var bus = container.GetService<IEventBus>();
        var state = ExecutionState.For(bus);
        var runner = new OptimizerTraceRunner(
            state,
            CurrentSession,
            () =>
            {
                ExecutionCommands.EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            },
            () => container.GetService<ITuningService>() ?? throw new NotSupportedException("튜닝 서비스가 없습니다."));
        return runner.RunAsync(stmt, ct);
    }

    private ISession? CurrentSession()
    {
        if (SessionProvider is { } provider)
        {
            return provider();
        }

        return _container is null ? null : ExecutionCommands.ActiveTabSession(_container) ?? ConnectionManager.SessionRouter.CurrentOf(_container);
    }

    private ToolPanelViewModelBase? Open()
    {
        var vm = _container is null ? new TraceViewerViewModel() : new TraceViewerViewModel(_container);
        if (OpenDocument is not null)
        {
            return OpenDocument(TuningCommands.TraceViewerPanelId, vm, "트레이스 보기");
        }

        return _container?.GetService<IShellUi>()?.Docking.OpenDocument(TuningCommands.TraceViewerPanelId, vm, "트레이스 보기")?.Panel as ToolPanelViewModelBase;
    }
}
