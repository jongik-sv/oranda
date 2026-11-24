using Oranda.App.Shell;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>「AI 가 볼 수 있는 접속」 표(설정 「AI」 탭 ①·도우미 「AI 를 쓸 접속」 단계)의 한/영 글. 글 공급자가 없으면 앱 글 입구의 언어를 본다.</summary>
internal sealed class AccessText(IShellText? text)
{
    public bool IsEnglish => text is not null ? text.Language == "en" : Contract.AiSetupText.CurrentIsEnglish;

    public string T(string ko, string en) => IsEnglish ? en : ko;

    /// <summary>도우미 단계 제목(왼쪽 단계 목록과 같다).</summary>
    public string Title => T("AI 를 쓸 접속", "Connections that use AI");

    /// <summary>도우미 단계 안내(처음 값).</summary>
    public string Intro => T(
        "처음 값: 개발·시험은 「계획」, 운영은 「조회 실행」(SELECT 를 돌려도 AI 에는 행 수만 갑니다).",
        "Initial values: development and test use \"Plan\", production uses \"Run SELECT\" (even when a SELECT runs, AI gets only the row count).");

    /// <summary>도우미 단계 아래 파란 띠.</summary>
    public string SharedNote => T(
        "이 표는 설정 > AI 의 「1. AI 가 볼 수 있는 접속」 과 같은 표입니다. 여기서 바꾸면 거기도 바뀌고, 밖의 AI 에이전트도 이 값을 따릅니다.",
        "This is the same table as \"1. Connections AI can see\" in Settings > AI. A change here changes it there too, and outside AI agents follow these values as well.");

    /// <summary>표 아래 한 줄 뜻풀이.</summary>
    public string Legend => T(
        "끔: AI 가 접속을 아예 모름 · 메타데이터: 테이블·컬럼 이름 · 계획: + 실행 계획 · 조회 실행: + SELECT 실행(행 수만) · 조회+행 값: + 결과 값",
        "Off: AI does not know the connection · Metadata: table and column names · Plan: + execution plans · Run SELECT: + runs SELECT (row counts only) · SELECT + rows: + result values");

    public string Environment(ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Production => T("운영", "Production"),
        ConnectionEnvironment.Test => T("시험", "Test"),
        _ => T("개발", "Development"),
    };

    /// <summary>수준 이름(단추·요약 줄).</summary>
    public string LevelName(AiAccessLevel level) => level switch
    {
        AiAccessLevel.Off => T("끔", "Off"),
        AiAccessLevel.Metadata => T("메타데이터", "Metadata"),
        AiAccessLevel.Plan => T("계획", "Plan"),
        AiAccessLevel.Query => T("조회 실행", "Run SELECT"),
        _ => T("조회+행 값", "SELECT + rows"),
    };

    /// <summary>
    /// 줄 설명(고른 수준의 뜻). 운영 줄: 「조회+행 값」 이면 운영 데이터가 AI 로 간다는 경고, 그 밖(끔 제외)은
    /// 「회사 밖 AI 에는 값을 가려서 보내고 …(「로컬 AI」는 값까지 봐요)」 를 붙인다.
    /// </summary>
    public string LevelDescription(AiAccessLevel level, bool production) => LevelDescription(level, production, maskSensitive: true);

    /// <summary>줄 설명. <paramref name="maskSensitive"/> 가 꺼져 있으면 「조회+행 값」 의 「민감 컬럼은 가려요」 를 「민감 컬럼도 보내요」 로 바꾼다(D-069).</summary>
    public string LevelDescription(AiAccessLevel level, bool production, bool maskSensitive) => (production, maskSensitive) switch
    {
        (_, false) when level == AiAccessLevel.QueryRows => RowsUnmaskedDescription + (production ? " · " + ProductionRowsWarning : string.Empty),
        _ => LevelDescriptionMasked(level, production),
    };

    private string LevelDescriptionMasked(AiAccessLevel level, bool production) => production switch
    {
        true when level == AiAccessLevel.QueryRows => LevelDescription(level) + " · " + ProductionRowsWarning,
        true when level != AiAccessLevel.Off => LevelDescription(level) + " · " + ProductionDescription,
        _ => LevelDescription(level),
    };

    /// <summary>수준의 뜻.</summary>
    public string LevelDescription(AiAccessLevel level) => level switch
    {
        AiAccessLevel.Off => T("AI 를 쓰지 않아요", "AI is not used"),
        AiAccessLevel.Metadata => T("테이블·컬럼 이름", "Table and column names"),
        AiAccessLevel.Plan => T("테이블 구조 · 실행 계획 · 통계", "Table structure · execution plans · statistics"),
        AiAccessLevel.Query => T("+ SELECT 실행(행 수만, 값은 보내지 않아요)", "+ runs SELECT (row counts only, no values)"),
        _ => T("+ 결과 값(민감 컬럼은 가려요)", "+ result values (sensitive columns masked)"),
    };

    public string ProductionDescription => T(
        "회사 밖 AI 에는 값을 가려서 보내고 행 값은 보내지 않아요(「로컬 AI」는 값까지 봐요).",
        "AI outside the company gets masked values and no row values (a \"Local AI\" sees the values).");

    private string RowsUnmaskedDescription => T(
        "+ 결과 값(전화·이메일·이름 같은 민감 컬럼도 보내요. 비밀번호·주민번호·카드·계좌·주소는 가려요)",
        "+ result values (sensitive columns such as phone, e-mail and names are sent too; passwords, resident numbers, cards, accounts and addresses stay masked)");

    /// <summary>운영 줄의 민감 가림 확인란 글(D-069).</summary>
    public string MaskSensitiveLabel => T("민감 데이터 가리기(전화·이메일·이름 등)", "Mask sensitive data (phone, e-mail, names …)");

    /// <summary>가림을 끈 운영 줄 아래 경고.</summary>
    public string MaskOffWarning => T(
        "꺼 두면 「조회+행 값」에서 전화·이메일·이름 같은 값도 AI 로 갑니다. 비밀번호·주민번호·카드·계좌·주소·인증값은 그래도 가립니다.",
        "When off, values such as phone numbers, e-mail addresses and names also go to AI at \"SELECT + rows\". Passwords, resident numbers, cards, accounts, addresses and credentials stay masked.");

    /// <summary>운영 「조회+행 값」 경고: 운영 데이터 값이 AI 업체로 간다.</summary>
    public string ProductionRowsWarning => T(
        "운영 데이터 값이 AI 업체로 갑니다. 학습에 쓰지 않는 기업용 AI 일 때만 고르세요.",
        "Production data values go to the AI provider. Choose this only with an enterprise AI that does not train on your data.");

    /// <summary>운영 줄 옆 한 줄: 수준을 올릴 때 이름을 다시 입력한다.</summary>
    public string ProductionLimit => T("운영은 올릴 때 접속 이름을 다시 입력", "Production: type the connection name to raise it");

    public string ConfirmPlaceholder(string name) => T($"올리려면 {name} 를 그대로 입력", $"Type {name} to raise it");

    public string SwitchName(string name) => T($"{name} 에서 AI 쓰기", $"Use AI on {name}");

    public string SkipNotice => T(
        "모두 꺼져 있어요. 다음의 「해 보기」는 건너뛰어요.",
        "Everything is off, so the next \"Try it\" step is skipped.");

    public string NoConnections => T(
        "아직 만든 접속이 없어요. 접속을 만든 뒤 접속 편집에서 AI 를 켤 수 있어요. 「해 보기」는 건너뛰어요.",
        "There are no connections yet. After you create one, you can turn AI on in the connection editor. \"Try it\" is skipped.");

    public string LoadFailed => T("접속 목록을 읽지 못했어요: ", "Could not read the connection list: ");

    public string SaveFailed(string name) => T($"「{name}」 접속을 저장하지 못했어요: ", $"Could not save the connection \"{name}\": ");
}
