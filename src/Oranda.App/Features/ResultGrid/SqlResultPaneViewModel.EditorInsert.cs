using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 머리글·값 더블클릭으로 이 결과를 만든 편집기에 글을 넣는다.
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>
    /// 글을 이 결과를 만든 편집기 탭의 커서 자리에 넣는 함수. 넣었으면 null, 넣지 못했으면 안내 글을 돌려준다.
    /// 편집기 결과 탭(결과 탭 묶음)만 채운다 — 비어 있으면 더블클릭은 예전 동작(객체 상세의 셀 편집 안내 등)을 유지한다.
    /// </summary>
    public Func<string, string?>? EditorInserter { get; set; }

    /// <summary>머리글·값 더블클릭이 편집기에 글을 넣는 결과 영역이다.</summary>
    public bool CanInsertToEditor => EditorInserter is not null;

    /// <summary>컬럼 이름을 그대로 편집기에 넣는다(머리글 더블클릭).</summary>
    public void InsertColumnName(int ordinal)
    {
        if (_model is not { HasResult: true } m || ordinal < 0 || ordinal >= m.Columns.Count)
        {
            return;
        }

        Insert(m, m.Columns[ordinal].Name);
    }

    /// <summary>
    /// 값을 SQL 리터럴로 편집기에 넣는다(값 더블클릭): 문자 <c>'ABC'</c>, 숫자는 그대로, NULL 은 <c>NULL</c> — 복사 기능(INSERT·IN 목록)과 같은 형식이다.
    /// LOB·이진 값은 넣지 않고 안내한다.
    /// </summary>
    public void InsertCellValue(GridCell cell)
    {
        if (_model is not { HasResult: true } m || cell.Col < 0 || cell.Col >= m.VisibleColumns.Count || cell.Row < 0 || cell.Row >= m.RowCount)
        {
            return;
        }

        var ordinal = m.VisibleColumns[cell.Col];
        if (!m.TryGetValue(cell.Row, ordinal, out var value))
        {
            m.ReportNotice("칸 값을 아직 가져오지 않았습니다. 잠시 뒤 다시 더블클릭하세요.");
            return;
        }

        if (value is LobValue || (value is byte[] && m.Columns[ordinal].Kind != ColumnDataKind.Vector))
        {
            m.ReportNotice("LOB·이진 값은 편집기에 넣지 않습니다. 값 보기에서 복사하세요.");
            return;
        }

        Insert(m, CellValueFormatter.ToSqlLiteral(m.Columns[ordinal], value));
    }

    private void Insert(ResultGridModel m, string text)
    {
        if (EditorInserter is not { } insert)
        {
            return;
        }

        if (insert(text) is { } notice)
        {
            m.ReportNotice(notice);
        }
    }
}
