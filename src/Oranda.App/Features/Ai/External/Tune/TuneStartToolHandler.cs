using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Threading;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Ai.External.Tune;

/// <summary>
/// <c>tune_start</c>(2단계 F2, 계약 §8.1, E-29): 탭을 앞으로 내고 그 탭의 커서 문장으로 앱 안 AI 튜닝(<see cref="AiTuningLauncher.RunAsync"/>)을 시작한다.
/// 밖의 에이전트가 끝까지 하는 자동 튜닝은 스킬 절차가 맡고, 이 도구는 사람이 Oranda 안에서 이어 볼 때 쓴다.
/// 앱 안 튜닝은 시작 확인 창(모델·제한 시간)을 띄우고 대화 내내 이어지므로 기다리지 않는다: 창을 띄우도록 UI 스레드에 올리고 바로 돌아온다
/// (<c>started</c> = 시작 창을 사람에게 보였다. 확정·취소는 사람이 한다). 탭 글은 밖으로 돌려주지 않으며, 앱 안 AI 는 그 접속의 앱 AI 설정(허용 수준·가림)을 따른다.
/// 수준 확인은 처리자가 한다: 탭 접속이 허가 안이고 수준 Plan 이상.
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class TuneStartToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    /// <summary>결과 <c>chat</c>: 튜닝이 이어지는 도구 창 이름(사람이 보는 이름).</summary>
    public const string ChatWindow = "AI 대화";

    public override string ToolName => AppToolNames.TuneStart;

    /// <summary>앱 서비스(시험이 바꾼다). 메뉴 명령(<c>AiCommandHandler</c>)과 같은 뿌리 서비스여야 튜닝 흐름을 함께 쓴다.</summary>
    internal Func<IServiceProvider?> Services { get; init; } = () => (Application.Current as App)?.Services;

    /// <summary>시작을 올린다(시험이 바꾼다). 기본 = 탭 활성화가 화면에 닿은 뒤(Background 우선순위) 튜닝 시작, 기다리지 않음.</summary>
    internal Action<IServiceProvider> Launch { get; init; } = DefaultLaunch;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var args = invocation.Arguments;
        var tabId = TabArg(args, required: true)!.Value;
        if (AppToolArgs.Text(args, AppFields.Range) is { } range && !string.Equals(range.Trim(), "statement", StringComparison.Ordinal))
        {
            throw new AppToolArgumentException(AppFields.Range, "must be statement (tuning starts from the statement at the caret)");
        }

        var tab = Service.ListTabs(access).FirstOrDefault(t => t.TabId == tabId);
        if (tab is null)
        {
            return Deny(AgentErrorCodes.TabNotFound, ExternalEditorText.TabNotFound);
        }

        if (tab.Locked)
        {
            return Deny(AgentErrorCodes.Locked, ExternalEditorText.Locked);
        }

        if (tab.ConnectionProfileId is not { } profileId)
        {
            return Deny(AgentErrorCodes.ConnectionRequired, TuneStartText.NoConnection);
        }

        if (access.Grant.LevelFor(profileId) < AiAccessLevel.Plan)
        {
            return Deny(AgentErrorCodes.Level, TuneStartText.NeedsPlan);
        }

        if (Services() is not { } services)
        {
            return Deny(AgentErrorCodes.NotImplemented, TuneStartText.Unavailable);
        }

        var focused = Service.FocusTab(tabId);
        if (!focused.IsOk)
        {
            return Task.FromResult(Deny(focused));
        }

        Launch(services);
        return Task.FromResult(AgentToolOutput.Ok(new JsonObject { ["started"] = true, ["chat"] = ChatWindow }));
    }

    private static Task<AgentToolOutput> Deny(string code, string message) => Task.FromResult(AppToolOutputs.Deny(code, message));

    private static void DefaultLaunch(IServiceProvider services) =>
        Dispatcher.UIThread.Post(() => _ = AiTuningLauncher.For(services).RunAsync(CancellationToken.None), DispatcherPriority.Background);
}

/// <summary><c>tune_start</c> 거부 글(영어, 에이전트가 읽는다).</summary>
internal static class TuneStartText
{
    public const string NoConnection = "This tab has no connection. Tuning needs a tab connected to a database.";

    public const string NeedsPlan = "tune_start needs level Plan or higher on the tab's connection.";

    public const string Unavailable = "AI tuning is not available in this Oranda window.";
}
