using Oranda.App.Shell;

namespace Oranda.App.Features.AiSetup.TryIt;

/// <summary>「해 보기」 단계의 한/영 글. 글 공급자가 없으면 앱 글 입구의 언어를 본다.</summary>
internal sealed class TryItText(IShellText? text)
{
    public bool IsEnglish => text is not null ? text.Language == "en" : Contract.AiSetupText.CurrentIsEnglish;

    public string T(string ko, string en) => IsEnglish ? en : ko;

    public string Title => T("마지막으로 한 번 해 봐요", "Let's try it once");

    public string DoneTitle => T("연결 끝! 이제 이렇게 쓰세요", "All set! Here is how to use it");

    public string Intro => T(
        "연습용 새 탭에 아래 문장을 넣고, AI 에게 설명을 받아 봅니다. 문장은 실행하지 않아요.",
        "We put the statement below in a new practice tab and ask AI to explain it. The statement is not run.");

    public string PickConnection => T("연습할 접속", "Connection to practice on");

    public string TabTitle => T("AI 연습", "AI practice");

    public string OpenTab => T("연습 탭 열기", "Open practice tab");

    public string Explain => T("SQL 설명 받아 보기", "Get an SQL explanation");

    public string TabOpened => T(
        "연습용 새 탭을 열었어요. 「SQL 설명 받아 보기」를 눌러 보세요.",
        "The practice tab is open. Press \"Get an SQL explanation\".");

    public string TabFailed => T(
        "연습 탭을 열지 못했어요. 편집기 창이 열려 있는지 확인하고 다시 눌러 보세요.",
        "Could not open the practice tab. Make sure the editor is open, then try again.");

    public string Waiting => T("AI 가 읽는 중…", "AI is reading…");

    public string NoAnswer => T(
        "답을 받지 못했어요. 그 접속에 연결돼 있는지 확인하고, 오른쪽 「AI 대화」 창에서 이유를 본 뒤 다시 눌러 보세요.",
        "No answer came back. Make sure you are connected, check the reason in the \"AI chat\" panel on the right, then try again.");

    public string Failed => T("설명을 시작하지 못했어요: ", "Could not start the explanation: ");

    public string AnswerNote => T("답 전체는 오른쪽 「AI 대화」 창에 있어요.", "The full answer is in the \"AI chat\" panel on the right.");

    public string Skipped => T(
        "AI 를 켠 접속이 없어 해 보기는 건너뛰어요. 나중에 접속 편집에서 켤 수 있어요.",
        "No connection has AI turned on, so this step is skipped. You can turn it on later in the connection editor.");

    public string AlsoExternalAgent => T(
        "검은 창의 Claude Code 에서도 Oranda 시키기(고급) — 끝내면 외부 에이전트 연결 창이 열려요",
        "Let Claude Code in the terminal use Oranda too (advanced) — the external agent window opens when you finish");

    public string TipTuneTitle => T("AI 튜닝", "AI tuning");

    public string TipTuneWhere => T("느린 SQL 에 커서를 두고 도구 모음의 「AI 튜닝」", "Put the cursor on a slow SQL and press \"AI tuning\" on the toolbar");

    public string TipFixTitle => T("오류 고치기", "Fix error");

    public string TipFixWhere => T("실행 오류가 나면 「AI 튜닝 ▾ > 오류 고치기」", "When a run fails, use \"AI tuning ▾ > Fix error\"");

    public string TipChatTitle => T("AI 대화", "AI chat");

    public string TipChatWhere => T("오른쪽 「AI 대화」 창에서 자유롭게 질문", "Ask anything in the \"AI chat\" panel on the right");
}
