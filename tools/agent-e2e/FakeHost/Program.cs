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

// 사용법: FakeHost --data <ORANDA_DATA_DIR> --log <기록.jsonl> [--home <가짜 HOME>] [--orandactl <절대 경로>] [--ready <파일>] [--max-seconds N] [--real-editor]
// --home 이 있으면 스킬 설치기(E)로 그 HOME 에 oranda 스킬을 깐다. SIGTERM·SIGINT·시한 초과 때 탭 최종 상태를 기록하고 끝낸다.
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
var profile = new ConnectionProfile("dev-sample", "sample", "localhost/FAKE", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows);

var editor = new FakeEditor(log);
editor.Add("t1", "주문 조회", Tab1);
editor.Add("t2", "고객 목록", Tab2);

// --real-editor: 편집기(C)·상태·결과(E) 도구는 실제 처리자. 실제에 없는 도구만 가짜로 채운다.
using var real = options.RealEditor ? new RealEditor(log, profile.Id, ("주문 조회", Tab1), ("고객 목록", Tab2)) : null;
string[] fakeOnlyWhenReal = [AppToolNames.GetStatus, AppToolNames.ListConnections];
var editorHandlers = real is null
    ? editor.Handlers()
    : [.. real.Handlers(), .. editor.Handlers().Where(h => fakeOnlyWhenReal.Contains(h.ToolName) && real.Handlers().All(r => r.ToolName != h.ToolName))];

var language = new LanguageService();
var services = new HandlerServices();
var tabReader = new TabTextReader(services);
IAppToolHandler[] appHandlers =
[
    .. editorHandlers,
    new CheckSqlHandler(language, new LintService((MetadataLookup?)null), tabReader),
    new FormatSqlHandler(new SqlFormatter(), new NoFormatterProfiles(), new InMemorySettingsStore(), tabReader),
];
services.Handlers = appHandlers;
log.Write("handlers", new JsonObject { ["mode"] = real is null ? "fake-editor" : "real-editor", ["tools"] = new JsonArray([.. appHandlers.Select(h => (JsonNode)(h.GetType().Name + ":" + h.ToolName))]) });

var grants = new AgentGrantStore();
var gate = new AgentGate(new FakeSessions(), new KindExtractor(language), [new FakeDescribe(log), new FakeQuery(log)], new NullAudit(), grants: grants)
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
        Label: "agent-e2e"));
    log.Write("ready", new JsonObject { ["socket"] = socket, ["grant"] = issue.Grant.GrantId, ["pid"] = Environment.ProcessId });
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

namespace Oranda.AgentE2e
{
    internal sealed record Options(string Data, string Log, string? Home, string Orandactl, string? Ready, int MaxSeconds, bool RealEditor)
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
            return new Options(data, log, Get("--home"), ctl, Get("--ready"), int.Parse(Get("--max-seconds") ?? "900", System.Globalization.CultureInfo.InvariantCulture), args.Contains("--real-editor"));
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
        private int _next = 3;

        private sealed class Tab(string id, string title, string text)
        {
            public string Id { get; } = id;

            public string Title { get; } = title;

            public string Text { get; set; } = text;

            public int Version { get; set; } = 1;
        }

        public void Add(string id, string title, string text) => _tabs[id] = new Tab(id, title, text);

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
                    ["dirty"] = false, ["read_only"] = false, ["version"] = t.Version, ["active"] = t.Id == "t1", ["locked"] = false,
                })]),
            }))),
            new Handler(AppToolNames.GetTab, inv => Locked(() =>
            {
                var id = AppToolArgs.Text(inv.Arguments, AppFields.Tab) ?? "t1";
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

    /// <summary><c>describe_object</c> 가짜: SAMPLE.ORDERS·SAMPLE.CUSTOMERS 의 열만 안다.</summary>
    internal sealed class FakeDescribe(EventLog log) : IAgentToolHandler
    {
        private static readonly Dictionary<string, string[]> Columns = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SAMPLE.ORDERS"] = ["ID NUMBER", "CUSTOMER_ID NUMBER", "STATUS VARCHAR2(20)", "ORDERED_AT DATE"],
            ["SAMPLE.CUSTOMERS"] = ["CUSTOMER_ID NUMBER", "NAME VARCHAR2(100)"],
        };

        public string ToolName => AgentToolNames.DescribeObject;

        public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
        {
            var obj = invocation.Resolved.Values.FirstOrDefault();
            var key = obj is null ? (string?)invocation.Arguments["object"] ?? string.Empty : obj.Owner + "." + obj.Name;
            log.Write("describe", new JsonObject { ["object"] = key });
            if (!Columns.TryGetValue(key, out var cols))
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

    /// <summary><c>query</c> 가짜: 관문을 통과한 문장을 기록하고 빈 결과를 준다(DB 없음).</summary>
    internal sealed class FakeQuery(EventLog log) : IAgentToolHandler
    {
        public string ToolName => AppToolNames.Query;

        public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
        {
            log.Write("query", new JsonObject { ["sql"] = (string?)invocation.Arguments["sql"] });
            return ValueTask.FromResult(AgentToolOutput.Ok(new JsonObject
            {
                ["columns"] = new JsonArray(new JsonObject { ["name"] = "ID", ["type"] = "NUMBER" }),
                ["row_values"] = new JsonArray(),
                ["row_count"] = 0,
                ["truncated"] = false,
                ["elapsed_ms"] = 1,
                ["sql_id"] = "fake000000001",
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
            return ValueTask.FromResult(new AgentStatementInfo(kind, false, [], statements.Count));
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
