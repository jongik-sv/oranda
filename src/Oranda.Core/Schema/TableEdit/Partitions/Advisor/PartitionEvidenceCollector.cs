using Oranda.Core.Composition;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// 무엇을 모을지(확인 창 체크). 열·키·통계는 늘 모은다.
/// </summary>
/// <param name="Predicates">조회 조건(V$SQL_PLAN, PA-2)</param>
/// <param name="Distributions">분포(월별·상위 값, PA-1)</param>
/// <param name="SamplePercent">표본 비율(%) 고정값. null = PA-1 규칙으로 행 수에서 정함</param>
public sealed record EvidenceOptions(bool Predicates = true, bool Distributions = true, double? SamplePercent = null)
{
    /// <summary>확인 창 기본값(둘 다 켬, 표본 자동).</summary>
    public static EvidenceOptions Default { get; } = new();
}

/// <summary>모으기 단계(진행 표시 순서).</summary>
public enum EvidenceStep
{
    /// <summary>열·키.</summary>
    ColumnsAndKeys,

    /// <summary>표·열 통계.</summary>
    Statistics,

    /// <summary>조회 조건.</summary>
    Predicates,

    /// <summary>분포(열마다 한 번씩 알림).</summary>
    Distributions,
}

/// <summary>진행 알림. 분포 단계는 <see cref="Column"/>·<see cref="Index"/>/<see cref="Total"/> 을 채운다.</summary>
public sealed record EvidenceProgress(EvidenceStep Step, string? Column = null, int Index = 0, int Total = 0);

/// <summary>근거 모으기(화면·시험이 가짜로 바꿀 수 있게 인터페이스).</summary>
public interface IPartitionEvidenceCollector
{
    /// <summary>
    /// 표 하나의 근거를 모은다. 항목 하나가 실패하면(권한·지원 안 함) 그 항목만 <see cref="PartitionEvidence.Skipped"/> 에 적고 계속한다.
    /// 취소는 <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<PartitionEvidence> CollectAsync(ISession s, DbObjectRef table, EvidenceOptions options, IProgress<EvidenceProgress>? progress, CancellationToken ct);
}

/// <summary>
/// 기본 모으기: <see cref="IMetadataProvider"/>(열·제약) + <see cref="ITuningService"/>(표·열 통계, 인덱스 열) + <see cref="IPartitionEvidenceProvider"/>(조건·분포).
/// </summary>
[OrandaService(typeof(IPartitionEvidenceCollector))]
public sealed class PartitionEvidenceCollector : IPartitionEvidenceCollector
{
    private readonly IMetadataProvider _metadata;
    private readonly ITuningService _tuning;
    private readonly IPartitionEvidenceProvider _provider;

    public PartitionEvidenceCollector(IMetadataProvider metadata, ITuningService tuning, IPartitionEvidenceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(provider);
        _metadata = metadata;
        _tuning = tuning;
        _provider = provider;
    }

    public Task<PartitionEvidence> CollectAsync(ISession s, DbObjectRef table, EvidenceOptions options, IProgress<EvidenceProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        // PC1 껍데기: 표 이름과 DBMS 만. 실제 모으기는 PC2.
        return Task.FromResult(PartitionEvidence.Empty(table.Owner, table.Name, s.Profile.Dbms, DateTime.Now, s.Server?.Version));
    }
}
