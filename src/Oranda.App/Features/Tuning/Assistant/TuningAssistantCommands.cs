using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Extensibility;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Tuning.Assistant;

/// <summary>
/// TSK-06-05 튜닝 도우미 명령: 열기(튜닝 메뉴·편집기 오른쪽 클릭), 인덱스·재작성 후보 탭으로 가기, 벤치마크 실행.
/// 편집기 현재 문장은 <see cref="IEditorContext"/> 로, 실제 계획은 그 문장을 마지막으로 실행한 커서(<c>StatementExecuted</c>)로 받는다.
/// </summary>
[OrandaCommand("oranda.tuning.assistant", CommandCategory.Sql, Icon = "star", Enablement = "hasSession || dedicatedSession",
    Placements = ["menu:tuning/assistant@400", "context:editor/tuningAssistant@1030"])]
[OrandaCommand("oranda.tuning.benchmark", CommandCategory.Sql, Icon = "timer", Enablement = "tuningAssistantOpen",
    Placements = ["menu:tuning/benchmark@410"])]
[OrandaCommand("oranda.tuning.indexCandidates", CommandCategory.Sql, Enablement = "tuningAssistantOpen")]
[OrandaCommand("oranda.tuning.rewriteCandidates", CommandCategory.Sql, Enablement = "tuningAssistantOpen")]
public sealed class TuningAssistantCommandHandler : ICommandHandler
{
    /// <summary>실제 계획 형식: 마지막 실행 통계 + 별칭 절(힌트 대안이 별칭을 쓴다).</summary>
    public const string PlanFormat = "ALLSTATS LAST +ALIAS";

    private readonly ServiceContainer? _container;
    private (Guid SessionId, string Text, CursorRef Cursor)? _last;

    public TuningAssistantCommandHandler()
    {
    }

    public TuningAssistantCommandHandler(ServiceContainer container)
    {
        _container = container;
        _container.GetService<IEventBus>()?.Subscribe<StatementExecuted>(this, e =>
        {
            if (e.Result.Cursor is { } cursor)
            {
                _last = (e.SessionId, Normalize(e.Statement.Text), cursor);
            }
        });
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_container is null || _container.GetService<IShellUi>() is not { } shell)
        {
            return;
        }

        if (context.CommandId == "oranda.tuning.assistant")
        {
            await OpenAsync(shell, cancellationToken).ConfigureAwait(true);
            return;
        }

        if ((context.Parameter as TuningAssistantViewModel ?? shell.Docking.ActiveDocument?.Panel as TuningAssistantViewModel) is not { } vm)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.tuning.indexCandidates":
                vm.CandidateTab = 0;
                break;
            case "oranda.tuning.rewriteCandidates":
                vm.CandidateTab = 1;
                break;
            case "oranda.tuning.benchmark":
                await vm.RunBenchmarkAsync(cancellationToken).ConfigureAwait(true);
                break;
        }
    }

    private async Task OpenAsync(IShellUi shell, CancellationToken ct)
    {
        var container = _container!;
        var editor = container.GetService<IEditorContext>();
        var statement = editor?.GetCurrentStatement();
        var snapshot = editor?.GetActiveDocument();
        var session = ExecutionCommands.ActiveTabSession(container);
        var docking = shell.Docking;
        if (docking.OpenDocument(TuningCommands.AssistantPanelId) is not { Panel: TuningAssistantViewModel vm } doc)
        {
            return;
        }

        var title = snapshot?.Title;
        doc.Title = (LiteralTranslator.IsEnglish ? "Tuning Assistant" : "튜닝 도우미") + (string.IsNullOrEmpty(title) ? string.Empty : " — " + title);
        if (vm.OpenInNewEditor is null)
        {
            vm.OpenInNewEditor = text =>
            {
                var tab = SqlEditorHost.Current.NewDocument(text);
                tab.ConnectionProfileId = vm.Input?.Session?.Profile.Id;
            };
            vm.Confirm = (message, token) => SqlEditorHost.Current.Dialogs.ConfirmAsync(message, token);

            // 문서 탭이 닫히면 벤치마크를 멈추고 메뉴 키를 끈다.
            void OnLayout(object? sender, EventArgs e)
            {
                if (!docking.Documents.Contains(doc))
                {
                    docking.LayoutChanged -= OnLayout;
                    docking.ActiveDocumentChanged -= OnLayout;
                    vm.Closed();
                }
            }

            docking.LayoutChanged += OnLayout;
            docking.ActiveDocumentChanged += OnLayout;
        }

        var plans = container.GetService<IExecutionPlanService>();
        vm.FetchActualPlan = session is null || statement is null || plans is null ? null : token => ActualPlanAsync(plans, session, statement, token);
        vm.FetchEstimatedPlan = session is null || statement is null || plans is null ? null : async token => await plans.ExplainAsync(session, statement, token).ConfigureAwait(true);

        CorePlan? plan = null;
        if (vm.FetchActualPlan is { } fetch)
        {
            try
            {
                plan = await fetch(ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                plan = null; // 계획 없음 화면에서 다시 받게 한다
            }
        }

        await vm.LoadAsync(new TuningAssistantInput(statement, session)
        {
            SourceTitle = title,
            FirstLine = statement is not null && snapshot is not null ? LineOf(snapshot.Text, statement.Range.Start) : null,
            Plan = plan,
            PlanIsActual = plan is not null,
            PlanTime = plan is null ? null : DateTime.Now,
        }, ct).ConfigureAwait(true);
    }

    // 이 문장을 같은 세션에서 마지막으로 실행한 커서가 있으면 그 실제 계획. 없으면 null.
    private async Task<CorePlan?> ActualPlanAsync(IExecutionPlanService plans, ISession session, Oranda.Core.Execution.SqlStatement statement, CancellationToken ct)
    {
        if (_last is not { } last || last.SessionId != session.Id || last.Text != Normalize(statement.Text))
        {
            return null;
        }

        return await plans.GetCursorPlanAsync(session, last.Cursor, PlanFormat, ct).ConfigureAwait(true);
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < Math.Min(offset, text.Length); i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
