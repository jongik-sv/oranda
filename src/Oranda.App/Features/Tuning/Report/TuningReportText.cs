namespace Oranda.App.Features.Tuning.Report;

/// <summary>보고서·대화 상자가 함께 쓰는 절 이름(ko/en). 보고서 본문 언어는 만들 때의 앱 언어를 따른다.</summary>
public static class TuningReportText
{
    public static string L(bool en, string ko, string english) => en ? english : ko;

    public static string SectionTitle(TuningReportSection s, bool en) => s switch
    {
        TuningReportSection.Summary => L(en, "요약 진단", "Summary"),
        TuningReportSection.SqlText => L(en, "SQL 전문", "SQL text"),
        TuningReportSection.Plans => L(en, "실행 계획", "Execution plans"),
        TuningReportSection.Graph => L(en, "계획 그래프", "Plan graph"),
        TuningReportSection.ExecStats => L(en, "실행 통계", "Execution statistics"),
        TuningReportSection.ObjectStats => L(en, "표·인덱스·열 통계", "Table, index and column statistics"),
        TuningReportSection.ChildCursors => L(en, "자식 커서·불일치 사유", "Child cursors and mismatch reasons"),
        TuningReportSection.Waits => L(en, "대기 분해", "Wait breakdown"),
        TuningReportSection.Awr => L(en, "AWR 계획 이력", "AWR plan history"),
        TuningReportSection.Candidates => L(en, "인덱스·재작성 후보", "Index and rewrite candidates"),
        TuningReportSection.Environment => L(en, "환경", "Environment"),
        _ => s.ToString(),
    };

    /// <summary>사용자가 고르지 않은 절의 이유.</summary>
    public static string NotChosen(bool en) => L(en, "만들 때 이 절을 고르지 않았습니다.", "This section was not chosen when the report was made.");

    /// <summary>Diagnostics Pack 선언이 없어 빠진 절의 이유.</summary>
    public static string NoDiagnostics(bool en) => L(en,
        "Diagnostics Pack 보유가 선언되지 않았습니다.",
        "Diagnostics Pack ownership is not declared.");

    /// <summary>팩 절이 빠졌을 때 무엇을 하면 켜지는지.</summary>
    public static string NoDiagnosticsHint(bool en) => L(en,
        "이 접속 프로필의 라이선스가 「없음」이라 dba_hist_* 를 읽지 않았습니다. 회사가 Diagnostics Pack 을 갖고 있으면 접속 관리자에서 라이선스를 고친 뒤 다시 만드세요.",
        "The connection profile declares no license, so dba_hist_* was not read. If your company owns the Diagnostics Pack, change the license in the connection manager and make the report again.");
}
