using Oranda.Core.Composition;
using Oranda.Core.Execution;
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
/// 기본 모으기: <see cref="IMetadataProvider"/>(열·제약·인덱스) + <see cref="ITuningService"/>(표·열 통계) + <see cref="IPartitionEvidenceProvider"/>(조건·날짜형·분포).
/// 항목 하나의 오류는 그 항목만 건너뛴다 — 「볼 수 없음」(객체 없음·권한 부족) = 권한 없음, 공급자 null·<see cref="NotSupportedException"/> = 지원 안 함, 그 밖 = 실패.
/// </summary>
[OrandaService(typeof(IPartitionEvidenceCollector))]
public sealed class PartitionEvidenceCollector : IPartitionEvidenceCollector
{
    /// <summary>분포를 읽는 열 수 상한(설계 5장).</summary>
    public const int MaxDistributionColumns = 8;

    /// <summary>상위 값 개수.</summary>
    public const int TopValueCount = 20;

    /// <summary>코드 열로 보는 고유값 수 상한.</summary>
    public const long CodeColumnMaxDistinct = 200;

    /// <summary>날짜형으로 보는 형식 일치 비율 하한(PA-10).</summary>
    public const double DateLikeMinRatio = 0.95;

    /// <summary>날짜형 판정을 하는 열 수 상한(조건 사용 순).</summary>
    public const int MaxDateLikeProbes = 16;

    private readonly IMetadataProvider _metadata;
    private readonly ITuningService _tuning;
    private readonly IPartitionEvidenceProvider _provider;
    private readonly IDbmsServiceSelector? _selector;

    /// <param name="selector">오류 분류기(<see cref="IDbErrorClassifier"/>)를 세션 DBMS 로 꺼내는 곳. null = 권한 오류도 「실패」로 적음</param>
    public PartitionEvidenceCollector(IMetadataProvider metadata, ITuningService tuning, IPartitionEvidenceProvider provider, IDbmsServiceSelector? selector = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(provider);
        _metadata = metadata;
        _tuning = tuning;
        _provider = provider;
        _selector = selector;
    }

    public async Task<PartitionEvidence> CollectAsync(ISession s, DbObjectRef table, EvidenceOptions options, IProgress<EvidenceProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var errors = _selector?.Find<IDbErrorClassifier>(s.Profile.Dbms);
        var skipped = new List<EvidenceSkip>();
        var evidence = PartitionEvidence.Empty(table.Owner, table.Name, s.Profile.Dbms, DateTime.Now, s.Server?.Version);

        // 1. 열·키
        progress?.Report(new EvidenceProgress(EvidenceStep.ColumnsAndKeys));
        TableInfo? info = null;
        try
        {
            info = await _metadata.GetTableAsync(s, table, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            skipped.Add(Skip(EvidenceItem.Columns, ex, errors));
            skipped.Add(Skip(EvidenceItem.Keys, ex, errors));
        }

        List<ColumnInfo> columns = info is null ? [] : [.. info.Columns.OrderBy(c => c.Position)];
        if (info is not null)
        {
            evidence = evidence with { Keys = Keys(info), Indexes = Indexes(info) };
        }

        // 2. 통계
        ct.ThrowIfCancellationRequested();
        progress?.Report(new EvidenceProgress(EvidenceStep.Statistics));
        IReadOnlyList<ColumnStatsRow> columnStats = [];
        try
        {
            var stats = await _tuning.GetTableStatsAsync(s, table.Owner, table.Name, ct).ConfigureAwait(false);
            if (stats is null)
            {
                skipped.Add(new EvidenceSkip(EvidenceItem.Statistics, EvidenceSkipReason.NoStatistics));
            }
            else
            {
                evidence = evidence with { NumRows = stats.NumRows, Blocks = stats.Blocks, LastAnalyzed = stats.LastAnalyzed };
            }

            columnStats = await _tuning.GetColumnStatsAsync(s, table.Owner, table.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            skipped.Add(Skip(EvidenceItem.Statistics, ex, errors));
        }

        var statsByName = columnStats
            .GroupBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var evidenceColumns = columns.Select(c => ToEvidence(c, statsByName.GetValueOrDefault(c.Name))).ToList();

        // 3. 조회 조건
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<PredicateUse> predicates = [];
        if (!options.Predicates)
        {
            skipped.Add(new EvidenceSkip(EvidenceItem.Predicates, EvidenceSkipReason.Off));
        }
        else
        {
            progress?.Report(new EvidenceProgress(EvidenceStep.Predicates));
            try
            {
                var uses = await _provider.GetPredicateUseAsync(s, table.Owner, table.Name, ct).ConfigureAwait(false);
                if (uses is null)
                {
                    skipped.Add(new EvidenceSkip(EvidenceItem.Predicates, EvidenceSkipReason.NotSupported));
                }
                else
                {
                    predicates = uses;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add(Skip(EvidenceItem.Predicates, ex, errors));
            }
        }

        // 4. 분포
        ct.ThrowIfCancellationRequested();
        var distributions = new List<ColumnDistribution>();
        if (!options.Distributions)
        {
            skipped.Add(new EvidenceSkip(EvidenceItem.Distributions, EvidenceSkipReason.Off));
        }
        else if (evidenceColumns.Count > 0)
        {
            progress?.Report(new EvidenceProgress(EvidenceStep.Distributions));
            var percent = options.SamplePercent ?? EvidenceSampling.PercentFor(evidence.NumRows);
            var executions = predicates
                .GroupBy(p => p.Column, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Executions), StringComparer.OrdinalIgnoreCase);
            long Uses(string column) => executions.GetValueOrDefault(column);
            var pkSingle = info?.Constraints.FirstOrDefault(k => k.Kind == ConstraintKind.PrimaryKey) is { Columns.Count: 1 } pk ? pk.Columns[0] : null;
            bool Eligible(EvidenceColumn c) => !c.IsVirtual && !string.Equals(c.Name, pkSingle, StringComparison.OrdinalIgnoreCase);

            // 4-1. 날짜형 판정(문자열·숫자 열, 조건 사용 순으로 상한까지)
            var probes = evidenceColumns
                .Select((c, i) => (Column: c, Position: i))
                .Where(x => Eligible(x.Column) && MayBeDateLike(columns[x.Position]))
                .OrderByDescending(x => Uses(x.Column.Name)).ThenBy(x => x.Position)
                .Take(MaxDateLikeProbes)
                .ToList();
            foreach (var (column, position) in probes)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await _provider.DetectDateLikeAsync(s, table.Owner, table.Name, column.Name, ct).ConfigureAwait(false) is { } dateLike)
                    {
                        evidenceColumns[position] = column with { DateLike = dateLike };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped.Add(Skip(EvidenceItem.Distributions, ex, errors, column.Name));
                }
            }

            // 4-2. 분포 열: DATE·TIMESTAMP + 날짜형 + 저카디널리티 코드 열, 조건 사용이 많은 순 최대 8열
            var targets = evidenceColumns
                .Select((c, i) => (Column: c, Position: i, Target: Target(c)))
                .Where(x => x.Target is not null && Eligible(x.Column))
                .OrderByDescending(x => Uses(x.Column.Name)).ThenBy(x => x.Position)
                .Take(MaxDistributionColumns)
                .ToList();
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (column, _, target) = targets[i];
                progress?.Report(new EvidenceProgress(EvidenceStep.Distributions, column.Name, i + 1, targets.Count));
                try
                {
                    var d = target!.Value.Kind == DistributionKind.Monthly
                        ? await _provider.SampleMonthlyAsync(s, table.Owner, table.Name, column.Name, target.Value.Format, percent, ct).ConfigureAwait(false)
                        : await _provider.SampleTopValuesAsync(s, table.Owner, table.Name, column.Name, TopValueCount, percent, ct).ConfigureAwait(false);
                    if (d is null)
                    {
                        skipped.Add(new EvidenceSkip(EvidenceItem.Distributions, EvidenceSkipReason.NotSupported, column.Name));
                    }
                    else
                    {
                        distributions.Add(d);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped.Add(Skip(EvidenceItem.Distributions, ex, errors, column.Name));
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        return evidence with
        {
            Columns = evidenceColumns,
            Predicates = predicates,
            Distributions = distributions,
            Skipped = skipped,
        };
    }

    /// <summary>열 → 분포 종류(대상이 아니면 null).</summary>
    private static (DistributionKind Kind, DateLikeFormat? Format)? Target(EvidenceColumn c)
    {
        if (IsDateType(c.DataType))
        {
            return (DistributionKind.Monthly, null);
        }

        if (c.DateLike is { MatchRatio: >= DateLikeMinRatio } dateLike)
        {
            return (DistributionKind.Monthly, dateLike.Format);
        }

        return (IsCharType(c.DataType) || IsNumberType(c.DataType)) && c.NumDistinct is <= CodeColumnMaxDistinct
            ? (DistributionKind.TopValues, null)
            : null;
    }

    /// <summary>날짜형 판정 대상: 길이 6~10(또는 모름) 문자열, 소수 없는 6~8 자리(또는 자리 모름) 숫자.</summary>
    private static bool MayBeDateLike(ColumnInfo c)
    {
        if (IsCharType(c.DataType))
        {
            return c.Length is null or (>= 6 and <= 10);
        }

        return IsNumberType(c.DataType) && c.Scale is null or 0 && c.Precision is null or (>= 6 and <= 8);
    }

    private static bool IsDateType(string type) =>
        type.Equals("DATE", StringComparison.OrdinalIgnoreCase) || type.StartsWith("TIMESTAMP", StringComparison.OrdinalIgnoreCase);

    private static bool IsCharType(string type) =>
        type.ToUpperInvariant() is "VARCHAR2" or "VARCHAR" or "CHAR" or "NVARCHAR2" or "NCHAR";

    private static bool IsNumberType(string type) =>
        type.ToUpperInvariant() is "NUMBER" or "INTEGER";

    private static EvidenceColumn ToEvidence(ColumnInfo c, ColumnStatsRow? stats) => new(
        c.Name,
        c.DataType,
        c.IsNullable,
        stats?.NumDistinct,
        stats?.NumNulls,
        stats?.Histogram is { } h && !string.IsNullOrWhiteSpace(h) && !h.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? h : null,
        null,
        c.IsVirtual);

    private static IReadOnlyList<EvidenceKey> Keys(TableInfo info) =>
    [
        .. info.Constraints
            .Select(k => k.Kind switch
            {
                ConstraintKind.PrimaryKey => new EvidenceKey(k.Name, EvidenceKeyKind.Primary, k.Columns),
                ConstraintKind.Unique => new EvidenceKey(k.Name, EvidenceKeyKind.Unique, k.Columns),
                ConstraintKind.ForeignKey => new EvidenceKey(k.Name, EvidenceKeyKind.Foreign, k.Columns),
                _ => null,
            })
            .OfType<EvidenceKey>(),
    ];

    private static IReadOnlyList<EvidenceIndex> Indexes(TableInfo info)
    {
        var pk = info.Constraints.FirstOrDefault(k => k.Kind == ConstraintKind.PrimaryKey)?.Name;
        return [.. info.Indexes.Select(i => new EvidenceIndex(i.Name, i.IsUnique, i.Columns, pk is not null && string.Equals(i.ConstraintName, pk, StringComparison.OrdinalIgnoreCase)))];
    }

    private static EvidenceSkip Skip(EvidenceItem item, Exception ex, IDbErrorClassifier? errors, string? column = null)
    {
        if (ex is NotSupportedException)
        {
            return new EvidenceSkip(item, EvidenceSkipReason.NotSupported, column);
        }

        return errors?.FromException(ex).IsNoAccess() == true
            ? new EvidenceSkip(item, EvidenceSkipReason.NoPrivilege, column)
            : new EvidenceSkip(item, EvidenceSkipReason.Failed, column, ex.Message);
    }
}

