using System.Text;
using Oranda.Core.Connection;

namespace Oranda.Core.Schema.TableEdit.Partitions;

/// <summary>파티션 줄에서 바뀐 것.</summary>
[Flags]
public enum PartitionRowChange
{
    None = 0,
    Name = 1,
    Truncated = 2,

    /// <summary>다른 줄로 합쳐진다(<see cref="PartitionDraft.MergedInto"/>).</summary>
    MergedAway = 4,

    /// <summary>다른 줄이 합쳐져 들어온다(경계가 넓어짐).</summary>
    MergeTarget = 8,

    /// <summary>새 줄이 이 줄에서 떼어진다(목록이면 값이 줄어듦).</summary>
    SplitSource = 16,

    /// <summary>원본 줄을 떼어 낸 새 줄.</summary>
    SplitOff = 32,
}

/// <summary>파티션 한 줄의 차이(원본에만 있으면 <see cref="Edited"/> = null, 새 줄이면 <see cref="Original"/> = null).</summary>
public sealed record PartitionRowDiff(string RowKey, DraftRowState State, PartitionRowChange Changes, PartitionDraft? Original, PartitionDraft? Edited);

/// <summary>문장으로 만들 수 없는 편집(화면은 그 줄에 이유를 보이고, 문장 빌더는 그 편집을 빼고 만든다).</summary>
/// <param name="ReasonKey">문자열 키(<c>TableEdit.partition.*</c> 또는 방언의 미지원 이유 키)</param>
public sealed record PartitionProblem(string RowKey, string ReasonKey);

/// <summary>파티션 페이지의 차이(설계 18.4). 바뀌지 않은 줄은 넣지 않는다.</summary>
public sealed record PartitionChanges
{
    public static PartitionChanges Empty { get; } = new();

    public IReadOnlyList<PartitionRowDiff> Rows { get; init; } = [];

    /// <summary>INTERVAL 이 바뀌었다(켬·끔·식 바꿈).</summary>
    public bool IntervalChanged { get; init; }

    /// <summary>일반 테이블을 파티션 테이블로 바꾼다(원본에 방식 없음, 편집에 있음).</summary>
    public bool Converting { get; init; }

    public IReadOnlyList<PartitionProblem> Problems { get; init; } = [];

    /// <summary>문장이 하나도 안 나온다(편집 켜고 안 고침·고쳤다 되돌림). 문제만 있는 편집도 문장이 없다.</summary>
    public bool IsEmpty => Rows.Count == 0 && !IntervalChanged && !Converting;
}

/// <summary>
/// 파티션 차이 계산(줄 키로 맞춤, 경계는 공백·대소문자를 맞춰 비교). 설계 18.4 규칙:
/// <list type="bullet">
/// <item>편집 목록 순서 = 경계 순서. 범위 새 줄: 마지막 원본 줄 뒤 = ADD, 원본 줄들 사이 = 바로 다음 원본 줄(지우거나 합치는 줄은 건너뜀)을 나누기
/// (<see cref="PartitionDraft.SplitFrom"/> 이 있으면 그 줄). 목록 새 줄: <see cref="PartitionDraft.SplitFrom"/> 이면 나누기, 아니면 ADD. 해시 새 줄 = ADD, 해시 지우기 = 끝에서부터 COALESCE.</item>
/// <item>범위 합치기는 붙어 있는 줄끼리만(대상까지 사이 줄이 모두 같은 대상으로 합쳐짐).</item>
/// <item>문장이 없는 편집은 <see cref="PartitionChanges.Problems"/>: 원본 경계·테이블스페이스·방식·키 바꾸기, MAXVALUE 뒤·INTERVAL 테이블·DEFAULT 있는 목록에 ADD,
/// 이름 겹침, 떼어 낼 원본·합칠 대상이 문장을 못 냄, 방언이 못 하는 문장(<see cref="ITableEditDialect.UnsupportedPartitionReasonKey"/>).</item>
/// <item>문제가 하나라도 있는 줄은 문장을 만들지 않는다(반쯤만 반영하지 않게). 새 줄에 지우기·합치기를 켜면 그 줄은 없는 것으로 본다.</item>
/// </list>
/// </summary>
public static class PartitionDiff
{
    public const string BoundChangeKey = "TableEdit.partition.boundChange";
    public const string TablespaceChangeKey = "TableEdit.partition.tablespaceChange";
    public const string SchemeChangeKey = "TableEdit.partition.schemeChange";
    public const string AddAfterMaxValueKey = "TableEdit.partition.addAfterMaxValue";
    public const string UnpartitionKey = "TableEdit.partition.unpartition";
    public const string BadMergeKey = "TableEdit.partition.badMerge";

    /// <summary>한 문장(<c>MODIFY PARTITION BY … ONLINE</c>)으로 못 바꿈 → 화면은 열 순서 바꾸기의 다시 만들기 방식으로 보낸다(TE-41, ⚠ 오래 걸림).</summary>
    public const string ConvertNeedsRebuildKey = "TableEdit.partition.convertNeedsRebuild";

    /// <summary>INTERVAL 이 켜진 테이블은 ADD 를 받지 않는다(Oracle ORA-14760) — 자동 추가가 만든다.</summary>
    public const string AddWithIntervalKey = "TableEdit.partition.addWithInterval";

    /// <summary>DEFAULT 파티션이 있는 목록은 ADD 를 받지 않는다(Oracle ORA-14323) — DEFAULT 에서 나누기.</summary>
    public const string AddWithDefaultKey = "TableEdit.partition.addWithDefault";

    /// <summary>편집 뒤 같은 이름의 파티션이 둘 이상.</summary>
    public const string DuplicateNameKey = "TableEdit.partition.duplicateName";

    /// <summary>이름이 아직 다른 파티션이 쓰는 이름이다(맞바꾸기 등, 차례로 바꾸면 겹침).</summary>
    public const string NameInUseKey = "TableEdit.partition.nameInUse";

    /// <summary>떼어 낼 원본 줄이 지워지거나 문장을 못 내 나누기를 할 수 없다.</summary>
    public const string SplitSourceUnavailableKey = "TableEdit.partition.splitSourceUnavailable";

    /// <summary>해시는 합치기가 없다(줄이기 = 끝에서부터 COALESCE).</summary>
    public const string HashMergeKey = "TableEdit.partition.hashMerge";

    /// <summary>해시 줄이기(COALESCE)는 서버가 마지막 파티션을 없앤다 — 끝에서부터만 지울 수 있다.</summary>
    public const string HashCoalesceLastKey = "TableEdit.partition.hashCoalesceLast";

    /// <summary>하위 파티션 템플릿은 LIST·HASH 만 만든다(PA-11).</summary>
    public const string SubpartitionMethodKey = "TableEdit.partition.subpartitionMethod";

    /// <summary>원본 <paramref name="original"/> 과 편집 <paramref name="edited"/> 의 파티션 차이.</summary>
    public static PartitionChanges Compute(PartitionTableDraft original, PartitionTableDraft edited, ITableEditDialect dialect, ServerInfo? server = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(dialect);

        if (original.Scheme is null && edited.Scheme is null)
        {
            return PartitionChanges.Empty;
        }

        var problems = new List<PartitionProblem>();
        if (original.Scheme is null)
        {
            if (dialect.UnsupportedPartitionReasonKey(PartitionOperation.Convert, edited.Scheme!.Method, server) is { } convertKey)
            {
                problems.Add(new PartitionProblem(TableDraft.TableRowKey, convertKey));
            }
            else if (!dialect.SupportsOnlinePartitionConversion(server))
            {
                problems.Add(new PartitionProblem(TableDraft.TableRowKey, ConvertNeedsRebuildKey));
            }

            // 하위 파티션 템플릿(PA-11): LIST·HASH 만, 방언이 못 하면 그 이유.
            if (edited.Scheme.SubpartitionTemplate is { } sub)
            {
                if (sub.Method == PartitionMethod.Range)
                {
                    problems.Add(new PartitionProblem(TableDraft.TableRowKey, SubpartitionMethodKey));
                }
                else if (dialect.UnsupportedPartitionReasonKey(PartitionOperation.Subpartition, sub.Method, server) is { } subKey)
                {
                    problems.Add(new PartitionProblem(TableDraft.TableRowKey, subKey));
                }
            }

            return new PartitionChanges { Converting = true, Problems = problems };
        }

        if (edited.Scheme is null)
        {
            return new PartitionChanges { Problems = [new PartitionProblem(TableDraft.TableRowKey, UnpartitionKey)] };
        }

        var scheme = original.Scheme;
        if (scheme.Method != edited.Scheme.Method || !scheme.KeyColumns.SequenceEqual(edited.Scheme.KeyColumns, StringComparer.Ordinal)
            || edited.Scheme.SubpartitionTemplate is not null)
        {
            return new PartitionChanges { Problems = [new PartitionProblem(TableDraft.TableRowKey, SchemeChangeKey)] };
        }

        var method = scheme.Method;
        var intervalChanged = !string.Equals(Canonical(scheme.Interval), Canonical(edited.Scheme.Interval), StringComparison.Ordinal);
        if (intervalChanged && Unsupported(PartitionOperation.SetInterval) is { } intervalKey)
        {
            problems.Add(new PartitionProblem(TableDraft.TableRowKey, intervalKey));
            intervalChanged = false;
        }

        var originals = original.Partitions.ToDictionary(p => p.RowKey, StringComparer.Ordinal);
        var edits = edited.Partitions.ToDictionary(p => p.RowKey, StringComparer.Ordinal);
        var order = edited.Partitions.Select((p, i) => (p.RowKey, i)).ToDictionary(x => x.RowKey, x => x.i, StringComparer.Ordinal);
        var mergeTargets = edited.Partitions.Where(p => p.MergedInto is not null && originals.ContainsKey(p.RowKey)).Select(p => p.MergedInto!).ToHashSet(StringComparer.Ordinal);
        var splitSources = SplitSources(original, edited);
        var rows = new List<PartitionRowDiff>();
        var rowProblems = new Dictionary<string, string>(StringComparer.Ordinal);
        void Problem(string rowKey, string key)
        {
            problems.Add(new PartitionProblem(rowKey, key));
            rowProblems.TryAdd(rowKey, key);
        }

        // 원본 줄(지운 것 포함 — 편집 목록에서 빠진 원본 줄도 지움).
        var droppedHash = new List<string>();
        foreach (var o in original.Partitions)
        {
            var e = edits.GetValueOrDefault(o.RowKey);
            if (e is null || e.IsDropped)
            {
                if (method == PartitionMethod.Hash)
                {
                    droppedHash.Add(o.RowKey);
                }

                var dropOp = method == PartitionMethod.Hash ? PartitionOperation.Coalesce : PartitionOperation.Drop;
                if (Unsupported(dropOp) is { } dropKey)
                {
                    Problem(o.RowKey, dropKey);
                    continue;
                }

                rows.Add(new PartitionRowDiff(o.RowKey, DraftRowState.Dropped, PartitionRowChange.None, o, e));
                continue;
            }

            if (e.MergedInto is not null)
            {
                if (method == PartitionMethod.Hash)
                {
                    Problem(o.RowKey, Unsupported(PartitionOperation.Merge) ?? HashMergeKey);
                }
                else if (!MergeIsValid(e, edits, originals, order, edited, method))
                {
                    Problem(o.RowKey, BadMergeKey);
                }
                else if (Unsupported(PartitionOperation.Merge) is { } mergeKey)
                {
                    Problem(o.RowKey, mergeKey);
                }
                else
                {
                    rows.Add(new PartitionRowDiff(o.RowKey, DraftRowState.Modified, PartitionRowChange.MergedAway, o, e));
                }

                continue;
            }

            var changes = PartitionRowChange.None;
            if (!string.Equals(o.Name, e.Name, StringComparison.Ordinal))
            {
                changes |= PartitionRowChange.Name;
            }

            if (e.IsTruncated && !o.IsTruncated)
            {
                changes |= PartitionRowChange.Truncated;
            }

            if (mergeTargets.Contains(e.RowKey))
            {
                changes |= PartitionRowChange.MergeTarget;
            }

            if (splitSources.ContainsKey(e.RowKey))
            {
                changes |= PartitionRowChange.SplitSource;
            }

            // 경계: 합쳐 받는 줄(넓어짐)·목록에서 떼어 주는 줄(값이 줄어듦)은 바뀌어도 된다. 그 밖은 문장이 없다.
            var boundMayChange = changes.HasFlag(PartitionRowChange.MergeTarget) || (method == PartitionMethod.List && changes.HasFlag(PartitionRowChange.SplitSource));
            if (!boundMayChange && !string.Equals(Canonical(o.Bound), Canonical(e.Bound), StringComparison.Ordinal))
            {
                Problem(e.RowKey, BoundChangeKey);
            }

            if (!string.Equals(Canonical(o.Tablespace), Canonical(e.Tablespace), StringComparison.Ordinal))
            {
                Problem(e.RowKey, TablespaceChangeKey);
            }

            if (changes.HasFlag(PartitionRowChange.Name) && (changes & (PartitionRowChange.MergeTarget | PartitionRowChange.SplitSource)) == 0
                && Unsupported(PartitionOperation.Rename) is { } renameKey)
            {
                Problem(e.RowKey, renameKey);
            }

            if (changes.HasFlag(PartitionRowChange.Truncated) && Unsupported(PartitionOperation.Truncate) is { } truncateKey)
            {
                Problem(e.RowKey, truncateKey);
            }

            if (changes != PartitionRowChange.None && !rowProblems.ContainsKey(e.RowKey))
            {
                rows.Add(new PartitionRowDiff(e.RowKey, DraftRowState.Modified, changes, o, e));
            }
        }

        // 해시 줄이기: 서버가 끝 파티션을 없애므로 끝에서부터 N개만.
        if (droppedHash.Count > 0)
        {
            var tail = original.Partitions.Skip(original.Partitions.Count - droppedHash.Count).Select(p => p.RowKey).ToHashSet(StringComparer.Ordinal);
            foreach (var key in droppedHash.Where(k => !tail.Contains(k)))
            {
                rows.RemoveAll(r => r.RowKey == key);
                Problem(key, HashCoalesceLastKey);
            }
        }

        // 새 줄.
        var hasDefault = method == PartitionMethod.List && original.Partitions.Any(o => edits.TryGetValue(o.RowKey, out var e) && !e.IsDropped && e.MergedInto is null
            && Canonical(e.Bound) == "DEFAULT");
        var intervalStaysOn = !string.IsNullOrWhiteSpace(scheme.Interval) && !string.IsNullOrWhiteSpace(edited.Scheme.Interval);
        foreach (var e in edited.Partitions.Where(e => !originals.ContainsKey(e.RowKey) && !e.IsDropped && e.MergedInto is null))
        {
            var source = splitSources.FirstOrDefault(kv => kv.Value.Contains(e.RowKey)).Key;
            if (source is not null)
            {
                if (Unsupported(PartitionOperation.Split) is { } splitKey)
                {
                    Problem(e.RowKey, splitKey);
                }
                else
                {
                    rows.Add(new PartitionRowDiff(e.RowKey, DraftRowState.Added, PartitionRowChange.SplitOff, null, e));
                }

                continue;
            }

            var addProblem = method switch
            {
                PartitionMethod.Range when EndsWithMaxValue(original) => AddAfterMaxValueKey,
                PartitionMethod.Range when intervalStaysOn => AddWithIntervalKey,
                PartitionMethod.List when hasDefault => AddWithDefaultKey,
                _ => Unsupported(PartitionOperation.Add),
            };
            if (addProblem is not null)
            {
                Problem(e.RowKey, addProblem);
                continue;
            }

            rows.Add(new PartitionRowDiff(e.RowKey, DraftRowState.Added, PartitionRowChange.None, null, e));
        }

        // 이름: 편집 뒤 남는 이름이 겹치면 안 되고, 차례로 바꿀 때 아직 남아 있는 이름을 쓰면 안 된다.
        var live = edited.Partitions.Where(e => !e.IsDropped && e.MergedInto is null && (originals.ContainsKey(e.RowKey) || rows.Any(r => r.RowKey == e.RowKey))).ToList();
        foreach (var group in live.GroupBy(e => e.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            foreach (var e in group)
            {
                Problem(e.RowKey, DuplicateNameKey);
            }
        }

        // 차례: 지우기 → 이름 바꾸기 → 합치기 → 나누기 → 추가. 이름 바꾸기·새 줄이 쓰는 이름이 그때 아직 다른 원본 줄의 이름이면 겹친다.
        var plainRenamed = rows.Where(r => r.Changes.HasFlag(PartitionRowChange.Name) && (r.Changes & (PartitionRowChange.MergeTarget | PartitionRowChange.SplitSource)) == 0)
            .Select(r => r.RowKey).ToHashSet(StringComparer.Ordinal);
        var dropped = rows.Where(r => r.State == DraftRowState.Dropped).Select(r => r.RowKey).ToHashSet(StringComparer.Ordinal);
        var mergedAway = rows.Where(r => r.Changes.HasFlag(PartitionRowChange.MergedAway)).Select(r => r.RowKey).ToHashSet(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => plainRenamed.Contains(r.RowKey)))
        {
            if (original.Partitions.Any(o => o.RowKey != r.RowKey && !dropped.Contains(o.RowKey) && string.Equals(o.Name, r.Edited!.Name, StringComparison.Ordinal)))
            {
                Problem(r.RowKey, NameInUseKey);
            }
        }

        foreach (var r in rows.Where(r => r.State == DraftRowState.Added))
        {
            // 새 줄을 만들 때까지 이름이 비지 않는 원본 줄: 지우지 않았고, 이름 바꾸기·합쳐져 사라지기로 먼저 이름을 내놓지 않는 줄.
            if (original.Partitions.Any(o => !dropped.Contains(o.RowKey) && !plainRenamed.Contains(o.RowKey) && !mergedAway.Contains(o.RowKey)
                && string.Equals(o.Name, r.Edited!.Name, StringComparison.Ordinal)))
            {
                Problem(r.RowKey, NameInUseKey);
            }
        }

        // 문제가 생긴 줄은 빼고, 그 줄에 기대는 나누기·합치기도 뺀다(말없이 사라지지 않게 문제를 단다).
        rows.RemoveAll(r => rowProblems.ContainsKey(r.RowKey));
        var changed = true;
        while (changed)
        {
            changed = false;
            var present = rows.ToDictionary(r => r.RowKey, StringComparer.Ordinal);
            foreach (var r in rows.ToList())
            {
                string? reason = null;
                if (r.Changes.HasFlag(PartitionRowChange.SplitOff))
                {
                    var source = splitSources.First(kv => kv.Value.Contains(r.RowKey)).Key;
                    reason = present.TryGetValue(source, out var s) && s.Changes.HasFlag(PartitionRowChange.SplitSource) ? null : SplitSourceUnavailableKey;
                }
                else if (r.Changes.HasFlag(PartitionRowChange.MergedAway))
                {
                    reason = present.TryGetValue(r.Edited!.MergedInto!, out var t) && t.Changes.HasFlag(PartitionRowChange.MergeTarget) ? null : BadMergeKey;
                }

                if (reason is not null)
                {
                    rows.Remove(r);
                    Problem(r.RowKey, reason);
                    changed = true;
                    continue;
                }

                // 합쳐 받을 줄·떼어 줄 줄이 다 빠진 대상·원본: 그 표시를 지우고, 그 때문에 허용한 경계 변경이 남으면 문제로.
                var flags = r.Changes;
                if (flags.HasFlag(PartitionRowChange.MergeTarget) && !rows.Any(x => x.Changes.HasFlag(PartitionRowChange.MergedAway) && x.Edited!.MergedInto == r.RowKey))
                {
                    flags &= ~PartitionRowChange.MergeTarget;
                }

                if (flags.HasFlag(PartitionRowChange.SplitSource) && !rows.Any(x => x.Changes.HasFlag(PartitionRowChange.SplitOff) && splitSources[r.RowKey].Contains(x.RowKey)))
                {
                    flags &= ~PartitionRowChange.SplitSource;
                }

                if (flags == r.Changes)
                {
                    continue;
                }

                rows.Remove(r);
                changed = true;
                if (!string.Equals(Canonical(r.Original!.Bound), Canonical(r.Edited!.Bound), StringComparison.Ordinal)
                    && !flags.HasFlag(PartitionRowChange.MergeTarget) && !(method == PartitionMethod.List && flags.HasFlag(PartitionRowChange.SplitSource)))
                {
                    Problem(r.RowKey, BoundChangeKey);
                }
                else if (flags != PartitionRowChange.None)
                {
                    rows.Add(r with { Changes = flags });
                }
            }
        }

        return new PartitionChanges { Rows = rows, IntervalChanged = intervalChanged, Problems = problems };

        string? Unsupported(PartitionOperation op) => dialect.UnsupportedPartitionReasonKey(op, method, server);
    }

    /// <summary>합칠 대상이 남는 원본 줄이고, 범위면 사이 줄이 모두 같은 대상으로 합쳐진다(붙어 있는 파티션끼리만 MERGE).</summary>
    private static bool MergeIsValid(PartitionDraft e, Dictionary<string, PartitionDraft> edits, Dictionary<string, PartitionDraft> originals,
        Dictionary<string, int> order, PartitionTableDraft edited, PartitionMethod method)
    {
        if (!edits.TryGetValue(e.MergedInto!, out var target) || !originals.ContainsKey(target.RowKey) || target.IsDropped || target.MergedInto is not null)
        {
            return false;
        }

        if (method != PartitionMethod.Range)
        {
            return true;
        }

        var (from, to) = (Math.Min(order[e.RowKey], order[target.RowKey]), Math.Max(order[e.RowKey], order[target.RowKey]));
        return edited.Partitions.Skip(from + 1).Take(to - from - 1).All(p => originals.ContainsKey(p.RowKey) && p.MergedInto == target.RowKey);
    }

    /// <summary>
    /// 나누기: 원본 줄 키 → 그 줄에서 떼어 낸 새 줄 키(편집 목록 순서). 새 줄의 <see cref="PartitionDraft.SplitFrom"/> 을 먼저 따르고,
    /// 범위에서 비어 있으면 편집 목록에서 그 줄 뒤에 오는 첫 원본 줄(지우거나 합쳐지는 줄은 건너뜀)을 쓴다. 마지막 원본 줄 뒤의 범위 새 줄은 ADD 다.
    /// </summary>
    internal static Dictionary<string, List<string>> SplitSources(PartitionTableDraft original, PartitionTableDraft edited)
    {
        var originals = original.Partitions.Select(p => p.RowKey).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var list = edited.Partitions;
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (originals.Contains(p.RowKey) || p.IsDropped || p.MergedInto is not null)
            {
                continue;
            }

            var source = p.SplitFrom is { } from && originals.Contains(from) ? from
                : original.Scheme?.Method == PartitionMethod.Range
                    ? list.Skip(i + 1).FirstOrDefault(n => originals.Contains(n.RowKey) && !n.IsDropped && n.MergedInto is null)?.RowKey
                    : null;
            if (source is null)
            {
                continue;
            }

            if (!result.TryGetValue(source, out var keys))
            {
                result[source] = keys = [];
            }

            keys.Add(p.RowKey);
        }

        return result;
    }

    /// <summary>마지막 원본 범위 줄이 MAXVALUE 다(그 뒤에는 ADD 를 못 하고 나누기만 된다).</summary>
    private static bool EndsWithMaxValue(PartitionTableDraft original) =>
        original.Partitions.Count > 0 && Canonical(original.Partitions[^1].Bound)?.Contains("MAXVALUE", StringComparison.Ordinal) == true;

    /// <summary>경계·식 비교 표기: 따옴표(<c>'…'</c>·<c>"…"</c>) 밖은 대문자로, 공백 덩어리는 한 칸으로, 기호 옆 공백은 지움. 빈 값 = null.</summary>
    internal static string? Canonical(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var sb = new StringBuilder(text.Length);
        char? quote = null;
        var pendingSpace = false;
        foreach (var c in text.Trim())
        {
            if (quote is { } q)
            {
                sb.Append(c);
                if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0 && IsWord(sb[^1]) && IsWord(c))
            {
                sb.Append(' ');
            }

            pendingSpace = false;
            if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();

        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '\'' or '"';
    }
}
