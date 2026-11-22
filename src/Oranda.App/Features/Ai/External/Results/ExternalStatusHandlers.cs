using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Update;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Ai.External.Results;

/// <summary>
/// <c>get_status</c>(설계서 §5.3 「상태·접속」): 앱 판·pid·창 이름·만료·클라이언트·허용 접속(별칭·환경·수준·DBMS·연결 여부)·도구 수.
/// 호스트·서비스·사용자 이름은 내지 않는다(허가 사본에 그 값이 없다).
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
public sealed class GetStatusHandler(IServiceProvider services) : IAppToolHandler
{
    public string ToolName => AppToolNames.GetStatus;

    /// <summary>접속에 열린 작업 세션이 있나. 기본은 접속 서비스(등록된 것만)로 본다. 시험이 바꾼다.</summary>
    internal Func<Guid, bool> IsConnected { get; set; } = ConnectedVia(services);

    /// <summary>창 이름(연결 파일의 <c>label</c>과 같은 뜻: 지금 작업 공간 이름). 시험이 바꾼다.</summary>
    internal Func<string> Label { get; set; } = () => SqlEditorHost.Current.ActiveWorkspace?.Name ?? string.Empty;

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) => AppToolOutputs.Guard(async () =>
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var grant = invocation.Grant;
        var label = await ExternalToolSupport.OnUiAsync(Label).ConfigureAwait(false);
        var result = new JsonObject
        {
            ["app_version"] = AppVersion.Installed ?? AppVersion.Assembly,
            ["pid"] = Environment.ProcessId,
            ["label"] = label,
            ["expires_at"] = ExternalToolSupport.Time(grant.ExpiresAt),
            ["clients"] = new JsonArray([.. grant.Clients.Select(c => (JsonNode?)JsonValue.Create(c))]),
            ["connections"] = new JsonArray([.. grant.Connections.Select(c => (JsonNode?)new JsonObject
            {
                ["alias"] = c.Alias,
                ["environment"] = ExternalToolSupport.EnvironmentName(c.Environment),
                ["level"] = ExternalToolSupport.LevelName(c.Level),
                ["dbms"] = ExternalToolSupport.DbmsName(c.Dbms),
                ["connected"] = IsConnected(c.ProfileId),
            })]),
            ["tool_count"] = ExternalToolSupport.ToolCount(grant),
        };
        return AgentToolOutput.Ok(result);
    });

    /// <summary>등록된 접속 서비스로 연결 여부를 본다. 없으면 언제나 거짓(모르는 형을 찾느라 지연 어셈블리를 깨우지 않는다).</summary>
    internal static Func<Guid, bool> ConnectedVia(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return id => services.GetRegisteredOrDefault<IConnectionService>()?.SessionOf(id) is not null;
    }
}

/// <summary>
/// <c>list_connections</c>: 이 허가가 쓸 수 있는 접속(별칭·환경·DBMS·수준·읽기 전용·연결 여부). 목록은 맨 위 객체의 <c>connections</c> 칸에 둔다(계약 §3).
/// <c>current_schema</c> 는 세션이 현재 스키마를 알려 주는 입구가 없어 넣지 않는다(선택 칸).
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
public sealed class ListConnectionsHandler(IServiceProvider services) : IAppToolHandler
{
    public string ToolName => AppToolNames.ListConnections;

    /// <summary>접속에 열린 작업 세션이 있나. 시험이 바꾼다.</summary>
    internal Func<Guid, bool> IsConnected { get; set; } = GetStatusHandler.ConnectedVia(services);

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) => AppToolOutputs.Guard(() =>
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var result = new JsonObject
        {
            ["connections"] = new JsonArray([.. invocation.Grant.Connections.Select(c => (JsonNode?)new JsonObject
            {
                ["alias"] = c.Alias,
                ["environment"] = ExternalToolSupport.EnvironmentName(c.Environment),
                ["dbms"] = ExternalToolSupport.DbmsName(c.Dbms),
                ["level"] = ExternalToolSupport.LevelName(c.Level),
                ["read_only"] = c.ReadOnly,
                ["connected"] = IsConnected(c.ProfileId),
            })]),
        };
        return ValueTask.FromResult(AgentToolOutput.Ok(result));
    });
}
