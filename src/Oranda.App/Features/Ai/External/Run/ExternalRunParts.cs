using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Transfer;
using Oranda.Language.Ai.Handlers.External;

namespace Oranda.App.Features.Ai.External.Run;

/// <summary>
/// 탭 실행·내보내기 처리자가 쓰는 앱 부품. 앱에서는 셸 컨테이너에서 처음 쓸 때 꺼내고(<see cref="From"/>), 시험은 가짜로 채운다.
/// </summary>
internal sealed class ExternalRunParts
{
    public required SqlEditorHost Host { get; init; }

    /// <summary>편집기 실행 경로(사람의 F5 와 같은 그리드·세션·취소·정책 관문).</summary>
    public required ExecutionController Controller { get; init; }

    public required IExternalSelectGuard Guard { get; init; }

    /// <summary>탭에 묶인 접속이 열려 있지 않으면 그 프로필 id(묻는 창을 띄우지 않고 거부하려고). 기본 = 늘 열림.</summary>
    public Func<SqlEditorViewModel, Guid?> MissingConnection { get; init; } = _ => null;

    public IDataTransferService? Transfer { get; init; }

    /// <summary>「폴더 열기」(파일 관리자). 시험이 바꾼다.</summary>
    public Func<string, Task> OpenFolder { get; init; } = OpenFolderAsync;

    /// <summary>셸 컨테이너에서 부품을 꺼낸다. 컨테이너가 아니거나 가드가 없으면 null.</summary>
    public static ExternalRunParts? From(IServiceProvider services)
    {
        if (services is not ServiceContainer container || container.GetService<IExternalSelectGuard>() is not { } guard)
        {
            return null;
        }

        return new ExternalRunParts
        {
            Host = SqlEditorHost.Current,
            Controller = ExecutionCommands.Resolve(container),
            Guard = guard,
            MissingConnection = tab => SessionRouter.Resolve(container).MissingProfileOf(tab),
            Transfer = container.GetService<IDataTransferService>(),
        };
    }

    /// <summary>파일 관리자로 폴더를 연다(.NET 의 셸 실행: Windows 탐색기·macOS open·리눅스 xdg-open).</summary>
    private static Task OpenFolderAsync(string folder)
    {
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>탭을 찾고 실행해도 되는지 본 결과(거부면 <see cref="Denial"/>).</summary>
internal sealed record ExternalRunTarget(SqlEditorViewModel? Tab, AgentGrantConnection? Connection, AgentToolOutput? Denial);

internal static class ExternalRunSupport
{
    /// <summary>UI 스레드에서 비동기 일을 돌린다. 앱이 없으면(시험) 그 자리에서.</summary>
    public static async Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess()
            ? await work().ConfigureAwait(true)
            : await Dispatcher.UIThread.InvokeAsync(work);
    }

    /// <summary>
    /// 공통 관문(계획 §3.1 「접속」): 탭이 있고, 접속이 있고, 허가 안이고(잠기지 않음), 수준 Query 이상이며 그 환경에서 실행해도 된다(<see cref="AgentGrantPolicy.MayExecute"/>).
    /// 운영 허용 판정은 이 정책 함수 하나만 부른다(스위치 한 곳).
    /// </summary>
    public static ExternalRunTarget Resolve(SqlEditorHost host, AgentGrantView grant, string tabId)
    {
        if (Results.ExternalToolSupport.FindTab(host, tabId) is not { } tab)
        {
            return new(null, null, AppToolOutputs.TabNotFound(tabId));
        }

        if (tab.ConnectionProfileId is not { } profileId)
        {
            return new(tab, null, AppToolOutputs.Deny(AgentErrorCodes.ConnectionRequired, ExternalRunText.NoConnection));
        }

        if (grant.FindConnection(profileId) is not { } connection)
        {
            return new(tab, null, AppToolOutputs.Locked());
        }

        if (connection.Level < AiAccessLevel.Query)
        {
            return new(tab, connection, AppToolOutputs.Deny(AgentErrorCodes.Level, ExternalRunText.LevelLow));
        }

        if (!AgentGrantPolicy.MayExecute(connection))
        {
            return new(tab, connection, AppToolOutputs.Deny(AgentErrorCodes.Environment, ExternalRunText.ProductionOff));
        }

        return new(tab, connection, null);
    }

    /// <summary>가드 거부를 도구 글로(<c>run_tab</c>·<c>export_result</c> 말투).</summary>
    public static AgentToolOutput Refusal(ExternalSelectCheck check) => AppToolOutputs.Deny(check.Code ?? AgentErrorCodes.SqlUnsafe, check.Refusal switch
    {
        ExternalSelectRefusal.UserFunction => ExternalRunText.UserFunction,
        ExternalSelectRefusal.Unsafe => ExternalRunText.Unsafe,
        ExternalSelectRefusal.NotSingleQuery => ExternalRunText.NotSingleSelect,
        _ => check.Message ?? ExternalRunText.NotSingleSelect,
    });

    /// <summary>
    /// 가드 조건: 탭 실행은 사용자 함수를 확인해 안전한 것만, 참조는 모두 풀려야 한다. 이름은 그 세션이 실행하는 대로 푼다 —
    /// 가드가 검사 직전에 그 탭 세션의 현재 스키마를 읽는다(<c>ALTER SESSION SET CURRENT_SCHEMA</c>·PL/SQL 블록이 바꾼 것 포함, 못 읽으면 거부).
    /// 미리 푼 이름이 없으므로 <see cref="ExternalSelectOptions.DefaultSchema"/> 는 비운다.
    /// </summary>
    public static ExternalSelectOptions GuardOptions { get; } = new() { VerifyUserFunctions = true, RequireResolvedNames = true };

    /// <summary>클라이언트 이름(없으면 「외부 에이전트」, 지금 언어로).</summary>
    public static string ClientName(string? client) => string.IsNullOrWhiteSpace(client) ? ExternalEditorText.DefaultClient : client.Trim();

    /// <summary>탭의 에이전트 띠(없으면 만든다).</summary>
    public static AgentEditorBarViewModel BarOf(SqlEditorViewModel tab) => tab.AgentBar ??= new AgentEditorBarViewModel();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AgentEditorBarViewModel, AgentBarItem> Status = new();

    /// <summary>
    /// 실행·내보내기 상태 항목을 띠에 세운다. 탭마다 하나만: 앞서 세운 상태(「실행했습니다」 등)는 빼고 새 것으로 바꾼다
    /// (에이전트가 여러 번 실행해도 띠에 끝난 알림이 쌓이지 않게). 제안·저장 요청 항목은 건드리지 않는다.
    /// </summary>
    public static AgentEditorBarViewModel ShowStatus(SqlEditorViewModel tab, AgentBarItem item)
    {
        var bar = BarOf(tab);
        if (Status.TryGetValue(bar, out var previous))
        {
            bar.Remove(previous);
        }

        Status.AddOrUpdate(bar, item);
        bar.Enqueue(item);
        return bar;
    }
}
