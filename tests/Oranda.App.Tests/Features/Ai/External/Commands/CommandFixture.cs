using Oranda.App.Features.Ai.External.Commands;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Ai.External.Commands;

internal static class CommandIds
{
    public const string History = "oranda.view.history"; // 바로
    public const string Save = "oranda.file.save"; // 띠 한 번
    public const string Commit = "oranda.txn.commit"; // 거부
    public const string Find = "oranda.grid.find"; // 바로, 활성 조건 gridHasResult
}

internal sealed class Counting(Func<Task>? body = null) : ICommandHandler
{
    public int Runs { get; private set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Runs++;
        if (body is not null)
        {
            await body();
        }
    }
}

/// <summary>명령 도구 시험의 셸: 실제 레지스트리·문맥 키·화면 글, 등급표가 아는 실제 id 의 가짜 처리기, 편집기 호스트.</summary>
internal sealed class CommandFixture
{
    public CommandRegistry Registry { get; } = new([]);

    public ContextKeyService Keys { get; } = new();

    public ShellText Text { get; } = new();

    public Dictionary<string, Counting> Handlers { get; } = new(StringComparer.Ordinal);

    public SqlEditorHost Host { get; } = new();

    public CommandFixture()
    {
        Add(CommandIds.History, placements: ["menu:view/history@10"], aliases: ["oranda.view.oldHistory"]);
        Add(CommandIds.Save, placements: ["menu:file/save@20"]);
        Add(CommandIds.Commit, placements: ["menu:sql/txn/commit@10"]);
        Add(CommandIds.Find, enablement: "gridHasResult", placements: ["context:grid/find@10"]);
    }

    public void Add(string id, string? enablement = null, string[]? placements = null, string[]? aliases = null, Func<Task>? body = null)
    {
        var handler = new Counting(body);
        Handlers[id] = handler;
        Registry.Register(
            new CommandDefinition { Id = id, Title = "Cmd." + id, Category = id.StartsWith("oranda.file.", StringComparison.Ordinal) ? CommandCategory.File : CommandCategory.View, Enablement = enablement, Placements = placements ?? [], Aliases = aliases ?? [] },
            handler);
    }

    public CommandShell Shell => new(Registry, Text, Keys);

    public ListCommandsToolHandler List() => new(new EmptyServices()) { Shell = () => Shell, OnUiThread = body => body() };

    public RunCommandToolHandler Run(TimeSpan? confirmWait = null, Func<Oranda.Core.Ai.Gate.AgentGrantConnection, bool>? mayExecute = null) => new(new EmptyServices())
    {
        Shell = () => Shell,
        OnUiThread = body => body(),
        Editors = () => Host,
        Settle = () => Task.CompletedTask,
        ConfirmWait = confirmWait ?? TimeSpan.FromSeconds(30),
        StartWait = TimeSpan.FromMilliseconds(200),
        MayExecute = mayExecute ?? (_ => true),
    };
}

internal sealed class EmptyServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
