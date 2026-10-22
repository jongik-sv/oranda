using System.Globalization;
using System.Runtime.CompilerServices;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Grid;

namespace Oranda.App.Features.GridEdit;

/// <summary>
/// 결과 그리드 모델과 편집 세션을 잇는다(TSK-06-04 의 화면 쪽). 바꾼 값·새 행·지울 행을 그리드에 그리고,
/// 화면 행 번호 ↔ 편집 행 번호(새 행은 -1, -2 …)를 바꾼다. 셀 편집기·편집 명령이 이 객체로 세션을 찾는다.
/// </summary>
public static class GridEditBinding
{
    private static readonly ConditionalWeakTable<ResultGridModel, GridEditSession> Sessions = new();

    /// <summary>모델에 편집 세션을 붙인다. 세션이 바뀌면 그리드를 다시 그린다.</summary>
    public static void Attach(ResultGridModel model, GridEditSession session)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(session);
        Sessions.AddOrUpdate(model, session);
        model.EditOverlay = new Overlay(model, session);
        var shown = session.Notice;
        session.Changed += (_, _) =>
        {
            model.NotifyEditChanged();

            // 반영 결과 안내(커밋 여부)를 결과 창 요약 줄에 보인다. 편집을 이어 하면 안내가 비워지며 함께 지운다.
            if (!string.Equals(shown, session.Notice, StringComparison.Ordinal))
            {
                shown = session.Notice;
                model.ReportNotice(shown ?? string.Empty);
            }
        };
    }

    /// <summary>
    /// 편집 세션에 넘기는 원래 값 읽기(<see cref="GridEditSession.OpenAsync"/> 의 read). 그리드에 보이는 바꾼 값을 거치지 않고 질의 결과 값을 읽는다
    /// (<see cref="ResultGridModel.TryGetBaseValue"/>) — 키 열을 고친 행의 UPDATE 가 새 키로 행을 찾거나, 낙관적 잠금이 바꾼 값을 원래 값으로 보내 거짓 충돌이 나지 않게.
    /// </summary>
    public static Func<long, int, object?> OriginalValues(ResultGridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return (row, ordinal) => model.TryGetBaseValue(row, ordinal, out var v) ? v : null;
    }

    /// <summary>이 그리드의 편집 세션(편집 모드가 아니면 null).</summary>
    public static GridEditSession? Of(ResultGridModel? model) =>
        model is not null && Sessions.TryGetValue(model, out var s) ? s : null;

    /// <summary>편집 세션이 붙은 그리드(저장 효과를 그릴 곳). 붙은 그리드가 없으면 null.</summary>
    public static ResultGridModel? ModelOf(GridEditSession? session)
    {
        if (session is null)
        {
            return null;
        }

        foreach (var (model, bound) in Sessions)
        {
            if (ReferenceEquals(bound, session))
            {
                return model;
            }
        }

        return null;
    }

    /// <summary>화면 행 → 편집 행 번호(질의 결과 뒤의 행은 새 행의 음수 번호).</summary>
    public static long ToEditRow(ResultGridModel model, GridEditSession session, long viewRow)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(session);
        if (viewRow < model.BaseRowCount)
        {
            return viewRow;
        }

        var inserted = Inserted(session);
        var i = (int)(viewRow - model.BaseRowCount);
        return i >= 0 && i < inserted.Count ? inserted[i] : viewRow;
    }

    /// <summary>
    /// 셀 편집기가 넣은 글자를 열 종류에 맞는 값으로 바꾼다. 빈 글자는 NULL. 숫자·날짜를 읽지 못하면 글자 그대로 넘겨
    /// 반영할 때 서버가 판단하게 한다(바로 거절하지 않는다).
    /// </summary>
    public static object? Parse(ResultGridModel model, int ordinal, string? text)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var kind = model.Columns[ordinal].Kind;
        if (kind is ColumnDataKind.Number && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
        {
            return n;
        }

        if (kind is ColumnDataKind.BinaryFloat && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        if (kind is ColumnDataKind.Date or ColumnDataKind.Timestamp
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t))
        {
            return t;
        }

        return text;
    }

    /// <summary>
    /// 화면 셀을 값 하나 단위로 고칠 수 있는지(행 보기 창). 편집 세션의 열 목록이 그리드와 같고(<c>ResolveTarget</c> 과 같은 검사),
    /// 편집 가능 열(키 열 포함, ROWID 는 제외)이며, 지울 행이 아닐 때만 true. 규칙은 그리드 셀 편집과 같다(TSK-13-20).
    /// </summary>
    public static bool CanEditCell(ResultGridModel model, long viewRow, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (Of(model) is not { } es || !ReferenceEquals(es.Columns, model.Columns))
        {
            return false;
        }

        return es.IsEditableColumn(ordinal)
            && es.GetRowState(ToEditRow(model, es, viewRow)) != GridEditRowState.Deleted;
    }

    /// <summary>화면 셀에 글자로 입력한 값을 반영한다(<see cref="Parse"/> 후 <see cref="GridEditSession.SetCell"/>). 고칠 수 없으면 false.</summary>
    public static bool TrySetCell(ResultGridModel model, long viewRow, int ordinal, string? text)
    {
        if (!CanEditCell(model, viewRow, ordinal) || Of(model) is not { } es)
        {
            return false;
        }

        return es.SetCell(ToEditRow(model, es, viewRow), ordinal, Parse(model, ordinal, text));
    }

    /// <summary>새 행 번호를 화면 순서로(-1, -2 …).</summary>
    private static List<long> Inserted(GridEditSession session) => [.. session.InsertedRows.OrderByDescending(r => r)];

    private sealed class Overlay(ResultGridModel model, GridEditSession session) : IGridEditOverlay
    {
        public int ExtraRows => session.InsertedRows.Count();

        public bool HasPendingChanges => session.IsDirty;

        public bool TryGetValue(long viewRow, int ordinal, out object? value) =>
            session.TryGetPendingValue(ToEditRow(model, session, viewRow), ordinal, out value);

        public bool IsEdited(long viewRow, int ordinal) =>
            session.TryGetPendingValue(ToEditRow(model, session, viewRow), ordinal, out _);

        // 새 행은 질의 결과 뒤에만 붙는다(복제·붙여넣기한 행도 끝에 더한다).
        public long BaseRowOf(long viewRow) => viewRow < model.BaseRowCount ? viewRow : -1;

        public bool NeedsChange(long viewRow, int ordinal) => session.NeedsChange(ToEditRow(model, session, viewRow), ordinal);

        public GridRowMark RowMark(long viewRow) => session.GetRowState(ToEditRow(model, session, viewRow)) switch
        {
            GridEditRowState.Modified => GridRowMark.Modified,
            GridEditRowState.Inserted => GridRowMark.Inserted,
            GridEditRowState.Deleted => GridRowMark.Deleted,
            GridEditRowState.Failed => GridRowMark.Failed,
            _ => GridRowMark.None,
        };
    }
}
