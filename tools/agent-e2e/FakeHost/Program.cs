using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.AgentE2e;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Language.Formatting;
using Oranda.Language.Completion;
using Oranda.Language.Lint;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Skills;
using Oranda.Platform.Storage;
using Oranda.Testing;

// 사용법: FakeHost --data <ORANDA_DATA_DIR> --log <기록.jsonl> [--home <가짜 HOME>] [--orandactl <절대 경로>] [--ready <파일>] [--max-seconds N] [--real-editor] [--scenario <fixture.json>]
// --scenario 가 있으면 그 준비 데이터(가짜 탭·표·결과·튜닝 응답·명령 목록)를 쓴다. --home 이 있으면 스킬 설치기(E)로 그 HOME 에 oranda 스킬을 깐다. SIGTERM·SIGINT·시한 초과 때 탭 최종 상태를 기록하고 끝낸다.
var options = Options.Parse(args);
using var log = new EventLog(options.Log);

if (options.Home is { } home)
{
    var installer = new AgentSkillInstaller(home, options.Orandactl);
    foreach (var result in installer.Install(AgentSkillTargets.All))
    {
        log.Write("skill", new JsonObject { ["target"] = result.Target.ToString(), ["path"] = result.FilePath, ["outcome"] = result.Outcome.ToString() });
    }
}

const string Tab1 = "SELECT * FROM SAMPLE.ORDERS WHER id = 1";
const string Tab2 = "SELECT customer_id, name\n  FROM SAMPLE.CUSTOMERS\n ORDER BY name";
var scenario = options.Scenario is { } fixture ? Scenario.Load(fixture) : Scenario.Empty;
// 접속 환경·AI 수준·로컬 모델 허가는 시나리오 connection 칸(기본 = 개발·QueryRows·클라우드 모델, 운영 기본 수준 = 정책 최대 Query). 운영이면 별칭도 prod-sample.
var profile = new ConnectionProfile(scenario.Production ? "prod-sample" : "dev-sample", "sample", "localhost/FAKE", Id: Guid.NewGuid(), AiAccess: scenario.AiAccess ?? (scenario.Production ? AiAccessLevel.Query : AiAccessLevel.QueryRows))
{
    Environment = scenario.Production ? ConnectionEnvironment.Production : ConnectionEnvironment.Development,
};
IReadOnlyList<(string Id, string Title, string Text, bool Active)> tabs = scenario.Tabs.Count > 0
    ? scenario.Tabs
    : [("t1", "주문 조회", Tab1, true), ("t2", "고객 목록", Tab2, false)];

var editor = new FakeEditor(log);
foreach (var tab in tabs)
{
    editor.Add(tab.Id, tab.Title, tab.Text, tab.Active);
    log.Write("initial_tab", new JsonObject { ["tab_id"] = tab.Id, ["title"] = tab.Title, ["text"] = tab.Text });
}

// --real-editor: 편집기(C)·상태·결과(E) 도구는 실제 처리자. 실제에 없는 도구만 가짜로 채운다.
using var real = options.RealEditor ? new RealEditor(log, profile.Id, [.. tabs.Select(t => (t.Title, t.Text))]) : null;
string[] fakeOnlyWhenReal = [AppToolNames.GetStatus, AppToolNames.ListConnections];
var editorHandlers = real is null
    ? editor.Handlers()
    : [.. real.Handlers(), .. editor.Handlers().Where(h => fakeOnlyWhenReal.Contains(h.ToolName) && real.Handlers().All(r => r.ToolName != h.ToolName))];

var language = new LanguageService();
var services = new HandlerServices();
var tabReader = new TabTextReader(services);
// 2단계 새 도구 5개(가짜)와, 가짜 편집기일 때 get_result. C0 계약에 도구가 없으면 게이트가 부르지 않는다.
// --real-editor: 머지된 실제 처리자(명령 2개·tune_start·run_tab·export_result)가 앞선다. 가짜는 실제가 없는 것만.
var realPhase2 = real?.Phase2Handlers(scenario.Commands, profile, scenario.Results, language, new KindExtractor(language)).ToList() ?? [];
List<string> objectNames = ["SAMPLE.ORDERS", "SAMPLE.CUSTOMERS"];
objectNames.AddRange(scenario.Objects.Keys.Where(k => !objectNames.Contains(k, StringComparer.OrdinalIgnoreCase)));
var phase2 = new Phase2Tools(log, editor, language, scenario.Results, scenario.Commands, objectNames);
IAppToolHandler[] phase2Handlers = [.. realPhase2, .. phase2.Handlers(withGetResult: real is null).Where(h => realPhase2.All(r => r.ToolName != h.ToolName))];
IAppToolHandler[] appHandlers =
[
    .. editorHandlers,
    .. phase2Handlers,
    new CheckSqlHandler(language, new LintService((MetadataLookup?)null), tabReader),
    new FormatSqlHandler(new SqlFormatter(), new NoFormatterProfiles(), new InMemorySettingsStore(), tabReader),
];
services.Handlers = appHandlers;
log.Write("handlers", new JsonObject { ["mode"] = real is null ? "fake-editor" : "real-editor", ["tools"] = new JsonArray([.. appHandlers.Select(h => (JsonNode)(h.GetType().Name + ":" + h.ToolName))]) });

var grants = new AgentGrantStore();
var gate = new AgentGate(new FakeSessions(), new KindExtractor(language), [new FakeDescribe(log, scenario.Objects), new FakeQuery(log, scenario.Results), .. AgentTools(log, scenario)], new NullAudit(), grants: grants)
{
    AppHandlers = appHandlers,
};
gate.ExternalToolCompleted += (_, a) => log.Write("call", new JsonObject
{
    ["client"] = a.Client,
    ["connection"] = a.Connection,
    ["tool"] = a.ToolName,
    ["judgement"] = a.Judgement.ToString(),
    ["size"] = a.Size,
    ["note"] = a.Note,
});

await using (var host = new AgentBridgeHost(gate, new AgentRunTokenStore(), new AppDataPaths(options.Data), grants, gate))
{
    var socket = host.Start();
    var issue = grants.Create(new AgentGrantRequest(
        [new AgentGrantTarget(profile, AgentGrantPolicy.DefaultCap(profile.Environment))],
        TimeSpan.FromSeconds(options.MaxSeconds + 60),
        Label: "agent-e2e",
        LocalModel: scenario.LocalModel));
    log.Write("ready", new JsonObject { ["environment"] = profile.Environment.ToString(), ["local_model"] = scenario.LocalModel, ["socket"] = socket, ["grant"] = issue.Grant.GrantId, ["pid"] = Environment.ProcessId });
    if (options.Ready is { } ready)
    {
        await File.WriteAllTextAsync(ready, socket);
    }

    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.MaxSeconds));
    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
    using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
    catch (OperationCanceledException)
    {
        // 끝낼 때다.
    }

    if (real is null)
    {
        editor.Dump();
    }
    else
    {
        real.Dump();
    }
}

grants.Dispose();
return 0;

// 시나리오의 튜닝 도구 응답과 벤치 규칙. describe_object·query 는 위에서 따로 건다.
static IEnumerable<IAgentToolHandler> AgentTools(EventLog log, Scenario scenario)
{
    foreach (var (name, response) in scenario.AgentTools)
    {
        yield return new ScriptedAgentTool(log, name, response);
    }

    if (scenario.Bench is { } bench)
    {
        yield return new FakeBench(log, bench);
    }
}

namespace Oranda.AgentE2e
{
    internal sealed record Options(string Data, string Log, string? Home, string Orandactl, string? Ready, int MaxSeconds, bool RealEditor, string? Scenario)
    {
        public static Options Parse(string[] args)
        {
            string? Get(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            var data = Get("--data") ?? throw new ArgumentException("--data 가 필요합니다.");
            var log = Get("--log") ?? Path.Combine(data, "agent-e2e.jsonl");
            var ctl = Get("--orandactl") ?? "orandactl";
            return new Options(data, log, Get("--home"), ctl, Get("--ready"), int.Parse(Get("--max-seconds") ?? "900", System.Globalization.CultureInfo.InvariantCulture), args.Contains("--real-editor"), Get("--scenario"));
        }
    }

    /// <summary>한 줄에 사건 하나(JSON). 판정 스크립트가 읽는다.</summary>
    internal sealed class EventLog(string path) : IDisposable
    {
        private readonly StreamWriter _writer = new(path, append: true) { AutoFlush = true };

        public void Write(string kind, JsonObject body)
        {
            body["at"] = DateTimeOffset.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            body["kind"] = kind;
            lock (_writer)
            {
                _writer.WriteLine(body.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
        }

        public void Dispose() => _writer.Dispose();
    }

    /// <summary>가짜 편집기: 탭 글·판 번호를 들고, 받은 제안은 판이 맞으면 곧바로 받아들인다(사람이 「받아들이기」를 누른 셈).</summary>
    internal sealed class FakeEditor(EventLog log)
    {
        private readonly Dictionary<string, Tab> _tabs = [];
        private readonly Dictionary<string, JsonObject> _proposals = [];
        private string _active = "t1";
        private int _next = 1;

        private sealed class Tab(string id, string title, string text)
        {
            public string Id { get; } = id;

            public string Title { get; } = title;

            public string Text { get; set; } = text;

            public int Version { get; set; } = 1;
        }

        public void Add(string id, string title, string text, bool active = false)
        {
            lock (_tabs)
            {
                _tabs[id] = new Tab(id, title, text);
                _active = active || _tabs.Count == 1 ? id : _active;
                _next = Math.Max(_next, _tabs.Count + 1);
            }
        }

        /// <summary>탭 글·판 번호(실행·내보내기 가짜가 읽는다).</summary>
        public bool TryRead(string id, out string text, out int version)
        {
            lock (_tabs)
            {
                var found = _tabs.TryGetValue(id, out var tab);
                text = tab?.Text ?? string.Empty;
                version = tab?.Version ?? 0;
                return found;
            }
        }

        public void Dump()
        {
            lock (_tabs)
            {
                foreach (var tab in _tabs.Values)
                {
                    log.Write("final_tab", new JsonObject { ["tab_id"] = tab.Id, ["title"] = tab.Title, ["version"] = tab.Version, ["text"] = tab.Text });
                }
            }
        }

        public IEnumerable<IAppToolHandler> Handlers() =>
        [
            new Handler(AppToolNames.GetStatus, inv => Ok(new JsonObject
            {
                ["app_version"] = "agent-e2e",
                ["label"] = inv.Grant.Label,
                ["clients"] = new JsonArray([.. inv.Grant.Clients.Select(c => (JsonNode)c)]),
                ["connections"] = new JsonArray([.. inv.Grant.Connections.Select(c => (JsonNode)new JsonObject
                {
                    ["alias"] = c.Alias, ["environment"] = c.Environment.ToString(), ["level"] = c.Level.ToString(), ["dbms"] = "oracle", ["connected"] = true,
                })]),
            })),
            new Handler(AppToolNames.ListConnections, inv => Ok(new JsonObject
            {
                ["connections"] = new JsonArray([.. inv.Grant.Connections.Select(c => (JsonNode)new JsonObject
                {
                    ["alias"] = c.Alias, ["environment"] = c.Environment.ToString(), ["dbms"] = "oracle", ["level"] = c.Level.ToString(), ["read_only"] = false, ["connected"] = true,
                })]),
            })),
            new Handler(AppToolNames.ListTabs, inv => Locked(() => Ok(new JsonObject
            {
                ["tabs"] = new JsonArray([.. _tabs.Values.Select(t => (JsonNode)new JsonObject
                {
                    ["tab_id"] = t.Id, ["title"] = t.Title, ["workspace"] = "기본", ["kind"] = "sql", ["connection"] = inv.Grant.Connections[0].Alias,
                    ["dirty"] = false, ["read_only"] = false, ["version"] = t.Version, ["active"] = t.Id == _active, ["locked"] = false,
                })]),
            }))),
            new Handler(AppToolNames.GetTab, inv => Locked(() =>
            {
                var id = AppToolArgs.Text(inv.Arguments, AppFields.Tab) ?? _active;
                if (!_tabs.TryGetValue(id, out var t))
                {
                    return AppToolOutputs.TabNotFound(id);
                }

                return Ok(new JsonObject
                {
                    ["tab_id"] = t.Id, ["version"] = t.Version, ["text"] = t.Text, ["truncated"] = false,
                    ["range"] = new JsonObject { ["start"] = 0, ["length"] = t.Text.Length },
                    ["caret"] = new JsonObject { ["offset"] = 0, ["line"] = 1, ["col"] = 1 },
                    ["selection"] = new JsonObject { ["start"] = 0, ["length"] = 0, ["block"] = false },
                    ["statement"] = new JsonObject { ["start"] = 0, ["length"] = t.Text.Length, ["kind"] = "query" },
                    ["connection"] = inv.Grant.Connections[0].Alias, ["binds"] = new JsonArray(),
                });
            })),
            new Handler(AppToolNames.NewTab, inv => Locked(() =>
            {
                var id = "t" + _next++;
                var text = AppToolArgs.RequiredText(inv.Arguments, AppFields.Text, allowEmpty: true);
                _tabs[id] = new Tab(id, AppToolValues.AiTitlePrefix + (AppToolArgs.Text(inv.Arguments, AppFields.Title) ?? "새 탭"), text);
                log.Write("new_tab", new JsonObject { ["tab_id"] = id, ["text"] = text });
                return Ok(new JsonObject { ["tab_id"] = id, ["version"] = 1 });
            })),
            new Handler(AppToolNames.ProposeEdit, inv => Locked(() => Propose(inv))),
            new Handler(AppToolNames.GetProposal, inv => Locked(() =>
            {
                var id = AppToolArgs.RequiredText(inv.Arguments, AppFields.ProposalId);
                return _proposals.TryGetValue(id, out var p) ? Ok((JsonObject)p.DeepClone()) : AppToolOutputs.Deny("proposal_not_found", "No such proposal.");
            })),
            new Handler(AppToolNames.FocusTab, _ => Ok(new JsonObject { ["ok"] = true })),
        ];

        private AgentToolOutput Propose(AppToolInvocation inv)
        {
            var id = AppToolArgs.RequiredText(inv.Arguments, AppFields.Tab);
            if (!_tabs.TryGetValue(id, out var t))
            {
                return AppToolOutputs.TabNotFound(id);
            }

            var baseVersion = AppToolArgs.Number(inv.Arguments, AppFields.BaseVersion, -1);
            var newText = AppToolArgs.RequiredText(inv.Arguments, AppFields.NewText, allowEmpty: true);
            var range = AppToolArgs.Range(inv.Arguments, AppFields.Range, AppToolValues.Scopes.Document);
            var proposalId = "p" + (_proposals.Count + 1);
            var entry = new JsonObject
            {
                ["tab"] = id, ["base_version"] = baseVersion, ["range"] = range.ToString(), ["new_text"] = newText,
                ["title"] = AppToolArgs.Text(inv.Arguments, AppFields.Title), ["reason"] = AppToolArgs.Text(inv.Arguments, AppFields.Reason),
                ["client"] = inv.Client,
            };
            JsonObject result;
            if (baseVersion != t.Version)
            {
                result = new JsonObject { ["proposal_id"] = proposalId, ["status"] = AppToolValues.ProposalStatus.Stale, ["version"] = t.Version };
            }
            else
            {
                // 범위 낱말은 문서·문장이 같다(가짜 탭은 문장 하나). 위치 범위는 그 부분만 바꾼다.
                t.Text = range.IsKeyword || range.Start + range.Length > t.Text.Length
                    ? newText
                    : string.Concat(t.Text.AsSpan(0, range.Start), newText, t.Text.AsSpan(range.Start + range.Length));
                t.Version++;
                result = new JsonObject
                {
                    ["proposal_id"] = proposalId, ["status"] = AppToolValues.ProposalStatus.Accepted, ["version"] = t.Version, ["final_text"] = t.Text,
                };
            }

            entry["status"] = result["status"]!.DeepClone();
            log.Write("proposal", entry);
            _proposals[proposalId] = result;
            return Ok((JsonObject)result.DeepClone());
        }

        private AgentToolOutput Locked(Func<AgentToolOutput> body)
        {
            lock (_tabs)
            {
                return body();
            }
        }

        private static AgentToolOutput Ok(JsonObject body) => AgentToolOutput.Ok(body);

        private sealed class Handler(string toolName, Func<AppToolInvocation, AgentToolOutput> respond) : IAppToolHandler
        {
            public string ToolName { get; } = toolName;

            public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
                AppToolOutputs.Guard(() => ValueTask.FromResult(respond(invocation)));
        }
    }

    /// <summary><c>describe_object</c> 가짜: SAMPLE.ORDERS·SAMPLE.CUSTOMERS(+시나리오 표)의 열만 안다.</summary>
    internal sealed class FakeDescribe(EventLog log, IReadOnlyDictionary<string, string[]> more) : IAgentToolHandler
    {
        private readonly Dictionary<string, string[]> _columns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SAMPLE.ORDERS"] = ["ID NUMBER", "CUSTOMER_ID NUMBER", "STATUS VARCHAR2(20)", "ORDERED_AT DATE"],
            ["SAMPLE.CUSTOMERS"] = ["CUSTOMER_ID NUMBER", "NAME VARCHAR2(100)"],
        }.Concat(more).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

        public string ToolName => AgentToolNames.DescribeObject;

        public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
        {
            var obj = invocation.Resolved.Values.FirstOrDefault();
            var key = obj is null ? (string?)invocation.Arguments["object"] ?? string.Empty : obj.Owner + "." + obj.Name;
            log.Write("describe", new JsonObject { ["object"] = key });
            if (!_columns.TryGetValue(key, out var cols))
            {
                return ValueTask.FromResult(AgentToolOutput.Error("tool_failed", "Object not found: " + key));
            }

            return ValueTask.FromResult(AgentToolOutput.Ok(new JsonObject
            {
                ["object"] = key,
                ["type"] = "TABLE",
                ["columns"] = new JsonArray([.. cols.Select(c => (JsonNode)new JsonObject { ["name"] = c.Split(' ')[0], ["type"] = c.Split(' ')[1] })]),
            }));
        }
    }

    /// <summary><c>query</c> 가짜: 관문을 통과한 문장을 기록하고, 시나리오 결과가 있으면 그 행(최대 max_rows), 없으면 빈 결과를 준다(DB 없음).</summary>
    internal sealed class FakeQuery(EventLog log, FakeResults results) : IAgentToolHandler
    {
        public string ToolName => AppToolNames.Query;

        public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
        {
            var sql = (string?)invocation.Arguments["sql"] ?? string.Empty;
            log.Write("query", new JsonObject { ["sql"] = sql });
            if (!results.IsEmpty)
            {
                var grid = results.Find(sql);
                var max = Math.Clamp((int?)invocation.Arguments["max_rows"] ?? 50, 1, 200);
                return ValueTask.FromResult(AgentToolOutput.Ok(new JsonObject
                {
                    ["columns"] = grid.ColumnsJson(), ["row_values"] = grid.RowsJson(0, max), ["row_count"] = Math.Min(max, grid.Rows.Count),
                    ["truncated"] = grid.Rows.Count > max, ["elapsed_ms"] = 3, ["sql_id"] = "3b8w1n6d2r9yk",
                }));
            }

            return ValueTask.FromResult(AgentToolOutput.Ok(new JsonObject
            {
                ["columns"] = new JsonArray(new JsonObject { ["name"] = "ID", ["type"] = "NUMBER" }),
                ["row_values"] = new JsonArray(),
                ["row_count"] = 0,
                ["truncated"] = false,
                ["elapsed_ms"] = 1,
                ["sql_id"] = "3b8w1n6d2r9yk",
            }));
        }
    }

    /// <summary>문장 종류를 실제 언어 서비스로 나눠 본다(DB 없음). 이름은 <c>OWNER.NAME</c> 대문자 그대로 푼다.</summary>
    internal sealed class KindExtractor(ILanguageService language) : IStatementReferenceExtractor
    {
        public ValueTask<AgentStatementInfo> AnalyzeAsync(ISession db, string sql, string? defaultSchema, CancellationToken ct)
        {
            var statements = language.Split(sql, SplitOptions.Default);
            var kind = statements.Count > 0 ? statements[0].Kind : StatementKind.Other;
            // 잠금 절·DB 링크는 글자로만 본다(실제 추출기는 DB 가 있어야 한다).
            return ValueTask.FromResult(new AgentStatementInfo(kind, false, [], statements.Count,
                HasLockingClause: System.Text.RegularExpressions.Regex.IsMatch(sql, @"(?i)\bFOR\s+UPDATE\b"),
                HasRemoteReference: System.Text.RegularExpressions.Regex.IsMatch(sql, @"\w@\w")));
        }

        public ValueTask<AgentObjectRef?> ResolveAsync(ISession db, string name, string? defaultSchema, CancellationToken ct)
        {
            var parts = name.Trim().ToUpperInvariant().Split('.');
            return ValueTask.FromResult<AgentObjectRef?>(new AgentObjectRef(parts.Length == 2 ? parts[0] : "SAMPLE", parts[^1], "TABLE"));
        }

        public ValueTask<IReadOnlyList<AgentObjectRef>> GetRelatedAsync(ISession db, IReadOnlyList<AgentObjectRef> objects, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentObjectRef>>([]);
    }

    internal sealed class FakeSessions : IAgentSessionSource
    {
        public AgentSession? Find(string runId) => null;

        public ValueTask<ISession> AcquireDbSessionAsync(AgentSession session, CancellationToken ct) =>
            ValueTask.FromResult<ISession>(new FakeSession(session.Profile));
    }

    internal sealed class NullAudit : IAgentAuditStore
    {
        public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentAuditEntry>>([]);
    }

    internal sealed class NoFormatterProfiles : Oranda.Core.Storage.IFormatterProfileStore
    {
        public Task<IReadOnlyList<Oranda.Core.Formatting.FormatterProfile>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Oranda.Core.Formatting.FormatterProfile>>([]);

        public Task SaveAsync(Oranda.Core.Formatting.FormatterProfile profile, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task ExportAsync(string id, string path, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Formatting.FormatterProfile> ImportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>언어 처리자의 탭 읽기가 get_tab 처리자를 찾는 곳.</summary>
    internal sealed class HandlerServices : IServiceProvider
    {
        public IAppToolHandler[] Handlers { get; set; } = [];

        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<IAppToolHandler>) ? Handlers : null;
    }
}
