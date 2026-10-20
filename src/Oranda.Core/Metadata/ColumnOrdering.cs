namespace Oranda.Core.Metadata;

/// <summary>열 표시 순서(표 상세·열 순서 바꾸기 공용, TSK-13-01).</summary>
public static class ColumnOrdering
{
    /// <summary>
    /// 열 위치(<c>column_id</c>) 순으로 정렬한다. <c>columns.sql</c> 은 <c>internal_column_id</c> 순이라 지운 열이 있었던 표나
    /// INVISIBLE 로 바꾼 열이 끼면 위치 순과 어긋나므로, 보이는 열을 위치 순으로 먼저 놓고 INVISIBLE 열은 뒤에 모은다
    /// (INVISIBLE 열은 <c>column_id</c> 가 없어 <see cref="ColumnInfo.Position"/> 이 <c>internal_column_id</c> 로 채워져 있다).
    /// </summary>
    public static IReadOnlyList<ColumnInfo> ByColumnId(IEnumerable<ColumnInfo> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return [.. columns.OrderBy(c => c.IsInvisible).ThenBy(c => c.Position)];
    }
}
