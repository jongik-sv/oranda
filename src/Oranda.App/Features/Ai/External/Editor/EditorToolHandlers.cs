using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Threading;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Text;

namespace Oranda.App.Features.Ai.External.Editor;

/// <summary>
/// 편집기 도구 처리자 공통(계약 A1 §2): 인자를 <see cref="AppToolArgs"/> 로 읽고, 본문은 UI 스레드에서 <see cref="ExternalEditorService"/> 로 처리한다.
/// 허가는 호출마다 <see cref="GrantTabAccess"/> 로 감싼다(탭이 접속을 정하므로 처리자가 판단한다). DB 를 쓰지 않는다.
/// </summary>
internal abstract class EditorToolHandler(ExternalEditorService service) : IAppToolHandler
{
    protected ExternalEditorService Service { get; } = service ?? throw new ArgumentNullException(nameof(service));

    public abstract string ToolName { get; }

    /// <summary>본문을 UI 스레드로 넘긴다(시험 = 바로). 앱이 없거나 이미 UI 스레드면 바로 부른다.</summary>
    internal Func<Func<Task<AgentToolOutput>>, Task<AgentToolOutput>> OnUiThread { get; init; } = DefaultOnUiThread;

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return AppToolOutputs.Guard(async () =>
        {
            // 인자 읽기는 UI 스레드 밖에서도 되지만 처리와 같은 곳에서 해 둔다(틀린 인자는 Guard 가 invalid_arguments 로).
            return await OnUiThread(() => HandleAsync(invocation, new GrantTabAccess(invocation.Grant), ct)).ConfigureAwait(false);
        });
    }

    protected abstract Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct);

    private static Task<AgentToolOutput> DefaultOnUiThread(Func<Task<AgentToolOutput>> body) =>
        Application.Current is null || Dispatcher.UIThread.CheckAccess() ? body() : Dispatcher.UIThread.InvokeAsync(body);

    /// <summary><c>tab</c> 인자를 탭 id 로. 없으면 null(활성 탭), 글이 id 가 아니면 <see cref="Guid.Empty"/>(없는 탭으로 거부된다).</summary>
    protected static Guid? TabArg(JsonObject args, bool required)
    {
        var text = required ? AppToolArgs.RequiredText(args, AppFields.Tab) : AppToolArgs.Text(args, AppFields.Tab);
        return text is null ? null : Guid.TryParse(text.Trim(), out var id) ? id : Guid.Empty;
    }

    /// <summary>거부 결과를 도구 결과로.</summary>
    protected static AgentToolOutput Deny<T>(ExternalEditorResult<T> result) =>
        AppToolOutputs.Deny(result.Error ?? Oranda.Core.Ai.Protocol.AgentErrorCodes.ToolFailed, result.Message ?? "The editor refused the call.");

    /// <summary>탭 접속의 별칭(허가 안 접속일 때만). 접속 없는 탭·허가 밖이면 null.</summary>
    protected static string? AliasOf(AppToolInvocation invocation, Guid? profileId) =>
        profileId is { } id ? invocation.Grant.FindConnection(id)?.Alias : null;

    protected static JsonObject ProposalJson(ExternalProposalView view)
    {
        var json = new JsonObject { ["proposal_id"] = view.ProposalId, ["status"] = view.StatusText };
        if (view.Version is { } version)
        {
            json["version"] = version;
        }

        if (view.FinalText is { } text)
        {
            json["final_text"] = text;
        }

        return json;
    }
}

/// <summary><c>list_tabs</c>.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class ListTabsToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.ListTabs;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var tabs = new JsonArray();
        foreach (var tab in Service.ListTabs(access, AppToolArgs.Text(invocation.Arguments, AppFields.Workspace)))
        {
            var json = new JsonObject { ["tab_id"] = tab.TabId.ToString(), ["title"] = tab.Title };
            if (!tab.Locked)
            {
                json["workspace"] = tab.Workspace;
                json["kind"] = tab.Kind;
                if (AliasOf(invocation, tab.ConnectionProfileId) is { } alias)
                {
                    json["connection"] = alias;
                }

                json["dirty"] = tab.Dirty;
                json["read_only"] = tab.ReadOnly;
                json["version"] = tab.Version;
                if (tab.FileName is { } file)
                {
                    json["file_name"] = file;
                }
            }

            json["active"] = tab.Active;
            json["locked"] = tab.Locked;
            tabs.Add(json);
        }

        return Task.FromResult(AgentToolOutput.Ok(new JsonObject { ["tabs"] = tabs }));
    }
}

/// <summary><c>get_tab</c>.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class GetTabToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.GetTab;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var args = invocation.Arguments;
        var scope = (AppToolArgs.Text(args, AppFields.Scope) ?? AppToolValues.Scopes.Document).Trim().ToLowerInvariant() switch
        {
            AppToolValues.Scopes.Document => ExternalTabScope.Document,
            AppToolValues.Scopes.Selection => ExternalTabScope.Selection,
            AppToolValues.Scopes.Statement => ExternalTabScope.Statement,
            _ => throw new AppToolArgumentException(AppFields.Scope, "must be document, selection or statement"),
        };
        var maxChars = AppToolArgs.Number(args, AppFields.MaxChars, ExternalEditorService.DefaultChars, 1, ExternalEditorService.MaxChars);
        var result = Service.GetTab(access, TabArg(args, required: false), scope, maxChars);
        if (!result.IsOk)
        {
            return Task.FromResult(Deny(result));
        }

        var tab = result.Value!;
        var json = new JsonObject
        {
            ["tab_id"] = tab.TabId.ToString(),
            ["version"] = tab.Version,
            ["text"] = tab.Text,
            ["truncated"] = tab.Truncated,
            ["range"] = new JsonObject { ["start"] = tab.Range.Start, ["length"] = tab.Range.Length },
            ["caret"] = new JsonObject { ["offset"] = tab.Caret.Offset, ["line"] = tab.Caret.Line, ["col"] = tab.Caret.Col },
            ["selection"] = new JsonObject { ["start"] = tab.Selection.Start, ["length"] = tab.Selection.Length, ["block"] = tab.Selection.Block },
        };
        if (tab.Statement is { } statement)
        {
            json["statement"] = new JsonObject { ["start"] = statement.Start, ["length"] = statement.Length, ["kind"] = statement.Kind };
        }

        if (AliasOf(invocation, tab.ConnectionProfileId) is { } alias)
        {
            json["connection"] = alias;
        }

        json["binds"] = new JsonArray([.. tab.Binds.Select(b => (JsonNode?)JsonValue.Create(b))]);
        return Task.FromResult(AgentToolOutput.Ok(json));
    }
}

/// <summary><c>focus_tab</c>.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class FocusTabToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.FocusTab;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var args = invocation.Arguments;
        var tabId = TabArg(args, required: true)!.Value;
        int? line = args.ContainsKey(AppFields.Line) && args[AppFields.Line] is not null ? AppToolArgs.Number(args, AppFields.Line, 1, 1) : null;
        TextRange? range = null;
        if (AppToolArgs.Text(args, AppFields.Range) is not null)
        {
            var parsed = AppToolArgs.Range(args, AppFields.Range, "0:0");
            range = parsed.IsKeyword ? throw new AppToolArgumentException(AppFields.Range, "must be start:length") : new TextRange(parsed.Start, parsed.Length);
        }

        var result = Service.FocusTab(tabId, line, range);
        return Task.FromResult(result.IsOk ? AgentToolOutput.Ok(new JsonObject { ["ok"] = true }) : Deny(result));
    }
}

/// <summary><c>new_tab</c>.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class NewTabToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.NewTab;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var args = invocation.Arguments;
        var text = AppToolArgs.RequiredText(args, AppFields.Text, allowEmpty: true);
        if ((AppToolArgs.Text(args, AppFields.Kind) ?? AppToolValues.TabKinds.Sql) != AppToolValues.TabKinds.Sql)
        {
            throw new AppToolArgumentException(AppFields.Kind, "must be sql");
        }

        var result = Service.NewTab(access, text, AppToolArgs.Text(args, AppFields.Title), invocation.Connection?.ProfileId, invocation.Client);
        return Task.FromResult(result.IsOk
            ? AgentToolOutput.Ok(new JsonObject { ["tab_id"] = result.Value!.TabId.ToString(), ["version"] = result.Value.Version })
            : Deny(result));
    }
}

/// <summary><c>propose_edit</c>: 띠에 세우고 <c>wait_s</c> 만큼 사람의 답을 기다린다.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class ProposeEditToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.ProposeEdit;

    protected override async Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var args = invocation.Arguments;
        var tabId = TabArg(args, required: true)!.Value;
        var baseVersion = AppToolArgs.Number(args, AppFields.BaseVersion, 0, 0);
        var newText = AppToolArgs.RequiredText(args, AppFields.NewText, allowEmpty: true);
        var wait = AppToolArgs.Number(args, AppFields.WaitSeconds, ExternalEditorService.DefaultWaitSeconds, 0, ExternalEditorService.MaxWaitSeconds);
        var parsed = AppToolArgs.Range(args, AppFields.Range, AppToolValues.Scopes.Document);
        var range = parsed.Keyword switch
        {
            null => ExternalRange.Of(parsed.Start, parsed.Length),
            AppToolValues.Scopes.Selection => ExternalRange.Selection,
            AppToolValues.Scopes.Statement => ExternalRange.Statement,
            AppToolValues.Scopes.Caret => ExternalRange.Caret,
            _ => ExternalRange.Document,
        };
        var proposed = Service.Propose(access, new ExternalProposalRequest(tabId, baseVersion, range, newText)
        {
            Title = AppToolArgs.Text(args, AppFields.Title),
            Reason = AppToolArgs.Text(args, AppFields.Reason),
            Client = invocation.Client,
        });
        if (!proposed.IsOk)
        {
            return Deny(proposed);
        }

        var waited = await Service.WaitAsync(proposed.Value!.ProposalId, TimeSpan.FromSeconds(wait), ct).ConfigureAwait(true);
        return waited.IsOk ? AgentToolOutput.Ok(ProposalJson(waited.Value!)) : Deny(waited);
    }
}

/// <summary><c>get_proposal</c>.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class GetProposalToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.GetProposal;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var result = Service.GetProposal(AppToolArgs.RequiredText(invocation.Arguments, AppFields.ProposalId));
        return Task.FromResult(result.IsOk ? AgentToolOutput.Ok(ProposalJson(result.Value!)) : Deny(result));
    }
}

/// <summary><c>save_tab</c>: 확인 띠 한 번. 기다림은 제안과 같은 기본 120초.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class SaveTabToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.SaveTab;

    /// <summary>사람의 답을 기다리는 시간.</summary>
    internal TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(ExternalEditorService.DefaultWaitSeconds);

    protected override async Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var tabId = TabArg(invocation.Arguments, required: true)!.Value;
        var result = await Service.RequestSaveAsync(access, tabId, invocation.Client, Wait, ct).ConfigureAwait(true);
        if (!result.IsOk)
        {
            return Deny(result);
        }

        var json = new JsonObject { ["status"] = result.Value!.StatusText };
        if (result.Value.FileName is { } file)
        {
            json["file_name"] = file;
        }

        return AgentToolOutput.Ok(json);
    }
}

/// <summary><c>get_binds</c>.</summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class GetBindsToolHandler(ExternalEditorService service) : EditorToolHandler(service)
{
    public override string ToolName => AppToolNames.GetBinds;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, GrantTabAccess access, CancellationToken ct)
    {
        var result = Service.GetBinds(access, TabArg(invocation.Arguments, required: true)!.Value);
        if (!result.IsOk)
        {
            return Task.FromResult(Deny(result));
        }

        var binds = new JsonArray();
        foreach (var bind in result.Value!)
        {
            var json = new JsonObject { ["name"] = bind.Name, ["type"] = bind.Type, ["has_value"] = bind.HasValue };
            if (bind.Value is { } value)
            {
                json["value"] = value;
            }

            binds.Add(json);
        }

        return Task.FromResult(AgentToolOutput.Ok(new JsonObject { ["binds"] = binds }));
    }
}
