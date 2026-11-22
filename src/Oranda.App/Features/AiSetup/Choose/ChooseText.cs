using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.Choose;

/// <summary>방법 고르기 글(한/영). 카드 제목은 계약 <see cref="AiSetupText.PathTitle"/>.</summary>
internal static class ChooseText
{
    public static string Headline(bool english) => AiSetupText.T(english, "어떤 AI 를 쓰나요?", "Which AI will you use?");

    public static string Intro(bool english) =>
        AiSetupText.T(english, "회사에서 안내받은 것을 고르세요. 모르면 맨 아래를 고르면 됩니다.", "Pick what your company told you. If you don't know, pick the last one.");

    /// <summary>카드 아래 한 줄.</summary>
    public static string When(AiSetupPath path, bool english) => path switch
    {
        AiSetupPath.ClaudeAccount => AiSetupText.T(english, "가장 쉬운 방법입니다. 대부분 이것을 고릅니다.", "The easiest way. Most people pick this."),
        AiSetupPath.CompanyServer => AiSetupText.T(english, "담당자에게 설정 파일이나 서버 주소를 받았을 때", "When your admin gave you a settings file or a server address"),
        _ => AiSetupText.T(english, "담당자에게 물어볼 글을 만들어 드립니다", "We'll write a message you can send to your admin"),
    };

    public static string Recommended(bool english) => AiSetupText.T(english, "추천", "Recommended");

    public static string Installed(bool english) => AiSetupText.T(english, "이 PC 에 설치됨", "Installed on this PC");

    public static string AlreadyConnected(bool english) =>
        AiSetupText.T(english, "이미 연결되어 있어요 — 시험만 해 볼 수 있어요", "AI is already set up. You can just run a test.");

    public static string ReadOnlyHeadline(bool english) =>
        AiSetupText.T(english, "조회만 쓰는 설정이라 AI 를 쓸 수 없어요", "AI is not available with the view-only setup");

    public static string ReadOnlyDetail(bool english) =>
        AiSetupText.T(english, "주 업무를 SQL 개발로 바꾸면 AI 를 쓸 수 있습니다.", "Change your main work to SQL development to use AI.");

    public static string ChangeRole(bool english) => AiSetupText.T(english, "주 업무를 SQL 개발로 바꾸기", "Change main work to SQL development");

    public static string AiOffHeadline(bool english) => AiSetupText.T(english, "AI 기능이 꺼져 있어요", "AI features are turned off");

    public static string AiOffDetail(bool english) =>
        AiSetupText.T(english, "기능 설정에서 「AI 튜닝」을 켜면 AI 를 쓸 수 있습니다.", "Turn on \"AI tuning\" in feature settings to use AI.");

    public static string OpenFeatures(bool english) => AiSetupText.T(english, "기능 설정 열기", "Open feature settings");
}
