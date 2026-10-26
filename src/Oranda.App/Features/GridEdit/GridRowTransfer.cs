using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Features.GridEdit;

/// <summary>
/// 행 붙여넣기(<c>oranda.grid.edit.pasteRows</c>)·행 복제(<c>oranda.grid.edit.duplicateRow</c>): 편집 모드(F9)에서 새 행(INSERT 대상)을 만든다.
/// 새 행은 지금처럼 질의 결과 뒤(끝)에 더하고 셀 커서를 첫 새 행으로 옮겨 보이게 한다. 결과는 상태 줄에 알린다.
/// 편집 모드가 아니면(고칠 수 없는 결과 포함) 결과 영역에 「편집 모드로 다시 실행」 안내 막대를 띄운다(조용히 무시하지 않는다).
/// 대상은 포커스가 있는 그리드의 세션이다(<see cref="GridEditCommands.Active"/> 는 마지막으로 편집한 다른 결과일 수 있다).
/// </summary>
public static class GridRowTransfer
{
    /// <summary>
    /// 클립보드의 행을 새 행으로 더한다. 앱 안 사본(<see cref="ResultGridCommands.CopiedRows"/>)이 지금 클립보드 글과 같으면 그 원시 값을,
    /// 아니면 TSV 글을 열 종류에 맞게 읽어 넣는다. 열은 보이는 열 순서로 위치를 맞추고, 열 수가 다르면 앞에서부터 맞추고 남는 값은 버린다.
    /// ROWID·읽기 전용 열은 비운다. 더한 행 수를 돌려준다.
    /// </summary>
    public static async Task<int> PasteRowsAsync(ResultGridModel grid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (GridEditBinding.Of(grid) is not { CanEdit: true } s)
        {
            grid.RequestEditHint();
            return 0;
        }

        ct.ThrowIfCancellationRequested();
        var text = grid.ClipboardReader is { } read ? await read().ConfigureAwait(true) : null;
        var targets = grid.VisibleColumns.ToList();
        var copy = ResultGridCommands.CopiedRows is { } c && c.Matches(text) ? c : null;
        IReadOnlyList<IReadOnlyList<object?>> rows;
        if (copy is not null)
        {
            rows = copy.Rows;
        }
        else
        {
            var lines = TsvReader.Parse(text ?? string.Empty);
            if (lines.Count > 0 && IsHeaderLine(lines[0], grid, targets))
            {
                lines.RemoveAt(0); // 「머리글 포함 복사」한 글이면 첫 줄은 열 이름이다
            }

            rows = [.. lines.Select(l => (IReadOnlyList<object?>)[.. l])];
        }

        if (rows.Count == 0 || targets.Count == 0)
        {
            grid.ReportNotice("클립보드에 붙여 넣을 행이 없습니다.");
            return 0;
        }

        var width = rows.Max(r => r.Count);
        var matched = Math.Min(width, targets.Count);
        var blanked = targets.Take(matched).Count(o => !s.IsEditableColumn(o));
        var firstNew = grid.RowCount;
        foreach (var row in rows)
        {
            var values = new List<KeyValuePair<int, object?>>(matched);
            for (var k = 0; k < Math.Min(row.Count, targets.Count); k++)
            {
                var ord = targets[k];
                values.Add(KeyValuePair.Create(ord, copy is null
                    ? GridEditBinding.Parse(grid, ord, row[k] as string)
                    : Convert(copy.Columns[k], row[k], grid, ord)));
            }

            s.AddRowWith(values);
        }

        grid.SelectAndReveal(new GridCell(firstNew, 0));
        List<string> notice = [$"{rows.Count}행을 새 행으로 끝에 붙여 넣었습니다"];
        if (width > targets.Count)
        {
            notice.Add($"값이 {width}열이라 앞 {targets.Count}열만 맞추고 남는 {width - targets.Count}열은 버렸습니다");
        }
        else if (width < targets.Count)
        {
            notice.Add($"값이 {width}열뿐이라 나머지 {targets.Count - width}열은 비웠습니다");
        }

        if (blanked > 0)
        {
            notice.Add($"ROWID·읽기 전용 열 {blanked}개는 비웠습니다");
        }

        grid.ReportNotice(string.Join(" · ", notice) + ".");
        return rows.Count;
    }

    /// <summary>
    /// 현재 행(여러 행을 골랐으면 그 행들)을 새 행으로 복제해 끝에 더한다. 숨긴 열 값도 복제하고 ROWID 는 비운다.
    /// 키 열(PK·유니크 키) 값은 그대로 두되 「바꿔야 함」 경고 색을 칠한다(같은 키로 반영하면 키 위반). 더한 행 수를 돌려준다.
    /// </summary>
    public static async Task<int> DuplicateRowsAsync(ResultGridModel grid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grid);

        // 편집 모드가 꺼진 편집 가능한 결과면 먼저 켠다(D-031). 켤 수 없으면 안내 막대가 이유를 보인다.
        if (await GridRowOps.EnsureEditModeAsync(grid, ct).ConfigureAwait(true) is not { } ready)
        {
            return 0;
        }

        (grid, var s) = ready;
        if (!grid.HasSelection)
        {
            return 0;
        }

        var ordinals = Enumerable.Range(0, grid.Columns.Count).ToList();
        var rows = await grid.ReadSelectedRowsAsync(ordinals, ct).ConfigureAwait(true);
        var firstNew = grid.RowCount;
        var col = grid.Active.Col;
        using (s.Batch()) // 여러 행을 더해도 격자는 한 번만 갱신한다
        {
            foreach (var row in rows)
            {
                s.AddRowWith(ordinals.Select(o => KeyValuePair.Create(o, row[o])), flagKeys: true);
            }
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        grid.SelectAndReveal(new GridCell(firstNew, col));
        var keys = s.Plan.UsesRowId ? [] : s.Capability.KeyColumnOrdinals.Select(o => grid.Columns[o].Name).ToList();
        grid.ReportNotice($"{rows.Count}행을 복제해 끝에 더했습니다"
                          + (keys.Count > 0 ? $" · 키 열({string.Join(", ", keys)}) 값을 바꾼 뒤 반영하세요." : "."));
        return rows.Count;
    }

    /// <summary>앱 안 사본의 원시 값을 붙여 넣을 열에 맞춘다. 열 종류가 같으면 그대로, 다르면 글자로 바꿔 대상 열 종류로 다시 읽는다.</summary>
    private static object? Convert(ResultColumn source, object? value, ResultGridModel grid, int ordinal)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return source.Kind == grid.Columns[ordinal].Kind
            ? value
            : GridEditBinding.Parse(grid, ordinal, CellValueFormatter.Format(source, value, GridDisplayOptions.Plain));
    }

    /// <summary>첫 줄이 붙여 넣을 열의 이름과 차례대로 같으면(대소문자 무시) 머리글 줄이다.</summary>
    private static bool IsHeaderLine(List<string> line, ResultGridModel grid, List<int> targets) =>
        line.Count > 0 && line.Count <= targets.Count
        && line.Select((name, i) => string.Equals(name, grid.Columns[targets[i]].Name, StringComparison.OrdinalIgnoreCase)).All(x => x);
}
