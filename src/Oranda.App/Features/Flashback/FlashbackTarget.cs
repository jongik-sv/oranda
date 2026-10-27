using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// 플래시백이 읽을 대상(TSK-13-05): 편집 가능한 단일 표 결과에서 표·읽을 열·키 열을 정리한 것.
/// <see cref="Names"/> 는 AS OF 질의가 읽는 표 열 이름(결과 열 순서, LOB 등은 뺀다), <see cref="Ordinals"/> 는 그 열의 결과 열 번호,
/// <see cref="KeyPositions"/>·<see cref="EditablePositions"/> 는 <see cref="Names"/> 안의 자리다.
/// </summary>
public sealed record FlashbackTarget(DbObjectRef Table, IReadOnlyList<string> Names, IReadOnlyList<int> Ordinals,
                                     IReadOnlyList<int> KeyPositions, IReadOnlyList<int> EditablePositions)
{
    public static string NeedsEditMode => EditModeKey.Localize(
        "편집 모드{0}로 다시 실행하면 플래시백을 쓸 수 있습니다.",
        "Rerun in edit mode{0} to use Flashback.");

    public const string MultipleTables = "여러 표가 얽힌 결과에서는 쓸 수 없습니다.";

    /// <summary>일반 실행 결과에 키(PK) 열이 빠졌을 때: 열을 함께 조회하거나 편집 모드(ROWID 키)로 다시 실행하라고 안내한다.</summary>
    public static string NeedsKeyColumns => EditModeKey.Localize(
        "키(PK) 열이 결과에 없어 플래시백을 쓸 수 없습니다(시노님으로 조회했다면 시노님이 가리키는 표의 PK). PK 열을 함께 조회하거나 편집 모드{0}로 다시 실행하세요.",
        "Flashback needs the key (PK) columns in the result (for a synonym, the PK of the table it points to). Select the PK columns too, or rerun in edit mode{0}.");

    /// <summary>「이전 값으로 되돌리기」는 쓰기라 편집 모드 결과에서만 한다.</summary>
    public static string RestoreNeedsEditMode => EditModeKey.Localize(
        "되돌리기는 편집 모드{0}로 다시 실행한 결과에서만 쓸 수 있습니다.",
        "Restore works only on a result run in edit mode{0}.");

    public IReadOnlyList<string> KeyNames => [.. KeyPositions.Select(p => Names[p])];

    /// <summary>결과 열 번호 목록(질의가 읽는 열 순서)에서 키 값만 뽑는다.</summary>
    public object?[] KeyOf(IReadOnlyList<object?> row) => [.. KeyPositions.Select(p => row[p])];

    /// <summary>
    /// 대상을 정한다. 단일 표·키(PK·ROWID)가 없으면 null 이고 <paramref name="reason"/> 에 이유 한 줄.
    /// <paramref name="plain"/> 은 편집 모드가 아닌 일반 실행 결과(키는 PK 뿐이라 빠지면 열을 더하라고 안내한다).
    /// </summary>
    public static FlashbackTarget? TryCreate(EditCapability? capability, IReadOnlyList<string?>? columnNames, IReadOnlyList<ResultColumn> columns,
                                             out string reason, bool plain = false)
    {
        ArgumentNullException.ThrowIfNull(columns);
        reason = Unavailable(capability, plain);
        if (reason.Length > 0 || capability is not { Target: { } table } || columnNames is null || capability.KeyColumnOrdinals.Count == 0)
        {
            return null;
        }

        var names = new List<string>();
        var ordinals = new List<int>();
        var keys = new List<int>();
        var editable = new List<int>();
        for (var o = 0; o < columns.Count && o < columnNames.Count; o++)
        {
            if (columnNames[o] is not { } name || names.Contains(name, StringComparer.Ordinal) || IsLarge(columns[o]))
            {
                continue;
            }

            if (capability.KeyColumnOrdinals.Contains(o))
            {
                keys.Add(names.Count);
            }
            else if (capability.EditableColumnOrdinals.Contains(o))
            {
                editable.Add(names.Count);
            }

            names.Add(name);
            ordinals.Add(o);
        }

        if (keys.Count != capability.KeyColumnOrdinals.Count)
        {
            reason = plain ? NeedsKeyColumns : "키 열을 읽을 수 없어 플래시백을 쓸 수 없습니다.";
            return null;
        }

        reason = string.Empty;
        return new FlashbackTarget(table, names, ordinals, keys, editable);
    }

    /// <summary>쓸 수 없는 이유(쓸 수 있으면 빈 글).</summary>
    public static string Unavailable(EditCapability? capability, bool plain = false)
    {
        if (capability is null)
        {
            return NeedsEditMode;
        }

        if (capability.CanEdit)
        {
            return string.Empty;
        }

        return capability.Reason switch
        {
            EditBlockReason.MultipleTables or EditBlockReason.NotKeyPreserved => MultipleTables,
            EditBlockReason.NoKeyColumns when plain => NeedsKeyColumns,
            EditBlockReason.NoKeyColumns => "행을 식별할 키(PK·ROWID)가 없는 결과라 플래시백을 쓸 수 없습니다(시노님은 가리키는 표의 PK 를 봅니다).",
            _ => capability.Message is { Length: > 0 } m ? "편집할 수 없는 결과라 플래시백을 쓸 수 없습니다: " + m : "편집할 수 없는 결과라 플래시백을 쓸 수 없습니다.",
        };
    }

    private static bool IsLarge(ResultColumn c) =>
        c.Kind is ColumnDataKind.Clob or ColumnDataKind.Blob or ColumnDataKind.Json or ColumnDataKind.Xml or ColumnDataKind.Vector
            or ColumnDataKind.UserDefinedType or ColumnDataKind.RefCursor or ColumnDataKind.Other;
}

/// <summary>되돌릴 변경 계획: 키로 짝지은 행마다 N분 전 값과 달라진 편집 가능 칸만 담는다.</summary>
public sealed record FlashbackRestorePlan(IReadOnlyList<RowChange> Changes, int Unchanged, int MissingThen);

public static class FlashbackPlanner
{
    /// <summary>
    /// 지금 값 행(<paramref name="now"/>, 그리드 행 번호와 함께)과 N분 전 값 행(<paramref name="past"/>)을 키로 짝지어 되돌리는 변경을 만든다.
    /// 그때 없던 행은 <see cref="FlashbackRestorePlan.MissingThen"/>, 바뀐 칸이 없는 행은 <see cref="FlashbackRestorePlan.Unchanged"/> 로 센다.
    /// </summary>
    public static FlashbackRestorePlan PlanRestore(FlashbackTarget target, IReadOnlyList<(long Row, object?[] Values)> now, IReadOnlyList<object?[]> past)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(past);
        var then = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (var p in past)
        {
            then[FlashbackDiff.KeyText(target.KeyOf(p))] = p;
        }

        var changes = new List<RowChange>();
        int unchanged = 0, missing = 0;
        foreach (var (row, values) in now)
        {
            if (!then.TryGetValue(FlashbackDiff.KeyText(target.KeyOf(values)), out var old))
            {
                missing++;
                continue;
            }

            var set = new Dictionary<string, object?>(StringComparer.Ordinal);
            var seen = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in target.EditablePositions)
            {
                if (!FlashbackDiff.SameValue(values[p], old[p]))
                {
                    set[target.Names[p]] = old[p] is DBNull ? null : old[p];
                    seen[target.Names[p]] = values[p] is DBNull ? null : values[p];
                }
            }

            if (set.Count == 0)
            {
                unchanged++;
                continue;
            }

            var key = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in target.KeyPositions)
            {
                key[target.Names[p]] = values[p];
            }

            changes.Add(new RowChange(RowChangeKind.Update, row, target.Table) { Key = key, NewValues = set, OriginalValues = seen });
        }

        return new FlashbackRestorePlan(changes, unchanged, missing);
    }
}

/// <summary>플래시백을 실행할 곳: 결과 탭·접속·대상 표. <see cref="Edit"/> 는 편집 모드 결과일 때만 있다(되돌리기·미반영 편집 검사에 쓴다).</summary>
internal sealed record FlashbackSource(ResultTabsViewModel Tabs, ResultTabItem Tab, ISession Session, FlashbackTarget Target, GridEditSession? Edit);
