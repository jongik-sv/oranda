using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Ai.External.Results;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai.External.Commands;

/// <summary>명령 도구가 쓰는 셸 조각: 레지스트리·화면 글·지금 문맥 키. 셸이 안 붙었으면(시험·시작 중) 만들지 않는다.</summary>
internal sealed record CommandShell(ICommandRegistry Registry, IShellText Text, IContextKeys Keys)
{
    public static CommandShell? From(IServiceProvider? services) =>
        services?.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry
        && services.GetService(typeof(IShellText)) is IShellText text
        && services.GetService(typeof(IContextKeys)) is IContextKeys keys
            ? new CommandShell(registry, text, keys)
            : null;
}

/// <summary>두 명령 도구의 공통: 본문을 UI 스레드에서 돌린다(문맥 키·메뉴 명령은 UI 상태다).</summary>
internal abstract class CommandToolHandler(IServiceProvider services) : IAppToolHandler
{
    public abstract string ToolName { get; }

    /// <summary>셸 조각(시험이 바꾼다).</summary>
    internal Func<CommandShell?> Shell { get; init; } = () => CommandShell.From(services);

    /// <summary>본문을 UI 스레드로 넘긴다(시험 = 바로).</summary>
    internal Func<Func<Task<AgentToolOutput>>, Task<AgentToolOutput>> OnUiThread { get; init; } = DefaultOnUiThread;

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return AppToolOutputs.Guard(async () =>
        {
            if (Shell() is not { } shell)
            {
                return AppToolOutputs.Deny(AgentErrorCodes.NotImplemented, CommandsText.NotAvailable);
            }

            return await OnUiThread(() => HandleAsync(invocation, shell, ct)).ConfigureAwait(false);
        });
    }

    protected abstract Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, CommandShell shell, CancellationToken ct);

    private static Task<AgentToolOutput> DefaultOnUiThread(Func<Task<AgentToolOutput>> body) =>
        Application.Current is null || Dispatcher.UIThread.CheckAccess() ? body() : Dispatcher.UIThread.InvokeAsync(body);
}

/// <summary>
/// <c>list_commands</c>(2단계 계획 U2): 레지스트리 명령(기능 게이트가 거른 것)을 <c>{id, title, category, menu_path?, grade, enabled}</c> 로.
/// 제목은 지금 화면 언어, <c>enabled</c> 는 지금 문맥 키로 계산한 활성 조건.
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class ListCommandsToolHandler(IServiceProvider services) : CommandToolHandler(services)
{
    public override string ToolName => AppToolNames.ListCommands;

    protected override Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, CommandShell shell, CancellationToken ct)
    {
        var args = invocation.Arguments;
        CommandCategory? category = null;
        if (AppToolArgs.Text(args, AppFields.Category) is { } categoryText)
        {
            category = CommandCatalog.ParseCategory(categoryText)
                ?? throw new AppToolArgumentException(AppFields.Category, "must be one of " + string.Join(", ", CommandCatalog.CategoryNames));
        }

        CommandGrade? grade = null;
        if (AppToolArgs.Text(args, AppFields.Grade) is { } gradeText)
        {
            grade = CommandGradeNames.FromWire(gradeText.Trim())
                ?? throw new AppToolArgumentException(AppFields.Grade, "must be one of " + string.Join(", ", AppToolValues.CommandGrades.All));
        }

        var commands = new JsonArray();
        foreach (var entry in CommandCatalog.List(shell.Registry, shell.Text, shell.Keys, category, grade))
        {
            var json = new JsonObject
            {
                ["id"] = entry.Id,
                ["title"] = entry.Title,
                ["category"] = CommandCatalog.CategoryName(entry.Category),
            };
            if (entry.MenuPath is { } path)
            {
                json["menu_path"] = path;
            }

            json["grade"] = entry.Grade.ToWire();
            json["enabled"] = entry.Enabled;
            commands.Add(json);
        }

        return Task.FromResult(AgentToolOutput.Ok(new JsonObject { ["commands"] = commands }));
    }
}

/// <summary>
/// <c>run_command</c>(2단계 계획 U3): 등급표로 거르고(거부·모르는 id 는 실행하지 않음), <c>tab</c> 이면 그 탭을 앞으로 가져온 뒤
/// 메뉴와 같은 길로 실행한다. 탭 접속을 쓰는 명령(<see cref="CommandGradeTable.Scope"/>)은 그 접속이 허가 밖이면 거부하고,
/// 데이터 SELECT 를 띄우는 명령은 운영 접속이면 <see cref="AgentGrantPolicy.MayExecute"/> 를 따른다. 「띠 한 번」 은 대상 탭(<c>tab</c>, 없으면 활성 SQL 탭)의 에이전트 띠에서 사람이 받아들여야 실행한다.
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class RunCommandToolHandler(IServiceProvider services) : CommandToolHandler(services)
{
    public override string ToolName => AppToolNames.RunCommand;

    /// <summary>SQL 편집기 호스트(시험이 바꾼다).</summary>
    internal Func<SqlEditorHost> Editors { get; init; } = () => SqlEditorHost.Current;

    /// <summary>띠 기다림(시험이 줄인다).</summary>
    internal TimeSpan ConfirmWait { get; init; } = CommandConfirmBar.DefaultWait;

    /// <summary>실행이 끝나기를 기다리는 시간(시험이 바꾼다).</summary>
    internal TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>탭을 앞으로 가져온 뒤 셸이 문맥 키를 다시 계산할 틈(앱 = 디스패처 한 바퀴, 시험 = 바로).</summary>
    internal Func<Task> Settle { get; init; } = DefaultSettle;

    protected override async Task<AgentToolOutput> HandleAsync(AppToolInvocation invocation, CommandShell shell, CancellationToken ct)
    {
        var args = invocation.Arguments;
        var id = AppToolArgs.RequiredText(args, AppFields.Id).Trim();
        var tabText = AppToolArgs.Text(args, AppFields.Tab);
        var runner = new AgentCommandRunner(shell.Registry, shell.Keys) { StartWait = StartWait };

        // 모르는·거부 명령은 탭을 움직이기 전에 돌려보낸다.
        var check = runner.Check(id);
        if (check.Refusal is CommandRefusal.NotFound or CommandRefusal.Denied)
        {
            return Refused(check);
        }

        var host = Editors();
        SqlEditorViewModel? tab;
        if (tabText is not null)
        {
            if (ExternalToolSupport.FindTab(host, tabText.Trim()) is not { } found)
            {
                return AppToolOutputs.TabNotFound(tabText);
            }

            tab = found;
        }
        else
        {
            tab = host.Active;
        }

        // 탭 접속을 쓰는 명령은 그 접속이 이 허가 안이어야 한다. 데이터 SELECT 를 띄우는 명령은 운영이면 실행 정책도 본다. 탭을 움직이기 전에 본다.
        if (Scoped(invocation.Grant, check.Command!.Id, tab) is { } scopeRefusal)
        {
            return scopeRefusal;
        }

        if (tab is not null && !ReferenceEquals(host.Active, tab))
        {
            host.Active = tab;
            await Settle().ConfigureAwait(true);
        }
        if (check.Grade == CommandGrade.Confirm && tab is null)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.TabNotFound, CommandsText.NoTabForConfirm);
        }

        var title = check.Command is { } command ? shell.Text.Title(command) : id;
        var result = await runner.RunAsync(
            id,
            (definition, token) => CommandConfirmBar.AskAsync(tab!.AgentBar ??= new AgentEditorBarViewModel(), invocation.Client, title, definition.Id, ConfirmWait, token),
            ct).ConfigureAwait(true);
        if (result.Refusal != CommandRefusal.None)
        {
            return Refused(result);
        }

        // 성공 호출의 등급·결과는 감사 사유에도 남긴다(G5: rows=N 과 같은 통로, 감사 스키마 그대로).
        var status = StatusName(result.Status);
        var grade = result.Grade.ToWire();
        return AgentToolOutput.Ok(new JsonObject
        {
            ["status"] = status,
            ["grade"] = grade,
        }) with { AuditNote = "grade=" + grade + ",status=" + status };
    }

    /// <summary>운영 접속에서 데이터 SELECT 를 띄워도 되는가(시험이 바꾼다). 기본 = <see cref="AgentGrantPolicy.MayExecute"/>.</summary>
    internal Func<AgentGrantConnection, bool> MayExecute { get; init; } = AgentGrantPolicy.MayExecute;

    private AgentToolOutput? Scoped(AgentGrantView grant, string id, SqlEditorViewModel? tab)
    {
        var scope = CommandGradeTable.Scope(id);
        if (scope == CommandScope.None || tab?.ConnectionProfileId is not { } profileId)
        {
            return null; // 접속과 상관없는 명령, 또는 접속 없는 탭(활성 조건이 거른다)
        }

        if (grant.FindConnection(profileId) is not { } connection)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.CommandDenied, CommandsText.OutsideGrant);
        }

        return scope == CommandScope.DataRead && connection.Environment == ConnectionEnvironment.Production && !MayExecute(connection)
            ? AppToolOutputs.Deny(AgentErrorCodes.CommandDenied, CommandsText.ProductionReadRefused)
            : null;
    }

    internal static string StatusName(CommandRunStatus status) => status switch
    {
        CommandRunStatus.Done => AppToolValues.CommandStatus.Done,
        CommandRunStatus.Confirmed => AppToolValues.CommandStatus.Confirmed,
        CommandRunStatus.Rejected => AppToolValues.CommandStatus.Rejected,
        CommandRunStatus.Timeout => AppToolValues.CommandStatus.Timeout,
        _ => AppToolValues.CommandStatus.Disabled,
    };

    private static AgentToolOutput Refused(CommandRunResult result) => result.Refusal switch
    {
        CommandRefusal.NotFound => AppToolOutputs.Deny(AgentErrorCodes.CommandNotFound, CommandsText.NotFound),
        CommandRefusal.Denied => AppToolOutputs.Deny(AgentErrorCodes.CommandDenied, CommandsText.Denied),
        _ => AppToolOutputs.Deny(AgentErrorCodes.CommandDisabled, CommandsText.Disabled),
    };

    private static Task DefaultSettle() =>
        Application.Current is null ? Task.CompletedTask : Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background).GetTask();
}
