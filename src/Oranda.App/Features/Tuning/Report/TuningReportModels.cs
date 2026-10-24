using System;
using System.Collections.Generic;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Tuning;
using Oranda.Language.Tuning;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>보고서 절(시안 tuning-report.html 순서). 바인드 값은 절이 아니라 SQL 전문 절의 선택이다.</summary>
public enum TuningReportSection
{
    Summary,
    SqlText,
    Plans,
    Graph,
    ExecStats,
    ObjectStats,
    ChildCursors,
    Waits,
    Awr,
    Candidates,
    Environment,
}

/// <summary>절 하나의 결과: 값, 수집 실패(이유), 포함 안 함(이유) 중 하나.</summary>
public sealed record ReportPart<T>(T? Value, string? Failure, string? Excluded)
    where T : class
{
#pragma warning disable CA1000 // 만들기 도우미는 형 이름과 함께 읽는 편이 분명하다(ReportPart<T>.Fail).
    public static ReportPart<T> Ok(T value) => new(value, null, null);

    public static ReportPart<T> Fail(string reason) => new(null, reason, null);

    public static ReportPart<T> Exclude(string reason) => new(null, null, reason);
#pragma warning restore CA1000

    public bool HasValue => Value is not null && Failure is null && Excluded is null;
}

/// <summary>자식 커서 하나의 실제 계획. 받지 못했으면 <see cref="Failure"/>.</summary>
public sealed record ReportChildPlan(int ChildNumber, int? InstanceId, CorePlan? Plan, string? Failure)
{
    public ChildCursorRow? Cursor { get; init; }
}

/// <summary>표 하나의 통계 묶음. 표 통계가 없으면 <see cref="Stats"/> 가 null(통계 없음).</summary>
public sealed record ReportObjectStats(string Owner, string Table)
{
    public TableStatsRow? Stats { get; init; }

    public IReadOnlyList<ColumnStatsRow> Columns { get; init; } = [];

    public IReadOnlyList<IndexStatsRow> Indexes { get; init; } = [];

    public IReadOnlyList<IndexColumnsRow>? IndexColumns { get; init; }

    /// <summary>조건 열 중 히스토그램이 있는 열의 버킷.</summary>
    public IReadOnlyList<(string Column, string Kind, IReadOnlyList<HistogramBucket> Buckets)> Histograms { get; init; } = [];

    /// <summary>이 표만 읽지 못했으면 이유.</summary>
    public string? Failure { get; init; }
}

/// <summary>자식 커서 목록과 불일치 사유.</summary>
public sealed record ReportCursors(IReadOnlyList<ChildCursorRow> Children, IReadOnlyList<SharedCursorReason> Reasons)
{
    /// <summary>사유를 읽지 못했으면 이유.</summary>
    public string? ReasonsFailure { get; init; }
}

/// <summary>대기 분해: 출처(gv$session 표본 또는 ASH)와 이벤트 비율(SQL 모니터와 같은 <see cref="WaitBreakdown"/> 규칙).</summary>
public sealed record ReportWaits(IReadOnlyList<WaitEventShare> Events, bool FromAsh, int SampleSeconds);

/// <summary>AWR 에 남은 한 plan_hash 의 계획(두 plan_hash 비교용). 받지 못했으면 <see cref="Failure"/>.</summary>
public sealed record ReportAwrPlan(long PlanHashValue, CorePlan? Plan, string? Failure);

/// <summary>AWR 계획 이력과(plan_hash 가 둘 이상이면) 가장 빠른·느린 plan_hash 의 계획.</summary>
public sealed record ReportAwr(IReadOnlyList<AwrPlanRow> Rows, IReadOnlyList<ReportAwrPlan> Plans);

/// <summary>규칙 기반 후보(TSK-06-05 규칙).</summary>
public sealed record ReportCandidates(IReadOnlyList<IndexAdvice> Indexes, IReadOnlyList<RewriteAdvice> Rewrites);

/// <summary>SQL 전문과 바인드(값을 빼면 <see cref="CapturedBind.Value"/> 는 null).</summary>
public sealed record ReportSqlText(string Text, IReadOnlyList<CapturedBind> Binds)
{
    /// <summary>공유 풀이 아니라 편집기 문장을 썼으면 true.</summary>
    public bool FromEditor { get; init; }
}

/// <summary>만들 때 고른 것(대화 상자 → 수집기·생성기).</summary>
public sealed record TuningReportOptions
{
    public required string SqlId { get; init; }

    /// <summary>담을 절. 빠진 절은 「포함 안 함: 고르지 않음」 으로 남는다.</summary>
    public IReadOnlySet<TuningReportSection> Sections { get; init; } = new HashSet<TuningReportSection>(Enum.GetValues<TuningReportSection>());

    /// <summary>바인드 캡처 값을 담는가(기본 아니오, 형만 적는다).</summary>
    public bool IncludeBindValues { get; init; }

    /// <summary>한 자식만(null = 전체, 실행 수 상위 <see cref="TuningReportCollector.MaxPlans"/>).</summary>
    public int? ChildNumber { get; init; }

    /// <summary>gv$session 대기 표본을 모을 초(팩 없을 때).</summary>
    public int WaitSampleSeconds { get; init; } = 30;

    /// <summary>보고서 본문 언어.</summary>
    public bool English { get; init; }

    /// <summary>편집기 문장(공유 풀에 글이 없을 때 쓴다).</summary>
    public string? EditorText { get; init; }

    public bool Includes(TuningReportSection s) => Sections.Contains(s);
}

/// <summary>보고서 머리의 접속·서버 정보.</summary>
public sealed record ReportContext(string ConnectionName, string User, ConnectionEnvironment Environment, string ServerVersion)
{
    public string? DatabaseName { get; init; }

    public bool IsRac { get; init; }

    /// <summary>보유 선언(서버 값을 알면 둘 중 낮은 것).</summary>
    public LicensePack HeldPack { get; init; }

    public string AppVersion { get; init; } = "0.1.0";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>수집 결과 전체(생성기 입력). 절마다 독립 결과.</summary>
public sealed record TuningReportData(TuningReportOptions Options, ReportContext Context)
{
    public ReportPart<ReportSqlText> SqlText { get; init; } = ReportPart<ReportSqlText>.Exclude("-");

    public ReportPart<IReadOnlyList<ReportChildPlan>> Plans { get; init; } = ReportPart<IReadOnlyList<ReportChildPlan>>.Exclude("-");

    public ReportPart<SqlStatsRow> ExecStats { get; init; } = ReportPart<SqlStatsRow>.Exclude("-");

    public ReportPart<IReadOnlyList<ReportObjectStats>> Objects { get; init; } = ReportPart<IReadOnlyList<ReportObjectStats>>.Exclude("-");

    public ReportPart<ReportCursors> Cursors { get; init; } = ReportPart<ReportCursors>.Exclude("-");

    public ReportPart<ReportWaits> Waits { get; init; } = ReportPart<ReportWaits>.Exclude("-");

    public ReportPart<ReportAwr> Awr { get; init; } = ReportPart<ReportAwr>.Exclude("-");

    public ReportPart<ReportCandidates> Candidates { get; init; } = ReportPart<ReportCandidates>.Exclude("-");

    public ReportPart<IReadOnlyList<TuningEnvRow>> Environment { get; init; } = ReportPart<IReadOnlyList<TuningEnvRow>>.Exclude("-");
}

/// <summary>수집 진행(대화 상자의 막대·글).</summary>
/// <param name="Done">끝난 단계 수</param>
/// <param name="Total">전체 단계 수</param>
/// <param name="Current">지금 하는 일</param>
/// <param name="Finished">끝난 절 이름(순서대로)</param>
public sealed record TuningReportProgress(int Done, int Total, string Current, IReadOnlyList<string> Finished)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}
