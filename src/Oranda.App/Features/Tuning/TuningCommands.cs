namespace Oranda.App.Features.Tuning;

/// <summary>TSK-06-01 「튜닝」 메뉴 — 명령 ID·패널 ID 상수만(commands.md 「튜닝」 절). 처리기는 TSK-06-03~05 가 만든다.</summary>
public static class TuningCommands
{
    /// <summary>통계 보기 문서 패널(TSK-06-03).</summary>
    public const string StatsPanelId = "oranda.panel.tuningStats";

    /// <summary>자식 커서 문서 패널(TSK-06-03).</summary>
    public const string ChildCursorsPanelId = "oranda.panel.childCursors";

    /// <summary>트레이스 보기 문서 패널(TSK-06-04).</summary>
    public const string TraceViewerPanelId = "oranda.panel.traceViewer";

    /// <summary>튜닝 도우미 문서 패널(TSK-06-05).</summary>
    public const string AssistantPanelId = "oranda.panel.tuningAssistant";

    /// <summary>튜닝 명령 ID 전체(등록은 각 Task).</summary>
    public static readonly string[] Ids =
    [
        "oranda.tuning.stats",
        "oranda.tuning.gatherStatsScript",
        "oranda.tuning.childCursors",
        "oranda.tuning.traceViewer",
        "oranda.tuning.optimizerTrace",
        "oranda.tuning.assistant",
        "oranda.tuning.indexCandidates",
        "oranda.tuning.rewriteCandidates",
        "oranda.tuning.benchmark",
        "oranda.tuning.awrHistory",
        "oranda.tuning.ashWaits",
        "oranda.tuning.planControls",
        "oranda.tuning.tuningAdvisorScript",
        "oranda.tuning.baselineScript",
        "oranda.tuning.report",
        "oranda.tuning.recommend",
    ];
}
