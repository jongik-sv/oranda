using Oranda.Core.Connection;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// AI 파티션 추천의 근거(설계 2026-11-18 4장). 앱이 미리 모아 프롬프트에 넣고, 앱 확인·채우기도 이 숫자를 쓴다.
/// 모으지 못한 항목은 빈 목록이고 이유는 <see cref="Skipped"/> 에 남는다.
/// </summary>
/// <param name="CollectedAt">모은 때(로컬). 통계가 오래됐는지(stats-old) 이 시각으로 잰다</param>
public sealed record PartitionEvidence(
    string Owner,
    string Table,
    Dbms Dbms,
    string? ServerVersion,
    long? NumRows,
    long? Blocks,
    DateTime? LastAnalyzed,
    IReadOnlyList<EvidenceColumn> Columns,
    IReadOnlyList<EvidenceIndex> Indexes,
    IReadOnlyList<EvidenceKey> Keys,
    IReadOnlyList<PredicateUse> Predicates,
    IReadOnlyList<ColumnDistribution> Distributions,
    IReadOnlyList<EvidenceSkip> Skipped,
    DateTime CollectedAt)
{
    /// <summary>표 이름만 있고 나머지는 빈 근거.</summary>
    public static PartitionEvidence Empty(string owner, string table, Dbms dbms, DateTime collectedAt, string? serverVersion = null) =>
        new(owner, table, dbms, serverVersion, null, null, null, [], [], [], [], [], [], collectedAt);

    /// <summary>열 하나(이름은 대소문자 무시).</summary>
    public EvidenceColumn? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>표의 열 하나와 통계.</summary>
/// <param name="DataType">사전 표기 형식 이름(<c>DATE</c>·<c>VARCHAR2</c>·<c>TIMESTAMP(6)</c> …)</param>
/// <param name="Histogram">히스토그램 종류(<c>FREQUENCY</c> 등). 없으면 null</param>
/// <param name="DateLike">날짜형 문자열·숫자 판정 결과. 판정하지 않았거나 날짜형이 아니면 null</param>
/// <param name="IsVirtual">가상 열(키로 못 씀, key-missing)</param>
public sealed record EvidenceColumn(
    string Name,
    string DataType,
    bool Nullable,
    long? NumDistinct,
    long? NumNulls,
    string? Histogram,
    DateLike? DateLike,
    bool IsVirtual = false);

/// <summary>날짜형 문자열·숫자의 형식.</summary>
public enum DateLikeFormat
{
    /// <summary><c>20260131</c>.</summary>
    Yyyymmdd,

    /// <summary><c>202601</c>.</summary>
    Yyyymm,

    /// <summary><c>2026-01-31</c>.</summary>
    YyyyDashMmDashDd,
}

/// <summary>날짜형 판정. <see cref="MatchRatio"/> = 표본 중 형식이 맞고 실제 날짜인 비율(0~1).</summary>
public sealed record DateLike(DateLikeFormat Format, double MatchRatio, int SampleSize);

/// <summary>인덱스 하나. <see cref="Columns"/> = 열 순서대로.</summary>
public sealed record EvidenceIndex(string Name, bool Unique, IReadOnlyList<string> Columns, bool PrimaryKey);

/// <summary>제약 종류.</summary>
public enum EvidenceKeyKind
{
    Primary,
    Unique,
    Foreign,
}

/// <summary>PK·UK·FK 와 그 열.</summary>
public sealed record EvidenceKey(string Name, EvidenceKeyKind Kind, IReadOnlyList<string> Columns);

/// <summary>열 하나가 실제 조회 조건에 쓰인 정도(V$SQL_PLAN 기반).</summary>
/// <param name="Executions">그 열이 나온 문장들의 실행 수 합</param>
/// <param name="Statements">그 열이 나온 문장 수</param>
/// <param name="Share">읽은 문장 실행 수 합 중 이 열이 나온 몫(0~1)</param>
public sealed record PredicateUse(string Column, long Executions, int Statements, double Share);

/// <summary>분포 종류.</summary>
public enum DistributionKind
{
    /// <summary>달마다 건수(<see cref="DistributionBucket.Label"/> = <c>YYYY-MM</c>).</summary>
    Monthly,

    /// <summary>값마다 건수(상위 값, <see cref="DistributionBucket.Label"/> = 실제 값 — 가림은 프롬프트에서).</summary>
    TopValues,
}

/// <summary>열 하나의 분포.</summary>
/// <param name="RestCount">버킷 밖 건수(36개월보다 이른 달, 상위 밖 값). 0 = 없음</param>
/// <param name="NullCount">NULL 건수(표본 기준). 모르면 null</param>
/// <param name="SampledRows">읽은 행 수</param>
/// <param name="SamplePercent">표본 비율(%). null = 표본 없이 전체</param>
public sealed record ColumnDistribution(
    string Column,
    DistributionKind Kind,
    IReadOnlyList<DistributionBucket> Buckets,
    long SampledRows,
    double? SamplePercent,
    long RestCount = 0,
    long? NullCount = null);

/// <summary>분포 버킷 하나.</summary>
public sealed record DistributionBucket(string Label, long Count);

/// <summary>근거 항목(확인 창 체크 5개와 같은 묶음).</summary>
public enum EvidenceItem
{
    /// <summary>열·형식.</summary>
    Columns,

    /// <summary>PK·UK·인덱스.</summary>
    Keys,

    /// <summary>표·열 통계.</summary>
    Statistics,

    /// <summary>실제 조회 조건(V$SQL_PLAN).</summary>
    Predicates,

    /// <summary>분포(월별·상위 값).</summary>
    Distributions,
}

/// <summary>건너뛴 이유.</summary>
public enum EvidenceSkipReason
{
    /// <summary>사용자가 확인 창에서 끔.</summary>
    Off,

    /// <summary>권한 없음(ORA-00942·ORA-01031, Tibero 같은 오류).</summary>
    NoPrivilege,

    /// <summary>이 DBMS·버전 공급자가 지원하지 않음.</summary>
    NotSupported,

    /// <summary>통계가 없음(수집 전).</summary>
    NoStatistics,

    /// <summary>그 밖의 오류(<see cref="EvidenceSkip.Detail"/> 에 메시지).</summary>
    Failed,
}

/// <summary>건너뛴 항목 하나. <see cref="Column"/> = 열 하나만 건너뛰었을 때 그 열.</summary>
public sealed record EvidenceSkip(EvidenceItem Item, EvidenceSkipReason Reason, string? Column = null, string? Detail = null);
