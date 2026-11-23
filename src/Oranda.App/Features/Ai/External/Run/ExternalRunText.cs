using System.Globalization;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.Ai.External.Run;

/// <summary>
/// 탭 실행·결과 내보내기(<c>run_tab</c>·<c>export_result</c>)의 글. 띠·메시지 창 글은 사람이 보므로 앱 언어(한/영)를 따르고 읽을 때마다 만든다
/// (띠가 언어를 바꾸면 다시 부른다). 도구 결과의 거부 글은 밖의 에이전트가 읽으므로 영어 한 문장이다(계약 A1 §2).
/// 글자 표(strings-*.json)를 다른 레인과 함께 고치지 않으려고 한/영을 이 파일에 둔다.
/// </summary>
internal static class ExternalRunText
{
    private static bool English => string.Equals(UiText.Current?.Language, "en", StringComparison.Ordinal);

    private static string L(string ko, string en) => English ? en : ko;

    private static string Count(long rows) => rows.ToString("N0", CultureInfo.InvariantCulture);

    // ---- 띠·메시지 창(사람) ----

    public static string Running(string client) => L($"{client} 가 이 문장을 실행하는 중입니다", $"{client} is running this statement");

    public static string Ran(string client, long rows, bool more) =>
        L($"{client} 가 이 문장을 실행했습니다 · {Count(rows)}행{(more ? "+" : string.Empty)}", $"{client} ran this statement · {Count(rows)}{(more ? "+" : string.Empty)} rows");

    public static string RunFailed(string client) => L($"{client} 가 실행한 문장이 실패했습니다", $"The statement {client} ran failed");

    public static string RunCanceled(string client) => L($"{client} 의 실행을 취소했습니다", $"The run by {client} was canceled");

    public static string Saving(string client, long rows) => L($"{client} 가 파일로 저장하는 중 … {Count(rows)}행", $"{client} is saving to a file … {Count(rows)} rows");

    public static string Saved(string path, long rows) => L($"저장함 · {path} · {Count(rows)}행", $"Saved · {path} · {Count(rows)} rows");

    public static string SaveFailed(string client) => L($"{client} 의 파일 저장이 실패했습니다", $"Saving the file for {client} failed");

    public static string SaveCanceled(string client) => L($"{client} 의 파일 저장을 취소했습니다", $"Saving the file for {client} was canceled");

    public static string Cancel => L("취소", "Cancel");

    public static string Close => L("닫기", "Close");

    public static string OpenFolder => L("폴더 열기", "Open folder");

    /// <summary>메시지 창 한 줄의 머리(「[에이전트]」).</summary>
    public static string LogPrefix => L("[에이전트] ", "[Agent] ");

    public static string LogRan(string client, string tab, long rows, string status) =>
        LogPrefix + L($"{client} 가 「{tab}」 탭 문장을 실행했습니다 · {status} · {Count(rows)}행", $"{client} ran the statement of tab \"{tab}\" · {status} · {Count(rows)} rows");

    public static string LogSaved(string client, string path, long rows, string status) =>
        LogPrefix + L($"{client} 가 결과를 파일로 저장했습니다 · {status} · {path} · {Count(rows)}행", $"{client} saved a result to a file · {status} · {path} · {Count(rows)} rows");

    /// <summary><c>set_binds</c> 뒤 띠 글.</summary>
    public static string BindsSet(string client, int count) =>
        L($"{client} 가 바인드 값 {count}개를 채웠습니다", $"{client} set {count} bind value{(count == 1 ? string.Empty : "s")}");

    /// <summary>바인드 값을 채운 뒤 메시지 창 한 줄(값은 바인드 창에 보이므로 이름만).</summary>
    public static string LogBindsSet(string client, string tab, IReadOnlyList<string> names) =>
        LogPrefix + L($"{client} 가 「{tab}」 탭 바인드 값을 채웠습니다 · {Names(names)}", $"{client} set bind values of tab \"{tab}\" · {Names(names)}");

    private static string Names(IReadOnlyList<string> names) => string.Join(", ", names.Select(n => ":" + n));

    // ---- 도구 결과의 거부 글(밖의 에이전트, 영어) ----

    public const string NoConnection = "This tab has no connection. Choose a connection for the tab in Oranda first.";

    public const string LevelLow = "Running statements needs the Query level on the tab's connection.";

    public const string ProductionOff = "Statements are not run automatically on production connections.";

    public const string Busy = "Another statement is running on this tab's session. Try again when it ends.";

    public static string ManyStatements(int count) =>
        $"The tab holds {count} statements; pass range statement to run the statement at the caret.";

    public const string EmptyTab = "The tab has no statement to run.";

    public const string NoCaretStatement = "There is no single statement at the caret (or the selection holds several).";

    public const string NotSingleSelect = "run_tab runs exactly one SELECT (or WITH) statement. Let the user run other statements.";

    public const string UserFunction = "The statement calls a function that could not be verified as read-only; it is not run automatically.";

    public const string Unsafe = "The statement uses FOR UPDATE, a database link, a sequence or an Oracle-supplied package; it is not run automatically.";

    public const string MissingBinds =
        "The statement uses binds that have no usable value in the tab's bind list. Pass them with binds (orandactl tab run --binds-json or tab binds set); confirm unclear values with the user first.";

    public static string UnknownBind(string name) =>
        $"The tab has no bind :{name}; nothing was set. Use the names listed by get_binds (orandactl tab binds).";

    public static string BindTwice(string name) => $"The bind :{name} is given twice; nothing was set.";

    public static string BindTakesNoValue(string name) => $"The bind :{name} is an OUT bind or a ref cursor and takes no value; nothing was set.";

    public static string BindWrongType(string name, string type, bool date) =>
        $"The value for :{name} does not fit its type {type} in the tab's bind list; nothing was set."
        + (date ? " Dates look like 2026-11-23, 20261123 or 2026-11-23 13:05." : string.Empty)
        + " Fix the value, or ask the user whether the bind type in the list is right.";

    public const string NoResultGrid = "This tab has no result grid at that index. Run the statement first (run_tab).";

    public const string ResultNotSelect = "That result is not from a SELECT statement, so it cannot be exported.";

    public const string RelativePath = "out must be an absolute path.";

    public const string NoFolder = "The folder of out does not exist.";

    public static string WrongExtension(string format) => $"out must end with .{format} for format {format}.";

    public const string Exists = "A file already exists at out; it is never overwritten. Choose a new file name.";

    public const string NoTransfer = "Exporting is not available in this window.";

    public static string ExportFailed(string? code) => code is null ? "The export failed." : $"The export failed ({code}).";
}
