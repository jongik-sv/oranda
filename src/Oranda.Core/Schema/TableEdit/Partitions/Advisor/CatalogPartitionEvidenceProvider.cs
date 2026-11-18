using System.Globalization;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// 카탈로그 질의(<c>advisor.*</c>) 위에서 네 가지 근거를 만드는 공급자의 뼈대(설계 2026-11-18 5장). DBMS 마다 하는 일은 질의를 보내 줄을 받아 오는 것뿐이고
/// (<see cref="QueryAsync"/>), 조건 글 해석·날짜형 판정·분포 만들기는 <see cref="EvidenceProviderRules"/> 의 순수 규칙이다.
/// 문장은 DBMS 의 카탈로그 파일에만 있다(TC-03). 권한 오류는 그대로 던진다(모으기가 「권한 없음」으로 적음).
/// </summary>
public abstract class CatalogPartitionEvidenceProvider : IPartitionEvidenceProvider
{
    /// <summary>
    /// 카탈로그 질의 <paramref name="id"/> 를 보내 줄을 모두 읽는다. <paramref name="replacements"/> 의 키(<c>@KEY@</c>)는 문장에서 값으로 바꾸고,
    /// <paramref name="binds"/> 는 이름 바인드로 보낸다.
    /// </summary>
    protected abstract Task<IReadOnlyList<object?[]>> QueryAsync(
        ISession s, string id, IReadOnlyDictionary<string, string> replacements, IReadOnlyDictionary<string, object?> binds, CancellationToken ct);

    /// <summary>월별 분포가 「이번 달」을 재는 때(시험이 바꾼다).</summary>
    protected virtual DateTime Now => DateTime.Now;

    public async Task<IReadOnlyList<PredicateUse>?> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        var binds = TableBinds(owner, table);

        var columnRows = await QueryAsync(s, "advisor.table_columns", NoReplacements, binds, ct).ConfigureAwait(false);
        var columns = columnRows.Select(r => Text(r[0])).Where(c => c is not null).Select(c => c!).ToList();
        if (columns.Count == 0)
        {
            return [];
        }

        var max = EvidenceProviderRules.MaxStatements.ToString(CultureInfo.InvariantCulture);
        var rows = await QueryAsync(s, "advisor.predicate_plans", new Dictionary<string, string> { ["@MAX@"] = max }, binds, ct).ConfigureAwait(false);

        // 같은 문장은 계획 줄(자식 커서)마다 여러 줄로 온다: sql_id 로 묶어 조건 글을 모으고, 실행 수는 줄마다 같은 값(문장 합)이다.
        var statements = new List<PlanStatement>();
        foreach (var group in rows.GroupBy(r => Text(r[0]) ?? string.Empty, StringComparer.Ordinal))
        {
            var predicates = group
                .SelectMany(r => new[] { Text(r[2]), Text(r[3]) })
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            statements.Add(new PlanStatement(group.Key, Number(group.First()[1]) ?? 0, predicates));
        }

        return EvidenceProviderRules.BuildPredicateUse(statements, columns);
    }

    public async Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        var rowCount = await QueryAsync(s, "advisor.table_rows", NoReplacements, TableBinds(owner, table), ct).ConfigureAwait(false);
        var percent = EvidenceSampling.PercentFor(rowCount.Count > 0 ? Number(rowCount[0][0]) : null);
        var rows = await QueryAsync(s, "advisor.date_sample", Source(owner, table, column, percent), NoBinds, ct).ConfigureAwait(false);
        return EvidenceProviderRules.DetectDateLike(rows.Select(r => Text(r[0])));
    }

    public async Task<ColumnDistribution?> SampleMonthlyAsync(
        ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        var replacements = Source(owner, table, column, samplePercent);
        replacements["@LABEL@"] = EvidenceProviderRules.MonthLabelExpression(format, SqlIdentifier.QuoteAlways(column));
        var rows = await QueryAsync(s, "advisor.monthly", replacements, NoBinds, ct).ConfigureAwait(false);
        return EvidenceProviderRules.BuildMonthly(column, rows.Select(r => (Text(r[0]), Number(r[1]) ?? 0)), samplePercent, Now);
    }

    public async Task<ColumnDistribution?> SampleTopValuesAsync(
        ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);

        var replacements = Source(owner, table, column, samplePercent);
        replacements["@TOP@"] = top.ToString(CultureInfo.InvariantCulture);
        var rows = await QueryAsync(s, "advisor.top_values", replacements, NoBinds, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return EvidenceProviderRules.BuildTopValues(column, [], 0, 0, top, samplePercent); // 빈 표
        }

        var total = Number(rows[0][2]) ?? 0;
        var nulls = Number(rows[0][3]) ?? 0;
        return EvidenceProviderRules.BuildTopValues(column, rows.Select(r => (Text(r[0]), Number(r[1]) ?? 0)), total, nulls, top, samplePercent);
    }

    /// <summary>값 → 글(NULL 은 null).</summary>
    protected static string? Text(object? value) => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>값 → 정수(NULL 은 null).</summary>
    protected static long? Number(object? value) => value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static readonly IReadOnlyDictionary<string, string> NoReplacements = new Dictionary<string, string>();

    private static readonly IReadOnlyDictionary<string, object?> NoBinds = new Dictionary<string, object?>();

    private static Dictionary<string, object?> TableBinds(string owner, string table) =>
        new(StringComparer.Ordinal) { ["owner"] = owner, ["name"] = table };

    private static Dictionary<string, string> Source(string owner, string table, string column, double? samplePercent) =>
        new(StringComparer.Ordinal)
        {
            ["@TABLE@"] = SqlIdentifier.QuoteAlways(owner) + "." + SqlIdentifier.QuoteAlways(table),
            ["@COL@"] = SqlIdentifier.QuoteAlways(column),
            ["@SAMPLE@"] = EvidenceProviderRules.SampleClause(samplePercent),
        };
}
