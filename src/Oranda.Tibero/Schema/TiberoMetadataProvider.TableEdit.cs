using Oranda.Core.Metadata;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 테이블 편집기 메타데이터(TE-29)를 Tibero 카탈로그 끝 열과 <c>meta.table_kind</c> 에서 읽어 <see cref="TableInfo"/> 에 더한다.
/// 공용 행 읽기(<see cref="MetadataRows"/>)는 앞 열만 보므로 건드리지 않고, 끝 열은 여기서 위치로 읽는다(실측 docs/design/tibero-table-edit-probe.md).
/// <list type="bullet">
/// <item><c>meta.columns</c> [14] char_used(B/C) → <see cref="ColumnInfo.CharSemantics"/>. Tibero 는 n CHAR 열도 DATA_LENGTH = n 이라 이 열로만 가른다.</item>
/// <item><c>meta.constraints</c> [9] delete_rule → <see cref="ConstraintInfo.DeleteRule"/>, [10] r_column_name → <see cref="ConstraintInfo.ReferencedColumns"/>.</item>
/// <item><c>meta.indexes</c> [5] descend → <see cref="IndexInfo.Descending"/>(DESC 가 하나라도 있을 때만), [6] column_expression → <see cref="IndexInfo.Expressions"/>(식이 있을 때만),
/// [7] tablespace_name → <see cref="IndexInfo.Tablespace"/>, [8] constraint_name → <see cref="IndexInfo.ConstraintName"/>.</item>
/// <item><c>meta.table_kind</c>(0행 = 뷰·모름) → <see cref="TableInfo.Kind"/>·<see cref="TableInfo.IsPartitioned"/>·<see cref="TableInfo.Tablespace"/>. 클러스터는 판정하지 못한다.</item>
/// </list>
/// 끝 열이 없는 예전 행 모양이면 그 값은 기본값으로 둔다.
/// </summary>
internal sealed partial class TiberoMetadataProvider
{
    private const int ColCharUsed = 14;
    private const int ConDeleteRule = 9;
    private const int ConReferencedColumn = 10;
    private const int IdxDescend = 5;
    private const int IdxExpression = 6;
    private const int IdxTablespace = 7;
    private const int IdxConstraint = 8;

    /// <summary>
    /// <paramref name="info"/>(<see cref="MetadataRows"/> 가 만든 것)에 TE-29 값을 더한다. 열·제약·인덱스 목록은 같은 행에서 같은 순서로 만들어졌다고 본다
    /// (열 = 행 순서, 제약·인덱스 = 이름으로 묶음).
    /// </summary>
    internal static TableInfo WithTableEditMetadata(
        TableInfo info, IReadOnlyList<object?[]> colRows, IReadOnlyList<object?[]> consRows, IReadOnlyList<object?[]> idxRows, IReadOnlyList<object?[]> kindRows)
    {
        var columns = info.Columns.Select((c, i) => i < colRows.Count && At(colRows[i], ColCharUsed) is { } used
            ? c with { CharSemantics = used switch { "B" => CharSemantics.Bytes, "C" => CharSemantics.Characters, _ => null } }
            : c).ToList();

        var consByName = consRows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var constraints = info.Constraints.Select(c =>
        {
            if (c.Kind != ConstraintKind.ForeignKey || !consByName.TryGetValue(c.Name, out var rows))
            {
                return c;
            }

            return c with
            {
                DeleteRule = At(rows[0], ConDeleteRule) switch
                {
                    "CASCADE" => DeleteRule.Cascade,
                    "SET NULL" => DeleteRule.SetNull,
                    _ => DeleteRule.NoAction,
                },
                ReferencedColumns = [.. rows.Select(r => At(r, ConReferencedColumn)).OfType<string>()],
            };
        }).ToList();

        var idxByName = idxRows.GroupBy(r => Str(r[0])!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var indexes = info.Indexes.Select(x =>
        {
            if (!idxByName.TryGetValue(x.Name, out var rows))
            {
                return x;
            }

            var descending = rows.Select(r => string.Equals(At(r, IdxDescend), "DESC", StringComparison.Ordinal)).ToList();
            var expressions = rows.Select(r => At(r, IdxExpression)).ToList();
            return x with
            {
                Descending = descending.Any(d => d) ? descending : [],
                Expressions = expressions.Any(e => e is not null) ? expressions : [],
                Tablespace = At(rows[0], IdxTablespace),
                ConstraintName = At(rows[0], IdxConstraint),
            };
        }).ToList();

        var result = info with { Columns = columns, Constraints = constraints, Indexes = indexes };
        if (kindRows.Count == 0)
        {
            return result;
        }

        var kind = kindRows[0];
        var tableKind =
            kind.Length > 5 && Int(kind[5]) is > 0 ? TableKind.External
            : IsYes(At(kind, 1)) ? TableKind.Temporary
            : string.Equals(At(kind, 3), "IOT", StringComparison.Ordinal) ? TableKind.Iot
            : TableKind.Heap;
        return result with { Kind = tableKind, IsPartitioned = IsYes(At(kind, 4)), Tablespace = At(kind, 0) };

        static bool IsYes(string? v) => string.Equals(v, "YES", StringComparison.Ordinal) || string.Equals(v, "Y", StringComparison.Ordinal);
    }

    /// <summary>행의 <paramref name="index"/> 칸 글(없거나 NULL 이면 null).</summary>
    private static string? At(object?[] row, int index) => index < row.Length ? Str(row[index]) : null;
}
