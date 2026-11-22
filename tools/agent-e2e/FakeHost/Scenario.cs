using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Language;
using Oranda.Core.Transfer;
using Oranda.Testing;

namespace Oranda.AgentE2e;

/// <summary>
/// 시나리오 준비 데이터(<c>scenarios/&lt;이름&gt;/fixture.json</c>): 가짜 탭·가짜 표 열·가짜 결과·튜닝 도구 응답·벤치 규칙·가짜 명령 목록.
/// 칸이 없으면 기존 기본값(탭 t1·t2, 표 둘)을 쓴다.
/// </summary>
internal sealed class Scenario
{
    private Scenario(JsonObject root) => Root = root;

    public JsonObject Root { get; }

    public static Scenario Empty { get; } = new(new JsonObject());

    public static Scenario Load(string path) =>
        new(JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("fixture.json 은 객체여야 합니다."));

    /// <summary>준비 탭(id·제목·글·활성). 없으면 빈 목록.</summary>
    public IReadOnlyList<(string Id, string Title, string Text, bool Active)> Tabs =>
        Root["tabs"] is JsonArray tabs
            ? [.. tabs.OfType<JsonObject>().Select(t => ((string)t["id"]!, (string)t["title"]!, (string)t["text"]!, (bool?)t["active"] ?? false))]
            : [];

    /// <summary>표 이름 → 「열 이름 타입」 목록.</summary>
    public IReadOnlyDictionary<string, string[]> Objects =>
        Root["objects"] is JsonObject objects
            ? objects.ToDictionary(p => p.Key, p => p.Value!.AsArray().Select(c => (string)c!).ToArray(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string[]>();

    /// <summary>튜닝 도구 이름 → 그대로 돌려줄 응답.</summary>
    public IReadOnlyDictionary<string, JsonObject> AgentTools =>
        Root["agent_tools"] is JsonObject tools
            ? tools.Where(p => p.Value is JsonObject).ToDictionary(p => p.Key, p => (JsonObject)p.Value!)
            : new Dictionary<string, JsonObject>();

    public JsonObject? Bench => Root["bench"] as JsonObject;

    public JsonArray Commands => Root["commands"] as JsonArray ?? [];

    /// <summary>connection.environment = "Production" 이면 운영 접속.</summary>
    public bool Production => string.Equals((string?)Root["connection"]?["environment"], "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>connection.ai_access = 접속 AI 수준 이름(Off·Metadata·Plan·Query·QueryRows). 없으면 null.</summary>
    public Oranda.Core.Ai.Access.AiAccessLevel? AiAccess =>
        Enum.TryParse<Oranda.Core.Ai.Access.AiAccessLevel>((string?)Root["connection"]?["ai_access"], out var level) ? level : null;

    /// <summary>connection.local_model = true 면 허가에 「로컬 모델(가리지 않음)」.</summary>
    public bool LocalModel => (bool?)Root["connection"]?["local_model"] ?? false;

    public FakeResults Results => new(Root["results"] as JsonArray ?? []);
}

/// <summary>가짜 결과 한 벌: 열과 전체 행(행 값은 글자, NUMBER 열은 숫자로 바꿔 쓴다).</summary>
internal sealed record FakeGrid(IReadOnlyList<ResultColumn> Columns, IReadOnlyList<string?[]> Rows)
{
    public JsonArray ColumnsJson() =>
        [.. Columns.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["type"] = c.DbTypeName })];

    public JsonArray RowsJson(int offset, int limit) =>
        [.. Rows.Skip(offset).Take(limit).Select(r => (JsonNode)new JsonArray([.. r.Select(v => (JsonNode?)JsonValue.Create(v))]))];

    public object? Raw(int column, string? value) =>
        value is not null && Columns[column].Kind == ColumnDataKind.Number && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : value;
}

/// <summary>
/// 문장 글로 가짜 결과를 고른다. 항목의 <c>match</c>(정규식)가 맞는 첫 항목, 없으면 <c>match</c> 없는 첫 항목.
/// <c>generate</c> = {count, row} 면 row 틀의 <c>{n}</c>(1부터, 네 자리)·<c>{rev}</c>(count-n+1)를 채워 count 행을 만든다.
/// </summary>
internal sealed class FakeResults(JsonArray items)
{
    public bool IsEmpty => items.Count == 0;

    public FakeGrid Find(string sql)
    {
        var entries = items.OfType<JsonObject>().ToList();
        var hit = entries.FirstOrDefault(e => e["match"] is JsonValue m && Regex.IsMatch(sql, (string)m!, RegexOptions.None, TimeSpan.FromSeconds(1)))
            ?? entries.FirstOrDefault(e => e["match"] is null);
        if (hit is null)
        {
            return new FakeGrid([new ResultColumn(0, "ID", "NUMBER", ColumnDataKind.Number)], [["1"]]);
        }

        var columns = hit["columns"]!.AsArray().OfType<JsonObject>()
            .Select((c, i) => new ResultColumn(i, (string)c["name"]!, (string)c["type"]!, ((string)c["type"]!).StartsWith("NUMBER", StringComparison.OrdinalIgnoreCase) ? ColumnDataKind.Number : ColumnDataKind.Text))
            .ToList();
        List<string?[]> rows;
        if (hit["generate"] is JsonObject gen)
        {
            var count = (int)gen["count"]!;
            var template = gen["row"]!.AsArray().Select(v => (string?)v).ToArray();
            rows = [.. Enumerable.Range(1, count).Select(n => template.Select(t => t?
                .Replace("{n}", n.ToString("0000", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{rev}", (count - n + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToArray())];
        }
        else
        {
            rows = [.. (hit["rows"] as JsonArray ?? []).OfType<JsonArray>().Select(r => r.Select(v => (string?)v).ToArray())];
        }

        return new FakeGrid(columns, rows);
    }
}

/// <summary>튜닝 도구 가짜: 받은 인자를 기록하고 준비 데이터의 응답을 그대로 준다.</summary>
internal sealed class ScriptedAgentTool(EventLog log, string toolName, JsonObject response) : IAgentToolHandler
{
    public string ToolName { get; } = toolName;

    public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
    {
        log.Write("agent_tool", new JsonObject { ["tool"] = ToolName, ["args"] = invocation.Arguments.DeepClone() });
        return ValueTask.FromResult(AgentToolOutput.Ok(response.DeepClone()));
    }
}

/// <summary>
/// <c>benchmark_queries</c> 가짜: sql 이 없는 변형 = 원래 문장(original_ms), fast_regex 가 맞고 slow_regex 가 안 맞으면 fast_ms, 아니면 slow_ms.
/// </summary>
internal sealed class FakeBench(EventLog log, JsonObject rules) : IAgentToolHandler
{
    public string ToolName => AgentToolNames.BenchmarkQueries;

    public ValueTask<AgentToolOutput> ExecuteAsync(AgentToolInvocation invocation, CancellationToken ct)
    {
        var fast = new Regex((string?)rules["fast_regex"] ?? "$^", RegexOptions.None, TimeSpan.FromSeconds(1));
        var slow = new Regex((string?)rules["slow_regex"] ?? "$^", RegexOptions.None, TimeSpan.FromSeconds(1));
        var logged = new JsonArray();
        var results = new JsonArray();
        foreach (var v in (invocation.Arguments[AgentFields.Variants] as JsonArray ?? []).OfType<JsonObject>())
        {
            var sql = (string?)v["sql"];
            var ms = sql is null ? (int?)rules["original_ms"] ?? 850
                : fast.IsMatch(sql) && !slow.IsMatch(sql) ? (int?)rules["fast_ms"] ?? 12
                : (int?)rules["slow_ms"] ?? 840;
            logged.Add(new JsonObject { ["label"] = (string?)v["label"], ["sql"] = sql, ["elapsed_ms"] = ms });
            results.Add(new JsonObject
            {
                ["label"] = (string?)v["label"], ["elapsed_ms_avg"] = ms, ["buffer_gets"] = ms > 100 ? 31180 : 96, ["physical_reads"] = 0,
                ["rows"] = 42, ["plan_hash_value"] = ms > 100 ? 3421987650L : 1180249311L,
            });
        }

        log.Write("bench", new JsonObject { ["variants"] = logged });
        return ValueTask.FromResult(AgentToolOutput.Ok(new JsonObject { ["repeat"] = (int?)invocation.Arguments["repeat"] ?? 3, ["results"] = results }));
    }
}

/// <summary>
/// 2단계 새 App 도구 5개(<c>run_tab</c>·<c>export_result</c>·<c>tune_start</c>·<c>list_commands</c>·<c>run_command</c>)와 가짜 편집기용 <c>get_result</c>.
/// 도구 이름·인자·값·오류 코드 = C0 계약(AppTools·AgentErrorCodes). 실제 처리자 모드에서 실제 처리자가 있으면 그쪽이 앞선다.
/// </summary>
internal sealed class Phase2Tools(EventLog log, FakeEditor editor, ILanguageService language, FakeResults results, JsonArray commands, IReadOnlyList<string> objects)
{
    // 결과 그리드 첫 페이지(run_tab 은 여기까지만 가져온다).
    private const int GridPage = 50;

    private readonly Dictionary<string, List<FakeGrid>> _grids = [];

    public IEnumerable<IAppToolHandler> Handlers(bool withGetResult)
    {
        IEnumerable<IAppToolHandler> list =
        [
            new Handler(AppToolNames.RunTab, RunTabAsync),
            new Handler(AppToolNames.ExportResult, ExportAsync),
            new Handler(AppToolNames.TuneStart, inv =>
            {
                var tab = AppToolArgs.RequiredText(inv.Arguments, AppFields.Tab);
                if (!editor.TryRead(tab, out _, out _))
                {
                    return Done(AppToolOutputs.TabNotFound(tab));
                }

                log.Write("tune_start", new JsonObject { ["tab"] = tab });
                return Done(AgentToolOutput.Ok(new JsonObject { ["started"] = true, ["chat"] = "AI 튜닝" }));
            }),
            new Handler(AppToolNames.ListCommands, inv =>
            {
                var category = AppToolArgs.Text(inv.Arguments, AppFields.Category);
                var grade = AppToolArgs.Text(inv.Arguments, AppFields.Grade);
                var list = commands.OfType<JsonObject>()
                    .Where(c => category is null || string.Equals((string?)c["category"], category, StringComparison.OrdinalIgnoreCase))
                    .Where(c => grade is null || string.Equals((string?)c["grade"], grade, StringComparison.OrdinalIgnoreCase));
                return Done(AgentToolOutput.Ok(new JsonObject { ["commands"] = new JsonArray([.. list.Select(c => c.DeepClone())]) }));
            }),
            new Handler(AppToolNames.RunCommand, RunCommandAsync),
        ];
        // find_objects 가짜(가짜 편집기·실제 모드 모두): 준비 표 이름에서 % _ 패턴으로 찾는다.
        list = [.. list, new Handler(AppToolNames.FindObjects, FindObjects)];
        return withGetResult ? [.. list, new Handler(AppToolNames.GetResult, GetResultAsync)] : list;
    }

    private ValueTask<AgentToolOutput> FindObjects(AppToolInvocation inv)
    {
        var pattern = AppToolArgs.RequiredText(inv.Arguments, AppFields.NameLike);
        var owner = AppToolArgs.Text(inv.Arguments, AppFields.Owner);
        var rx = new Regex("^" + Regex.Escape(pattern.ToUpperInvariant()).Replace("%", ".*", StringComparison.Ordinal).Replace("_", ".", StringComparison.Ordinal) + "$", RegexOptions.None, TimeSpan.FromSeconds(1));
        var found = objects
            .Select(o => o.Split('.') is [var own, var name] ? (Owner: own, Name: name) : (Owner: "SAMPLE", Name: o))
            .Where(o => rx.IsMatch(o.Name) && (owner is null || string.Equals(o.Owner, owner, StringComparison.OrdinalIgnoreCase)))
            .Select(o => (JsonNode)new JsonObject { ["owner"] = o.Owner, ["name"] = o.Name, ["type"] = "TABLE", ["status"] = "VALID", ["comment"] = null });
        log.Write("find_objects", new JsonObject { ["name_like"] = pattern });
        return Done(AgentToolOutput.Ok(new JsonObject { ["objects"] = new JsonArray([.. found]), ["truncated"] = false }));
    }

    private ValueTask<AgentToolOutput> RunTabAsync(AppToolInvocation inv)
    {
        var tab = AppToolArgs.RequiredText(inv.Arguments, AppFields.Tab);
        if (!editor.TryRead(tab, out var text, out var version))
        {
            return Done(AppToolOutputs.TabNotFound(tab));
        }

        var baseVersion = AppToolArgs.Number(inv.Arguments, AppFields.BaseVersion, -1);
        var range = AppToolArgs.Text(inv.Arguments, AppFields.Range) ?? "document";
        AppToolArgs.Number(inv.Arguments, AppFields.TimeoutSeconds, AppToolValues.DefaultTimeoutSeconds, 1, AppToolValues.MaxTimeoutSeconds);
        var entry = new JsonObject { ["tab"] = tab, ["base_version"] = baseVersion, ["range"] = range, ["text"] = text };
        if (baseVersion >= 0 && baseVersion != version)
        {
            entry["status"] = "stale";
            log.Write("run_tab", entry);
            return Done(AgentToolOutput.Ok(new JsonObject { ["status"] = "stale", ["version"] = version }));
        }

        // §3.1: 한 문장 SELECT/WITH 만. 문장이 둘 이상이면 range=statement 가 있어야 한다(가짜 탭은 커서가 맨 앞 = 첫 문장).
        var statements = language.Split(text, SplitOptions.Default);
        if (statements.Count == 0)
        {
            return Refuse(entry, AgentErrorCodes.NoStatement, "The tab has no statement.");
        }

        if (statements.Count > 1 && !string.Equals(range, "statement", StringComparison.Ordinal))
        {
            return Refuse(entry, AgentErrorCodes.SqlNotSingleQuery, "The tab has more than one statement. Pass range=statement to run the statement at the caret.");
        }

        var statement = statements[0];
        if (statement.Kind != StatementKind.Query)
        {
            return Refuse(entry, AgentErrorCodes.SqlNotSingleQuery, "Only a single SELECT or WITH statement can be run.");
        }

        var grid = results.Find(statement.Text);
        List<FakeGrid> grids;
        lock (_grids)
        {
            grids = _grids.TryGetValue(tab, out var g) ? g : _grids[tab] = [];
            grids.Clear();
            grids.Add(grid);
        }

        var fetched = Math.Min(GridPage, grid.Rows.Count);
        entry["status"] = "succeeded";
        entry["fetched"] = fetched;
        entry["total"] = grid.Rows.Count;
        log.Write("run_tab", entry);
        return Done(AgentToolOutput.Ok(new JsonObject
        {
            ["status"] = "succeeded", ["result_index"] = 0, ["statement_kind"] = "query", ["fetched"] = fetched,
            ["more"] = grid.Rows.Count > fetched, ["elapsed_ms"] = 41, ["sql_id"] = "7q2m9x4k1b0zc",
        }));
    }

    private ValueTask<AgentToolOutput> GetResultAsync(AppToolInvocation inv)
    {
        var tab = AppToolArgs.RequiredText(inv.Arguments, AppFields.Tab);
        var grid = Grid(tab, AppToolArgs.Number(inv.Arguments, AppFields.Index, 0, 0));
        if (grid is null)
        {
            return Done(AppToolOutputs.Deny(AgentErrorCodes.NoResult, "This tab has no result. Run it first (tab run)."));
        }

        var fetched = Math.Min(GridPage, grid.Rows.Count);
        var offset = AppToolArgs.Number(inv.Arguments, AppFields.Offset, 0, 0);
        var limit = AppToolArgs.Number(inv.Arguments, AppFields.Limit, 50, 1, 200);
        log.Write("get_result", new JsonObject { ["tab"] = tab, ["offset"] = offset, ["limit"] = limit });
        return Done(AgentToolOutput.Ok(new JsonObject
        {
            ["columns"] = grid.ColumnsJson(), ["row_values"] = grid.RowsJson(offset, Math.Min(limit, Math.Max(0, fetched - offset))),
            ["fetched"] = fetched, ["more"] = grid.Rows.Count > fetched, ["statement_kind"] = "query", ["elapsed_ms"] = 41,
        }));
    }

    private async ValueTask<AgentToolOutput> ExportAsync(AppToolInvocation inv)
    {
        var tab = AppToolArgs.RequiredText(inv.Arguments, AppFields.Tab);
        var format = AppToolArgs.RequiredText(inv.Arguments, AppFields.Format).ToLowerInvariant();
        var output = AppToolArgs.RequiredText(inv.Arguments, AppFields.Out);
        var sheet = AppToolArgs.Text(inv.Arguments, AppFields.Sheet);
        var entry = new JsonObject { ["tab"] = tab, ["format"] = format, ["out"] = output };
        if (format is not ("xlsx" or "csv"))
        {
            return await RefuseAsync(entry, AgentErrorCodes.InvalidArguments, "format must be xlsx or csv.");
        }

        // §3.1 파일 규칙: 절대 경로·부모 폴더 있음·확장자 = 형식·이미 있으면 덮지 않음.
        if (!Path.IsPathFullyQualified(output) || !Directory.Exists(Path.GetDirectoryName(output))
            || !string.Equals(Path.GetExtension(output), "." + format, StringComparison.OrdinalIgnoreCase))
        {
            return await RefuseAsync(entry, AgentErrorCodes.PathInvalid, "out must be an absolute path in an existing folder with the ." + format + " extension.");
        }

        if (File.Exists(output))
        {
            return await RefuseAsync(entry, AgentErrorCodes.FileExists, "The file already exists. Choose another name; Oranda does not overwrite.");
        }

        var grid = Grid(tab, AppToolArgs.Number(inv.Arguments, AppFields.Index, 0, 0));
        if (grid is null)
        {
            return await RefuseAsync(entry, AgentErrorCodes.NoResult, "This tab has no result. Run it first (tab run).");
        }

        // 앱처럼 임시 이름에 쓰고 끝나면 바꿔 넣는다. 행은 LLM 에 가지 않는다(전체 행, 한도 없음).
        var started = DateTime.UtcNow;
        var temp = output + ".part";
        var service = new DataTransferService(new FakeExecutionService(), new FakeMetadataProvider(), Unused<IDataTransferDialect>(), Unused<IImportValueConverter>());
        TransferSummary summary;
        await using (var stream = File.Create(temp))
        {
            var options = new ExportOptions(format == "xlsx" ? TransferFormat.Xlsx : TransferFormat.Csv) { SheetName = sheet };
            summary = await service.ExportRowsAsync(grid.Columns, Rows(grid), options, stream, null, CancellationToken.None);
        }

        File.Move(temp, output);
        var bytes = new FileInfo(output).Length;
        entry["status"] = "written";
        entry["rows"] = summary.Rows;
        entry["bytes"] = bytes;
        log.Write("export", entry);
        return AgentToolOutput.Ok(new JsonObject
        {
            ["path"] = output, ["rows"] = summary.Rows, ["bytes"] = bytes,
            ["elapsed_ms"] = (long)(DateTime.UtcNow - started).TotalMilliseconds, ["status"] = "written",
        });
    }

    private ValueTask<AgentToolOutput> RunCommandAsync(AppToolInvocation inv)
    {
        var id = AppToolArgs.RequiredText(inv.Arguments, AppFields.Id);
        var tab = AppToolArgs.Text(inv.Arguments, AppFields.Tab);
        var entry = new JsonObject { ["id"] = id, ["tab"] = tab };
        var command = commands.OfType<JsonObject>().FirstOrDefault(c => string.Equals((string?)c["id"], id, StringComparison.Ordinal));
        if (command is null)
        {
            return Refuse(entry, AgentErrorCodes.CommandNotFound, "No command has this id. Call list_commands.");
        }

        var grade = (string?)command["grade"] ?? "deny";
        entry["grade"] = grade;
        if (tab is not null && !editor.TryRead(tab, out _, out _))
        {
            return Refuse(entry, AgentErrorCodes.TabNotFound, "No tab has this id. Call list_tabs.");
        }

        if (grade == "deny")
        {
            return Refuse(entry, AgentErrorCodes.CommandDenied, "Agents may not run this command. Use the matching tool (propose_edit, run_tab, tune_start) or ask the user.");
        }

        if ((bool?)command["enabled"] == false)
        {
            return Refuse(entry, AgentErrorCodes.CommandDisabled, "This command cannot be used right now.");
        }

        // confirm = 사람이 띠에서 한 번 확인(가짜는 곧바로 확인함).
        var status = grade == "confirm" ? "confirmed" : "done";
        entry["status"] = status;
        log.Write("ui_run", entry);
        return Done(AgentToolOutput.Ok(new JsonObject { ["status"] = status, ["grade"] = grade }));
    }

    private FakeGrid? Grid(string tab, int index)
    {
        lock (_grids)
        {
            return _grids.TryGetValue(tab, out var g) && index < g.Count ? g[index] : null;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(FakeGrid grid)
    {
        foreach (var row in grid.Rows)
        {
            yield return row.Select((v, i) => grid.Raw(i, v)).ToArray();
        }

        await Task.CompletedTask;
    }

    private ValueTask<AgentToolOutput> Refuse(JsonObject entry, string code, string message) => Done(RefuseCore(entry, code, message));

    private ValueTask<AgentToolOutput> RefuseAsync(JsonObject entry, string code, string message) => Done(RefuseCore(entry, code, message));

    private AgentToolOutput RefuseCore(JsonObject entry, string code, string message)
    {
        entry["status"] = "refused";
        entry["error"] = code;
        log.Write("refused", entry);
        return AppToolOutputs.Deny(code, message);
    }

    private static ValueTask<AgentToolOutput> Done(AgentToolOutput output) => ValueTask.FromResult(output);

    private static T Unused<T>()
        where T : class => DispatchProxy.Create<T, UnusedProxy>();

    /// <summary>내보내기(이미 든 행 쓰기)가 부르지 않는 의존성 자리. DispatchProxy 는 봉인 타입을 못 쓴다.</summary>
#pragma warning disable CA1852
    public class UnusedProxy : DispatchProxy
#pragma warning restore CA1852
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class Handler(string toolName, Func<AppToolInvocation, ValueTask<AgentToolOutput>> respond) : IAppToolHandler
    {
        public string ToolName { get; } = toolName;

        public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
            AppToolOutputs.Guard(() => respond(invocation));
    }
}
