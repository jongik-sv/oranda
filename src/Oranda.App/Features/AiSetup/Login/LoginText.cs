using Oranda.App.Features.AiSetup.Contract;

namespace Oranda.App.Features.AiSetup.Login;

/// <summary>로그인 단계 글(한/영).</summary>
internal static class LoginText
{
    public static string Intro(bool english) =>
        AiSetupText.T(english, "인터넷 창이 열리면 회사에서 받은 Claude 계정으로 로그인하고 「허용」을 누르세요. 끝나면 이 창이 저절로 넘어갑니다.",
            "When the browser opens, sign in with the Claude account from your company and press Allow. This window moves on by itself.");

    public static string Checking(bool english) => AiSetupText.T(english, "로그인했는지 확인하는 중…", "Checking whether you are signed in…");

    public static string LoggedIn(bool english) => AiSetupText.T(english, "로그인했습니다", "Signed in");

    public static string LoggedInNoAccount(bool english) => AiSetupText.T(english, "「다음」을 누르세요.", "Press Next.");

    public static string NotLoggedIn(bool english) => AiSetupText.T(english, "아직 로그인하지 않았습니다", "Not signed in yet");

    public static string NotLoggedInDetail(bool english) => AiSetupText.T(english, "아래 「로그인 창 열기」를 누르세요.", "Press Open sign-in below.");

    public static string Start(bool english) => AiSetupText.T(english, "로그인 창 열기", "Open sign-in");

    public static string StartAgain(bool english) => AiSetupText.T(english, "로그인 창 다시 열기", "Open sign-in again");

    public static string Waiting(bool english) => AiSetupText.T(english, "인터넷 창에서 로그인을 기다리는 중", "Waiting for you to sign in in the browser");

    public static string WaitingDetail(bool english) => AiSetupText.T(english, "로그인을 마치면 여기로 돌아오세요.", "Come back here when you have signed in.");

    public static string StartFailed(bool english) => AiSetupText.T(english, "로그인 창을 열지 못했습니다", "Could not open sign-in");

    public static string Closed(bool english) =>
        AiSetupText.T(english, "로그인 창이 닫혔습니다. 「로그인 창 다시 열기」를 눌러 주세요.", "Sign-in stopped. Press Open sign-in again.");

    public static string NoBrowser(bool english) => AiSetupText.T(english, "인터넷 창이 안 열려요", "The browser did not open");

    public static string NoBrowserHow(bool english) =>
        AiSetupText.T(english, "아래 주소를 복사해서 인터넷 창 주소 칸에 붙여 넣으세요.", "Copy the address below and paste it into your browser's address bar.");

    public static string NoBrowserNoUrl(bool english) =>
        AiSetupText.T(english, "아직 주소를 받지 못했습니다. 「로그인 창 열기」를 다시 눌러 주세요.", "No address yet. Press Open sign-in again.");

    public static string Copy(bool english) => AiSetupText.T(english, "주소 복사", "Copy address");

    public static string Copied(bool english) => AiSetupText.T(english, "복사했습니다", "Copied");

    public static string Done(bool english) => AiSetupText.T(english, "로그인 끝났어요", "I've signed in");

    public static string NotYet(bool english) =>
        AiSetupText.T(english, "아직 로그인이 끝나지 않았습니다. 인터넷 창에서 「허용」까지 눌렀는지 확인해 주세요.", "Not signed in yet. Check that you pressed Allow in the browser.");
}
