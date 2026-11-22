using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Profiles;

namespace Oranda.App.Features.AiSetup.Install;

/// <summary>설치 단계 글(한/영).</summary>
internal static class InstallText
{
    public static string Intro(bool english) =>
        AiSetupText.T(english, "Oranda 가 AI 와 이야기할 때 쓰는 프로그램(Claude Code)입니다. 단추 하나로 설치합니다. 1~2분 걸립니다.",
            "Oranda talks to the AI through a program called Claude Code. One button installs it. It takes 1 to 2 minutes.");

    public static string Checking(bool english) => AiSetupText.T(english, "이 PC 에 있는지 찾는 중…", "Looking for it on this PC…");

    public static string Installed(string? version, bool english) => version is { Length: > 0 }
        ? AiSetupText.T(english, $"설치되어 있습니다(판 {version})", $"Installed (version {version})")
        : AiSetupText.T(english, "설치되어 있습니다", "Installed");

    public static string InstalledDetail(bool english) => AiSetupText.T(english, "「다음」을 누르세요.", "Press Next.");

    public static string NotInstalled(bool english) => AiSetupText.T(english, "아직 설치되지 않았습니다", "Not installed yet");

    public static string NotInstalledDetail(bool english) => AiSetupText.T(english, "아래 「설치하기」를 누르세요.", "Press Install below.");

    public static string InstallButton(bool english) => AiSetupText.T(english, "설치하기", "Install");

    public static string Installing(bool english) => AiSetupText.T(english, "설치하는 중입니다", "Installing");

    public static string InstallingDetail(bool english) => AiSetupText.T(english, "창을 닫지 말고 기다려 주세요.", "Please wait and keep this window open.");

    public static string Failed(bool english) => AiSetupText.T(english, "자동 설치가 되지 않았습니다", "Automatic install did not work");

    public static string Unavailable(bool english) =>
        AiSetupText.T(english, "이 PC 에서는 자동 설치를 할 수 없습니다. 아래 방법으로 직접 설치해 주세요.", "Automatic install is not available here. Please install it yourself as shown below.");

    public static string Log(bool english) => AiSetupText.T(english, "진행 글 보기", "Show progress text");

    public static string ManualHeader(bool english) => AiSetupText.T(english, "자동 설치가 안 되면 직접 설치하는 방법", "If automatic install doesn't work");

    public static string ManualHow(AiHostPlatform platform, bool english) => platform == AiHostPlatform.Windows
        ? AiSetupText.T(english, "PowerShell 을 열고 아래 한 줄을 붙여 넣은 뒤 Enter 를 누르세요. 끝나면 「다시 확인」을 누르세요.",
            "Open PowerShell, paste the line below and press Enter. When it finishes, press Check again.")
        : AiSetupText.T(english, "터미널(검은 창)을 열고 아래 한 줄을 붙여 넣은 뒤 Enter 를 누르세요. 끝나면 「다시 확인」을 누르세요.",
            "Open a terminal, paste the line below and press Enter. When it finishes, press Check again.");

    public static string Copy(bool english) => AiSetupText.T(english, "복사", "Copy");

    public static string Copied(bool english) => AiSetupText.T(english, "복사했습니다", "Copied");

    public static string Recheck(bool english) => AiSetupText.T(english, "다시 확인", "Check again");

    public static string StillMissing(bool english) => AiSetupText.T(english, "아직 찾지 못했습니다. 설치가 끝났는지 확인해 주세요.", "Still not found. Check that the install finished.");

    public static string LeaveQuestion(bool english) => AiSetupText.T(english, "설치를 그만둘까요?", "Stop the install?");
}
