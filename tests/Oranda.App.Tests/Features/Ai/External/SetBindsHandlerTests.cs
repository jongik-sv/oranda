using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Run;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.TestSupport;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Language.Ai.Handlers.External;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>
/// <c>set_binds</c>·<c>run_tab</c> 의 <c>binds</c>(agent-cli-set-binds 2026-11-23): 탭 바인드 목록에 값을 넣는 길·목록 형식 검사·모르는 이름·
/// 잠긴 탭·낡은 판·run_tab 과 같은 허가 규칙·실행 값이 목록에서 나오는지. 실행 엔진·세션은 가짜, 바인드 목록·실행 경로는 실제다.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class SetBindsHandlerTests
{
    private const string OneBind = "SELECT n FROM sample_orders WHERE order_dt = :FROM_DT";

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static RunTabHandlerTests.Env Env(string text = OneBind, AiAccessLevel level = AiAccessLevel.QueryRows, ConnectionEnvironment env = ConnectionEnvironment.Development) =>
        new(text, env, level);

    private static Task<AgentToolOutput> SetAsync(RunTabHandlerTests.Env env, JsonObject binds, Action<JsonObject>? more = null)
    {
        var handler = new SetBindsHandler(new NoServices()) { Parts = env.Parts };
        return handler.ExecuteAsync(env.Call(env.Args(a =>
        {
            a["binds"] = binds;
            more?.Invoke(a);
        })), CancellationToken.None).AsTask();
    }

    [Fact]
    public Task Set_binds_fills_the_tab_bind_list_the_person_sees_and_answers_in_get_binds_shape() => Ui.Run(async () =>
    {
        using var env = Env();

        var output = await SetAsync(env, new JsonObject { [":from_dt"] = "20261101" });

        Assert.False(output.IsError, output.ErrorMessage);
        var result = output.Result!;
        Assert.Equal("set", result["status"]!.GetValue<string>());
        Assert.Equal(env.Tab.Version, result["version"]!.GetValue<long>());
        var bind = Assert.Single(result["binds"]!.AsArray())!;
        Assert.Equal("FROM_DT", bind["name"]!.GetValue<string>());
        Assert.Equal("varchar2", bind["type"]!.GetValue<string>()); // 새 줄: 글 = VARCHAR2
        Assert.True(bind["has_value"]!.GetValue<bool>());
        Assert.Equal("20261101", bind["value"]!.GetValue<string>()); // QueryRows 라 값이 보인다
        Assert.Equal(env.Rig.Profile.Id, output.ProfileId);

        var row = env.Tab.Binds.Find("FROM_DT")!;
        Assert.Equal("20261101", row.Text);
        Assert.Equal(BindDataType.Varchar2, row.Type);
        Assert.Empty(env.Rig.Exec.Statements); // 실행하지 않는다
        Assert.Equal("Claude Code 가 바인드 값 1개를 채웠습니다", env.Tab.AgentBar!.Headline);
        Assert.Contains(env.Rig.Log.Messages, m => m.Text.StartsWith("[에이전트] Claude Code 가", StringComparison.Ordinal) && m.Text.Contains(":FROM_DT", StringComparison.Ordinal));
        Assert.DoesNotContain(env.Rig.Log.Messages, m => m.Text.Contains("20261101", StringComparison.Ordinal)); // 값은 바인드 창에만
    });

    [Fact]
    public Task Values_follow_the_type_of_the_existing_row() => Ui.Run(async () =>
    {
        using var env = Env("SELECT n FROM sample_orders WHERE order_dt >= :FROM_DT AND dept = :DEPT AND code = :CODE");
        env.Tab.Binds.Row("FROM_DT").Type = BindDataType.Date;
        env.Tab.Binds.Row("DEPT").Type = BindDataType.Number;

        var output = await SetAsync(env, new JsonObject { ["FROM_DT"] = "20261101", ["DEPT"] = "1,000", ["CODE"] = 7 });

        Assert.False(output.IsError, output.ErrorMessage);
        Assert.Equal("2026-11-01 00:00:00", env.Tab.Binds.Find("FROM_DT")!.Text); // 칸을 떠날 때처럼 날짜 서식으로
        Assert.Equal(BindDataType.Date, env.Tab.Binds.Find("FROM_DT")!.Type); // 형식은 사람이 고른 그대로
        Assert.Equal("1000", env.Tab.Binds.Find("DEPT")!.Text);
        Assert.Equal(BindDataType.Number, env.Tab.Binds.Find("CODE")!.Type); // 새 줄: 숫자 = NUMBER
        Assert.Equal("7", env.Tab.Binds.Find("CODE")!.Text);
    });

    [Fact]
    public Task A_value_that_does_not_fit_the_row_type_is_bind_invalid_and_changes_nothing() => Ui.Run(async () =>
    {
        using var env = Env("SELECT n FROM sample_orders WHERE order_dt >= :FROM_DT AND dept = :DEPT");
        env.Tab.Binds.Row("FROM_DT").Type = BindDataType.Date;
        var dept = env.Tab.Binds.Row("DEPT");
        dept.Type = BindDataType.Number;
        dept.Text = "10";

        var number = await SetAsync(env, new JsonObject { ["FROM_DT"] = "2026-11-01", ["DEPT"] = "ten" });
        var date = await SetAsync(env, new JsonObject { ["FROM_DT"] = "next monday" });

        Assert.Equal("bind_invalid", number.ErrorCode);
        Assert.Contains(":DEPT", number.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("number", number.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("ten", number.ErrorMessage, StringComparison.Ordinal); // 값 글은 싣지 않는다
        Assert.Equal("bind_invalid", date.ErrorCode);
        Assert.Contains("20261123", date.ErrorMessage, StringComparison.Ordinal); // 날짜 꼴 안내
        Assert.Equal(string.Empty, env.Tab.Binds.Find("FROM_DT")!.Text); // 하나도 넣지 않는다(앞의 맞는 값도)
        Assert.Equal("10", dept.Text);
        Assert.Null(env.Tab.AgentBar);
        Assert.Equal(Oranda.Core.Ai.Protocol.AgentCliExit.Usage, Oranda.Core.Ai.Protocol.AgentCliExit.For(number.ErrorCode));
    });

    [Fact]
    public Task Unknown_or_doubled_names_and_out_binds_are_refused() => Ui.Run(async () =>
    {
        using var env = Env();

        var unknown = await SetAsync(env, new JsonObject { ["FROM_DATE"] = "20261101" });
        var twice = await SetAsync(env, new JsonObject { ["FROM_DT"] = "20261101", ["from_dt"] = "20261102" });
        env.Tab.Binds.Row("FROM_DT").Direction = BindDirection.Out;
        var outBind = await SetAsync(env, new JsonObject { ["FROM_DT"] = "20261101" });
        var shape = await SetAsync(env, new JsonObject { ["FROM_DT"] = new JsonArray(1) });

        Assert.Equal("bind_invalid", unknown.ErrorCode);
        Assert.Contains(":FROM_DATE", unknown.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("bind_invalid", twice.ErrorCode);
        Assert.Equal("bind_invalid", outBind.ErrorCode);
        Assert.Equal("invalid_arguments", shape.ErrorCode);
        Assert.Equal(string.Empty, env.Tab.Binds.Find("FROM_DT")!.Text);
        Assert.Single(env.Tab.Binds.Rows);
    });

    [Fact]
    public Task Locked_tabs_stale_versions_and_the_run_tab_connection_rules_set_nothing() => Ui.Run(async () =>
    {
        using var locked = Env();
        locked.Grant = new AgentGrantView("g1", [], DateTimeOffset.UnixEpoch, null, ["Claude Code"]);
        Assert.Equal("locked", (await SetAsync(locked, new JsonObject { ["FROM_DT"] = "20261101" })).ErrorCode);
        Assert.DoesNotContain(locked.Tab.Binds.Rows, r => r.Text.Length > 0);

        using var stale = Env();
        var old = stale.Tab.Version;
        stale.Tab.Document.Insert(0, " ");
        var staleOutput = await SetAsync(stale, new JsonObject { ["FROM_DT"] = "20261101" }, a => a["base_version"] = old);
        Assert.False(staleOutput.IsError, staleOutput.ErrorMessage);
        Assert.Equal("stale", staleOutput.Result!["status"]!.GetValue<string>());
        Assert.Equal(stale.Tab.Version, staleOutput.Result["version"]!.GetValue<long>());
        Assert.DoesNotContain(stale.Tab.Binds.Rows, r => r.Text.Length > 0);

        using var current = Env();
        var ok = await SetAsync(current, new JsonObject { ["FROM_DT"] = "20261101" }, a => a["base_version"] = current.Tab.Version);
        Assert.Equal("set", ok.Result!["status"]!.GetValue<string>());

        using var low = Env(level: AiAccessLevel.Plan);
        Assert.Equal("level", (await SetAsync(low, new JsonObject { ["FROM_DT"] = "20261101" })).ErrorCode);
        Assert.DoesNotContain(low.Tab.Binds.Rows, r => r.Text.Length > 0);

        using var bare = Env();
        bare.Tab.ConnectionProfileId = null;
        Assert.Equal("connection_required", (await SetAsync(bare, new JsonObject { ["FROM_DT"] = "20261101" })).ErrorCode);
    });

    [Fact]
    public Task Query_level_hides_values_and_a_grant_that_cannot_read_the_tab_is_refused_like_get_tab() => Ui.Run(async () =>
    {
        using var query = Env(level: AiAccessLevel.Query);
        var queryOutput = await SetAsync(query, new JsonObject { ["FROM_DT"] = "20261101" });
        Assert.Equal("set", queryOutput.Result!["status"]!.GetValue<string>());
        Assert.Null(queryOutput.Result["binds"]![0]!["value"]); // 행 값 수준이 아니면 값은 돌려주지 않는다
        Assert.Equal("20261101", query.Tab.Binds.Find("FROM_DT")!.Text);

        // 결정 ③: 운영 탭 + 클라우드 모델은 탭 글을 읽을 수 없다 → get_tab 과 같은 코드(environment)로 거부, 모르는 이름(:OTHER)을 더듬어 볼 수 없다.
        using var prod = Env("SELECT n FROM sample_orders WHERE order_dt = :FROM_DT AND secret_code = :OTHER", AiAccessLevel.Query, ConnectionEnvironment.Production);
        var probe = await SetAsync(prod, new JsonObject { ["NOT_THERE"] = "x" });
        var known = await SetAsync(prod, new JsonObject { ["FROM_DT"] = "20261101" });
        Assert.Equal("environment", probe.ErrorCode);
        Assert.Equal("environment", known.ErrorCode); // 있는 이름과 없는 이름이 같은 답
        Assert.Equal(probe.ErrorMessage, known.ErrorMessage);
        Assert.DoesNotContain(prod.Tab.Binds.Rows, r => r.Text.Length > 0);

        var run = await prod.RunAsync(a => a["binds"] = new JsonObject { ["FROM_DT"] = "20261101" });
        Assert.Equal("environment", run.ErrorCode); // run_tab --binds-json 도 같다
        Assert.Empty(prod.Rig.Exec.Statements);
    });

    [Fact]
    public Task Typed_values_set_the_type_of_a_new_or_unchosen_row_and_refuse_a_different_chosen_type() => Ui.Run(async () =>
    {
        // 결정 ①: {"value": …, "type": "varchar2|number|date|timestamp"}.
        using var env = Env("SELECT n FROM sample_orders WHERE d1 = :NEW_DT AND d2 = :BLANK_DT AND d3 = :DATE_DT AND n1 = :NUM");
        env.Tab.Binds.Row("BLANK_DT"); // 실행이 만든 줄: 빈 값·VARCHAR2 = 아직 형식을 고르지 않음
        env.Tab.Binds.Row("DATE_DT").Type = BindDataType.Date;
        var num = env.Tab.Binds.Row("NUM");
        num.Type = BindDataType.Number;
        num.Text = "10";

        var ok = await SetAsync(env, new JsonObject
        {
            ["NEW_DT"] = new JsonObject { ["value"] = "20261101", ["type"] = "date" },
            ["BLANK_DT"] = new JsonObject { ["value"] = "20261102", ["type"] = "DATE" },
            ["DATE_DT"] = new JsonObject { ["value"] = "20261103", ["type"] = "date" },
        });

        Assert.False(ok.IsError, ok.ErrorMessage);
        Assert.Equal((BindDataType.Date, "2026-11-01 00:00:00"), (env.Tab.Binds.Find("NEW_DT")!.Type, env.Tab.Binds.Find("NEW_DT")!.Text));
        Assert.Equal((BindDataType.Date, "2026-11-02 00:00:00"), (env.Tab.Binds.Find("BLANK_DT")!.Type, env.Tab.Binds.Find("BLANK_DT")!.Text));
        Assert.Equal("2026-11-03 00:00:00", env.Tab.Binds.Find("DATE_DT")!.Text);

        var conflict = await SetAsync(env, new JsonObject { ["NUM"] = new JsonObject { ["value"] = "11", ["type"] = "varchar2" } });
        Assert.Equal("bind_invalid", conflict.ErrorCode);
        Assert.Contains("number", conflict.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal((BindDataType.Number, "10"), (num.Type, num.Text)); // 말없이 바꾸지 않는다

        var badType = await SetAsync(env, new JsonObject { ["NUM"] = new JsonObject { ["value"] = "11", ["type"] = "clob" } });
        var extra = await SetAsync(env, new JsonObject { ["NUM"] = new JsonObject { ["value"] = "11", ["kind"] = "x" } });
        Assert.Equal("invalid_arguments", badType.ErrorCode);
        Assert.Equal("invalid_arguments", extra.ErrorCode);

        var untyped = await SetAsync(env, new JsonObject { ["NUM"] = 12 }); // 형식 없이 = 지금 규칙
        Assert.False(untyped.IsError, untyped.ErrorMessage);
        Assert.Equal("12", num.Text);
    });

    [Fact]
    public Task Run_tab_with_a_typed_date_runs_with_a_date_value() => Ui.Run(async () =>
    {
        using var env = Env();

        var output = await env.RunAsync(a => a["binds"] = new JsonObject { ["FROM_DT"] = new JsonObject { ["value"] = "20261101", ["type"] = "date" } });

        Assert.False(output.IsError, output.ErrorMessage);
        var used = env.Rig.Exec.LastBinds!["FROM_DT"];
        Assert.Equal((BindDataType.Date, (object?)new DateTime(2026, 11, 1)), (used.Type, used.Value));
    });

    [Fact]
    public Task Run_tab_with_binds_fills_the_list_then_runs_with_those_values_as_agent() => Ui.Run(async () =>
    {
        using var env = Env();
        env.Tab.Binds.Row("FROM_DT").Type = BindDataType.Date;

        var output = await env.RunAsync(a => a["binds"] = new JsonObject { ["FROM_DT"] = "20261101" });

        Assert.False(output.IsError, output.ErrorMessage);
        Assert.Equal("succeeded", output.Result!["status"]!.GetValue<string>());
        Assert.Equal(OneBind, Assert.Single(env.Rig.Exec.Statements).Text);
        Assert.Equal(HistorySource.Agent, Assert.Single(env.Rig.Exec.SingleOptions)!.Source);
        var used = env.Rig.Exec.LastBinds!["FROM_DT"];
        Assert.Equal(BindDataType.Date, used.Type);
        Assert.Equal(new DateTime(2026, 11, 1), used.Value);
        Assert.Equal("2026-11-01 00:00:00", env.Tab.Binds.Find("FROM_DT")!.Text); // 사람이 보는 목록에 남는다
        Assert.Contains(env.Rig.Log.Messages, m => m.Text.Contains(":FROM_DT", StringComparison.Ordinal));
    });

    [Fact]
    public Task Run_tab_without_binds_still_refuses_binds_missing_from_the_list() => Ui.Run(async () =>
    {
        using var env = Env();

        var output = await env.RunAsync();

        Assert.Equal("invalid_arguments", output.ErrorCode);
        Assert.Contains("--binds-json", output.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(env.Rig.Exec.Statements);
    });

    [Fact]
    public Task Run_tab_sets_nothing_when_binds_are_wrong_stale_or_the_guard_refuses() => Ui.Run(async () =>
    {
        using var wrong = Env();
        wrong.Tab.Binds.Row("FROM_DT").Type = BindDataType.Number;
        var wrongOutput = await wrong.RunAsync(a => a["binds"] = new JsonObject { ["FROM_DT"] = "20261101x" });
        Assert.Equal("bind_invalid", wrongOutput.ErrorCode);
        Assert.Empty(wrong.Rig.Exec.Statements);
        Assert.Empty(wrong.Guard.Checked);
        Assert.Equal(string.Empty, wrong.Tab.Binds.Find("FROM_DT")!.Text);

        using var stale = Env();
        var old = stale.Tab.Version;
        stale.Tab.Document.Insert(0, " ");
        var staleOutput = await stale.RunAsync(a =>
        {
            a["base_version"] = old;
            a["binds"] = new JsonObject { ["FROM_DT"] = "20261101" };
        });
        Assert.Equal("stale", staleOutput.Result!["status"]!.GetValue<string>());
        Assert.DoesNotContain(stale.Tab.Binds.Rows, r => r.Text.Length > 0);

        using var refused = Env();
        refused.Guard.Refusal = ExternalSelectRefusal.Unsafe;
        var refusedOutput = await refused.RunAsync(a => a["binds"] = new JsonObject { ["FROM_DT"] = "20261101" });
        Assert.Equal("sql_unsafe", refusedOutput.ErrorCode);
        Assert.DoesNotContain(refused.Tab.Binds.Rows, r => r.Text.Length > 0); // 실행하지 않을 문장에는 값도 넣지 않는다

        using var locked = Env();
        locked.Grant = new AgentGrantView("g1", [], DateTimeOffset.UnixEpoch, null, ["Claude Code"]);
        Assert.Equal("locked", (await locked.RunAsync(a => a["binds"] = new JsonObject { ["FROM_DT"] = "20261101" })).ErrorCode);
        Assert.DoesNotContain(locked.Tab.Binds.Rows, r => r.Text.Length > 0);
    });
}
