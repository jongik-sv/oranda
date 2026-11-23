using Oranda.App.Features.Localization;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.Contract;

/// <summary>
/// 도우미 공용 글(한/영, <c>AiSettingsText</c> 의 <c>T(ko, en)</c> 방식). 함수는 <c>english</c> 를 받는다:
/// 단계 화면은 <see cref="AiSetupContext.IsEnglish"/> 를 넘긴다(언어를 바꾸는 병렬 시험에 흔들리지 않게 정적 언어를 직접 읽지 않는다).
/// 여기는 단계 제목·창 제목·공용 단추만 둔다. 단계별 글은 각 단계 폴더의 <c>*Text.cs</c>(같은 방식).
/// </summary>
public static class AiSetupText
{
    /// <summary>지금 화면 언어가 영어다(앱 글 입구 <see cref="UiText.Current"/>). 맥락이 없는 곳(메뉴·대화상자)만 쓴다.</summary>
    public static bool CurrentIsEnglish => UiText.Current?.Language == "en";

    /// <summary>한/영 중 하나.</summary>
    public static string T(bool english, string ko, string en) => english ? en : ko;

    /// <summary>창 제목·메뉴 이름(「…」 없음).</summary>
    public static string WindowTitle(bool english) => T(english, "AI 연결 도우미", "AI Setup Assistant");

    /// <summary>[이전].</summary>
    public static string Back(bool english) => T(english, "이전", "Back");

    /// <summary>[다음].</summary>
    public static string Next(bool english) => T(english, "다음", "Next");

    /// <summary>[끝내기](마지막 단계).</summary>
    public static string Finish(bool english) => T(english, "끝내기", "Finish");

    /// <summary>[닫기](언제든, 이어 하기 남음).</summary>
    public static string Close(bool english) => T(english, "닫기", "Close");

    /// <summary>단계 제목.</summary>
    public static string StepTitle(AiSetupStepId step, bool english) => step switch
    {
        AiSetupStepId.Choose => T(english, "방법 고르기", "Choose a way"),
        AiSetupStepId.Install => T(english, "설치", "Install"),
        AiSetupStepId.Login => T(english, "로그인", "Sign in"),
        AiSetupStepId.Test => T(english, "연결 시험", "Connection test"),
        AiSetupStepId.Server => T(english, "회사 AI 서버", "Company AI server"),
        AiSetupStepId.Access => T(english, "AI 를 쓸 접속", "Connections for AI"),
        AiSetupStepId.TryIt => T(english, "해 보기", "Try it"),
        AiSetupStepId.AskAdmin => T(english, "담당자에게 묻기", "Ask your admin"),
        _ => step.ToString(),
    };

    /// <summary>갈래 이름(카드 제목).</summary>
    public static string PathTitle(AiSetupPath path, bool english) => path switch
    {
        AiSetupPath.ClaudeAccount => T(english, "회사 Claude 계정", "Company Claude account"),
        AiSetupPath.CompanyServer => T(english, "회사 AI 서버", "Company AI server"),
        AiSetupPath.NotSure => T(english, "잘 모르겠어요", "Not sure"),
        _ => path.ToString(),
    };
}
