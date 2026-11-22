using System.Text.Json.Nodes;
using Avalonia;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Ai.External.Editor;

/// <summary>
/// <c>reveal_object</c>(화면 등급, 최소 수준 Metadata 는 다리가 본다): 그 접속의 열린 작업 세션으로 이름을 풀고(동의어·현재 스키마),
/// 스키마 브라우저 트리에서 고른다. <c>open</c> 이면 객체를 연다. 접속이 Oranda 에 열려 있지 않으면 거부한다(밖에서 새 세션을 열지 않는다).
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class RevealObjectToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.RevealObject;

    /// <summary>앱 서비스(시험이 바꾼다).</summary>
    internal Func<IServiceProvider?> Services { get; init; } = () => (Application.Current as App)?.Services;

    /// <summary>트리 고르기·열기(시험이 바꾼다). 기본 = 앱 서비스로 만든 것.</summary>
    internal Func<IServiceProvider, ExternalObjectRevealer> Revealer { get; init; } = services => new ExternalObjectRevealer(services);

    protected override async Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var name = AppToolArgs.RequiredText(invocation.Arguments, AgentFields.ObjectName).Trim();
        var open = AppToolArgs.Flag(invocation.Arguments, AppFields.Open, fallback: false);
        if (invocation.Connection is not { } connection)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.ConnectionRequired, "Give connection (an alias from list_connections).");
        }

        if (Services() is not { } services)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NotImplemented, "The schema browser is not available.");
        }

        var session = services.GetRegisteredOrDefault<ISessionManager>()?.Sessions
            .LastOrDefault(s => s.Profile.Id == connection.ProfileId && s.Purpose == SessionPurpose.Work && s.State is not (SessionState.Closed or SessionState.Broken));
        if (session is null)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NotAllowed, ExternalEditorText.NotConnected);
        }

        if (services.GetService(typeof(IStatementReferenceExtractor)) is not IStatementReferenceExtractor names)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NotImplemented, "Name resolution is not available.");
        }

        var resolved = await names.ResolveAsync(session, name, defaultSchema: null, ct).ConfigureAwait(true);
        if (resolved is null)
        {
            return AgentToolOutput.Ok(new JsonObject { ["found"] = false, ["opened"] = false });
        }

        var result = await Revealer(services).RevealAsync(new DbObjectRef(resolved.Owner, resolved.Name, resolved.Kind), session, open, ct).ConfigureAwait(true);
        return AgentToolOutput.Ok(new JsonObject { ["found"] = result.Found, ["opened"] = result.Opened });
    }
}
