using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Run;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers.External;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>
/// <c>run_tab</c>(계획 agent-cli-phase2 §3.1): 탭 글 그대로·버전 확인·한 문장·가드·출처 Agent·시간 상한·바쁨·사람이 보는 표시.
/// 실행 엔진·세션은 가짜, 실행 경로(<see cref="ExecutionController.RunPreparedAsync"/>)와 결과 창은 실제다.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class RunTabHandlerTests
{
    internal sealed class FakeGuard : IExternalSelectGuard
    {
        public ExternalSelectRefusal Refusal { get; set; }

        public List<string> Checked { get; } = [];

        public ValueTask<ExternalSelectCheck> CheckAsync(ISession db, string sql, ExternalSelectOptions options, CancellationToken ct)
        {
            Checked.Add(sql);
            Assert.True(options.VerifyUserFunctions);
            Assert.True(options.RequireResolvedNames);
            return ValueTask.FromResult(Refusal == ExternalSelectRefusal.None
                ? new ExternalSelectCheck(ExternalSelectRefusal.None, null, null, null)
                : new ExternalSelectCheck(Refusal, Refusal == ExternalSelectRefusal.NotSingleQuery ? "sql_not_single_query" : "sql_unsafe", "no", null));
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    internal sealed class Env : IDisposable
    {
        public Env(string text = "SELECT n FROM sample_orders", ConnectionEnvironment env = ConnectionEnvironment.Development, AiAccessLevel level = AiAccessLevel.Query)
        {
            Rig = new Rig(new ConnectionProfile("dev", "scott", "localhost:1521/FREEPDB1") { Environment = env });
            Tab = Rig.Host.NewDocument(text);
            Tab.ConnectionProfileId = Rig.Profile.Id;
            Rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s, "a", "b", "c"));
            Parts = new ExternalRunParts { Host = Rig.Host, Controller = Rig.Controller, Guard = Guard, OpenFolder = f => { Opened.Add(f); return Task.CompletedTask; } };
            Grant = new AgentGrantView("g1", [new AgentGrantConnection(Rig.Profile.Id, "dev-erp", env, Dbms.Oracle, level)], DateTimeOffset.UnixEpoch, null, ["Claude Code"]);
            Run = new RunTabHandler(new NoServices()) { Parts = Parts };
        }

        public Rig Rig { get; }

        public SqlEditorViewModel Tab { get; }

        public FakeGuard Guard { get; } = new();

        public List<string> Opened { get; } = [];

        public ExternalRunParts Parts { get; set; }

        public AgentGrantView Grant { get; set; }

        public RunTabHandler Run { get; }

        public AppToolInvocation Call(JsonObject args) => new(Grant, "Claude Code", args);

        public JsonObject Args(Action<JsonObject>? more = null)
        {
            var args = new JsonObject { ["tab"] = Tab.TabId.ToString() };
            more?.Invoke(args);
            return args;
        }

        public async Task<AgentToolOutput> RunAsync(Action<JsonObject>? more = null, CancellationToken ct = default) =>
            await Run.ExecuteAsync(Call(Args(more)), ct);

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public Task Runs_the_tab_select_in_its_grid_as_agent_and_shows_it_to_the_person() => Ui.Run(async () =>
    {
        using var env = new Env();

        var output = await env.RunAsync();

        Assert.False(output.IsError, output.ErrorMessage);
        var result = output.Result!;
        Assert.Equal("succeeded", result["status"]!.GetValue<string>());
        Assert.Equal(0, result["result_index"]!.GetValue<int>());
        Assert.Equal("query", result["statement_kind"]!.GetValue<string>());
        Assert.Equal(3, result["fetched"]!.GetValue<long>());
        Assert.False(result["more"]!.GetValue<bool>());
        Assert.NotNull(result["elapsed_ms"]);
        Assert.Null(result["row_values"]); // 행은 돌려주지 않는다(get_result 로 읽는다)
        Assert.Equal(3, output.RowCount);

        Assert.Equal("SELECT n FROM sample_orders", Assert.Single(env.Rig.Exec.Statements).Text);
        Assert.Equal(HistorySource.Agent, Assert.Single(env.Rig.Exec.SingleOptions)!.Source);
        Assert.Equal(["SELECT n FROM sample_orders"], env.Guard.Checked);

        var results = (ResultTabsViewModel)env.Tab.ResultPane!;
        Assert.StartsWith("AI·", results.Tabs[0].Title, StringComparison.Ordinal);
        Assert.Equal("Claude Code 가 이 문장을 실행했습니다 · 3행", env.Tab.AgentBar!.Headline);
        Assert.Equal("닫기", env.Tab.AgentBar.DiscardLabel);
        Assert.Contains(env.Rig.Log.Messages, m => m.Text.StartsWith("[에이전트] Claude Code", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_different_base_version_is_stale_and_runs_nothing() => Ui.Run(async () =>
    {
        using var env = new Env();
        var old = env.Tab.Version;
        env.Tab.Document.Insert(0, " ");

        var output = await env.RunAsync(a => a["base_version"] = old);

        Assert.Equal("stale", output.Result!["status"]!.GetValue<string>());
        Assert.Equal(env.Tab.Version, output.Result["version"]!.GetValue<long>());
        Assert.Empty(env.Rig.Exec.Statements);
        Assert.Empty(env.Guard.Checked);
    });

    [Fact]
    public Task Document_range_needs_one_statement_and_statement_range_runs_the_caret_one() => Ui.Run(async () =>
    {
        using var env = new Env("SELECT 1 FROM dual;\n\nSELECT n FROM sample_orders;");

        var many = await env.RunAsync();
        Assert.Equal("sql_not_single_query", many.ErrorCode);
        Assert.Empty(env.Rig.Exec.Statements);

        env.Tab.CaretOffset = env.Tab.Text.IndexOf("sample_orders", StringComparison.Ordinal);
        var caret = await env.RunAsync(a => a["range"] = "statement");

        Assert.False(caret.IsError, caret.ErrorMessage);
        Assert.Contains("sample_orders", Assert.Single(env.Rig.Exec.Statements).Text, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(ExternalSelectRefusal.Unsafe, "sql_unsafe")]
    [InlineData(ExternalSelectRefusal.UserFunction, "sql_unsafe")]
    [InlineData(ExternalSelectRefusal.NotSingleQuery, "sql_not_single_query")]
    public Task Guard_refusal_runs_nothing(ExternalSelectRefusal refusal, string code) => Ui.Run(async () =>
    {
        using var env = new Env();
        env.Guard.Refusal = refusal;

        var output = await env.RunAsync();

        Assert.Equal(code, output.ErrorCode);
        Assert.Empty(env.Rig.Exec.Statements);
        Assert.Null(env.Tab.AgentBar);
    });

    [Fact]
    public Task Connection_rules_lock_low_level_production_and_tabs_without_connection() => Ui.Run(async () =>
    {
        using var low = new Env(level: AiAccessLevel.Plan);
        Assert.Equal("level", (await low.RunAsync()).ErrorCode);

        using var prod = new Env(env: ConnectionEnvironment.Production);
        var prodOutput = await prod.RunAsync();
        if (AgentGrantPolicy.ProductionRuns)
        {
            Assert.False(prodOutput.IsError, prodOutput.ErrorMessage);
        }
        else
        {
            Assert.Equal("environment", prodOutput.ErrorCode);
            Assert.Empty(prod.Rig.Exec.Statements);
        }

        using var locked = new Env();
        locked.Grant = new AgentGrantView("g1", [], DateTimeOffset.UnixEpoch, null, ["Claude Code"]);
        Assert.Equal("locked", (await locked.RunAsync()).ErrorCode);

        using var bare = new Env();
        bare.Tab.ConnectionProfileId = null;
        Assert.Equal("connection_required", (await bare.RunAsync()).ErrorCode);

        using var closed = new Env();
        closed.Parts = new ExternalRunParts { Host = closed.Rig.Host, Controller = closed.Rig.Controller, Guard = closed.Guard, MissingConnection = _ => closed.Rig.Profile.Id };
        var handler = new RunTabHandler(new NoServices()) { Parts = closed.Parts };
        Assert.Equal("connection", (await handler.ExecuteAsync(closed.Call(closed.Args()), CancellationToken.None)).ErrorCode);

        Assert.Equal("tab_not_found", (await low.Run.ExecuteAsync(low.Call(new JsonObject { ["tab"] = Guid.NewGuid().ToString() }), CancellationToken.None)).ErrorCode);
    });

    [Fact]
    public Task A_busy_session_answers_tab_busy_at_once() => Ui.Run(async () =>
    {
        using var env = new Env();
        var release = new TaskCompletionSource();
        env.Rig.Exec.Handler = async (s, _) =>
        {
            await release.Task;
            return Rig.Query(s, "a");
        };

        var first = env.RunAsync();
        var second = await env.RunAsync();
        release.SetResult();
        var done = await first;

        Assert.Equal("tab_busy", second.ErrorCode);
        Assert.Equal("succeeded", done.Result!["status"]!.GetValue<string>());
        Assert.Single(env.Rig.Exec.Statements);
    });

    [Fact]
    public Task Time_limit_cancels_the_run() => Ui.Run(async () =>
    {
        using var env = new Env();
        env.Rig.Exec.Handler = async (s, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            return new ExecutionResult(s, ExecutionStatus.Cancelled);
        };

        var output = await env.RunAsync(a => a["timeout_s"] = 1);

        Assert.Equal("canceled", output.Result!["status"]!.GetValue<string>());
        Assert.Equal("Claude Code 의 실행을 취소했습니다", env.Tab.AgentBar!.Headline);
    });

    [Fact]
    public Task The_person_can_cancel_from_the_bar() => Ui.Run(async () =>
    {
        using var env = new Env();
        var started = new TaskCompletionSource();
        env.Rig.Exec.Handler = async (s, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            return new ExecutionResult(s, ExecutionStatus.Cancelled);
        };

        var run = env.RunAsync();
        await started.Task;
        var bar = env.Tab.AgentBar!;
        Assert.Equal("Claude Code 가 이 문장을 실행하는 중입니다", bar.Headline);
        Assert.Equal("취소", bar.DiscardLabel);
        bar.DiscardCommand.Execute(null);

        var output = await run;
        Assert.Equal("canceled", output.Result!["status"]!.GetValue<string>());
    });

    [Fact]
    public Task A_failed_statement_reports_the_error_code() => Ui.Run(async () =>
    {
        using var env = new Env();
        env.Rig.Exec.Handler = (s, _) => Task.FromResult(new ExecutionResult(s, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") });

        var output = await env.RunAsync();

        Assert.False(output.IsError, output.ErrorMessage);
        Assert.Equal("failed", output.Result!["status"]!.GetValue<string>());
        Assert.Equal("ORA-00942", output.Result["error"]!["code"]!.GetValue<string>());
        Assert.Equal("Claude Code 가 실행한 문장이 실패했습니다", env.Tab.AgentBar!.Headline);
    });
}
