using Oranda.App.Features.AiSetup.Contract;

namespace Oranda.App.Features.AiSetup.Entry;

/// <summary>진입점 3곳(메뉴·첫 실행 묻기·실패 띠)의 한/영 글. <see cref="AiSetupText.T"/> 방식.</summary>
public static class AiSetupEntryText
{
    /// <summary>메뉴 제목(한국어 원문, 영어는 <c>strings-en.json</c> 의 <c>Cmd.oranda.ai.setup.open</c>).</summary>
    public const string MenuTitle = "AI 연결 도우미…";

    /// <summary>「도구 → AI 설정…」 메뉴 제목(영어는 <c>strings-en.json</c> 의 <c>Cmd.oranda.ai.settings.open</c>).</summary>
    public const string SettingsMenuTitle = "AI 설정…";

    /// <summary>실패 띠·설정의 단추 글.</summary>
    public static string OpenButton(bool english) => AiSetupText.T(english, "AI 연결 도우미 열기", "Open AI Setup Assistant");

    /// <summary>실패 띠의 안내 한 줄(단추 옆).</summary>
    public static string BandHint(bool english) => AiSetupText.T(english, "AI 가 아직 연결되지 않았습니다. 도우미를 따라 하면 단추만 눌러 연결할 수 있습니다.", "AI is not connected yet. The setup assistant walks you through it with a few clicks.");

    /// <summary>첫 실행 묻기 창 제목.</summary>
    public static string AskTitle(bool english) => AiSetupText.T(english, "AI 연결", "Connect AI");

    /// <summary>첫 실행 묻기 본문.</summary>
    public static string AskMessage(bool english) => AiSetupText.T(english, "AI 를 연결할까요?\n도우미를 따라 하면 몇 분 안에 끝납니다. 나중에 도구 메뉴의 「AI 설정…」에서도 열 수 있습니다.", "Connect AI now?\nThe setup assistant takes a few minutes. You can also open it later from Tools > AI Settings….");

    /// <summary>첫 실행 묻기 [나중에].</summary>
    public static string Later(bool english) => AiSetupText.T(english, "나중에", "Later");
}
