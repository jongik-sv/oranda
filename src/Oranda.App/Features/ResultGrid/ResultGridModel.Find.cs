using System.Globalization;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// ResultGridModel 의 일부: 찾기(ResultGridModel.cs 에서 나눔, 동작 불변).
public sealed partial class ResultGridModel
{
    // ---- 찾기 ----

    /// <summary>
    /// 활성 셀 다음부터 <paramref name="text"/>(대소문자 무시)가 든 셀을 찾아 선택한다. 끝까지 가도 없으면 처음부터 한 바퀴.
    /// 아직 안 받은 행은 가져오면서 찾는다.
    /// </summary>
    public async Task<bool> FindNextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text) || RowCount == 0)
        {
            return false;
        }

        var cols = _visible.Count;
        var start = HasSelection ? Active.Row * cols + Active.Col + 1 : 0;
        for (var pass = 0; pass < 2; pass++)
        {
            var from = pass == 0 ? start : 0;
            var r = from / cols;
            var c = (int)(from % cols);
            while (true)
            {
                var total = TotalRowCount;
                if (_match is not null ? r >= BaseRowCount : total is { } t && r >= t)
                {
                    break;
                }

                var src = SourceRow(r);
                var page = await _cache.GetPageAsync((int)(src / PageSize), ct).ConfigureAwait(true);

                OnPage(page);
                var i = (int)(src - page.FirstRowIndex);
                if (i < 0 || i >= page.RowCount)
                {
                    break;
                }

                for (; c < cols; c++)
                {
                    var o = _visible[c];
                    var s = CellValueFormatter.Format(Columns[o], page.Columns[o][i], GridDisplayOptions.Plain);
                    if (s.Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectCell(new GridCell(r, c));
                        return true;
                    }
                }

                c = 0;
                r++;
            }
        }

        return false;
    }
}
