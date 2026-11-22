using Oranda.App.Features.Localization;

namespace Oranda.App.Features.Ai.External.Commands;

/// <summary>
/// <c>run_command</c> 띠 글과 에이전트에게 돌려주는 거부 글. 띠 글은 지금 화면 언어(영어면 영어, 아니면 한국어),
/// 거부 글은 다른 외부 도구처럼 영어다(에이전트가 읽는다).
/// </summary>
internal static class CommandsText
{
    private static bool IsEnglish => UiText.Current?.Language == "en";

    private static string T(string ko, string en) => IsEnglish ? en : ko;

    public static string DefaultClient => T("외부 에이전트", "External agent");

    public static string ConfirmHeadline(string client) => T($"{client} 가 명령 실행을 요청했습니다", $"{client} asks to run a command");

    public static string ConfirmDetail(string title, string id) => T($"「{title}」 ({id}) — 받아들이면 바로 실행합니다", $"\"{title}\" ({id}) — accepting runs it now");

    public const string NotFound = "No such command. Use list_commands for ids.";

    public const string Denied = "This command is refused for agents (grade deny). Use the matching tool instead (propose_edit, run_tab, tune_start), or ask the user.";

    public const string Disabled = "The command is not available right now (its menu item is greyed out). Check enabled in list_commands.";

    public const string NoTabForConfirm = "This command needs the user's confirmation on a SQL tab's agent bar. Open a SQL tab or give tab.";

    public const string NotAvailable = "Commands are not available (the app shell is not ready).";
}
