using Oranda.App.Features.Localization;
using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Features.Ai.External.Editor;

/// <summary>
/// 밖의 에이전트 편집기 도구의 글(설계 agent-cli-control §6.1). 띠 글은 화면 글자 표(<c>AgentBar.*</c>, strings-ko/en)로 한/영을 고르고,
/// 도구 결과의 거부 글은 밖의 에이전트가 읽으므로 영어 한 문장이다(계약 A1 §2).
/// </summary>
internal static class ExternalEditorText
{
    /// <summary>클라이언트 이름이 없을 때.</summary>
    public static string DefaultClient => UiText.Get("AgentBar.defaultClient", "외부 에이전트");

    /// <summary>새 탭 제목 앞 표시.</summary>
    public const string TitlePrefix = AppToolValues.AiTitlePrefix;

    /// <summary>제안 띠 머리 글: 「Claude Code 가 편집을 제안했습니다」.</summary>
    public static string ProposalHeadline(string client) => UiText.Format("AgentBar.proposal", "{0} 가 편집을 제안했습니다", client);

    /// <summary>저장 요청 띠 머리 글.</summary>
    public static string SaveHeadline(string client) => UiText.Format("AgentBar.save", "{0} 가 이 탭 저장을 요청했습니다", client);

    /// <summary>새 탭에 변경문이 있을 때 띠 글.</summary>
    public static string ChangeStatementNotice => UiText.Get("AgentBar.changeNotice", "AI 가 만든 변경문 — 검토 후 직접 실행");

    /// <summary>새 탭 알림 띠의 둘째 줄.</summary>
    public static string NewTabHeadline(string client) => UiText.Format("AgentBar.newTab", "{0} 가 이 탭을 만들었습니다", client);

    /// <summary>줄 서 있는 다음 항목 수.</summary>
    public static string Queue(int count) => UiText.Format("AgentBar.queue", "대기 {0}", count);

    public static string Accept => UiText.Get("AgentBar.accept", "받아들이기");

    public static string Discard => UiText.Get("AgentBar.discard", "버리기");

    public static string Save => UiText.Get("AgentBar.saveButton", "저장");

    public static string Ignore => UiText.Get("AgentBar.ignore", "무시");

    public static string Close => UiText.Get("AgentBar.close", "닫기");

    /// <summary>비교 창 제목(제안 제목이 없을 때).</summary>
    public static string CompareTitle(string client) => UiText.Format("AgentBar.compareTitle", "{0} 의 제안", client);

    /// <summary>비교 창에서 받아들이지 않고 닫았을 때.</summary>
    public static string NotApplied => UiText.Get("AgentBar.notApplied", "넣지 않았습니다. 다시 받아들이거나 버리세요.");

    /// <summary>저장하지 못했을 때(다른 이름으로 창을 취소 등).</summary>
    public static string NotSaved => UiText.Get("AgentBar.notSaved", "저장하지 않았습니다.");

    // ---- 도구 결과의 거부 글(밖의 에이전트가 읽는다, 영어) ----

    public const string TabNotFound = "No tab has this id. Call list_tabs.";

    public const string NoActiveTab = "There is no active tab.";

    public const string Locked = "This tab belongs to a connection that this external connection may not use.";

    public const string ProductionText = "Text of production tabs is never given to external agents.";

    public const string LevelOff = "The access level of this tab's connection does not allow reading its text.";

    public const string UnboundText = "Reading tabs without a connection is turned off for this external connection.";

    public const string ReadOnly = "This tab is read-only. Use new_tab to put the text in a new tab.";

    public const string RateLimited = "At most 10 new tabs can be opened a minute. Try again shortly.";

    public const string BlockSelection = "A column block or multi-caret selection cannot be replaced as one range.";

    public const string RangeOutside = "The range is outside the text.";

    public const string NoStatement = "There is no statement at the caret.";

    public const string ProposalNotFound = "No proposal has this id.";

    public const string LineOutside = "The line number is outside the text.";

    public const string NotConnected = "This connection is not open in Oranda. Connect it in Oranda first.";
}
