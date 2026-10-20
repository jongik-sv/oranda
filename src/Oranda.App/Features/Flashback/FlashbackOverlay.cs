using Oranda.App.Features.ResultGrid;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// 「N분 전 값」 결과 탭에 꽂는 칸 강조(TSK-13-05). 탭의 행을 키로 지금 값과 짝지어, 값이 달라진 칸을 「바뀐 칸」 색으로 칠하고 그 행에 변경 표시를 단다.
/// 값은 건드리지 않는다(결과 그리드는 그대로 읽는다). 정렬해도 키로 짝지으므로 어긋나지 않는다.
/// </summary>
public sealed class FlashbackOverlay : IGridEditOverlay
{
    private readonly ResultGridModel _model;
    private readonly IReadOnlyList<int> _keyPositions;
    private readonly Dictionary<string, object?[]> _nowByKey;
    private readonly Dictionary<string, HashSet<int>> _cache = [];

    /// <param name="model">N분 전 값 탭의 그리드(열 순서 = <see cref="FlashbackTarget.Names"/>)</param>
    /// <param name="keyPositions">탭 열 중 키 열 번호</param>
    /// <param name="now">지금 값 행(열 순서 같음)</param>
    public FlashbackOverlay(ResultGridModel model, IReadOnlyList<int> keyPositions, IEnumerable<object?[]> now)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(keyPositions);
        ArgumentNullException.ThrowIfNull(now);
        _model = model;
        _keyPositions = keyPositions;
        _nowByKey = [];
        foreach (var row in now)
        {
            _nowByKey[FlashbackDiff.KeyText(keyPositions.Select(p => row[p]))] = row;
        }
    }

    public int ExtraRows => 0;

    public bool HasPendingChanges => false;

    public bool TryGetValue(long viewRow, int ordinal, out object? value)
    {
        value = null;
        return false;
    }

    public bool IsEdited(long viewRow, int ordinal) => Changed(viewRow)?.Contains(ordinal) == true;

    public GridRowMark RowMark(long viewRow) => Changed(viewRow) is { Count: > 0 } ? GridRowMark.Modified : GridRowMark.None;

    public long BaseRowOf(long viewRow) => viewRow < _model.BaseRowCount ? viewRow : -1;

    public bool NeedsChange(long viewRow, int ordinal) => false;

    /// <summary>이 행에서 지금 값과 다른 칸 번호. 아직 못 읽은 행·지금 목록에 없는 행은 null(캐시하지 않는다).</summary>
    private HashSet<int>? Changed(long viewRow)
    {
        var past = new object?[_model.Columns.Count];
        for (var i = 0; i < past.Length; i++)
        {
            if (!_model.TryGetBaseValue(_model.BaseRowOf(viewRow), i, out past[i]))
            {
                return null;
            }
        }

        var key = FlashbackDiff.KeyText(_keyPositions.Select(p => past[p]));
        if (!_nowByKey.TryGetValue(key, out var now))
        {
            return null;
        }

        // 정렬해도 행 번호가 아니라 키로 캐시하므로 강조가 어긋나지 않는다.
        if (!_cache.TryGetValue(key, out var set))
        {
            set = new HashSet<int>(FlashbackDiff.ChangedColumns(now, past));
            _cache[key] = set;
        }

        return set;
    }
}
