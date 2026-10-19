using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.Language.Editing;

/// <summary>메타데이터(<see cref="TableInfo"/>) → 행 식별 키(PRD 6.5.1-1).</summary>
public static class TableKeys
{
    /// <summary>
    /// 켜진 PK 가 있으면 PK 전체 열. 없으면 모든 열이 NOT NULL 인 켜진 유니크 제약(열 수가 가장 적은 것, 같으면 이름순).
    /// 둘 다 없으면 <see cref="TableKeyKind.None"/>(ROWID 만 쓸 수 있음).
    /// </summary>
    public static TableKeyInfo From(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var pk = table.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey && c.IsEnabled && c.Columns.Count > 0);
        if (pk is not null)
        {
            return new TableKeyInfo(table.Table, TableKeyKind.PrimaryKey, pk.Columns);
        }

        var notNull = new HashSet<string>(table.Columns.Where(c => !c.IsNullable).Select(c => c.Name), StringComparer.Ordinal);
        var uk = table.Constraints
            .Where(c => c.Kind == ConstraintKind.Unique && c.IsEnabled && c.Columns.Count > 0 && c.Columns.All(notNull.Contains))
            .OrderBy(c => c.Columns.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return uk is not null
            ? new TableKeyInfo(table.Table, TableKeyKind.UniqueNotNull, uk.Columns)
            : new TableKeyInfo(table.Table, TableKeyKind.None, []);
    }
}
