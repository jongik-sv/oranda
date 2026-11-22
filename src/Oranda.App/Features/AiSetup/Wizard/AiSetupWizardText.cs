using Oranda.App.Features.AiSetup.Contract;

namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>창 틀 글(한/영). 공용 단추·단계 제목은 계약 <see cref="AiSetupText"/>.</summary>
internal static class AiSetupWizardText
{
    /// <summary>아래 왼쪽 진도 글.</summary>
    public static string Progress(int number, int count, bool english) =>
        AiSetupText.T(english, $"{number} / {count} 단계", $"Step {number} of {count}");

    /// <summary>아래 왼쪽 이어 하기 안내.</summary>
    public static string ResumeHint(bool english) =>
        AiSetupText.T(english, "닫아도 다음에 여기서 이어 합니다", "Close any time. You can pick up here later.");

    /// <summary>왼쪽 목록 머리.</summary>
    public static string RailHeader(bool english) => AiSetupText.T(english, "단계", "Steps");

    /// <summary>자리 표시 단계 제목줄.</summary>
    public static string PlaceholderHeadline(bool english) => AiSetupText.T(english, "이 단계는 준비 중입니다", "This step is not ready yet");

    /// <summary>자리 표시 단계 설명.</summary>
    public static string PlaceholderDetail(bool english) =>
        AiSetupText.T(english, "「다음」을 눌러 넘어가세요.", "Press Next to go on.");
}
