using Oranda.App.Shell;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>「AI 를 쓸 접속」 단계의 한/영 글. 글 공급자가 없으면 앱 글 입구의 언어를 본다.</summary>
internal sealed class AccessText(IShellText? text)
{
    public bool IsEnglish => text is not null ? text.Language == "en" : Contract.AiSetupText.CurrentIsEnglish;

    public string T(string ko, string en) => IsEnglish ? en : ko;

    public string Title => T("AI 를 쓸 접속", "Connections that use AI");

    public string Intro => T(
        "켜진 접속에서는 AI 가 테이블 구조와 실행 계획을 보고 도와줍니다. 나중에 접속 편집에서 바꿀 수 있어요.",
        "On connections that are turned on, AI looks at table structure and execution plans to help. You can change this later in the connection editor.");

    public string Environment(ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Production => T("운영", "Production"),
        ConnectionEnvironment.Test => T("시험", "Test"),
        _ => T("개발", "Development"),
    };

    public string OnDescription => T("테이블 구조 · 실행 계획 · 통계", "Table structure · execution plans · statistics");

    public string ProductionDescription => T(
        "값은 항상 가려서 보내고, 행 값은 보내지 않아요.",
        "Values are always masked, and row values are never sent.");

    public string OffDescription => T("AI 를 쓰지 않아요", "AI is not used");

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
