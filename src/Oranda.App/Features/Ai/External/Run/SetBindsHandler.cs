using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Ai.External.Run;

/// <summary>
/// <c>set_binds</c>(agent-cli-set-binds 2026-11-23): 탭의 「바인드 변수」 목록에 값을 넣는다. 실행하지 않는다.
/// <list type="bullet">
/// <item>관문은 <c>run_tab</c> 과 같다(<see cref="ExternalRunSupport.Resolve"/>: 탭·접속·허가 안(잠김 아님)·수준 Query·<see cref="AgentGrantPolicy.MayExecute"/>).
/// 값을 넣는 일은 실행 바로 앞 단계라 실행할 수 없는 접속에서는 넣지도 않는다. 접속이 열려 있을 필요는 없다(DB 를 쓰지 않는다).</item>
/// <item><c>base_version</c> 이 지금 판과 다르면 넣지 않고 <c>stale</c>(<c>run_tab</c>·<c>propose_edit</c> 과 같은 상태 글, 종료 0).</item>
/// <item>값 검사·넣기 = <see cref="ExternalBindFill"/>. 맞지 않으면 <c>bind_invalid</c> 로 하나도 넣지 않는다.</item>
/// <item>결과 = <c>{status, version, binds}</c>. <c>binds</c> 는 <c>get_binds</c> 와 같은 모양·같은 가림(값은 행 값 수준에서만, 비밀 이름 <c>***</c>).
/// 탭 글을 읽을 수 없는 허가(운영·클라우드 모델·끔)는 <c>get_tab</c> 과 같은 코드로 거부한다(결정 ③ — <c>bind_invalid</c> 로 이름을 더듬지 못하게).</item>
/// <item>사람이 보는 표시: 바인드 창의 값(그 탭 목록), 탭 띠 「… 가 바인드 값 N개를 채웠습니다 [닫기]」, 메시지 창 「[에이전트] …」.</item>
/// </list>
/// 감사: 게이트가 인자 요약(<c>binds={…}</c>, 100자)을 남긴다 — <c>query</c> 의 <c>binds</c> 와 같다.
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
public sealed class SetBindsHandler(IServiceProvider services) : IAppToolHandler
{
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

    public string ToolName => AppToolNames.SetBinds;

    /// <summary>부품(시험이 바꾼다). 기본은 셸 컨테이너에서 처음 쓸 때.</summary>
    internal ExternalRunParts? Parts { get; set; }

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) => AppToolOutputs.Guard(async () =>
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = invocation.Arguments;
        var tabId = AppToolArgs.RequiredText(args, AppFields.Tab);
        var baseVersion = args.ContainsKey(AppFields.BaseVersion) ? (long?)AppToolArgs.Number(args, AppFields.BaseVersion, 0, 0) : null;
        var binds = ExternalBindFill.Arg(args) ?? throw new AppToolArgumentException(AppFields.Binds, "is required");
        if ((Parts ??= ExternalRunParts.From(_services)) is not { } parts)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NotImplemented, "Setting bind values is not available in this window.");
        }

        return await ExternalRunSupport.OnUiAsync(() => Task.FromResult(Set(parts, invocation, tabId, baseVersion, binds))).ConfigureAwait(false);
    });

    private static AgentToolOutput Set(ExternalRunParts parts, AppToolInvocation invocation, string tabId, long? baseVersion, JsonObject binds)
    {
        var target = ExternalRunSupport.Resolve(parts.Host, invocation.Grant, tabId);
        var output = target.Denial ?? SetOn(parts, invocation, target.Tab!, baseVersion, binds);
        return ExternalRunSupport.WithProfile(output, target.Tab);
    }

    private static AgentToolOutput SetOn(ExternalRunParts parts, AppToolInvocation invocation, SqlEditorViewModel tab, long? baseVersion, JsonObject binds)
    {
        // 결정 ③(2026-11-24): 탭 글을 읽을 수 없는 허가(운영·클라우드 모델 등)에서는 바인드 이름을 더듬어 볼 수 없게 get_tab 과 같은 코드로 거부한다.
        if (ExternalBindFill.RefuseUnreadable(invocation.Grant, tab) is { } unreadable)
        {
            return unreadable;
        }

        if (baseVersion is { } expected && expected != tab.Version)
        {
            return AgentToolOutput.Ok(new JsonObject { ["status"] = AppToolValues.BindStatus.Stale, ["version"] = tab.Version });
        }

        var plan = ExternalBindFill.Plan(tab, binds);
        if (plan.Denial is { } denied)
        {
            return denied;
        }

        var controller = parts.Controller;
        var names = ExternalBindFill.Apply(tab, plan, line => controller.Log.AddMessage(MessageKind.Info, line), invocation.Client);
        if (names.Count > 0)
        {
            var client = ExternalRunSupport.ClientName(invocation.Client);
            ExternalRunSupport.ShowStatus(tab, new AgentBarItem(AgentBarItemKind.Notice, () => ExternalRunText.BindsSet(client, names.Count)));
        }

        var shown = ExternalEditorService.DescribeBinds(new GrantTabAccess(invocation.Grant), tab);
        return AgentToolOutput.Ok(new JsonObject
        {
            ["status"] = AppToolValues.BindStatus.Set,
            ["version"] = tab.Version,
            ["binds"] = GetBindsToolHandler.BindsJson(shown),
        });
    }
}
