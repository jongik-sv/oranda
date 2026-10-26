using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 외래키 따라가기(SqlResultPaneViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class SqlResultPaneViewModel
{
    // ---- 외래키 따라가기(GRID-11) -----------------------------------------------------------

    /// <summary>
    /// 이 결과를 만든 문장의 바인드 값을 넘겨받는다(외래키 따라가기로 연 결과). 거르기·전체 행 수 세기가 함께 넘긴다.
    /// 셀 값 바인드와 같은 곳에 두므로 결과를 바꿔도(거르기·거르기 지우기) 남는다.
    /// </summary>
    public void UseStatementBinds(IReadOnlyDictionary<string, BindValue> binds)
    {
        ArgumentNullException.ThrowIfNull(binds);
        foreach (var (name, value) in binds)
        {
            _filterBinds[name] = value;
        }

        _hasStatementBinds = binds.Count > 0;
        OnPropertyChanged(nameof(HasStatementBinds));
        OnPropertyChanged(nameof(EditHintAction));
        OnPropertyChanged(nameof(HasEditHintAction));
        OnPropertyChanged(nameof(OffersRerunForEdit));
    }

    /// <summary>
    /// 외래키 따라가기(<c>oranda.grid.followForeignKey</c>·<c>oranda.grid.showReferencingRows</c>): 활성 행의 값과 활성 칸의 열로
    /// 부모 행(이 열이 든 외래키가 참조하는 행) 또는 자식 행(이 행의 키를 참조하는 행)을 조회해 새 결과 탭에 연다(<see cref="RelatedRowsOpened"/>).
    /// 이 결과는 그대로 둔다. 실패·취소는 상태 줄에 보이고 밖으로 던지지 않는다. 그새 결과가 바뀌면 받은 결과는 버린다.
    /// </summary>
    public async Task OpenRelatedRowsAsync(RelatedRowsKind kind, CancellationToken ct = default)
    {
        if (_isOpeningRelated || Statement is not { Kind: StatementKind.Query } query || _model is not { HasResult: true, HasSelection: true } m)
        {
            return;
        }

        if (RelatedRowsOpened is null)
        {
            ShowRelatedMessage("이 결과 영역에서는 새 결과 탭을 열 수 없습니다.");
            return;
        }

        if (IsEditDirty)
        {
            ShowRelatedMessage("반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 따라가세요.");
            return;
        }

        if (ResultGridCommands.OpenRelatedRows is not { } open)
        {
            ShowRelatedMessage("외래키를 따라갈 수 없습니다(실행 엔진이 연결되지 않았습니다).");
            return;
        }

        var cell = m.Active;
        if (cell.Col < 0 || cell.Col >= m.VisibleColumns.Count)
        {
            return;
        }

        // 행 값은 지금(UI 스레드에서) 읽어 둔다. 숨긴 열도 키일 수 있으므로 모든 열을 읽는다.
        var row = new object?[m.Columns.Count];
        for (var i = 0; i < row.Length; i++)
        {
            if (!m.TryGetValue(cell.Row, i, out row[i]))
            {
                ShowRelatedMessage("행 값을 아직 가져오지 않았습니다. 잠시 뒤 다시 하세요.");
                return;
            }
        }

        var request = new RelatedRowsRequest(kind, query, m.Columns, m.VisibleColumns[cell.Col], row);
        var generation = _resultGeneration;
        _isOpeningRelated = true;
        OnPropertyChanged(nameof(IsOpeningRelatedRows));
        ShowRelatedMessage(kind == RelatedRowsKind.Parent ? "부모 행 여는 중…" : "참조하는 행 여는 중…");

        RelatedRowsResult? result = null;
        string? message = null;
        try
        {
            result = await open(request, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            message = "외래키 따라가기를 취소했습니다.";
        }
        catch (Exception e)
        {
            message = (kind == RelatedRowsKind.Parent ? "부모 행을 열지 못했습니다: " : "참조하는 행을 열지 못했습니다: ")
                      + e.Message.ReplaceLineEndings(" ");
        }

        _isOpeningRelated = false;
        OnPropertyChanged(nameof(IsOpeningRelatedRows));
        if (generation != _resultGeneration || RelatedRowsOpened is not { } opened)
        {
            // 그새 결과 영역이 비었거나 다른 결과로 바뀌었다: 늦게 온 결과는 버린다.
            if (result is not null)
            {
                await result.Result.DisposeAsync().ConfigureAwait(true);
            }

            return;
        }

        ShowRelatedMessage(message);
        if (result is not null)
        {
            opened(this, result);
        }
    }

    private ResultFilterRequest OwnBindsRequest(SqlStatement query) => new(query, string.Empty, string.Empty)
    {
        Binds = new Dictionary<string, BindValue>(_filterBinds, StringComparer.OrdinalIgnoreCase),
        RunBinds = RunBinds,
    };

    private void ShowRelatedMessage(string? message)
    {
        _relatedMessage = message;
        UpdateStatus();
    }
}
