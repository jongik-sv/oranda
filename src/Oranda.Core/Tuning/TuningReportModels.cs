namespace Oranda.Core.Tuning;

// SQL 튜닝 HTML 보고서(TSK-06-07, TN-17) 가 더 읽는 값. 조회는 카탈로그 tuning.sql_stats·tuning.report_env(SELECT 전용, 팩 없음).

/// <summary>sql_id 의 누적 실행 통계(모든 인스턴스·자식 합). 출처: gv$sql.</summary>
/// <param name="Children">자식 커서 행 수(인스턴스별 포함)</param>
/// <param name="Instances">커서가 있는 인스턴스 수</param>
/// <param name="Executions">executions 합</param>
/// <param name="ElapsedMicros">elapsed_time 합(마이크로초)</param>
/// <param name="CpuMicros">cpu_time 합(마이크로초)</param>
/// <param name="BufferGets">buffer_gets 합</param>
/// <param name="DiskReads">disk_reads 합</param>
/// <param name="RowsProcessed">rows_processed 합</param>
public sealed record SqlStatsRow(
    int Children, int Instances, long Executions, long ElapsedMicros, long CpuMicros, long BufferGets, long DiskReads, long RowsProcessed)
{
    /// <summary>application_wait_time 합(잠금 대기 등, 마이크로초).</summary>
    public long ApplicationWaitMicros { get; init; }

    /// <summary>concurrency_wait_time 합(마이크로초).</summary>
    public long ConcurrencyWaitMicros { get; init; }

    /// <summary>cluster_wait_time 합(마이크로초).</summary>
    public long ClusterWaitMicros { get; init; }

    /// <summary>user_io_wait_time 합(마이크로초).</summary>
    public long UserIoWaitMicros { get; init; }

    /// <summary>parse_calls 합.</summary>
    public long ParseCalls { get; init; }

    /// <summary>fetches 합.</summary>
    public long Fetches { get; init; }

    /// <summary>parsing_schema_name(여럿이면 하나).</summary>
    public string? ParsingSchema { get; init; }

    /// <summary>module(여럿이면 하나).</summary>
    public string? Module { get; init; }

    /// <summary>last_active_time 의 최댓값.</summary>
    public DateTime? LastActiveTime { get; init; }

    /// <summary>first_load_time 의 최솟값(서버 글 그대로, 예: 2026-10-24/14:02:11).</summary>
    public string? FirstLoadTime { get; init; }
}

/// <summary>보고서 「환경」 절의 값 종류.</summary>
public enum TuningEnvKind
{
    /// <summary>v$version 배너.</summary>
    Version,

    /// <summary>v$parameter 의 optimizer 관련 일부.</summary>
    Parameter,

    /// <summary>nls_database_parameters 일부.</summary>
    Nls,
}

/// <summary>보고서 「환경」 절 한 줄. 출처: v$version·v$parameter·nls_database_parameters.</summary>
public sealed record TuningEnvRow(TuningEnvKind Kind, string Name, string? Value);
