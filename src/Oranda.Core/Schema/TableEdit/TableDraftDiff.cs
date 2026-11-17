using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>편집 줄의 상태(화면 색: 노랑·초록·빨강, 설계 2장).</summary>
public enum DraftRowState
{
    Unchanged,
    Modified,
    Added,
    Dropped,
}

/// <summary>열에서 바뀐 칸.</summary>
[Flags]
public enum ColumnChange
{
    None = 0,
    Name = 1,

    /// <summary>형식·길이·길이 기준·정밀도·소수.</summary>
    Type = 2,
    Nullable = 4,
    Default = 8,
    VirtualExpression = 16,
    Comment = 32,
    Identity = 64,
    Invisible = 128,
    Domain = 256,

    /// <summary>LOB 저장 절(「저장」 페이지, 설계 18.2 — 열 페이지 건수에 넣지 않는다).</summary>
    Lob = 512,
}

/// <summary>테이블 저장 속성에서 바뀐 칸(설계 18.2).</summary>
[Flags]
public enum StorageChange
{
    None = 0,

    /// <summary>테이블스페이스(MOVE).</summary>
    Tablespace = 1,
    PctFree = 2,
    InitTrans = 4,

    /// <summary>압축(MOVE).</summary>
    Compression = 8,
    Logging = 16,
    Parallel = 32,

    /// <summary><c>STORAGE (INITIAL/NEXT)</c>: 기존 테이블은 문장을 만들지 않는다(TE-39) — 검사가 ⚠ 로 알린다.</summary>
    Extents = 64,

    /// <summary>앞 실행에서 남은 REBUILD(<see cref="TableDraft.RebuildIndexes"/>).</summary>
    Rebuild = 128,
}

/// <summary>제약에서 바뀐 칸.</summary>
[Flags]
public enum ConstraintChange
{
    None = 0,
    Name = 1,

    /// <summary>열·조건·참조·삭제 규칙(DROP + ADD 두 문장).</summary>
    Definition = 2,
    Enabled = 4,
}

/// <summary>인덱스에서 바뀐 칸.</summary>
[Flags]
public enum IndexChange
{
    None = 0,
    Name = 1,

    /// <summary>열 구성·순서·DESC·유일·종류·테이블스페이스(DROP + CREATE 두 문장).</summary>
    Definition = 2,
}

/// <summary>열 한 줄의 차이. 원본에만 있으면 <see cref="Edited"/> 는 null(지움), 편집에만 있으면 <see cref="Original"/> 이 null(추가).</summary>
public sealed record ColumnDiff(string RowKey, DraftRowState State, ColumnChange Changes, ColumnDraft? Original, ColumnDraft? Edited);

/// <summary>제약 한 줄의 차이.</summary>
public sealed record ConstraintDiff(string RowKey, DraftRowState State, ConstraintChange Changes, ConstraintDraft? Original, ConstraintDraft? Edited);

/// <summary>인덱스 한 줄의 차이.</summary>
public sealed record IndexDiff(string RowKey, DraftRowState State, IndexChange Changes, IndexDraft? Original, IndexDraft? Edited);

/// <summary>
/// 원본과 편집 모델의 차이(정규화한 뒤 비교, 설계 8.4). 바뀌지 않은 줄은 넣지 않는다. 화면은 줄 상태·칸 표시·페이지별 건수를,
/// 문장 빌더는 문장을 여기서 얻는다.
/// </summary>
public sealed record TableDraftChanges
{
    public IReadOnlyList<ColumnDiff> Columns { get; init; } = [];

    public IReadOnlyList<ConstraintDiff> Constraints { get; init; } = [];

    public IReadOnlyList<IndexDiff> Indexes { get; init; } = [];

    /// <summary>테이블 주석이 바뀌었나.</summary>
    public bool CommentChanged { get; init; }

    /// <summary>테이블 이름이 바뀌었나(기존 테이블, 설계 18.1).</summary>
    public bool Renamed { get; init; }

    /// <summary>바뀐 저장 속성(기존 테이블, 설계 18.2). LOB 은 열 차이의 <see cref="ColumnChange.Lob"/>.</summary>
    public StorageChange Storage { get; init; }

    /// <summary>바뀐 것이 없다(편집 켜고 안 고침 또는 고쳤다 되돌림 = 문장 0개).</summary>
    public bool IsEmpty => Columns.Count == 0 && Constraints.Count == 0 && Indexes.Count == 0 && !CommentChanged && !Renamed && Storage == StorageChange.None;

    /// <summary>페이지 이름 옆 바뀐 건수(설계 7.1).</summary>
    public int CountFor(TableEditPage page) => page switch
    {
        TableEditPage.Columns => Columns.Count(d => d.Changes != ColumnChange.Lob),
        TableEditPage.Constraints => Constraints.Count,
        TableEditPage.Indexes => Indexes.Count,
        TableEditPage.Description => (CommentChanged ? 1 : 0) + (Renamed ? 1 : 0),
        TableEditPage.Storage => System.Numerics.BitOperations.PopCount((uint)Storage) + Columns.Count(d => d.Changes.HasFlag(ColumnChange.Lob)),
        _ => 0,
    };

    /// <summary>줄 키의 상태(차이에 없으면 <see cref="DraftRowState.Unchanged"/>).</summary>
    public DraftRowState StateOf(string rowKey) =>
        Columns.FirstOrDefault(d => d.RowKey == rowKey)?.State
        ?? Constraints.FirstOrDefault(d => d.RowKey == rowKey)?.State
        ?? Indexes.FirstOrDefault(d => d.RowKey == rowKey)?.State
        ?? DraftRowState.Unchanged;
}

/// <summary>
/// 차이 계산(설계 8.1: 줄 키로 맞춤, 8.4: 정규화 뒤 비교). 지울 열을 쓰는 제약·인덱스(열 줄 키로 가리키는 것)는 사용자가 표시하지 않았어도 지움으로 본다
/// (설계 8.3: 먼저 지워야 열 지우기가 된다). PK·UK 가 만든 인덱스(<see cref="IndexDraft.ConstraintName"/>)는 인덱스 페이지에서 고치지 않으므로 비교하지 않는다.
/// </summary>
public static class TableDraftDiff
{
    /// <summary>원본 <paramref name="original"/> 과 편집 <paramref name="edited"/> 의 차이.</summary>
    public static TableDraftChanges Compute(TableDraft original, TableDraft edited, ITableEditDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(dialect);

        var columns = Rows(original.Columns, edited.Columns, c => c.RowKey, c => c.IsDropped, (o, e) => ColumnChanges(o, e, dialect),
            (key, state, changes, o, e) => new ColumnDiff(key, state, changes, o, e));
        var droppedColumns = columns.Where(d => d.State == DraftRowState.Dropped).Select(d => d.RowKey).ToHashSet(StringComparer.Ordinal);

        // 지우고 다시 만드는 열(가상↔일반, 가상 식을 MODIFY 로 못 바꾸는 방언)을 쓰는 제약·인덱스는 열과 함께 사라지므로 지우고 다시 만든다.
        var recreatedColumns = columns.Where(d => IsRecreated(d, dialect)).Select(d => d.RowKey).ToHashSet(StringComparer.Ordinal);

        var constraints = Rows(original.Constraints, edited.Constraints, c => c.RowKey, c => c.IsDropped,
            (o, e) => ConstraintChanges(o, e, dialect) | (o.ColumnKeys.Any(recreatedColumns.Contains) ? ConstraintChange.Definition : ConstraintChange.None),
            (key, state, changes, o, e) => new ConstraintDiff(key, state, changes, o, e),
            o => o.ColumnKeys.Any(droppedColumns.Contains));

        var indexes = Rows(
            [.. original.Indexes.Where(i => i.ConstraintName is null)], [.. edited.Indexes.Where(i => i.ConstraintName is null)],
            i => i.RowKey, i => i.IsDropped,
            (o, e) => IndexChanges(o, e, dialect)
                      | (o.Columns.Any(c => c.ColumnKey is { } k && recreatedColumns.Contains(k)) ? IndexChange.Definition : IndexChange.None),
            (key, state, changes, o, e) => new IndexDiff(key, state, changes, o, e),
            o => o.Columns.Any(c => c.ColumnKey is { } k && droppedColumns.Contains(k)));

        return new TableDraftChanges
        {
            Columns = columns,
            Constraints = constraints,
            Indexes = indexes,
            CommentChanged = !string.Equals(original.Comment ?? string.Empty, edited.Comment ?? string.Empty, StringComparison.Ordinal),
            Renamed = !edited.IsNewTable && !original.IsNewTable && !string.Equals(original.Name, edited.Name, StringComparison.Ordinal),
            Storage = edited.IsNewTable ? StorageChange.None : StorageChanges(original, edited),
        };
    }

    /// <summary>
    /// 저장 속성 차이: 편집 칸이 비어 있지 않고(null 아님) 원본과 다를 때만(설계 18.2 「바꿀 값」만 비교 — 서버 기본값 표기로 거짓 차이가 나지 않는다).
    /// </summary>
    public static StorageChange StorageChanges(TableDraft original, TableDraft edited)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        var (o, e) = (original.Storage, edited.Storage);
        var changes = StorageChange.None;
        if (NameSet(original.Tablespace, edited.Tablespace))
        {
            changes |= StorageChange.Tablespace;
        }

        changes |= Set(o.PctFree, e.PctFree, StorageChange.PctFree) | Set(o.InitTrans, e.InitTrans, StorageChange.InitTrans)
                   | Set(o.Compression, e.Compression, StorageChange.Compression) | Set(o.Logging, e.Logging, StorageChange.Logging)
                   | Set(o.Parallel, e.Parallel, StorageChange.Parallel);
        if (TextSet(o.InitialExtent, e.InitialExtent) || TextSet(o.NextExtent, e.NextExtent))
        {
            changes |= StorageChange.Extents;
        }

        if (edited.RebuildIndexes.Count > 0)
        {
            changes |= StorageChange.Rebuild;
        }

        return changes;
    }

    /// <summary>
    /// 이름 칸(테이블스페이스) 차이: 칸 글을 <see cref="TableEditNames.FromInput"/> 로 읽어(따옴표 없으면 대문자) 사전 표기와 비교. 빈 칸 = 그대로.
    /// </summary>
    private static bool NameSet(string? original, string? edited) =>
        !string.IsNullOrWhiteSpace(edited) && !string.Equals(original, TableEditNames.FromInput(edited), StringComparison.Ordinal);

    /// <summary>LOB 저장 절이 바뀌었나(같은 규칙: 편집 칸이 비지 않고 원본과 다름).</summary>
    public static bool LobChanged(LobStorage? original, LobStorage? edited) =>
        edited is not null
        && (Set(original?.Kind, edited.Kind, true) || NameSet(original?.Tablespace, edited.Tablespace) || Set(original?.Compression, edited.Compression, true)
            || Set(original?.Deduplicate, edited.Deduplicate, true) || Set(original?.InRow, edited.InRow, true));

    private static T Set<TValue, T>(TValue? o, TValue? e, T flag)
        where TValue : struct
        where T : struct =>
        e is { } v && !EqualityComparer<TValue?>.Default.Equals(o, v) ? flag : default;

    private static bool TextSet(string? o, string? e) =>
        !string.IsNullOrWhiteSpace(e) && !string.Equals(o?.Trim(), e.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 함수 기반 인덱스의 식이 이 편집에서 지우는 열을 쓰는가. 서버가 열과 함께 지우므로 문장은 만들지 않지만(T1b 결정), 뒤 문장(REBUILD·규칙 이름 바꾸기)은
    /// 이 인덱스를 다루면 안 된다(ORA-01418).
    /// </summary>
    public static bool GoesWithDroppedColumn(IndexDraft index, TableDraftChanges changes)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(changes);
        var dropped = changes.Columns.Where(d => d.State == DraftRowState.Dropped).Select(d => d.Original!.Name).ToHashSet(StringComparer.Ordinal);
        return dropped.Count > 0 && index.Columns.Any(c => c.Expression is { } x && TableEditExpression.Names(x).Any(dropped.Contains));
    }

    /// <summary>열을 MODIFY 가 아니라 DROP + ADD 로 바꾸는가(가상↔일반, 또는 방언이 가상 식 MODIFY 를 못 함).</summary>
    public static bool IsRecreated(ColumnDiff d, ITableEditDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(dialect);
        return d.State == DraftRowState.Modified && d.Changes.HasFlag(ColumnChange.VirtualExpression)
               && (d.Original!.IsVirtual != d.Edited!.IsVirtual || !dialect.SupportsVirtualColumnModify);
    }

    /// <summary>
    /// 줄 맞추기. 편집 순서(빠진 원본 줄은 끝에 원본 순서로)로 돌려준다. <paramref name="dropsWith"/> 가 참인 원본 줄은 편집에 살아 있어도 지움으로 본다.
    /// </summary>
    private static List<TDiff> Rows<TRow, TChange, TDiff>(
        IReadOnlyList<TRow> original, IReadOnlyList<TRow> edited, Func<TRow, string> key, Func<TRow, bool> dropped, Func<TRow, TRow, TChange> compare,
        Func<string, DraftRowState, TChange, TRow?, TRow?, TDiff> make, Func<TRow, bool>? dropsWith = null)
        where TRow : class
        where TChange : struct, Enum
    {
        var originals = original.ToDictionary(key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TDiff>();
        foreach (var e in edited)
        {
            var k = key(e);
            seen.Add(k);
            if (!originals.TryGetValue(k, out var o))
            {
                if (!dropped(e))
                {
                    result.Add(make(k, DraftRowState.Added, default, null, e));
                }

                continue;
            }

            if (dropped(e) || dropsWith?.Invoke(o) == true)
            {
                result.Add(make(k, DraftRowState.Dropped, default, o, e));
                continue;
            }

            var changes = compare(o, e);
            if (!EqualityComparer<TChange>.Default.Equals(changes, default))
            {
                result.Add(make(k, DraftRowState.Modified, changes, o, e));
            }
        }

        foreach (var o in original.Where(o => !seen.Contains(key(o))))
        {
            result.Add(make(key(o), DraftRowState.Dropped, default, o, null));
        }

        return result;
    }

    private static ColumnChange ColumnChanges(ColumnDraft o, ColumnDraft e, ITableEditDialect dialect)
    {
        var changes = ColumnChange.None;
        if (!string.Equals(o.Name, e.Name, StringComparison.Ordinal))
        {
            changes |= ColumnChange.Name;
        }

        if (!(e.IsVirtual && string.IsNullOrWhiteSpace(e.DataType))
            && !string.Equals(dialect.NormalizeType(o), dialect.NormalizeType(e with { CharSemantics = e.CharSemantics ?? o.CharSemantics }), StringComparison.Ordinal))
        {
            changes |= ColumnChange.Type;
        }

        if (o.IsNullable != e.IsNullable)
        {
            changes |= ColumnChange.Nullable;
        }

        // 신원 열의 기본값도 비교한다(신원 열에 기본값을 쓰면 검사가 ✖ — 소리 없이 버리지 않는다).
        if (!e.IsVirtual
            && !string.Equals(dialect.NormalizeDefault(o.DefaultValue), dialect.NormalizeDefault(e.DefaultValue), StringComparison.Ordinal))
        {
            changes |= ColumnChange.Default;
        }

        if (o.IsVirtual != e.IsVirtual
            || (e.IsVirtual && !string.Equals(dialect.NormalizeExpression(o.VirtualExpression), dialect.NormalizeExpression(e.VirtualExpression), StringComparison.Ordinal)))
        {
            changes |= ColumnChange.VirtualExpression;
        }

        if (!string.Equals(o.Comment ?? string.Empty, e.Comment ?? string.Empty, StringComparison.Ordinal))
        {
            changes |= ColumnChange.Comment;
        }

        if (o.Identity != e.Identity || o.IsIdentityUnknown != e.IsIdentityUnknown)
        {
            changes |= ColumnChange.Identity;
        }

        if (o.IsInvisible != e.IsInvisible)
        {
            changes |= ColumnChange.Invisible;
        }

        if (!string.Equals(Blank(o.Domain), Blank(e.Domain), StringComparison.OrdinalIgnoreCase))
        {
            changes |= ColumnChange.Domain;
        }

        if (LobChanged(o.Lob, e.Lob))
        {
            changes |= ColumnChange.Lob;
        }

        return changes;
    }

    private static ConstraintChange ConstraintChanges(ConstraintDraft o, ConstraintDraft e, ITableEditDialect dialect)
    {
        var changes = ConstraintChange.None;
        if (!string.IsNullOrWhiteSpace(e.Name) && !string.Equals(o.Name, e.Name, StringComparison.Ordinal))
        {
            changes |= ConstraintChange.Name;
        }

        if (o.Kind != e.Kind
            || !o.ColumnKeys.SequenceEqual(e.ColumnKeys, StringComparer.Ordinal)
            || !string.Equals(o.ReferencedTable?.Owner, e.ReferencedTable?.Owner, StringComparison.Ordinal)
            || !string.Equals(o.ReferencedTable?.Name, e.ReferencedTable?.Name, StringComparison.Ordinal)
            || !o.ReferencedColumns.SequenceEqual(e.ReferencedColumns, StringComparer.Ordinal)
            || o.DeleteRule != e.DeleteRule
            || !string.Equals(dialect.NormalizeCondition(o.Condition), dialect.NormalizeCondition(e.Condition), StringComparison.Ordinal))
        {
            changes |= ConstraintChange.Definition;
        }

        if (o.IsEnabled != e.IsEnabled)
        {
            changes |= ConstraintChange.Enabled;
        }

        return changes;
    }

    private static IndexChange IndexChanges(IndexDraft o, IndexDraft e, ITableEditDialect dialect)
    {
        var changes = IndexChange.None;
        if (!string.IsNullOrWhiteSpace(e.Name) && !string.Equals(o.Name, e.Name, StringComparison.Ordinal))
        {
            changes |= IndexChange.Name;
        }

        var sameColumns = o.Columns.Count == e.Columns.Count && o.Columns.Zip(e.Columns).All(p =>
            string.Equals(p.First.ColumnKey, p.Second.ColumnKey, StringComparison.Ordinal)
            && p.First.Descending == p.Second.Descending
            && string.Equals(dialect.NormalizeExpression(p.First.Expression), dialect.NormalizeExpression(p.Second.Expression), StringComparison.Ordinal));
        var tablespaceChanged = !string.IsNullOrWhiteSpace(e.Tablespace) && !string.Equals(o.Tablespace, e.Tablespace, StringComparison.OrdinalIgnoreCase);
        if (o.IsUnique != e.IsUnique || o.Kind != e.Kind || !sameColumns || tablespaceChanged)
        {
            changes |= IndexChange.Definition;
        }

        return changes;
    }

    private static string Blank(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
}
