using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Text;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>
/// TSK-06-06 실행 이력·이름 붙인 스크립트 — 공용 동작(활성 세션 찾기, 이력 다시 실행, 편집기로 보내기, @파일 실행).
/// 서비스는 모두 처음 쓸 때 <see cref="IServiceProvider"/> 에서 꺼낸다(D-006: 첫 프레임 전에 Oracle 어셈블리를 올리지 않는다).
/// </summary>
public static class HistoryScriptsCommands
{
    /// <summary>
    /// 이력 창·스크립트 창이 실행 결과를 보여 줄 곳이 없을 때 쓰는 알림 연결 지점(상태 줄 등). 비어 있으면 무시한다.
    /// </summary>
    public static Action<string>? Notify { get; set; }

    /// <summary>스크립트 창에서 지금 고른 파일(<c>oranda.sqlplus.runFile</c> 의 기본 대상). 없으면 null.</summary>
    public static string? SelectedScriptPath { get; set; }

    /// <summary>실행 통계 수집 켜짐(<c>oranda.exec.toggleStatistics</c>). 켜면 세션에 <c>SET STATISTICS ON</c> 을 보낸다.</summary>
    public static bool StatisticsEnabled { get; private set; }

    /// <summary>
    /// 지금 작업할 세션: 선호 프로필(이력 항목의 접속)이 있으면 그 프로필의 열린 작업 세션(열려 있지 않으면 null — 다른 접속으로 대신하지 않는다),
    /// 없으면 활성 문서의 접속(<see cref="SessionRouter.ShownOf"/>: 활성 SQL 탭의 세션, 접속 안 됨 탭이면 null).
    /// </summary>
    internal static ISession? ResolveSession(IServiceProvider services, Guid? preferredProfileId = null)
    {
        if (preferredProfileId is { } pid)
        {
            return SessionRouter.ForProfile(services, pid);
        }

        return SessionRouter.ShownOf(services);
    }

    /// <summary>
    /// 다시 실행할 세션과 그 실행을 시작한 작업 공간(TSK-14-08). 활성 SQL 탭의 접속이 이력 항목의 접속이면(항목에 접속이 없어도) 그 탭의 작업 공간 세션
    /// (<see cref="SessionRouter.ForWriteAsync(IServiceProvider?, SqlEditorViewModel?, CancellationToken)"/>: 실행처럼 먼저 고르고, 첫 실행 전인 자기 세션 작업 공간이면 연다),
    /// 아니면 <see cref="ResolveSession"/>(항목의 접속의 세션 — 사용자가 고른 접속).
    /// </summary>
    private static async Task<(ISession? Session, object? Owner)> RerunSessionAsync(IServiceProvider services, Guid? profileId, CancellationToken ct)
    {
        if (SessionRouter.Find(services) is { SqlDocumentActive: true } router && SqlEditorHost.Current.Active is { } tab
            && (profileId is null || tab.ConnectionProfileId == profileId))
        {
            return (await SessionRouter.ForWriteAsync(services, tab, ct).ConfigureAwait(true), router.ActiveWorkspace);
        }

        return (ResolveSession(services, profileId), null);
    }

    /// <summary>
    /// 이력 항목의 SQL 을 다시 실행한다(실행 엔진 경유 — 정책 관문·이력 기록이 그대로 적용된다). 활성 탭과 같은 접속이면 그 작업 공간의 세션으로 하고,
    /// 도는 동안 그 세션을 실행 중으로 둔다(TSK-14-08: 세션 창 커밋·닫기가 꺼지고 같은 세션을 쓰는 다른 작업 공간은 바쁨 막대를 본다). 그 세션이 이미 실행 중이면
    /// 섞어 보내지 않고 false. 문장마다 결과는 닫는다(결과 창에 올리는 것은 실행 화면 Task 의 몫). 세션이 없으면 false.
    /// </summary>
    internal static async Task<bool> RerunAsync(IServiceProvider services, string sql, Guid? profileId, CancellationToken ct)
    {
        var (session, owner) = await RerunSessionAsync(services, profileId, ct).ConfigureAwait(true);
        if (session is null
            || services.GetService(typeof(IExecutionService)) is not IExecutionService exec
            || services.GetService(typeof(ILanguageService)) is not ILanguageService language)
        {
            return false;
        }

        var state = ExecutionState.For(services.GetService(typeof(IEventBus)) as IEventBus);
        if (state.IsBusy(session.Id))
        {
            Notify?.Invoke("그 세션은 지금 실행 중입니다. 실행이 끝난 뒤 다시 실행하세요.");
            return false;
        }

        var statements = language.Split(sql, SplitOptions.Default);
        var failed = 0;
        state.BeginRun(session.Id, owner);
        try
        {
            await foreach (var result in exec.ExecuteScriptAsync(session, statements, ScriptErrorMode.Stop,
                               new ExecutionOptions { Source = HistorySource.Editor }, ct).ConfigureAwait(true))
            {
                if (result.Status != ExecutionStatus.Succeeded)
                {
                    failed++;
                }

                if (result.ResultSet is not null)
                {
                    await result.ResultSet.DisposeAsync().ConfigureAwait(true);
                }
            }
        }
        finally
        {
            state.ApplyTransaction(session.Id, session.HasPendingTransaction);
            state.EndRun(session.Id);
        }

        Notify?.Invoke(failed == 0 ? $"다시 실행 완료({statements.Count}문장)" : $"다시 실행 중 오류 {failed}건");
        return failed == 0;
    }

    /// <summary>활성 편집기의 커서 위치에 글을 넣는다(차이 미리보기를 거쳐 사용자가 받아들일 때만).</summary>
    internal static async Task<bool> SendToEditorAsync(IServiceProvider services, string text, string description, CancellationToken ct)
    {
        if (services.GetService(typeof(IEditorContext)) is not IEditorContext editor || editor.GetActiveDocument() is not { } doc)
        {
            return false;
        }

        var edit = new TextEdit(new TextRange(doc.CaretOffset, 0), text) { Description = description };
        return await editor.ProposeEditAsync(edit, ct).ConfigureAwait(true);
    }

    /// <summary>세션에서 SQL*Plus 문장 한 줄을 실행한다(이력에는 남긴다 — 사용자가 누른 명령이다).</summary>
    internal static async Task<ExecutionResult?> RunSqlPlusAsync(IServiceProvider services, string text, bool skipHistory, CancellationToken ct)
    {
        var session = ResolveSession(services);
        if (session is null || services.GetService(typeof(IExecutionService)) is not IExecutionService exec)
        {
            return null;
        }

        var verb = text.StartsWith('@') ? "@" : text.Split(' ', 2)[0].ToUpperInvariant();
        var stmt = new SqlStatement(text, default, StatementKind.SqlPlus) { Verb = verb };
        var result = await exec.ExecuteAsync(session, stmt, new System.Collections.Generic.Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Script, SkipHistory = skipHistory }, ct).ConfigureAwait(true);
        return result;
    }

    /// <summary>시험용: 통계 플래그를 직접 바꾼다.</summary>
    internal static void SetStatisticsForTest(bool on) => StatisticsEnabled = on;

    internal static void ToggleStatistics() => StatisticsEnabled = !StatisticsEnabled;
}

/// <summary>
/// 이력·스크립트 창 열기(<c>oranda.view.history</c>, <c>oranda.view.scripts</c>), 실행 통계 켜기/끄기, @파일 실행.
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 아무것도 하지 않는다.
/// </summary>
[OrandaCommand("oranda.view.history", CommandCategory.View, Title = "실행 이력")]
[OrandaCommand("oranda.view.scripts", CommandCategory.View, Title = "스크립트")]
[OrandaCommand("oranda.exec.toggleStatistics", CommandCategory.Sql, Title = "실행 통계 수집 켜기/끄기", Enablement = "sqlDocActive",
    Placements = ["menu:sql/statistics@50"])]
[OrandaCommand("oranda.sqlplus.runFile", CommandCategory.Sql, Title = "@파일 실행", When = "sqlDocFocus", Enablement = "hasSession && !sessionBusy && !mybatisDocActive",
    Placements = ["menu:sql/runFile@60"])]
public sealed class HistoryScriptsCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public HistoryScriptsCommandHandler()
    {
    }

    public HistoryScriptsCommandHandler(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.view.history":
                (_services.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.history");
                break;
            case "oranda.view.scripts":
                (_services.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.scripts");
                break;
            case "oranda.exec.toggleStatistics":
                HistoryScriptsCommands.ToggleStatistics();
                await HistoryScriptsCommands.RunSqlPlusAsync(_services,
                    HistoryScriptsCommands.StatisticsEnabled ? "SET STATISTICS ON" : "SET STATISTICS OFF", skipHistory: true, cancellationToken)
                    .ConfigureAwait(true);
                break;
            case "oranda.sqlplus.runFile":
                // 인자(경로)가 없으면 스크립트 창에서 고른 파일.
                var path = context.Parameter as string ?? HistoryScriptsCommands.SelectedScriptPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var result = await HistoryScriptsCommands.RunSqlPlusAsync(_services, "@\"" + path + "\"", skipHistory: false, cancellationToken)
                        .ConfigureAwait(true);
                    if (result?.ResultSet is not null)
                    {
                        await result.ResultSet.DisposeAsync().ConfigureAwait(true);
                    }
                }

                break;
        }
    }
}
