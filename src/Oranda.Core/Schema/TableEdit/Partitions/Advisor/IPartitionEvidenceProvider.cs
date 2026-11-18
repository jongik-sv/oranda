using Oranda.Core.Composition;
using Oranda.Core.Sessions;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// 파티션 근거 중 DBMS 사전·표본 조회가 필요한 몫(설계 5장). 구현 등록 = <c>[OrandaService(typeof(IPartitionEvidenceProvider), Dbms = …)]</c>.
/// 모든 조회는 SELECT 다. 지원하지 않으면 null 을 돌려주고(모으기가 「건너뜀(지원 안 함)」으로 적음), 권한 오류는 그대로 던진다(모으기가 「권한 없음」으로 적음).
/// </summary>
public interface IPartitionEvidenceProvider
{
    /// <summary>표 열이 실제 조회 조건에 쓰인 정도(상위 200 문장). null = 지원 안 함.</summary>
    Task<IReadOnlyList<PredicateUse>?> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct);

    /// <summary>문자열·숫자 열이 날짜형인지(표본 500행). null = 지원 안 함 또는 날짜형 아님.</summary>
    Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct);

    /// <summary>
    /// 달마다 건수(최근 36개월 + 나머지). <paramref name="format"/> = 문자열·숫자 날짜형의 형식(DATE·TIMESTAMP 는 null).
    /// <paramref name="samplePercent"/> null = 표본 없이 전체. 반환 null = 지원 안 함.
    /// </summary>
    Task<ColumnDistribution?> SampleMonthlyAsync(ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct);

    /// <summary>상위 <paramref name="top"/> 값의 건수 + 나머지. 반환 null = 지원 안 함.</summary>
    Task<ColumnDistribution?> SampleTopValuesAsync(ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct);
}

/// <summary>기본 구현: 아무것도 지원하지 않음(모든 답 null). 공급자가 없는 DBMS 의 자리.</summary>
public sealed class UnsupportedPartitionEvidenceProvider : IPartitionEvidenceProvider
{
    public static UnsupportedPartitionEvidenceProvider Instance { get; } = new();

    private UnsupportedPartitionEvidenceProvider()
    {
    }

    public Task<IReadOnlyList<PredicateUse>?> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PredicateUse>?>(null);

    public Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct) =>
        Task.FromResult<DateLike?>(null);

    public Task<ColumnDistribution?> SampleMonthlyAsync(ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct) =>
        Task.FromResult<ColumnDistribution?>(null);

    public Task<ColumnDistribution?> SampleTopValuesAsync(ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct) =>
        Task.FromResult<ColumnDistribution?>(null);
}

/// <summary>
/// 세션의 DBMS 로 공급자를 골라 넘긴다(D-046). 그 DBMS 구현이 없으면 예외 대신 <see cref="UnsupportedPartitionEvidenceProvider"/> —
/// 근거가 덜 모일 뿐 추천은 할 수 있어서다.
/// </summary>
[OrandaService(typeof(IPartitionEvidenceProvider))]
public sealed class PartitionEvidenceProviderDispatcher : IPartitionEvidenceProvider
{
    private readonly IDbmsServiceSelector _selector;

    public PartitionEvidenceProviderDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    /// <summary>세션 DBMS 의 공급자(없으면 지원 안 함 구현).</summary>
    public IPartitionEvidenceProvider For(ISession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return _selector.Find<IPartitionEvidenceProvider>(s.Profile.Dbms) ?? UnsupportedPartitionEvidenceProvider.Instance;
    }

    public Task<IReadOnlyList<PredicateUse>?> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct) =>
        For(s).GetPredicateUseAsync(s, owner, table, ct);

    public Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct) =>
        For(s).DetectDateLikeAsync(s, owner, table, column, ct);

    public Task<ColumnDistribution?> SampleMonthlyAsync(ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct) =>
        For(s).SampleMonthlyAsync(s, owner, table, column, format, samplePercent, ct);

    public Task<ColumnDistribution?> SampleTopValuesAsync(ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct) =>
        For(s).SampleTopValuesAsync(s, owner, table, column, top, samplePercent, ct);
}
