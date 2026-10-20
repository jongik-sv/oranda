using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Features.DataTransfer;

/// <summary>「Excel 로 내보내기」가 쓸 행의 범위.</summary>
public enum ExcelExportScope
{
    /// <summary>질의를 서버에서 다시 실행해 처음부터 끝까지 흘려 저장한다.</summary>
    AllRows,

    /// <summary>다시 실행하지 않고 지금 그리드에 불러온 행을 저장한다.</summary>
    LoadedRows,

    /// <summary>선택 영역이 걸친 행·열만 저장한다.</summary>
    Selection,
}

/// <summary>저장할 열과 행. <see cref="Rows"/> 는 <see cref="Columns"/> 순서의 원시 값이다.</summary>
public sealed record ExcelExportData(IReadOnlyList<ResultColumn> Columns, IAsyncEnumerable<IReadOnlyList<object?>> Rows, long RowCount);

/// <summary>
/// 「Excel 로 내보내기」의 순수 계산: 기본 파일 이름·폴더 규칙과 범위별 열·행 만들기.
/// 화면(대화상자·파일 선택)과 분리해 시험한다.
/// </summary>
public static class ExcelExportPlan
{
    /// <summary>표 이름을 알 수 없을 때 파일 이름과 시트 이름에 쓰는 말.</summary>
    public const string DefaultBaseName = "조회결과";

    /// <summary>결과 영역에서 한 번에 읽는 행 수(페이지 크기와 비슷한 크기로 메모리를 일정하게 둔다).</summary>
    private const int ReadChunk = ResultGridModel.DefaultPageSize;

    /// <summary>이번 실행 중 마지막으로 내보낸 폴더. 없으면 문서 폴더.</summary>
    public static string? LastFolder { get; set; }

    /// <summary><c>{표 이름 또는 조회결과}_yyyyMMdd_HHmmss.xlsx</c>. 파일 이름에 못 쓰는 글자는 <c>_</c> 로 바꾼다.</summary>
    public static string DefaultFileName(string? name, DateTime now)
    {
        // 다른 운영체제(Windows)에서 열 파일일 수 있으므로 어디서나 못 쓰는 글자를 모두 바꾼다.
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToArray();
        var clean = new string((name ?? string.Empty).Trim().Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0)
        {
            clean = DefaultBaseName;
        }

        return $"{clean}_{now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.xlsx";
    }

    /// <summary>저장 위치를 고를 때 처음 보여 줄 폴더: 마지막으로 내보낸 폴더(아직 있으면), 없으면 문서 폴더.</summary>
    public static string DefaultFolder(string? lastFolder = null, Func<string, bool>? exists = null)
    {
        lastFolder ??= LastFolder;
        if (!string.IsNullOrWhiteSpace(lastFolder) && (exists ?? Directory.Exists)(lastFolder))
        {
            return lastFolder;
        }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(docs) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : docs;
    }

    /// <summary>결과 열에서 표 이름을 알아낸다(모든 열의 기준 표가 같을 때만). 모르면 null.</summary>
    public static string? TableNameOf(IReadOnlyList<ResultColumn> columns)
    {
        var names = columns.Select(c => c.BaseTable).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 1 ? names[0] : null;
    }

    /// <summary>선택 영역이 걸친 (행 수, 열 수). 선택이 없으면 (0, 0).</summary>
    public static (long Rows, int Columns) SelectionSize(ResultGridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.HasSelection)
        {
            return (0, 0);
        }

        var (from, to) = model.SelectionRect();
        return (to.Row - from.Row + 1, to.Col - from.Col + 1);
    }

    /// <summary>지금 그리드에 불러온 행 수(화면 행 기준).</summary>
    public static long LoadedRows(ResultGridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Math.Min(model.LoadedRowCount, model.RowCount);
    }

    /// <summary>
    /// 그리드에서 저장할 데이터를 만든다. 열은 지금 보이는 열이고(숨긴 열은 뺀다) 행은 화면 순서(정렬·편집 상태 그대로)다.
    /// 선택 범위면 선택 영역의 열·행만, 불러온 행이면 불러온 행 전체다. <see cref="ExcelExportScope.AllRows"/> 는 서버에서 읽으므로 여기서 만들지 않는다.
    /// </summary>
    public static ExcelExportData FromGrid(ResultGridModel model, ExcelExportScope scope)
    {
        ArgumentNullException.ThrowIfNull(model);
        switch (scope)
        {
            case ExcelExportScope.Selection:
                if (!model.HasSelection)
                {
                    throw new InvalidOperationException("선택한 셀이 없다.");
                }

                var (from, to) = model.SelectionRect();
                var selected = model.VisibleColumns.Skip(from.Col).Take(to.Col - from.Col + 1).ToList();
                return new ExcelExportData(selected.Select(o => model.Columns[o]).ToList(), Stream(model, selected, from.Row, to.Row), to.Row - from.Row + 1);
            case ExcelExportScope.LoadedRows:
                var visible = model.VisibleColumns.ToList();
                var count = LoadedRows(model);
                return new ExcelExportData(visible.Select(o => model.Columns[o]).ToList(), Stream(model, visible, 0, count - 1), count);
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "서버에서 다시 읽는 범위는 그리드에서 만들 수 없다.");
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Stream(ResultGridModel model, IReadOnlyList<int> ordinals, long first, long last,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var r = first; r <= last; r += ReadChunk)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await model.ReadRowsAsync(r, Math.Min(last, r + ReadChunk - 1), ordinals, ct).ConfigureAwait(true);
            foreach (var row in rows)
            {
                yield return row;
            }

            if (rows.Count == 0)
            {
                yield break;
            }
        }
    }
}
