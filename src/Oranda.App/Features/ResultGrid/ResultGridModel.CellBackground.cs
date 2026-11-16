namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 칸 바탕 강조(VERSIONS 결과의 앞 버전과 달라진 칸 등).
public sealed partial class ResultGridModel
{
    /// <summary>
    /// (화면 행, 열 ordinal)을 받아 참이면 그 칸 바탕을 「바뀐 값」 색으로 깐다(「이 행 변경 이력」 결과에서 앞 버전과 값이 다른 칸).
    /// 편집 모드 색이 먼저이고 현재 행·선택 색이 위에 온다. 기본 없음.
    /// </summary>
    public Func<long, int, bool>? CellChanged { get; set; }

    /// <summary>이 칸 바탕을 「바뀐 값」 색으로 그리는가(<see cref="CellChanged"/>).</summary>
    public bool IsChangedCell(long viewRow, int ordinal) => CellChanged?.Invoke(viewRow, ordinal) == true;

    /// <summary>강조가 바뀌었으니 다시 그린다.</summary>
    public void NotifyCellBackgroundChanged() => RaiseChanged();

    /// <summary>
    /// 결과 순서(정렬·거르기 전 원래 행 번호)의 값. 페이지가 메모리에 없으면 false(새로 읽지 않는다).
    /// </summary>
    public bool TryGetSourceValue(long sourceRow, int ordinal, out object? value)
    {
        if (sourceRow >= 0 && sourceRow < LoadedRowCount && _cache.TryGetResident((int)(sourceRow / PageSize), out var page))
        {
            var i = (int)(sourceRow - page.FirstRowIndex);
            if (i >= 0 && i < page.RowCount)
            {
                value = page.Columns[ordinal][i];
                return true;
            }
        }

        value = null;
        return false;
    }
}
