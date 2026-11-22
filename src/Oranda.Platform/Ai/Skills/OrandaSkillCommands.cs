namespace Oranda.Platform.Ai.Skills;

/// <summary>스킬 본문의 명령 한 줄: 사용법과 짧은 설명(영어, 에이전트가 읽는다).</summary>
public sealed record OrandaSkillCommand(string Usage, string Description);

/// <summary>
/// 스킬 본문에 넣는 <c>orandactl</c> 명령과 예시. 설계서(agent-cli-control-2026-11-22) §4.2 표·§5.4 흐름을 옮겼다.
/// 명령 이름이 바뀌면 이 파일만 고친다(레인 B 의 하위 명령 해석이 같은 표를 따른다).
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
        new("orandactl tabs", "editor tabs (tab_id, title, connection, version, dirty, read_only, locked)"),
        new("orandactl tab get [--tab <id>] [--scope document|selection|statement] [--max-chars N]", "read a tab; no --tab = the active tab. Keep the returned version"),
        new("orandactl tab new [--title T] [--conn A] (--file F | -)", "open a new tab with your SQL (never overwrites anything)"),
        new("orandactl tab propose --tab <id> --base-version N [--range document|selection|statement|caret|S:L] [--title T] [--reason R] [--wait 120] (--file F | -)", "propose a change to an existing tab; the user accepts or rejects it. status = pending|accepted|modified|rejected|stale|timeout"),
        new("orandactl tab proposal <id>", "state of an earlier proposal"),
        new("orandactl tab focus --tab <id> [--line L] [--range S:L]", "show a tab and place to the user"),
        new("orandactl tab save --tab <id>", "ask the user to save the tab to its file"),
        new("orandactl tab binds --tab <id>", "bind variables of a tab"),
        new("orandactl sql check [--conn A] (--file F | - | --tab <id>)", "parse and lint SQL without the database (statements, diagnostics with line/col, binds)"),
        new("orandactl sql format [--profile P] (--file F | - | --tab <id>)", "formatted text; does not change the tab"),
        new("orandactl obj find <pattern> [--conn A] [--type T] [--owner O] [--limit N]", "find objects by name (% and _ wildcards)"),
        new("orandactl obj schemas [--conn A]", "schema names"),
        new("orandactl obj describe <name> [--conn A] [--parts columns,keys,indexes]", "columns, keys and indexes of a table or view"),
        new("orandactl obj ddl <name> [--conn A]", "DDL of an object"),
        new("orandactl obj source <name> [--member M] [--conn A]", "PL/SQL source, optionally one package member"),
        new("orandactl obj resolve <name...> [--conn A]", "real OWNER.NAME through synonyms and the current schema"),
        new("orandactl obj args <name> [--conn A]", "arguments of a procedure, function or package members"),
        new("orandactl obj deps <name> [--conn A]", "objects that depend on this object"),
        new("orandactl obj reveal <name> [--conn A] [--open]", "select the object in the schema browser"),
        new("orandactl query [--conn A] [--max-rows 50] [--timeout 30] [--binds-json J] (--file F | -)", "run one SELECT (max 200 rows, 60 s)"),
        new("orandactl result get --tab <id> [--index 0] [--offset 0] [--limit 50]", "rows already fetched in a tab's result grid"),
        new("orandactl error [--tab <id>]", "last error of a tab (code, message, line, col)"),
        new("orandactl history [--conn A] [--limit 20] [--source editor|agent]", "recent executions"),
        new("orandactl tune context|plan|explain|stats|index|histogram|sqlstats|bench|awr-history|awr-plan ...", "tuning tools (plans, statistics, benchmarks)"),
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
