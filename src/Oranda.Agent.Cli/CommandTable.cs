using Oranda.Core.Ai.Schemas;

namespace Oranda.Agent.Cli;

/// <summary>
/// 설계 §4.2 하위 명령 표 전부. 명령 → 도구 이름 + 인자 이름 대응만 둔다(도구 스키마는 앱이 가진다).
/// 시험이 모든 인자 이름이 그 도구 스키마에 있는지, 외부 도구 40개가 모두 명령으로 닿는지 본다.
/// <c>docs</c>(매뉴얼 링크)는 앱 없이 도는 CLI 자체 명령이라 이 표에 없다(도구가 아니다, 2단계 E-27). 본체 = m-skill-docs.
/// </summary>
internal static class CommandTable
{
    private static readonly OptionSpec Conn = new("--conn", AgentFields.Connection);
    private static readonly OptionSpec Tab = new("--tab", AppFields.Tab);
    private static readonly OptionSpec Limit = new("--limit", AppFields.Limit, ValueKind.Integer);
    private static readonly PositionalSpec Object = new(AgentFields.ObjectName);
    private static readonly PositionalSpec SqlId = new(AgentFields.SqlId);
    private static readonly PositionalSpec Table = new(AgentFields.Table);

    public static IReadOnlyList<CommandSpec> All { get; } =
    [
        // ---- 상태·접속·탭 ----
        new("status", AppToolNames.GetStatus, "[--all]", [], []),
        new("conn list", AppToolNames.ListConnections, string.Empty, [], []),
        new("tabs", AppToolNames.ListTabs, "[--workspace W]", [new("--workspace", AppFields.Workspace)], []),
        new("tab get", AppToolNames.GetTab, "[--tab ID] [--scope document|selection|statement] [--max-chars N]",
            [Tab, new("--scope", AppFields.Scope), new("--max-chars", AppFields.MaxChars, ValueKind.Integer)], []),
        new("tab new", AppToolNames.NewTab, "[--title T] [--conn A] (--file F | -)",
            [new("--title", AppFields.Title), Conn], [], AppFields.Text, BodyRule.Required),
        new("tab propose", AppToolNames.ProposeEdit,
            "--tab ID --base-version N [--range document|selection|statement|caret|S:L] [--title T] [--reason R] [--wait 120] (--file F | -)",
            [Tab, new("--base-version", AppFields.BaseVersion, ValueKind.Integer), new("--range", AppFields.Range), new("--title", AppFields.Title),
             new("--reason", AppFields.Reason), new("--wait", AppFields.WaitSeconds, ValueKind.Integer)],
            [], AppFields.NewText, BodyRule.Required),
        new("tab proposal", AppToolNames.GetProposal, "<proposal_id>", [], [new(AppFields.ProposalId)]),
        new("tab focus", AppToolNames.FocusTab, "--tab ID [--line L] [--range S:L]",
            [Tab, new("--line", AppFields.Line, ValueKind.Integer), new("--range", AppFields.Range)], []),
        new("tab save", AppToolNames.SaveTab, "--tab ID", [Tab], []),
        new("tab binds", AppToolNames.GetBinds, "--tab ID", [Tab], []),
        new("tab run", AppToolNames.RunTab, "--tab ID [--base-version N] [--range document|statement] [--timeout 30]",
            [Tab, new("--base-version", AppFields.BaseVersion, ValueKind.Integer), new("--range", AppFields.Range),
             new("--timeout", AppFields.TimeoutSeconds, ValueKind.Integer)], []),

        // ---- 언어 ----
        new("sql check", AppToolNames.CheckSql, "[--conn A] [--no-lint] (--file F | - | --tab ID)",
            [Conn, Tab, new("--no-lint", AppFields.Lint, ValueKind.FalseFlag)], [], AgentFields.Sql, BodyRule.RequiredUnlessTab),
        new("sql format", AppToolNames.FormatSql, "[--profile P] [--scope document|selection|statement] (--file F | - | --tab ID)",
            [new("--profile", AppFields.Profile), new("--scope", AppFields.Scope), Tab], [], AgentFields.Sql, BodyRule.RequiredUnlessTab),

        // ---- 메타데이터 ----
        new("obj find", AppToolNames.FindObjects, "<pattern> [--conn A] [--type T[,T]] [--owner O] [--limit N]",
            [Conn, new("--type", AppFields.Types, ValueKind.List), new("--owner", AppFields.Owner), Limit], [new(AppFields.NameLike)]),
        new("obj schemas", AppToolNames.ListSchemas, "[--conn A]", [Conn], []),
        new("obj describe", AgentToolNames.DescribeObject, "<name> [--conn A] [--parts columns,keys,indexes]",
            [Conn, new("--parts", AgentFields.Parts, ValueKind.List)], [Object]),
        new("obj ddl", AppToolNames.GetDdl, "<name> [--conn A]", [Conn], [Object]),
        new("obj source", AppToolNames.GetSource, "<name> [--member M] [--conn A]", [Conn, new("--member", AppFields.Member)], [Object]),
        new("obj resolve", AppToolNames.ResolveNames, "<name...> [--conn A]", [Conn], [new(AppFields.Names, Many: true)]),
        new("obj args", AppToolNames.GetProcedureArgs, "<name> [--conn A]", [Conn], [Object]),
        new("obj deps", AgentToolNames.GetDependents, "<name> [--conn A]", [Conn], [Object]),
        new("obj reveal", AppToolNames.RevealObject, "<name> [--conn A] [--open]", [Conn, new("--open", AppFields.Open, ValueKind.Flag)], [Object]),

        // ---- 실행·결과·이력 ----
        new("query", AppToolNames.Query, "[--conn A] [--max-rows 50] [--timeout 30] [--binds-json J] (--file F | -)",
            [Conn, new("--max-rows", AppFields.MaxRows, ValueKind.Integer), new("--timeout", AppFields.TimeoutSeconds, ValueKind.Integer),
             new("--binds-json", AppFields.Binds, ValueKind.JsonObject)],
            [], AgentFields.Sql, BodyRule.Required),
        new("result get", AppToolNames.GetResult, "--tab ID [--index 0] [--offset 0] [--limit 50]",
            [Tab, new("--index", AppFields.Index, ValueKind.Integer), new("--offset", AppFields.Offset, ValueKind.Integer), Limit], []),
        new("result export", AppToolNames.ExportResult, "--tab ID --format xlsx|csv --out PATH [--index N] [--sheet S]",
            [Tab, new("--format", AppFields.Format), new("--out", AppFields.Out), new("--index", AppFields.Index, ValueKind.Integer),
             new("--sheet", AppFields.Sheet)], []),
        new("error", AppToolNames.GetLastError, "[--tab ID]", [Tab], []),
        new("history", AppToolNames.GetHistory, "[--conn A] [--limit 20] [--source editor|agent|...] [--since ISO8601]",
            [Conn, Limit, new("--source", AppFields.Source), new("--since", AppFields.Since)], []),

        // ---- 튜닝(기존 도구) ----
        new("tune context", AgentToolNames.GetTuningContext, "[--conn A] [--sql-id ID] [--budget N] [--file F | -]",
            [Conn, new("--sql-id", AgentFields.SqlId), new("--budget", "budget_tokens", ValueKind.Integer)], [], AgentFields.Sql, BodyRule.Optional),
        new("tune plan", AgentToolNames.GetCursorPlan, "<sql_id> [--child N] [--conn A]", [Conn, new("--child", "child", ValueKind.Integer)], [SqlId]),
        new("tune explain", AgentToolNames.ExplainPlan, "[--conn A] (--file F | -)", [Conn], [], AgentFields.Sql, BodyRule.Required),
        new("tune stats", AgentToolNames.GetTableStats, "<table> [--columns C[,C]] [--conn A]",
            [Conn, new("--columns", AgentFields.Columns, ValueKind.List)], [Table]),
        new("tune index", AgentToolNames.GetIndexInfo, "<table> [--conn A]", [Conn], [Table]),
        new("tune histogram", AgentToolNames.GetHistogram, "<table> <column> [--conn A]", [Conn], [Table, new(AgentFields.Column)]),
        new("tune sqlstats", AgentToolNames.GetSqlStats, "<sql_id> [--conn A]", [Conn], [SqlId]),
        new("tune bench", AgentToolNames.BenchmarkQueries, "[--conn A] [--repeat N] (--file F | -)   (JSON: [{\"label\":…,\"sql\":…}] or {\"variants\":[…]})",
            [Conn, new("--repeat", "repeat", ValueKind.Integer)], [], AgentFields.Variants, BodyRule.Variants),
        new("tune awr-history", AgentToolNames.GetAwrPlanHistory, "<sql_id> [--conn A]", [Conn], [SqlId]),
        new("tune awr-plan", AgentToolNames.GetAwrPlan, "<sql_id> <plan_hash_value> [--conn A]",
            [Conn], [SqlId, new(AgentFields.PlanHashValue, ValueKind.Integer)]),
        new("tune start", AppToolNames.TuneStart, "--tab ID", [Tab], []),

        // ---- 앱 명령(2단계) ----
        new("ui commands", AppToolNames.ListCommands, "[--category C] [--grade now|confirm|deny]",
            [new("--category", AppFields.Category), new("--grade", AppFields.Grade)], []),
        new("ui run", AppToolNames.RunCommand, "<id> [--tab ID]", [Tab], [new(AppFields.Id)]),
    ];

    /// <summary>
    /// 낱말들로 명령을 찾는다(긴 이름 먼저). <paramref name="used"/> = 명령 이름에 쓴 낱말 수.
    /// </summary>
    public static CommandSpec? Find(IReadOnlyList<string> words, out int used)
    {
        foreach (var spec in All.OrderByDescending(s => s.Words.Length))
        {
            if (spec.Words.Length <= words.Count && spec.Words.Select((w, i) => string.Equals(w, words[i], StringComparison.Ordinal)).All(x => x))
            {
                used = spec.Words.Length;
                return spec;
            }
        }

        used = 0;
        return null;
    }
}
