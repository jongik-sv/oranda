using Oranda.Core.Ai.Manual;

namespace Oranda.Platform.Ai.Skills;

/// <summary>
/// 스킬 본문의 절차 글(영어, 에이전트가 읽는다). 명령 한 줄 표(<see cref="OrandaSkillCommands"/>)와 달리 「어떤 순서로 무엇을 하라」를 적는다.
/// 글 속 <c>orandactl …</c> 명령은 모두 <see cref="OrandaSkillCommands.All"/> 에 있는 명령이어야 한다(시험이 확인한다).
/// </summary>
public static class OrandaSkillGuides
{
    /// <summary>기능 질문 절 머리.</summary>
    public const string DocsHeading = "Questions about Oranda features";

    /// <summary>탭 실행·내보내기 절 머리.</summary>
    public const string RunHeading = "Running a tab and saving all rows to a file";

    /// <summary>메뉴 명령 절 머리.</summary>
    public const string UiHeading = "Oranda menu commands";

    /// <summary>운영 접속 절 머리.</summary>
    public const string ProductionHeading = "Production connections";

    /// <summary>매뉴얼 쪽 목록 절 머리.</summary>
    public const string ManualHeading = "Manual pages";

    /// <summary>
    /// 기능 질문(D3, E-27): 모르는 기능은 <c>orandactl docs</c> 로 찾아 매뉴얼 링크로 답한다. 앱 없이 돈다. 쪽 목록은 본문 끝 <see cref="ManualHeading"/>.
    /// </summary>
    public static IReadOnlyList<string> Docs { get; } =
    [
        "When the user asks how to do something in Oranda or what a feature, menu, window or setting does, do not guess. "
            + "Run `orandactl docs <words>` (it works even when Oranda is not running) and answer briefly with the matching links: "
            + "the page `url`, or a heading `url` when one matches.",
        "The manual is in Korean. Search with Korean words from the page titles below (for example `orandactl docs 실행 계획`); "
            + "if nothing matches, try fewer words or `orandactl docs` for every page.",
        "If the manual does not cover it, say so instead of inventing menu paths.",
        "Give manual links only from `orandactl docs` output, not from a web search or from memory.",
    ];

    /// <summary>
    /// 탭 실행·내보내기(D1, 계획서 §3.1, E-26·E-33·E-34): 탭의 SELECT 한 문장을 사람 그리드에서 실행하고, 전체 행을 앱이 파일로 쓴다(행은 에이전트에게 가지 않는다).
    /// </summary>
    public static IReadOnlyList<string> Run { get; } =
    [
        "- Find a tab by its title in `orandactl tabs` and use the `tab_id` from that output; do not guess ids from names the user gives (such as t1).",
        "- `orandactl tab run --tab <id> --base-version <version>` runs the tab's one SELECT/WITH in the user's result grid, like the user pressing F5. "
            + "Pass the `version` from `tab get` or `tab new`; `stale` means the user changed the tab, so read it again first. "
            + "A tab with several statements needs `--range statement` (the statement at the caret). "
            + "`connection` (exit 4) means the tab is not connected in Oranda; ask the user to connect the tab (Oranda does not ask for you).",
        "- It runs for at most 30 s by default (`--timeout`, up to 60); after that it is canceled (`canceled`). "
            + "`tab_busy` (exit 1) means the tab's session is already running something or saving a file; tell the user or try once more later, do not loop.",
        "- `tab run` and `result export` refuse FOR UPDATE, database links, sequences, Oracle-supplied packages and user functions Oranda cannot verify as read-only "
            + "(`sql_unsafe`, exit 4), and binds without a value in the tab. Rewrite the SELECT or ask the user.",
        "- No rows come back from `tab run`, only `fetched` (rows in the grid) and `more`. Read a few with `orandactl result get --tab <id> --index <result_index> --limit 5`.",
        "- `orandactl result export --tab <id> --format xlsx --out <absolute path>` makes Oranda read all rows again and write the file itself (no row limit; rows do not come to you). "
            + "It only creates a new file: `file_exists` (exit 4) means pick another name; never delete the user's files. "
            + "`path_invalid` (exit 2) means a relative path, a missing folder or an extension that does not match `--format`.",
        "- Typical flow for a new query: `orandactl tab new` → `orandactl tab run` → `orandactl result get` (check a few rows) → `orandactl result export`, then report the file path and row count.",
    ];

    /// <summary>메뉴 명령(F5, 계획서 §3.3, E-30·E-31): 등급 now/confirm/deny, 모르는 id = 거부.</summary>
    public static IReadOnlyList<string> Ui { get; } =
    [
        "- `orandactl ui commands` lists Oranda's menu commands with `id`, `menu_path`, `grade` and `enabled`. Use it when the user asks you to open a window, panel or view.",
        "- `orandactl ui run <id> [--tab <id>]`: grade `now` runs at once; `confirm` asks the user once in Oranda (`confirmed`, `rejected` or `timeout`); "
            + "`deny` is refused (`command_denied`, exit 4). `command_disabled` means it cannot be used in the current context (for example no tab or no connection).",
        "- Commands that change text, run SQL or start AI are denied on purpose. Use `orandactl tab propose`, `orandactl tab run` or `orandactl tune start` for those.",
    ];

    /// <summary>운영 접속 주의(D5, 계획서 §3.2 「스킬」 줄). 운영 실행이 허용되는지는 사용자 설정이라 약속하지 않는다.</summary>
    public static IReadOnlyList<string> Production { get; } =
    [
        "- On a production connection every SELECT you run (`query`, `tab run`, `tune bench`, `result export`) runs on the live database. Use a dev or test connection first.",
        "- With a cloud model, production results come without row values (row counts and column types only), sensitive columns are `***`, "
            + "literals and error texts are masked, and production tab text is refused (`environment`). Do not ask the user to work around this. "
            + "A grant marked local model sees everything unmasked.",
        "- What a grant allows is the user's setting in Oranda. A refusal (exit 4) is a decision; report it and stop.",
        "- The company's database access-control solution may block, hold for approval or record these runs. If a run fails because of such a policy, tell the user; do not retry another way.",
    ];

    /// <summary>매뉴얼 쪽 목록(E-28: 제목·장·주소만, h2 는 <c>docs</c> 검색에만). 표 순서 = 사이드바 순서.</summary>
    public static IReadOnlyList<string> ManualPages() =>
    [
        "Base: " + OrandaManual.BaseUrl + "/ . Use `orandactl docs` to search headings too.",
        .. OrandaManual.All.Select(e => $"- {e.Title} ({e.Section}): {e.Url}"),
    ];

    /// <summary>튜닝 절차의 머리(<c>## …</c> 뒤 글).</summary>
    public const string TuningHeading = "Tuning a slow query";

    /// <summary>
    /// 튜닝 절차(2단계 D2 「자동으로 끝까지」, E-29). 밖의 LLM 이 기존 튜닝 도구로 원인을 찾고 후보를 재어 가장 나은 문장을 새 탭에 넣는다.
    /// 원래 탭은 바꾸지 않는다. 운영 접속은 먼저 개발·시험 접속을 권하고, 거부(종료 4)면 알리고 멈춘다.
    /// </summary>
    public static IReadOnlyList<string> Tuning { get; } =
    [
        "When the user asks to tune a query, do the whole procedure without stopping to ask between steps. "
            + "Stop early only when Oranda refuses (exit 4) or is not reachable (exit 3), and tell the user why.",
        "1. Get the statement: from the user, from a file, or `orandactl tab get --tab <id> --scope statement`. "
            + "Note the connection alias of the tab.",
        "2. `orandactl tune context --conn <alias> - < stmt.sql` — plan, objects, statistics and the cursor `sql_id` when Oranda knows it.",
        "3. Read the plan: `orandactl tune plan <sql_id> --conn <alias>` (cursor plan with actual rows) and `orandactl tune sqlstats <sql_id> --conn <alias>`; "
            + "without a `sql_id`, or when Oranda refuses that `sql_id` (`sql_id_scope`: only ids returned by Oranda are allowed), "
            + "use `orandactl tune explain --conn <alias> - < stmt.sql` and go on.",
        "4. For each table with a full scan, a bad row estimate or a costly join, check `orandactl tune stats <table> --conn <alias>`, "
            + "`orandactl tune index <table> --conn <alias>` and, for filter columns with skewed values, `orandactl tune histogram <table> <column> --conn <alias>`.",
        "5. Write up to 4 candidate SELECTs (rewrite, join order, hints, using an existing index). Each must return the same rows as the original. "
            + "Check each with `orandactl sql check --conn <alias> -`.",
        "6. Measure the original and the candidates together in one command that pipes the JSON from printf: "
            + "`printf '%s' '[{\"label\":\"original\",\"sql\":…},{\"label\":\"c1\",\"sql\":…}]' | orandactl tune bench --conn <alias> --repeat 3 -` "
            + "(at most 5 statements, repeat at most 5; no heredoc and no temporary file for JSON, some CLIs refuse them). "
            + "Drop a candidate whose `rows` differs from the original. Prefer the lowest `logical_reads`, then `elapsed_ms`. "
            + "An entry with `substituted` was not run (Oranda showed cursor statistics instead); it cannot win on measured numbers.",
        "7. Put the best statement in a new tab: `orandactl tab new --title \"Tuned: <short name>\" --conn <alias> - < best.sql`. "
            + "Start the tab with a SQL comment: what changed and why. A new index the measurement cannot test (CREATE INDEX) goes in that comment only; never run it. "
            + "Do not change the original tab.",
        "8. Report in one line: original vs best `logical_reads` and `elapsed_ms`, and what changed. If no candidate was better, say so and keep the original.",
        "Production connections: prefer a dev or test connection with the same schema. `tune bench` runs every statement again (repeat x statements) "
            + "and loads the server; on production use `--repeat 1`. A refusal on production is the user's setting; report it and stop.",
        "`orandactl tune start --tab <id>` is not part of this procedure: it opens Oranda's own AI tuning start window for the user, who confirms it there. "
            + "Use it only when the user asks to continue tuning inside Oranda.",
    ];
}
