namespace Oranda.Language.Ai.Handlers.External;

/// <summary>
/// 외부 연결 도구(언어·메타·실행·이력)가 LLM 에 돌려주는 거부 글. 계약(A1)대로 영어 한 문장이고 DB·드라이버 원문을 넣지 않는다.
/// </summary>
internal static class ExternalToolText
{
    public const string SqlOrTab = "Pass sql, or tab to use the text of an editor tab.";

    public const string TabReaderMissing = "Reading editor tabs is not available in this window; pass sql instead.";

    public const string TabTooLong = "The tab text is longer than 50000 characters; pass sql instead.";

    public const string UnknownProfile = "No formatter profile has this name or id.";

    public const string ObjectNotFound = "The object was not found, or it is not visible to this connection.";

    public const string MemberNotFound = "The package has no member with this name.";

    public const string NotPlSql = "This object has no PL/SQL source; use get_ddl.";

    public const string NoArguments = "This object is not a procedure, function or package.";

    public const string QueryNotAllowed = "This connection does not allow query (Query level is needed and production connections are excluded).";

    public const string QueryProduction = "query is not available on production connections.";

    public const string QuerySingleSelect = "query runs exactly one SELECT statement. Put other statements in a tab with new_tab and let the user run them.";

    public const string QueryUserFunction = "query does not run statements that call user or package functions. Use explain_plan, or put it in a tab with new_tab.";

    public const string QueryTimeout = "The query took longer than timeout_s and was canceled.";

    public const string QueryDenied = "The execution policy refused this statement.";

    public const string QuerySchemaMismatch = "Names in the statement resolve to different objects in the current schema and the connection user. Write OWNER.NAME.";

    public const string QueryCanceled = "The query was canceled.";

    public const string HistoryNoConnection = "No connection of this external connection may read history (Metadata level is needed).";

    public const string HistorySourceProblem = "must be one of editor, script, grid_edit, schema_browser, monitor, extension, agent, data_copy, table_editor, change_bundle";

    public const string HistorySinceProblem = "must be an ISO 8601 time, for example 2026-11-22T09:00:00+09:00";

    public static string MissingBinds(IEnumerable<string> names) => "Values are missing for the binds: " + string.Join(", ", names) + ".";

    public static string QueryFailed(string? code) => code is null ? "The query failed." : $"The query failed ({code}).";

    public static string LookupFailed(string? code) => code is null ? "The lookup failed." : $"The lookup failed ({code}).";
}
