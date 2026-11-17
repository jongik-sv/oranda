using Oranda.Core.Catalog.Providers;

namespace Oranda.Core.Metadata;

/// <summary>
/// 메타데이터 카탈로그 행(<c>meta.objects</c>·<c>meta.columns</c>·<c>meta.constraints</c>·<c>meta.indexes</c>) → 모델. Oracle·Tibero 가 같은 열 순서로 질의해
/// 같은 규칙으로 읽는다.
/// </summary>
public static partial class MetadataRows
{
    /// <summary><c>meta.objects</c> 한 줄(owner, object_name, object_type, status, last_ddl_time) → 객체. 시각은 UTC 로 읽는다.</summary>
    public static MetaObject ToMeta(string dbKey, object?[] r, DateTimeOffset fetchedAt) => new()
    {
        DbKey = dbKey,
        Owner = Str(r[0])!,
        Name = Str(r[1])!,
        Type = Str(r[2])!,
        Status = Str(r[3]),
        LastDdlTime = r[4] is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null,
        FetchedAt = fetchedAt,
    };

    /// <summary>
    /// <c>meta.columns</c> 한 줄(column_name, position, data_type, data_length, data_precision, data_scale, nullable,
    /// data_default, virtual_column, identity_flag, hidden_column, domain_name, comments[, byte_length]) → 열.
    /// </summary>
    public static ColumnInfo ToColumn(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new ColumnInfo(Str(r[0])!, Int(r[1]) ?? 0, Str(r[2])!)
        {
            Length = Int(r[3]),
            Precision = Int(r[4]),
            Scale = Int(r[5]),
            IsNullable = !string.Equals(Str(r[6]), "N", StringComparison.Ordinal),
            DefaultValue = Str(r[7])?.Trim(),
            IsVirtual = Yes(r[8]),
            IsIdentity = Yes(r[9]),
            IsInvisible = Yes(r[10]),
            Domain = Str(r[11]),
            Comment = Str(r[12]),
            ByteLength = r.Length > 13 ? Int(r[13]) : null, // 문자 열의 data_length(바이트, TR1). 열이 없는 예전 행 모양이면 모름
        };
    }

    /// <summary>
    /// <c>meta.constraints</c> 행(constraint_name, constraint_type, status, search_condition, r_owner, r_table_name, column_name, position, r_constraint_name)
    /// → 제약 목록. NOT NULL 검사 제약(<see cref="IsNotNullCheck"/>)은 열 정보에 있으므로 뺀다. SQL 이 이미 거르는 판도 있고,
    /// Oracle 11g 는 조건 열이 LONG 이라 SQL 에서 거르지 못해 여기서 뺀다(meta/constraints.11.sql).
    /// </summary>
    public static List<ConstraintInfo> BuildConstraints(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new List<ConstraintInfo>();
        foreach (var group in rows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal))
        {
            var first = group.First();
            var kind = Str(first[1]) switch
            {
                "P" => ConstraintKind.PrimaryKey,
                "U" => ConstraintKind.Unique,
                "R" => ConstraintKind.ForeignKey,
                _ => ConstraintKind.Check,
            };
            if (kind == ConstraintKind.Check && IsNotNullCheck(Str(first[3])))
            {
                continue;
            }

            var columns = group.Where(r => r[6] is not null).Select(r => Str(r[6])!).ToList();
            result.Add(new ConstraintInfo(group.Key, kind, columns)
            {
                IsEnabled = string.Equals(Str(first[2]), "ENABLED", StringComparison.Ordinal),
                Condition = kind == ConstraintKind.Check ? Str(first[3]) : null,
                ReferencedTable = kind == ConstraintKind.ForeignKey && first[4] is not null && first[5] is not null
                    ? new DbObjectRef(Str(first[4])!, Str(first[5])!, "TABLE")
                    : null,
                ReferencedConstraint = kind == ConstraintKind.ForeignKey && first.Length > 8 ? Str(first[8]) : null,
            });
        }

        return result;
    }

    /// <summary>
    /// NOT NULL 검사 제약인가: 조건 전체가 따옴표 열 하나 + <c>IS NOT NULL</c>(<c>"C" IS NOT NULL</c>, 서버가 만드는 꼴, 대소문자 구분). Oracle <c>meta.constraints</c>(19+) SQL 의
    /// <c>REGEXP_LIKE</c> 와 같은 판정. <c>a &gt; 0 OR b IS NOT NULL</c> 처럼 끝만 같은 CHECK 는 사용자 제약이라 남긴다(형식 변환 5단계가 되살린다, 설계 18.9).
    /// </summary>
    public static bool IsNotNullCheck(string? condition)
        => condition is not null && NotNullCheckPattern().IsMatch(condition);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*""[^""]+""\s+IS NOT NULL\s*$")]
    private static partial System.Text.RegularExpressions.Regex NotNullCheckPattern();

    /// <summary><c>meta.indexes</c> 행(index_name, uniqueness, index_type, column_name, column_position) → 인덱스 목록.</summary>
    public static List<IndexInfo> BuildIndexes(IReadOnlyList<object?[]> rows)
        => [.. rows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal).Select(g => new IndexInfo(
            g.Key,
            string.Equals(Str(g.First()[1]), "UNIQUE", StringComparison.Ordinal),
            [.. g.Select(r => Str(r[3])!)])
        {
            IndexType = Str(g.First()[2]),
        })];

    private static string? Str(object? v) => CatalogValues.Str(v);

    private static int? Int(object? v) => CatalogValues.Int(v);

    /// <summary>글 값이 <c>YES</c> 와 글자 그대로 같은가(대소문자 구분 — <see cref="CatalogValues.Yes"/> 와 다르다).</summary>
    private static bool Yes(object? v) => string.Equals(Str(v), "YES", StringComparison.Ordinal);
}
