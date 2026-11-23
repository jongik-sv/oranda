using System.Globalization;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Ai.External.Run;
using Oranda.App.Tests.Features.Ai.External;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;

namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// 끝 왕복(agent-cli-set-binds 2026-11-23): 실제 <c>orandactl</c> → 실제 다리·관문·허가 → <b>실제 get_binds·set_binds·run_tab 처리자</b> → 실제 탭 바인드 목록·실행 경로.
/// 실행 엔진·세션·문장 가드만 가짜다. 에이전트가 바인드 값을 사용자에게 치라고 하지 않고 <c>tab run --binds-json</c> 으로 넘기는 흐름,
/// 형식이 맞지 않는 값·잠긴 탭·낡은 판의 거부와 종료 코드, 감사 줄의 <c>binds=…</c>(query 의 binds 와 같은 요약)를 본다.
/// </summary>
[Collection(AgentCliSerial.Name)]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class AgentCliBindsRoundTripTests
{
    private const string Sql = "SELECT order_id FROM sample_orders WHERE order_dt = TO_DATE(:BASE_DT, 'YYYYMMDD') AND dept = :DEPT";

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public Task Agent_fills_binds_with_tab_run_and_gets_refusals_with_exit_codes() => HeadlessHost.Run(async () =>
    {
        var dev = new ConnectionProfile("dev-erp", "scott", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows);
        var other = new ConnectionProfile("other-erp", "scott", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows);
        using var exec = new Rig(dev);
        exec.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s, "1001", "1002"));
        var tab = exec.Host.NewDocument(Sql);
        tab.ConnectionProfileId = dev.Id;
        tab.Title = "주문 조회";
        tab.Binds.Add("DEPT").Type = BindDataType.Number; // 사람이 형식만 골라 둔 줄
        var locked = exec.Host.NewDocument("SELECT 1 FROM dual WHERE x = :X");
        locked.ConnectionProfileId = other.Id; // 허가 밖 접속의 탭

        var parts = new ExternalRunParts { Host = exec.Host, Controller = exec.Controller, Guard = new RunTabHandlerTests.FakeGuard() };
        var service = new ExternalEditorService { HostSource = () => exec.Host };
        IAppToolHandler[] handlers =
        [
            new GetBindsToolHandler(service),
            new SetBindsHandler(new NoServices()) { Parts = parts },
            new RunTabHandler(new NoServices()) { Parts = parts },
        ];
        await using var rig = new AgentCliRig(handlers, profiles: [dev]);
        var id = tab.TabId.ToString("D");
        var version = tab.Version.ToString(CultureInfo.InvariantCulture);

        // 1) 바인드 읽기(tab binds 는 그대로): 두 이름, 값 없음
        var read = await rig.RunAsync(["tab", "binds", "--tab", id], client: "Claude Code");
        Assert.True(read.ExitCode == 0, read.ToString());
        Assert.Equal(["BASE_DT", "DEPT"], read.Json["binds"]!.AsArray().Select(b => (string?)b!["name"]));
        Assert.All(read.Json["binds"]!.AsArray(), b => Assert.False((bool)b!["has_value"]!));

        // 2) 값 없이 실행하면 막힌다 — 글이 다음 길(--binds-json)을 알려 준다
        var blocked = await rig.RunAsync(["tab", "run", "--tab", id, "--base-version", version], client: "Claude Code");
        Assert.Equal(AgentCliExit.Usage, blocked.ExitCode);
        Assert.Equal(AgentErrorCodes.InvalidArguments, (string?)blocked.Json["code"]);
        Assert.Contains("--binds-json", (string?)blocked.Json["error"], StringComparison.Ordinal);
        Assert.Empty(exec.Exec.Statements);

        // 3) 목록 형식(NUMBER)에 맞지 않는 값: bind_invalid(종료 2), 아무것도 바뀌지 않는다
        var wrong = await rig.RunAsync(["tab", "run", "--tab", id, "--binds-json", """{"BASE_DT":"20261101","DEPT":"sales"}"""], client: "Claude Code");
        Assert.Equal(AgentCliExit.Usage, wrong.ExitCode);
        Assert.Equal(AgentErrorCodes.BindInvalid, (string?)wrong.Json["code"]);
        Assert.Null(tab.Binds.Find("BASE_DT"));
        Assert.Empty(exec.Exec.Statements);

        // 4) 모르는 이름도 bind_invalid
        var unknown = await rig.RunAsync(["tab", "binds", "set", "--tab", id, "--binds-json", """{"BASE_DATE":"20261101"}"""]);
        Assert.Equal(AgentCliExit.Usage, unknown.ExitCode);
        Assert.Equal(AgentErrorCodes.BindInvalid, (string?)unknown.Json["code"]);

        // 5) 잠긴 탭(허가 밖 접속): locked(종료 4)
        var lockedRun = await rig.RunAsync(["tab", "run", "--tab", locked.TabId.ToString("D"), "--binds-json", """{"X":1}"""]);
        Assert.Equal(AgentCliExit.Denied, lockedRun.ExitCode);
        Assert.Equal(AgentErrorCodes.Locked, (string?)lockedRun.Json["code"]);
        Assert.Empty(locked.Binds.Rows);

        // 6) 낡은 판: 사람이 고친 뒤 옛 판으로 부르면 stale(종료 0), 채우지 않는다
        tab.Document.Insert(0, " ");
        var stale = await rig.RunAsync(["tab", "binds", "set", "--tab", id, "--base-version", version, "--binds-json", """{"BASE_DT":"20261101"}"""]);
        Assert.True(stale.ExitCode == 0, stale.ToString());
        Assert.Equal("stale", (string?)stale.Json["status"]);
        Assert.Null(tab.Binds.Find("BASE_DT"));
        version = ((long)stale.Json["version"]!).ToString(CultureInfo.InvariantCulture);

        // 7) 채우기만(set): get_binds 모양으로 돌아오고, 사람이 보는 바인드 목록에 값이 있다
        var set = await rig.RunAsync(["tab", "binds", "set", "--tab", id, "--base-version", version, "--binds-json", """{"BASE_DT":"20261101"}"""], client: "Claude Code");
        Assert.True(set.ExitCode == 0, set.ToString());
        Assert.Equal("set", (string?)set.Json["status"]);
        var baseDt = set.Json["binds"]!.AsArray().Single(b => (string?)b!["name"] == "BASE_DT")!;
        Assert.Equal("varchar2", (string?)baseDt["type"]);
        Assert.Equal("20261101", (string?)baseDt["value"]);
        Assert.Equal("20261101", tab.Binds.Find("BASE_DT")!.Text);
        Assert.Empty(exec.Exec.Statements);

        // 8) 실행 전에 채우고 실행(tab run --binds-json): 탭 그리드에서 에이전트 출처로, 목록 값으로 실행한다
        var run = await rig.RunAsync(["tab", "run", "--tab", id, "--base-version", version, "--binds-json", """{":dept":"1,000"}"""], client: "Claude Code");
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("succeeded", (string?)run.Json["status"]);
        Assert.Equal(2L, (long)run.Json["fetched"]!);
        Assert.Equal("1000", tab.Binds.Find("DEPT")!.Text); // NUMBER 줄: 칸을 떠날 때처럼 쉼표를 뺀다
        Assert.Equal(HistorySource.Agent, Assert.Single(exec.Exec.SingleOptions)!.Source);
        Assert.Equal("20261101", exec.Exec.LastBinds!["BASE_DT"].Value);
        Assert.Equal(1000m, exec.Exec.LastBinds["DEPT"].Value);

        // 감사: 에이전트가 넘긴 값은 query 의 binds 처럼 인자 요약(binds=…)으로 남는다
        Assert.Contains(rig.Audit.Entries, e => e.Tool == "set_binds" && (e.Arguments ?? string.Empty).Contains("binds={\"BASE_DT\":\"20261101\"}", StringComparison.Ordinal));
        Assert.Contains(rig.Audit.Entries, e => e.Tool == "run_tab" && (e.Arguments ?? string.Empty).Contains("binds=", StringComparison.Ordinal) && e.ProfileId == dev.Id);
    });
}
