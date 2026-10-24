using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

// SqlResultPaneViewModel 의 일부: 서버 정렬(SqlResultPaneViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class SqlResultPaneViewModel
{
    // ---- 서버 정렬(큰 결과의 머리글 정렬, TSK-13-17) -------------------------------------------

    /// <summary>
    /// 서버 ORDER BY 로 다시 조회할 수 없는 이유(묻는 창의 첫째 선택지를 끄고 보인다). 할 수 있으면 null.
    /// 단순 SELECT(WITH 포함) 결과만 다시 조회한다. FOR UPDATE 문장·안쪽 열 이름이 겹치는 문장(ORA-00918)·정렬할 수 없는 형식의 열·
    /// 편집 모드의 반영하지 않은 변경이 있으면 쓸 수 없다.
    /// </summary>
    internal string? ServerSortBlockedReason(int ordinal, bool desc)
    {
        if (Statement is not { Kind: StatementKind.Query } query || (Runner?.Filter ?? ResultGridCommands.Filter) is null || _model is not { } m)
        {
            return "서버에서 다시 조회할 수 없는 결과입니다.";
        }

        if (IsEditDirty)
        {
            return "반영하지 않은 변경이 있어 다시 조회할 수 없습니다.";
        }

        if (ResultQueryWrapper.HasForUpdate(query.Text))
        {
            return "FOR UPDATE 문장은 서버에서 다시 조회할 수 없습니다.";
        }

        if (m.Columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != m.Columns.Count || m.Columns.Any(c => string.IsNullOrEmpty(c.Name)))
        {
            return "열 이름이 겹치거나 비어 있어 다시 조회할 수 없습니다(ORA-00918).";
        }

        if (ordinal >= 0 && ordinal < m.Columns.Count && m.Columns[ordinal].Kind is ColumnDataKind.Clob or ColumnDataKind.Blob or ColumnDataKind.Json
            or ColumnDataKind.Vector or ColumnDataKind.Xml or ColumnDataKind.UserDefinedType or ColumnDataKind.RefCursor or ColumnDataKind.Other)
        {
            return "이 형식의 열은 서버에서 정렬할 수 없습니다.";
        }

        if (ordinal >= 0 && ordinal < m.Columns.Count && m.Columns[ordinal].DbTypeName.StartsWith("LONG", StringComparison.OrdinalIgnoreCase))
        {
            return "LONG 열은 서버에서 정렬할 수 없습니다.";
        }

        return null;
    }

    /// <summary>
    /// 서버 정렬: 이 결과를 만든 원래 문장을 <c>SELECT * FROM (원문) ORDER BY 열 위치 [DESC] NULLS LAST</c> 로 감싸 거르기와 같은 실행 경로로 다시 조회하고,
    /// 같은 결과 탭의 결과를 바꾼다. 감싸기는 언제나 원래 문장 기준이라 두 번째 누르기에서 중첩되지 않고, 거른 결과면 거르기 조건은 그대로 두고 정렬만 바꾼다.
    /// 편집 모드였으면 편집 세션을 새 결과에 다시 붙인다(원래 문장이 결과의 정본이므로 편집 대상·키 계획은 그대로다).
    /// <paramref name="ordinal"/> 이 음수면 정렬을 풀고 다시 조회한다. 정렬 열 위치는 데이터 열 위치(화면 열 순서·ROWID 열과 섞지 않는다).
    /// 실패·취소는 상태 줄에 보이고 지금 결과를 그대로 둔다.
    /// </summary>
    internal async Task ServerSortAsync(int ordinal, bool desc, CancellationToken ct)
    {
        if (ServerSortBlockedReason(ordinal, desc) is { } blocked)
        {
            ShowFilterMessage(blocked);
            return;
        }

        var query = Statement!;
        var run = (Runner?.Filter ?? ResultGridCommands.Filter)!;
        var request = new ResultFilterRequest(query, _activeFilter?.Where ?? string.Empty,
            ordinal >= 0 ? ResultQueryWrapper.OrderByClause(ordinal + 1, desc) : _activeFilter is null ? string.Empty : OrderByText.Trim())
        {
            Binds = new Dictionary<string, BindValue>(_filterBinds, StringComparer.OrdinalIgnoreCase),
        };
        var generation = _resultGeneration;
        ShowFilterMessage(null);

        IResultSet? result = null;
        BeginServerSort();
        try
        {
            result = await run(request, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ShowFilterMessage("서버 정렬을 취소했습니다.");
            return;
        }
        catch (Exception e)
        {
            ShowFilterMessage("서버에서 다시 조회하지 못했습니다: " + e.Message.ReplaceLineEndings(" "));
            return;
        }
        finally
        {
            EndServerSort();
        }

        if (generation != _resultGeneration || _model is null)
        {
            await result.DisposeAsync().ConfigureAwait(true); // 그새 결과 영역이 비었거나 다른 결과로 바뀌었다
            return;
        }

        var editing = _editSession;
        var filter = _activeFilter;
        SetResultCore(result, _model.Display, keepLocalFilter: true); // 편집 상태·거른 표시를 풀고 새 모델을 만든다. 아래에서 되살린다
        _activeFilter = filter is null ? null : filter with { OrderBy = request.OrderBy };
        OnPropertyChanged(nameof(IsFiltered));
        if (_model is { } sorted)
        {
            if (editing is not null)
            {
                editing.Rebind(GridEditBinding.OriginalValues(sorted), () => sorted.LoadedRowCount);
                GridEditBinding.Attach(sorted, editing);
                SetEditSession(editing);
            }

            if (ordinal >= 0)
            {
                sorted.MarkServerSorted(ordinal, desc);
            }
        }

        UpdateStatus();
    }
}
