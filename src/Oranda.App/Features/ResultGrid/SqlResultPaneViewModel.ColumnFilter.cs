using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 머리글 열 필터 켜기·끄기(결과 탭 줄의 필터 단추).
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>
    /// 새 편집기 결과가 시작할 때의 열 필터 상태를 읽는 함수(마지막으로 고른 상태, 설정 <c>grid.columnFilterEnabled</c>).
    /// 시작 때 설정 서비스가 채운다. 비어 있으면 켬.
    /// </summary>
    public static Func<bool>? ColumnFilterDefaultProvider { get; set; }

    /// <summary>필터 단추를 눌러 바뀐 상태를 설정에 저장하는 함수. 시작 때 설정 서비스가 채운다.</summary>
    public static Func<bool, Task>? ColumnFilterDefaultSaver { get; set; }

    private bool _columnFilterEnabled = true;

    /// <summary>
    /// 이 결과 영역의 머리글 열 필터(▾·깔때기)를 쓴다. 끄면 걸려 있던 열 필터가 풀리고 머리글 클릭이 열 선택이 된다.
    /// 같은 영역에서 결과를 바꿔도(서버 거르기·서버 정렬로 다시 조회) 이 값이 새 결과에 이어진다. 새 문장을 실행해 새 영역이 생기면 마지막으로 고른 상태로 시작한다. 편집기 결과 탭이 아닌 영역(객체 상세 등)은 항상 켬이다.
    /// </summary>
    public bool ColumnFilterEnabled => _columnFilterEnabled;

    /// <summary>편집기 결과 탭의 시작 상태를 마지막으로 고른 상태로 맞춘다(결과를 넣기 전에 부른다).</summary>
    public void UseColumnFilterDefault()
    {
        _columnFilterToggleable = true;
        _columnFilterEnabled = ColumnFilterDefaultProvider?.Invoke() ?? true;
    }

    /// <summary>열 필터를 켜고 끌 수 있는 영역이다(편집기 결과 탭).</summary>
    public bool ColumnFilterToggleable => _columnFilterToggleable;

    // 켜고 끌 수 있는 영역은 편집기 결과 탭뿐이다. 객체 상세 등은 단추가 없어 명령으로도 바뀌지 않는다(설정도 건드리지 않는다).
    private bool _columnFilterToggleable;

    /// <summary>
    /// 열 필터를 켜거나 끈다(<c>oranda.grid.toggleColumnFilter</c>). 끌 때 걸려 있던 열 필터를 먼저 지운다 — 반영하지 않은 편집이 있어
    /// 지우지 못하면 끄지 않는다(필터를 지울 길이 없는 채로 남기지 않는다). 빠른 거르기 글은 그대로 둔다.
    /// </summary>
    public async Task<bool> SetColumnFilterEnabledAsync(bool enabled)
    {
        if (!_columnFilterToggleable || _columnFilterEnabled == enabled)
        {
            return _columnFilterEnabled == enabled;
        }

        RequestCommitEditor(); // 열린 셀 편집기의 값을 먼저 확정한다(필터가 풀리면 행 번호가 바뀐다)

        if (!enabled && _model is { } m && m.LocalFilter.Columns.Values.Any(c => !c.IsEmpty))
        {
            var withoutColumns = m.LocalFilter with { Columns = new Dictionary<int, ColumnFilter>() };
            if (!await m.SetLocalFilterAsync(withoutColumns).ConfigureAwait(true))
            {
                return false;
            }
        }

        _columnFilterEnabled = enabled;
        _model?.SetColumnFilterEnabled(enabled);
        OnPropertyChanged(nameof(ColumnFilterEnabled));
        if (ColumnFilterDefaultSaver is { } save)
        {
            try
            {
                await save(enabled).ConfigureAwait(true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 저장하지 못해도 이 결과의 상태는 바뀌었다. 다음 시작 때 이전 상태로 돌아갈 뿐이다.
            }
        }

        return true;
    }

    private bool _togglingColumnFilter;

    private async Task ToggleColumnFilterFromCommandAsync()
    {
        if (_togglingColumnFilter)
        {
            return; // 끄느라 열 필터를 지우는 중에 다시 눌러도 같은 방향을 두 번 계산하지 않는다
        }

        _togglingColumnFilter = true;
        try
        {
            await SetColumnFilterEnabledAsync(!_columnFilterEnabled).ConfigureAwait(true);
        }
        finally
        {
            _togglingColumnFilter = false;
        }
    }

    private void OnColumnFilterToggleRequested(object? sender, EventArgs e) => _ = ToggleColumnFilterFromCommandAsync();
}
