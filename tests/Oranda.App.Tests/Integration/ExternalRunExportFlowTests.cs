using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Oranda.App.Features.Ai.External.Run;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.Ai.AgentCli;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Integration;

// 소유: agent-cli-1122 레인 w-it-p2(R5·R6). 앱 한 벌(FlowRig) + 실제 26ai 로 run_tab·export_result 처리자를 앱 조립 그대로 부른다.
// 문장 가드만 따로 본 시험 = Oranda.Oracle.IntegrationTests/Ai/ExternalRunExportIntegrationTests.

/// <summary>
/// R5: 개발 탭 <c>run_tab</c>(출처 Agent·그리드와 같은 fetched/more)·거부 문장이 엔진에 가지 않음·사용자 함수·<c>export_result</c> xlsx 전체 행·<c>file_exists</c>·CURRENT_SCHEMA 를 세션 값으로 검사(m-schema 뒤 확인).
/// R6: 운영 라벨 접속(<see cref="AgentGrantPolicy.ProductionRuns"/>·<see cref="AgentGrantPolicy.LocalModelCoversProduction"/> 켜짐)에서
/// 실제 <see cref="AgentGate"/> 를 지난 <c>query</c>·<c>run_tab</c>·<c>get_result</c>·<c>get_tab</c> — 클라우드 허가와 로컬 모델 허가의 차이와 감사 한 줄.
/// 시험 객체는 <see cref="ItDb"/> 의 임시 계정 안(<c>ORT_AC_</c> 이름)과 CURRENT_SCHEMA 용 두 번째 계정 <c>ORT_AC_RS_…</c>(끝에 지움).
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ExternalRunExportFlowTests(ItDb db) : IClassFixture<ItDb>
{
    private const string Client = "Claude Code";
    private const string Table = "ORT_AC_ORD";
    private const int Rows = 5000;

    private static readonly object SeedLock = new();
    private static bool _seeded;

    /// <summary>표(5,000행, 한 쪽 넘음)·시퀀스·함수 둘을 계정에 한 번만 만든다(앱 밖에서, 자동 커밋).</summary>
    private void Seed()
    {
        lock (SeedLock)
        {
            if (_seeded)
            {
                return;
            }

            db.Exec($"create table {Table} (ord_id number primary key, ord_nm varchar2(30), phone_no varchar2(20))");
            db.Exec($"insert into {Table} select level, 'N' || level, '010-' || level from dual connect by level <= {Rows}");
            db.Exec("create sequence ORT_AC_SQ");
            db.Exec("create table ORT_AC_LOG (n number)");
            db.Exec("create function ORT_AC_FN_PLAIN(x number) return number is begin return x * 2; end;");
            db.Exec("create function ORT_AC_FN_AUTO(x number) return number is pragma autonomous_transaction; begin insert into ORT_AC_LOG values (x); commit; return x; end;");
            _seeded = true;
        }
    }

    private static AgentGrantView DevGrant(ConnectionProfile profile, AiAccessLevel level = AiAccessLevel.Query) =>
        new("g-it", [new AgentGrantConnection(profile.Id, profile.Name, profile.Environment, Dbms.Oracle, level)], DateTimeOffset.UnixEpoch, null, [Client]);

    private static SqlEditorViewModel NewTab(FlowRig rig, ConnectionProfile profile, string text)
    {
        var tab = rig.Host.NewDocument(text);
        tab.ConnectionProfileId = profile.Id;
        return tab;
    }

    private static async Task<AgentToolOutput> RunTabAsync(FlowRig rig, AgentGrantView grant, SqlEditorViewModel tab) =>
        await new RunTabHandler(rig.Container).ExecuteAsync(new AppToolInvocation(grant, Client, new JsonObject { ["tab"] = tab.TabId.ToString() }), default);

    private static async Task<AgentToolOutput> ExportAsync(FlowRig rig, AgentGrantView grant, SqlEditorViewModel tab, string path) =>
        await new ExportResultHandler(rig.Container).ExecuteAsync(
            new AppToolInvocation(grant, Client, new JsonObject { ["tab"] = tab.TabId.ToString(), ["format"] = "xlsx", ["out"] = path }), default);

    private static JsonNode Ok(AgentToolOutput output)
    {
        Assert.False(output.IsError, output.ErrorCode + ": " + output.ErrorMessage + " / " + output.LogDetail);
        return output.Result!;
    }

    private static int ResultCount(SqlEditorViewModel tab) => (tab.ResultPane as ResultTabsViewModel)?.Tabs.Count ?? 0;

    private long Count(string sql) => Convert.ToInt64(db.Scalar(sql), CultureInfo.InvariantCulture);

    [ItDbFact]
    public Task Run_tab_runs_the_dev_select_in_the_grid_as_agent_and_fetched_more_match_the_grid() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        Seed();
        using var rig = new FlowRig(db);
        await rig.StartHistoryRecorderAsync();
        var profile = rig.Profile();
        await rig.ConnectAsync(profile);
        var tab = NewTab(rig, profile, $"SELECT ord_id, ord_nm FROM {Table} ORDER BY ord_id");

        var result = Ok(await RunTabAsync(rig, DevGrant(profile), tab));

        Assert.Equal("succeeded", result["status"]!.GetValue<string>());
        var item = ((ResultTabsViewModel)tab.ResultPane!).Tabs[result["result_index"]!.GetValue<int>()];
        var model = item.Pane!.Model!;
        Assert.Equal(model.RowCount, result["fetched"]!.GetValue<long>());
        Assert.Equal(!model.Source.IsComplete, result["more"]!.GetValue<bool>());
        Assert.True(result["more"]!.GetValue<bool>(), "5,000행은 한 쪽을 넘는다");
        Assert.InRange(result["fetched"]!.GetValue<long>(), 1, Rows - 1);
        Assert.StartsWith("AI·", item.Title, StringComparison.Ordinal);

        var history = await rig.WaitHistoryAsync(rows => rows.Any(r => r.SqlText.Contains(Table, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(HistorySource.Agent, history.First(r => r.SqlText.Contains(Table, StringComparison.OrdinalIgnoreCase)).Source);
    });

    [ItDbFact]
    public Task Refused_statements_never_reach_the_engine_and_a_safe_function_runs_but_an_autonomous_one_is_refused() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        Seed();
        using var rig = new FlowRig(db);
        var profile = rig.Profile();
        await rig.ConnectAsync(profile);
        var grant = DevGrant(profile);
        var logBefore = Count("select count(*) from ORT_AC_LOG");

        (string Sql, string Code)[] refused =
        [
            ($"UPDATE {Table} SET ord_nm = 'X' WHERE ord_id = 1", "sql_not_single_query"),
            ($"SELECT ord_id FROM {Table} WHERE ord_id = 1 FOR UPDATE", "sql_unsafe"),
            ($"SELECT * FROM {Table}@ort_ac_nolink", "sql_unsafe"),
            ("SELECT ORT_AC_SQ.NEXTVAL FROM dual", "sql_unsafe"),
            ("SELECT ORT_AC_FN_AUTO(1) FROM dual", "sql_unsafe"),
        ];
        foreach (var (sql, code) in refused)
        {
            var tab = NewTab(rig, profile, sql);
            var output = await RunTabAsync(rig, grant, tab);
            Assert.True(output.IsError, sql);
            Assert.Equal(code, output.ErrorCode);
            Assert.Equal(0, ResultCount(tab)); // 그리드가 생기지 않았다 = 실행 경로에 들어가지 않았다
        }

        Assert.Equal("N1", db.Text($"select ord_nm from {Table} where ord_id = 1"));
        Assert.Equal(logBefore, Count("select count(*) from ORT_AC_LOG"));
        Assert.Equal(0, Count("select count(*) from user_sequences where sequence_name = 'ORT_AC_SQ' and last_number > 1"));

        var safe = NewTab(rig, profile, "SELECT ORT_AC_FN_PLAIN(21) AS v FROM dual");
        var ran = Ok(await RunTabAsync(rig, grant, safe));
        Assert.Equal("succeeded", ran["status"]!.GetValue<string>());
        Assert.Equal("42", await FlowRig.CellAsync(((ResultTabsViewModel)safe.ResultPane!).Tabs[0]));
    });

    [ItDbFact]
    public Task Export_result_writes_every_row_to_xlsx_without_a_part_file_and_the_same_path_again_is_file_exists() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        Seed();
        using var rig = new FlowRig(db);
        var profile = rig.Profile();
        await rig.ConnectAsync(profile);
        var grant = DevGrant(profile);
        var tab = NewTab(rig, profile, $"SELECT ord_id, ord_nm, phone_no FROM {Table} ORDER BY ord_id");
        Assert.True(Ok(await RunTabAsync(rig, grant, tab))["more"]!.GetValue<bool>());
        var dir = Path.Combine(Path.GetTempPath(), "oranda-itest-acx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "orders.xlsx");

            var written = Ok(await ExportAsync(rig, grant, tab, path));
            var again = await ExportAsync(rig, grant, tab, path);

            Assert.Equal("written", written["status"]!.GetValue<string>());
            Assert.Equal(Count($"select count(*) from {Table}"), written["rows"]!.GetValue<long>());
            Assert.Equal(new FileInfo(path).Length, written["bytes"]!.GetValue<long>());
            Assert.Equal(Rows + 1, SheetRows(path)); // 머리글 + 전체 행
            Assert.Equal([path], Directory.GetFiles(dir)); // .part 가 남지 않았다
            Assert.Equal("file_exists", again.ErrorCode);
            Assert.Equal([path], Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    /// <summary>
    /// CURRENT_SCHEMA(m-schema 42c4e5aa 뒤 = 「닫혔는지 확인」): 사람이 탭에서 <c>ALTER SESSION SET CURRENT_SCHEMA = S</c>(직접 또는 PL/SQL 블록) 를 한 뒤
    /// 에이전트가 같은 탭에서 <c>run_tab</c> 하면 가드가 그 탭 세션의 현재 스키마 S 로 이름을 푼다.
    /// <list type="bullet">
    /// <item>이름 없는 함수 <c>ORT_AC_FN_PLAIN</c> = S 의 자치 트랜잭션 함수 → <c>sql_unsafe</c>, S 의 표에 아무것도 쓰지 않는다
    /// (고치기 전 실측 2026-11-23: 가드는 접속 계정의 안전 함수로 풀어 통과, 실행은 S 함수를 불러 S.ORT_AC_LOG 에 커밋했다).</item>
    /// <item>A·S 에 같은 이름 표 <c>ORT_AC_SAME</c> → 실행 결과가 S 의 값. S 에 없는 이름(<c>ORT_AC_ORD</c>) → 실행이 ORA-00942 로 실패(가드는 사전에 없는 이름을 거부하지 않는다).</item>
    /// <item>일부만 가져온 <c>run_tab</c> 직후 <c>export_result</c>: 가드가 같은 세션에서 현재 스키마를 다시 읽어도 열린 그리드 커서와 부딪히지 않고 S 의 전체 행을 쓴다.</item>
    /// </list>
    /// </summary>
    [ItDbTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RunTab_checks_objects_in_the_altered_current_schema_also_when_changed_inside_plsql_and_export_follows(bool insidePlsql) => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        Seed();
        var other = "ORT_AC_RS_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        db.Admin($"create user {other} identified by \"{db.Password}\" quota 10M on users");
        var dir = Path.Combine(Path.GetTempPath(), "oranda-itest-acs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            db.Admin($"create table {other}.ORT_AC_LOG (n number)");
            db.Admin($"create function {other}.ORT_AC_FN_PLAIN(x number) return number is pragma autonomous_transaction; begin insert into {other}.ORT_AC_LOG values (x); commit; return x; end;");
            db.Admin($"create table {other}.ORT_AC_SAME (src varchar2(1), n number)");
            db.Admin($"insert into {other}.ORT_AC_SAME select 'S', level from dual connect by level <= 1200");
            db.Admin("commit");
            db.Admin($"grant execute on {other}.ORT_AC_FN_PLAIN to {db.User}");
            db.Admin($"grant select on {other}.ORT_AC_LOG to {db.User}");
            db.Admin($"grant select on {other}.ORT_AC_SAME to {db.User}");
            if (Count("select count(*) from user_tables where table_name = 'ORT_AC_SAME'") == 0)
            {
                db.Exec("create table ORT_AC_SAME (src varchar2(1), n number)");
                db.Exec("insert into ORT_AC_SAME values ('A', 1)");
            }

            using var rig = new FlowRig(db);
            var profile = rig.Profile();
            await rig.ConnectAsync(profile);
            var grant = DevGrant(profile);
            var tab = NewTab(rig, profile, insidePlsql
                ? $"BEGIN EXECUTE IMMEDIATE 'ALTER SESSION SET CURRENT_SCHEMA = {other}'; END;"
                : $"ALTER SESSION SET CURRENT_SCHEMA = {other}");
            tab.Select(0, 0);
            await rig.Run("oranda.exec.runStatement"); // 사람이 한 일

            tab.Document.Text = "SELECT ORT_AC_FN_PLAIN(7) AS v FROM dual";
            var function = await RunTabAsync(rig, grant, tab);
            Assert.Equal("sql_unsafe", function.ErrorCode);
            Assert.Equal(0, Count($"select count(*) from {other}.ORT_AC_LOG"));

            tab.Document.Text = "SELECT src, n FROM ORT_AC_SAME ORDER BY n";
            var same = Ok(await RunTabAsync(rig, grant, tab));
            var item = ((ResultTabsViewModel)tab.ResultPane!).Tabs[same["result_index"]!.GetValue<int>()];
            Assert.Equal("succeeded", same["status"]!.GetValue<string>());
            Assert.Equal("S", await FlowRig.CellAsync(item)); // 검사한 S.ORT_AC_SAME 을 실행도 읽었다
            Assert.True(same["more"]!.GetValue<bool>(), "1,200행은 한 쪽을 넘는다(일부만 가져옴)");

            var path = Path.Combine(dir, "same.xlsx");
            var exported = Ok(await ExportAsync(rig, grant, tab, path));
            Assert.Equal("written", exported["status"]!.GetValue<string>());
            Assert.Equal(1200, exported["rows"]!.GetValue<long>());
            Assert.Equal(1201, SheetRows(path));

            tab.Document.Text = $"SELECT ord_id FROM {Table}";
            var missing = Ok(await RunTabAsync(rig, grant, tab));
            Assert.Equal("failed", missing["status"]!.GetValue<string>());
            Assert.Contains("942", missing["error"]!["code"]?.GetValue<string>() ?? missing["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        finally
        {
            DropUser(other);
            Directory.Delete(dir, recursive: true);
        }
    });

    /// <summary>
    /// R6 클라우드 허가(로컬 모델 아님)·운영 라벨: <c>query</c> 는 실행되지만 행 값이 오지 않고, <c>run_tab</c> 결과를 <c>get_result</c> 로 읽어도 행 값이 없으며,
    /// 실패 글은 <c>***</c>, 운영 탭 글(<c>get_tab</c>)은 거부. 감사: 문장 리터럴이 남지 않고 run_tab 사유에 <c>rows=N</c>.
    /// </summary>
    [ItDbFact]
    public Task Production_cloud_grant_runs_selects_but_withholds_values_masks_errors_refuses_tab_text_and_audits_rows() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        Seed();
        await using var prod = await ProductionAsync(localModel: false);

        var query = await prod.CallAsync(AppToolNames.Query, new JsonObject { ["sql"] = $"SELECT ord_id, phone_no FROM {Table} WHERE ord_nm = 'N7'" });
        var tab = NewTab(prod.Rig, prod.Profile, $"SELECT ord_id, phone_no FROM {Table} WHERE ord_nm = 'N7'");
        var run = await prod.CallAsync(AppToolNames.RunTab, new JsonObject { ["tab"] = tab.TabId.ToString() });
        var read = await prod.CallAsync(AppToolNames.GetResult, new JsonObject { ["tab"] = tab.TabId.ToString() });
        var text = await prod.CallAsync(AppToolNames.GetTab, new JsonObject { ["tab"] = tab.TabId.ToString() });
        var bad = NewTab(prod.Rig, prod.Profile, "SELECT TO_NUMBER('ORT_AC_SECRET') FROM dual");
        var failed = await prod.CallAsync(AppToolNames.RunTab, new JsonObject { ["tab"] = bad.TabId.ToString() });

        var q = Ok(query);
        Assert.Null(q["row_values"]);
        Assert.Equal(1, q["row_count"]!.GetValue<int>());
        Assert.Equal("succeeded", Ok(run)["status"]!.GetValue<string>());
        Assert.Equal(1, Ok(run)["fetched"]!.GetValue<long>());
        Assert.Null(Ok(read)[AgentFields.RowValues]);
        Assert.True(text.Result.IsError, text.Result.Text);
        Assert.Equal("failed", Ok(failed)["status"]!.GetValue<string>());
        Assert.Equal("***", Ok(failed)["error"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain("ORT_AC_SECRET", failed.Result.Text, StringComparison.Ordinal);

        var audit = prod.Audit.Entries;
        var queryAudit = Assert.Single(audit, e => e.Tool == AppToolNames.Query);
        Assert.DoesNotContain("'N7'", queryAudit.Arguments ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("data=local_ai", queryAudit.Arguments ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("rows=1", queryAudit.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(audit, e => e.Tool == AppToolNames.RunTab && (e.Reason ?? string.Empty).Contains("rows=1", StringComparison.Ordinal));
    });

    /// <summary>R6 로컬 모델 허가·운영 라벨: 행 값·민감 열 원래 값·탭 글(리터럴 포함)·실패 글이 보이고, <c>query</c> 감사 인자 요약에 <c>data=local_ai</c>(탭 도구는 아래 감사 틈).</summary>
    [ItDbFact]
    public Task Production_local_model_grant_shows_values_literals_and_tab_text_and_audits_local_ai() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        Seed();
        await using var prod = await ProductionAsync(localModel: true);

        var query = await prod.CallAsync(AppToolNames.Query, new JsonObject { ["sql"] = $"SELECT ord_id, phone_no FROM {Table} WHERE ord_nm = 'N7'" });
        var tab = NewTab(prod.Rig, prod.Profile, $"SELECT ord_id, phone_no FROM {Table} WHERE ord_nm = 'N7'");
        var run = await prod.CallAsync(AppToolNames.RunTab, new JsonObject { ["tab"] = tab.TabId.ToString() });
        var read = await prod.CallAsync(AppToolNames.GetResult, new JsonObject { ["tab"] = tab.TabId.ToString() });
        var text = await prod.CallAsync(AppToolNames.GetTab, new JsonObject { ["tab"] = tab.TabId.ToString() });
        var bad = NewTab(prod.Rig, prod.Profile, "SELECT TO_NUMBER('ORT_AC_SECRET') FROM dual");
        var failed = await prod.CallAsync(AppToolNames.RunTab, new JsonObject { ["tab"] = bad.TabId.ToString() });

        var row = Ok(query)["row_values"]!.AsArray().Single()!.AsArray();
        Assert.Equal("010-7", row[1]!.GetValue<string>());
        Assert.Equal("succeeded", Ok(run)["status"]!.GetValue<string>());
        Assert.Equal("010-7", Ok(read)[AgentFields.RowValues]!.AsArray().Single()![1]!.GetValue<string>());
        Assert.Contains("'N7'", Ok(text)["text"]!.GetValue<string>(), StringComparison.Ordinal); // 운영 탭 글(리터럴 포함)을 로컬 모델은 읽는다
        Assert.Equal("failed", Ok(failed)["status"]!.GetValue<string>());
        Assert.NotEqual("***", Ok(failed)["error"]!["message"]!.GetValue<string>());

        var audit = prod.Audit.Entries;
        Assert.Contains("data=local_ai", Assert.Single(audit, e => e.Tool == AppToolNames.Query).Arguments ?? string.Empty, StringComparison.Ordinal);

        // 감사 틈(현재 동작 고정, w-it-p2 보고): 탭이 접속을 정하는 앱 도구(run_tab·get_result·get_tab)는 게이트가 접속을 모른다(connection 인자 없음).
        // 그래서 감사 줄에 접속(ProfileId)과 data=local_ai 가 없다 — 운영 외부 호출을 접속으로 거를 수 없다. 행 수(rows=N)는 남는다.
        // 고치면(처리자가 쓴 접속을 게이트에 알림) 아래 두 줄을 Equal(profile.Id)·Contains("data=local_ai") 로 바꾼다.
        var runAudit = audit.First(e => e.Tool == AppToolNames.RunTab);
        Assert.Null(runAudit.ProfileId);
        Assert.DoesNotContain("data=local_ai", runAudit.Arguments ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("rows=1", runAudit.Reason ?? string.Empty, StringComparison.Ordinal);
    });

    private static JsonNode Ok(AgentGateResponse response)
    {
        Assert.False(response.Result.IsError, response.Result.Text);
        return JsonNode.Parse(response.Result.Text)!;
    }

    /// <summary>xlsx 첫 시트의 행 수(파일이 zip·XML 로 열리는지도 함께 본다).</summary>
    private static int SheetRows(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var sheet = zip.Entries.First(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal));
        using var stream = sheet.Open();
        return XDocument.Load(stream).Descendants().Count(e => e.Name.LocalName == "row");
    }

    private void DropUser(string user)
    {
        try
        {
            db.Admin($"drop user {user} cascade");
        }
        catch (global::Oracle.ManagedDataAccess.Client.OracleException)
        {
            // 만들다 만 계정은 없을 수 있다.
        }
    }

    private async Task<ProductionRig> ProductionAsync(bool localModel)
    {
        Assert.True(AgentGrantPolicy.ProductionRuns && AgentGrantPolicy.LocalModelCoversProduction, "R6 는 D5 스위치 둘이 켜진 상태를 본다");
        var rig = new FlowRig(db);
        try
        {
            var profile = rig.Profile(ConnectionEnvironment.Production, name: "it-prod") with { AiAccess = AiAccessLevel.Query };
            await rig.ConnectAsync(profile);
            using var credential = new SecureCredential(db.Password);
            var monitor = await rig.Container.GetRequiredService<ISessionManager>().OpenAsync(profile, credential, SessionPurpose.Monitor, default);
            return new ProductionRig(rig, profile, monitor, localModel);
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    /// <summary>운영 접속 앱 한 벌 + 실제 허가 저장소·게이트(처리자·추출기·가림은 앱 조립 것). DB 도구 세션 = 따로 연 모니터 세션.</summary>
    private sealed class ProductionRig : IAsyncDisposable
    {
        private readonly ISession _monitor;
        private readonly AgentGrantStore _grants = new();
        private readonly AgentGate _gate;

        public ProductionRig(FlowRig rig, ConnectionProfile profile, ISession monitor, bool localModel)
        {
            Rig = rig;
            Profile = profile;
            _monitor = monitor;
            var c = rig.Container;
            _grants.AttachWriter(new NoEndpointFile()); // 연결 파일은 쓰지 않는다(다리 없이 게이트만 부른다)
            Grant = _grants.Create(new AgentGrantRequest(
                [new AgentGrantTarget(profile, AgentGrantPolicy.DefaultCap(profile.Environment))], AgentGrantPolicy.DefaultLifetime, LocalModel: localModel)).Grant;
            _gate = new AgentGate(
                new MonitorSessions(monitor),
                c.GetRequiredService<IStatementReferenceExtractor>(),
                c.GetRequiredService<IEnumerable<IAgentToolHandler>>(),
                Audit,
                masker: c.GetService<ISqlLiteralMasker>(),
                userFunctions: c.GetService<IUserFunctionSafetyChecker>(),
                grants: _grants,
                services: c);
        }

        public FlowRig Rig { get; }

        public ConnectionProfile Profile { get; }

        public AgentGrantView Grant { get; }

        public MemoryAudit Audit { get; } = new();

        public Task<AgentGateResponse> CallAsync(string tool, JsonObject args) => _gate.CallToolAsync(Grant, Client, tool, args, default);

        public async ValueTask DisposeAsync()
        {
            _grants.Dispose();
            await _monitor.DisposeAsync();
            Rig.Dispose();
        }
    }

    private sealed class NoEndpointFile : IAgentEndpointWriter
    {
        public void Publish(AgentGrantIssue issue)
        {
        }

        public void Update(AgentGrantView grant)
        {
        }

        public void Remove(AgentGrantView grant)
        {
        }
    }

    private sealed class MonitorSessions(ISession monitor) : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) => ValueTask.FromResult(monitor);
    }
}
