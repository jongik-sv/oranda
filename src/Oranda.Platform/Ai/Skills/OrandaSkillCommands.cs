namespace Oranda.Platform.Ai.Skills;

/// <summary>스킬 본문의 명령 한 줄: 사용법과 짧은 설명(영어, 에이전트가 읽는다).</summary>
public sealed record OrandaSkillCommand(string Usage, string Description);

/// <summary>
/// 스킬 본문에 넣는 <c>orandactl</c> 명령과 예시. 설계서(agent-cli-control-2026-11-22) §4.2 표·§5.4 흐름을 옮겼다.
/// 명령 이름이 바뀌면 이 파일만 고친다. 사용법 글은 <c>orandactl</c> 의 명령 표(<c>src/Oranda.Agent.Cli/CommandTable.cs</c>)와 같게 둔다
/// (Platform 은 그 프로젝트를 참조하지 않으므로 손으로 맞춘다).
/// 사용법의 앞머리는 언제나 <see cref="Name"/> 이고, 본문 첫머리에서 이 이름이 어느 절대 경로인지 알려 준다.
/// </summary>
public static class OrandaSkillCommands
{
    /// <summary>본문에서 쓰는 명령 이름. 실제로 부를 때는 절대 경로로 바꿔 부르라고 안내한다.</summary>
    public const string Name = "orandactl";

    /// <summary>§4.2 명령 표(설명은 짧게). 순서 = 에이전트가 흔히 부르는 순서.</summary>
    public static IReadOnlyList<OrandaSkillCommand> All { get; } =
    [
        new("orandactl status [--all]", "window, connections and their levels; --all lists every running Oranda window"),
        new("orandactl conn list", "connections you may use (alias, environment, dbms, level, read_only)"),
        new("orandactl tabs [--workspace W]", "editor tabs (tab_id, title, connection, version, dirty, read_only, locked)"),
        new("orandactl tab get [--tab ID] [--scope document|selection|statement] [--max-chars N]", "read a tab; no --tab = the active tab. Keep the returned version"),
        new("orandactl tab new [--title T] [--conn A] (--file F | -)", "open a new tab with your SQL (never overwrites anything)"),
        new("orandactl tab propose --tab ID --base-version N [--range document|selection|statement|caret|S:L] [--title T] [--reason R] [--wait 120] (--file F | -)", "propose a change to an existing tab; the user accepts or rejects it. status = pending|accepted|modified|rejected|stale|timeout"),
        new("orandactl tab proposal <proposal_id>", "state of an earlier proposal"),
        new("orandactl tab focus --tab ID [--line L] [--range S:L]", "show a tab and place to the user"),
        new("orandactl tab save --tab ID", "ask the user to save the tab to its file"),
        new("orandactl tab binds --tab ID", "bind variables of a tab"),
        new("orandactl tab run --tab ID [--base-version N] [--range document|statement] [--timeout 30]", "run the tab's one SELECT in Oranda's result grid; status = succeeded|canceled|failed|stale. Read rows with result get"),
        new("orandactl sql check [--conn A] [--no-lint] (--file F | - | --tab ID)", "parse and lint SQL without the database (statements, diagnostics with line/col, binds)"),
        new("orandactl sql format [--profile P] [--scope document|selection|statement] (--file F | - | --tab ID)", "formatted text; does not change the tab"),
        new("orandactl obj find <pattern> [--conn A] [--type T[,T]] [--owner O] [--limit N]", "find objects by name (% and _ wildcards)"),
        new("orandactl obj schemas [--conn A]", "schema names"),
        new("orandactl obj describe <name> [--conn A] [--parts columns,keys,indexes]", "columns, keys and indexes of a table or view"),
        new("orandactl obj ddl <name> [--conn A]", "DDL of an object"),
        new("orandactl obj source <name> [--member M] [--conn A]", "PL/SQL source, optionally one package member"),
        new("orandactl obj resolve <name...> [--conn A]", "real OWNER.NAME through synonyms and the current schema"),
        new("orandactl obj args <name> [--conn A]", "arguments of a procedure, function or package members"),
        new("orandactl obj deps <name> [--conn A]", "objects that depend on this object"),
        new("orandactl obj reveal <name> [--conn A] [--open]", "select the object in the schema browser"),
        new("orandactl query [--conn A] [--max-rows 50] [--timeout 30] [--binds-json J] (--file F | -)", "run one SELECT (max 200 rows, 60 s)"),
        new("orandactl result get --tab ID [--index 0] [--offset 0] [--limit 50]", "rows already fetched in a tab's result grid"),
        new("orandactl result export --tab ID --format xlsx|csv --out PATH [--index N] [--sheet S]", "Oranda writes all rows of a tab's result to a new file (absolute path, never overwrites); no rows come back"),
        new("orandactl error [--tab ID]", "last error of a tab or the window (code, message, line, col)"),
        new("orandactl history [--conn A] [--limit 20] [--source editor|agent|...] [--since ISO8601]", "recent executions"),
        new("orandactl tune context [--conn A] [--sql-id ID] [--budget N] [--file F | -]", "tuning context of a statement"),
        new("orandactl tune plan <sql_id> [--child N] [--conn A]", "cursor plan from the shared pool"),
        new("orandactl tune explain [--conn A] (--file F | -)", "EXPLAIN PLAN of a statement"),
        new("orandactl tune stats <table> [--columns C[,C]] [--conn A]", "table and column statistics"),
        new("orandactl tune index <table> [--conn A]", "indexes of a table"),
        new("orandactl tune histogram <table> <column> [--conn A]", "column histogram"),
        new("orandactl tune sqlstats <sql_id> [--conn A]", "execution statistics of a cursor"),
        new("orandactl tune bench [--conn A] [--repeat N] (--file F | -)", "benchmark SELECT variants; JSON body [{\"label\":…,\"sql\":…}] or {\"variants\":[…]}"),
        new("orandactl tune awr-history <sql_id> [--conn A]", "plan history from AWR"),
        new("orandactl tune awr-plan <sql_id> <plan_hash_value> [--conn A]", "one plan from AWR"),
        new("orandactl tune start --tab ID", "start Oranda's own AI tuning for the tab's statement"),
        new("orandactl ui commands [--category C] [--grade now|confirm|deny]", "Oranda menu commands with id, menu path, grade and enabled"),
        new("orandactl ui run <id> [--tab ID]", "run one Oranda command; grade now runs, confirm asks the user once, deny is refused"),
        new("orandactl docs [query...]", "links to the Oranda manual; works without the app. Use it when asked about an Oranda feature"),
        new("orandactl call <tool> (--json J | -)", "call any tool by name with JSON arguments"),
        new("orandactl tools", "tool names, descriptions and input schemas"),
    ];

    /// <summary>§5.4 흐름: 쿼리를 쓰고 고치는 순서의 예. 이름·id 는 예시 값이다.</summary>
    public static IReadOnlyList<string> Example { get; } =
    [
        "# 1) where am I",
        "orandactl status",
        "orandactl tabs",
        "# 2) read the current statement",
        "orandactl tab get --tab 6f1c --scope statement",
        "# 3) check objects and columns",
        "orandactl obj find \"ORDERS%\" --conn dev",
        "orandactl obj describe SAMPLE.ORDERS --conn dev --parts columns,keys",
        "# 4) write new SQL and check it",
        "orandactl sql check --conn dev - < new.sql",
        "orandactl query --conn dev --max-rows 20 - < new.sql",
        "# 5) propose it to the editor (the user accepts)",
        "orandactl tab propose --tab 6f1c --base-version 41 --range statement --title \"Add status filter\" --wait 180 - < new.sql",
        "# 6) after the user runs it, read the error and fix again",
        "orandactl error --tab 6f1c",
    ];
}
