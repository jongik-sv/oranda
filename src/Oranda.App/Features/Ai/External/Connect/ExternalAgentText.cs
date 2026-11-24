using Oranda.App.Features.Localization;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 연결 창·스킬 설치 창의 한/영 글(기존 <c>AiSettingsText</c> 방식). 지금 화면 언어가 영어면 영어, 아니면 한국어.
/// 상태 표시줄 띠는 문자열 표(<c>AgentLink.*</c>)를 쓴다(운영 포함 표시처럼 2단계에서 더한 글은 여기 둔다).
/// </summary>
internal static class ExternalAgentText
{
    public static bool IsEnglish => UiText.Current?.Language == "en";

    public static string T(string ko, string en) => IsEnglish ? en : ko;

    /// <summary>「로컬 모델(가리지 않음)」 체크 글(D4).</summary>
    public static string LocalModel => T("로컬 모델(가리지 않음)", "Local model (no masking)");

    /// <summary>「로컬 모델」을 켰을 때 아래에 보이는 경고(D4: 진짜 로컬인지 Oranda 는 확인하지 못한다).</summary>
    public static string LocalModelWarning => T(
        "민감 열·리터럴·오류 글을 가리지 않고 결과 값을 그대로 보냅니다. 이 컴퓨터나 사내에서 도는 모델일 때만 켜십시오 — Oranda 는 모델이 정말 로컬인지 확인하지 못합니다.",
        "Sensitive columns, literals and error text are sent unmasked. Turn this on only for a model running on this computer or inside your company — Oranda cannot check that the model is really local.");

    /// <summary>운영 행 경고(D5, §3.2): 운영 접속에서 SELECT 가 자동으로 실행되는 수준일 때.</summary>
    public static string ProductionRunsWarning => T("운영 접속 — LLM 이 SELECT 를 자동 실행합니다", "Production connection — the LLM runs SELECT statements automatically");

    /// <summary>운영 행 경고 한 줄 더(D5, §3.2): 로컬 모델과 운영을 함께 골랐을 때.</summary>
    public static string ProductionUnmaskedWarning => T("운영 값이 가리지 않고 나갑니다", "Production values are sent unmasked");

    /// <summary>운영 상한이 메타데이터뿐일 때의 「쓸 접속」 설명(스위치를 끈 상태).</summary>
    public static string ConnectionsCaptionMetadataOnly => T(
        "개발은 기본으로 고릅니다. 운영은 메타데이터만 쓰고, 고르려면 접속 이름을 다시 입력합니다.",
        "Development is chosen by default. Production gives metadata only and needs its name typed again.");

    /// <summary>운영에서도 조회를 실행할 때의 「쓸 접속」 설명(D5).</summary>
    public static string ConnectionsCaptionProductionRuns => T(
        "개발은 기본으로 고릅니다. 운영은 기본으로 고르지 않고, 고르려면 접속 이름을 다시 입력합니다.",
        "Development is chosen by default. Production is never chosen by default and needs its name typed again.");

    /// <summary>「조회 허용」 체크 글: 운영 상한이 메타데이터뿐일 때(개발·시험만 올린다).</summary>
    /// <summary>「조회 허용」 체크 글: 운영도 올릴 때(운영 처음 값은 「조회 실행」, 최대는 「조회+행 값」).</summary>
    public static string ConnectionsTitle => T("에이전트가 볼 수 있는 접속", "Connections the agent can see");

    public static string LevelsLink => T("수준 바꾸기: 설정 > AI 의 접속 표 열기", "Change levels: open the connection table in Settings > AI");

    public static string OnlyChooseNote => T(
        "이 창에서는 「이번 연결에 쓸 접속」만 고릅니다. 접속마다 볼 수 있는 수준은 한곳(설정 > AI)에서만 바꿉니다.",
        "In this window you only choose the connections for this session. What each connection lets AI see is changed in one place: Settings > AI.");

    /// <summary>운영 줄 수준 글 뒤에 붙는 말(Mac 시안 문구: 「조회 실행 (고르려면 접속 이름을 다시 입력)」).</summary>
    public static string ProductionLevelNote => T(" (고르려면 접속 이름을 다시 입력)", " (type the connection name again to choose it)");

    /// <summary>외부 상한이 실제로 낮췄지만 ① 표에서 이미 정한 운영 줄의 말.</summary>
    public static string CappedLevelNote => T(" (외부 상한)", " (external cap)");

    /// <summary>외부 상한이 실제로 수준을 낮춘 운영 줄의 말.</summary>
    public static string ProductionCappedLevelNote => T(" (외부 상한 · 고르려면 접속 이름을 다시 입력)", " (external cap · type the connection name again to choose it)");

    /// <summary>상태 표시줄 띠: 허가에 운영 접속이 들어 있다(§3.2 경고색 띠).</summary>
    public static string StatusProduction => T("운영 포함", "includes production");
}
