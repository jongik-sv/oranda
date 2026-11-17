using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit.Partitions;

/// <summary>
/// 파티션 차이 → 문장(설계 18.4, TE-40). 이름은 늘 소유자를 붙이고 따옴표는 대문자 규칙을 벗어나거나 예약어일 때만. 문장은 모두 DDL, 페이지 = 파티션.
/// <para>순서: 일반 → 파티션(한 문장) / INTERVAL 끄기 → 지우기(DROP·COALESCE) → 비우기 → 이름 바꾸기 → 합치기 → 나누기 → (방언이 요구하면) 전역 인덱스 다시 만들기 → 추가 → INTERVAL 켜기·바꾸기.</para>
/// <list type="bullet">
/// <item>DROP·TRUNCATE·SPLIT·MERGE·해시 ADD·COALESCE 에는 늘 <c>UPDATE INDEXES</c>(인덱스가 UNUSABLE 이 안 되게, TE-40). Tibero 는 받지만 나누기·합치기 뒤 전역 인덱스가
/// UNUSABLE 로 남아(실측 19) <see cref="ITableEditDialect.KeepsGlobalIndexesOnSplitMerge"/> 가 거짓이면 끝에 <c>ALTER INDEX … REBUILD</c> 를 붙인다.</item>
/// <item>나누기·합치기에 같이 한 이름 바꾸기는 <c>INTO</c> 이름으로 흡수한다(따로 RENAME 하지 않음). 합치기는 대상과 가까운 줄부터 두 개씩(붙은 파티션끼리, 구버전·Tibero 호환).</item>
/// <item><see cref="PartitionChanges.Problems"/> 에 걸린 편집은 문장이 없다.</item>
/// </list>
/// </summary>
public static class PartitionScriptBuilder
{
    /// <summary>기존 테이블의 파티션 편집 문장. <paramref name="globalIndexes"/> = 이 테이블의 전역(비파티션) 인덱스 이름(소유자 = 테이블 소유자).</summary>
    public static IReadOnlyList<TableEditStatement> Build(
        string owner, string table, PartitionTableDraft original, PartitionTableDraft edited, ITableEditDialect dialect,
        ServerInfo? server = null, IReadOnlyList<string>? globalIndexes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(dialect);

        var changes = PartitionDiff.Compute(original, edited, dialect, server);
        var result = new List<TableEditStatement>();
        var target = Q(owner) + "." + Q(table);
        string Q(string name) => SqlIdentifier.Quote(name, dialect.IsReservedWord);
        void Add(string rowKey, string text, TableEditMarks marks = TableEditMarks.None) =>
            result.Add(new TableEditStatement(new SqlStatement(text, default, StatementKind.Ddl) { Verb = "ALTER" }, rowKey, TableEditPage.Partitions, marks));

        if (changes.Converting)
        {
            if (changes.Problems.Count == 0)
            {
                Add(TableDraft.TableRowKey, $"ALTER TABLE {target} MODIFY {Clause(edited.Scheme!, edited.Partitions, Q)} ONLINE");
            }

            return result;
        }

        if (changes.IsEmpty)
        {
            return result;
        }

        var method = original.Scheme!.Method;
        var rows = changes.Rows.ToDictionary(r => r.RowKey, StringComparer.Ordinal);
        var editedOrder = edited.Partitions.Select((p, i) => (p.RowKey, i)).ToDictionary(x => x.RowKey, x => x.i, StringComparer.Ordinal);

        // 원본 줄의 지금 서버 이름(이름 바꾸기·합치기·나누기를 지나며 바뀐다).
        var names = original.Partitions.ToDictionary(o => o.RowKey, o => o.Name, StringComparer.Ordinal);
        var intervalOff = changes.IntervalChanged && string.IsNullOrWhiteSpace(edited.Scheme!.Interval);

        // 0. INTERVAL 끄기는 맨 앞(끈 뒤에야 범위 구역 마지막 파티션 DROP·ADD 가 된다 — Oracle ORA-14758·14760).
        if (intervalOff)
        {
            Add(TableDraft.TableRowKey, $"ALTER TABLE {target} SET INTERVAL ()");
        }

        // 1. 지우기(원본 순서). 해시는 이름을 고를 수 없어 줄 하나마다 COALESCE 하나(차이 계산이 끝에서부터만 허용).
        foreach (var o in original.Partitions.Where(o => rows.TryGetValue(o.RowKey, out var d) && d.State == DraftRowState.Dropped))
        {
            Add(o.RowKey, method == PartitionMethod.Hash
                ? $"ALTER TABLE {target} COALESCE PARTITION UPDATE INDEXES"
                : $"ALTER TABLE {target} DROP PARTITION {Q(o.Name)} UPDATE INDEXES", TableEditMarks.Irreversible);
        }

        // 2. 비우기(원래 이름).
        foreach (var d in Modified(PartitionRowChange.Truncated))
        {
            Add(d.RowKey, $"ALTER TABLE {target} TRUNCATE PARTITION {Q(names[d.RowKey])} UPDATE INDEXES", TableEditMarks.Irreversible);
        }

        // 3. 이름 바꾸기(합치기 대상·나누기 원본은 INTO 이름으로 흡수).
        foreach (var d in Modified(PartitionRowChange.Name).Where(d => (d.Changes & (PartitionRowChange.MergeTarget | PartitionRowChange.SplitSource)) == 0))
        {
            Add(d.RowKey, $"ALTER TABLE {target} RENAME PARTITION {Q(names[d.RowKey])} TO {Q(d.Edited!.Name)}");
            names[d.RowKey] = d.Edited!.Name;
        }

        var splitOrMerge = false;
        var splitSources = PartitionDiff.SplitSources(original, edited);

        // 4. 합치기: 대상 줄마다 가까운 원본부터 두 개씩(붙어 있는 파티션끼리). 이 줄이 나누기 원본도 되면 이름은 나누기 끝에 바꾼다.
        foreach (var t in Modified(PartitionRowChange.MergeTarget))
        {
            var sources = changes.Rows
                .Where(r => r.Changes.HasFlag(PartitionRowChange.MergedAway) && r.Edited!.MergedInto == t.RowKey)
                .OrderBy(r => Math.Abs(editedOrder[r.RowKey] - editedOrder[t.RowKey]))
                .ToList();
            var finalName = t.Changes.HasFlag(PartitionRowChange.SplitSource) ? names[t.RowKey] : t.Edited!.Name;
            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                var current = names[t.RowKey];
                var into = i == sources.Count - 1 ? finalName : current;
                var (first, second) = editedOrder[source.RowKey] < editedOrder[t.RowKey] ? (names[source.RowKey], current) : (current, names[source.RowKey]);
                Add(source.RowKey, $"ALTER TABLE {target} MERGE PARTITIONS {Q(first)}, {Q(second)} INTO PARTITION {Q(into)} UPDATE INDEXES");
                names[t.RowKey] = into;
                splitOrMerge = true;
            }
        }

        // 5. 나누기: 원본 줄마다 떼어 낸 새 줄을 편집 순서대로. 남는 쪽은 마지막에만 편집 이름.
        foreach (var s in Modified(PartitionRowChange.SplitSource))
        {
            var parts = splitSources[s.RowKey].Where(rows.ContainsKey).Select(k => rows[k].Edited!).ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var current = names[s.RowKey];
                var rest = i == parts.Count - 1 ? s.Edited!.Name : current;
                var at = method == PartitionMethod.List ? $"VALUES ({part.Bound})" : $"AT ({part.Bound})";
                Add(part.RowKey, $"ALTER TABLE {target} SPLIT PARTITION {Q(current)} {at} INTO (PARTITION {Q(part.Name)}{Space(part.Tablespace, Q)}, PARTITION {Q(rest)}) UPDATE INDEXES");
                names[s.RowKey] = rest;
                splitOrMerge = true;
            }
        }

        // 6. 나누기·합치기 바로 뒤 전역 인덱스(방언이 남기지 못하면, Tibero 실측 19). 비파티션 전역 인덱스만 받는다.
        if (splitOrMerge && !dialect.KeepsGlobalIndexesOnSplitMerge)
        {
            foreach (var index in globalIndexes ?? [])
            {
                Add(TableDraft.TableRowKey, $"ALTER INDEX {Q(owner)}.{Q(index)} REBUILD");
            }
        }

        // 7. 추가(편집 순서).
        foreach (var p in edited.Partitions.Where(p => rows.TryGetValue(p.RowKey, out var d) && d.State == DraftRowState.Added && !d.Changes.HasFlag(PartitionRowChange.SplitOff)))
        {
            Add(p.RowKey, method switch
            {
                PartitionMethod.Range => $"ALTER TABLE {target} ADD PARTITION {Q(p.Name)} VALUES LESS THAN ({p.Bound}){Space(p.Tablespace, Q)}",
                PartitionMethod.List => $"ALTER TABLE {target} ADD PARTITION {Q(p.Name)} VALUES ({p.Bound}){Space(p.Tablespace, Q)}",
                _ => $"ALTER TABLE {target} ADD PARTITION {Q(p.Name)}{Space(p.Tablespace, Q)} UPDATE INDEXES",
            });
        }

        // 8. INTERVAL 켜기·바꾸기는 맨 뒤(ADD 를 먼저 끝낸 뒤).
        if (changes.IntervalChanged && !intervalOff)
        {
            Add(TableDraft.TableRowKey, $"ALTER TABLE {target} SET INTERVAL ({edited.Scheme!.Interval!.Trim()})");
        }

        return result;

        IEnumerable<PartitionRowDiff> Modified(PartitionRowChange flag) =>
            original.Partitions.Select(o => rows.GetValueOrDefault(o.RowKey)).OfType<PartitionRowDiff>()
                .Where(d => d.State == DraftRowState.Modified && d.Changes.HasFlag(flag))
                .OrderBy(d => editedOrder.GetValueOrDefault(d.RowKey, int.MaxValue));
    }

    /// <summary>새 테이블 <c>CREATE TABLE</c> 안의 파티션 절(<c>PARTITION BY … [INTERVAL (…)] (PARTITION …, …)</c>). 하위 파티션은 만들지 않는다(TE-42).</summary>
    public static string CreateClause(PartitionScheme scheme, IReadOnlyList<PartitionDraft> partitions, ITableEditDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(dialect);
        return Clause(scheme, partitions, name => SqlIdentifier.Quote(name, dialect.IsReservedWord));
    }

    /// <summary>
    /// 일반 → 파티션 바꾸기의 <c>PARTITION BY …</c> 절(테이블 다시 만들기 스크립트가 새 표에 붙인다, TE-41). 바꾸기가 아니면 null.
    /// </summary>
    public static string? ConversionClause(PartitionTableDraft original, PartitionTableDraft edited, ITableEditDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(dialect);
        if (original.Scheme is not null || edited.Scheme is not { KeyColumns.Count: > 0 } scheme)
        {
            return null;
        }

        return Clause(scheme, edited.Partitions, name => SqlIdentifier.Quote(name, dialect.IsReservedWord));
    }

    private static string Clause(PartitionScheme scheme, IReadOnlyList<PartitionDraft> partitions, Func<string, string> q)
    {
        var method = scheme.Method switch
        {
            PartitionMethod.Range => "RANGE",
            PartitionMethod.List => "LIST",
            _ => "HASH",
        };
        var text = $"PARTITION BY {method} ({string.Join(", ", scheme.KeyColumns.Select(q))})";
        if (scheme.Method == PartitionMethod.Range && !string.IsNullOrWhiteSpace(scheme.Interval))
        {
            text += $" INTERVAL ({scheme.Interval.Trim()})";
        }

        var live = partitions.Where(p => !p.IsDropped && p.MergedInto is null).ToList();
        if (live.Count == 0)
        {
            return text;
        }

        var items = live.Select(p => scheme.Method switch
        {
            PartitionMethod.Range => $"PARTITION {q(p.Name)} VALUES LESS THAN ({p.Bound}){Space(p.Tablespace, q)}",
            PartitionMethod.List => $"PARTITION {q(p.Name)} VALUES ({p.Bound}){Space(p.Tablespace, q)}",
            _ => $"PARTITION {q(p.Name)}{Space(p.Tablespace, q)}",
        });
        return text + " (" + string.Join(", ", items) + ")";
    }

    private static string Space(string? tablespace, Func<string, string> q) =>
        string.IsNullOrWhiteSpace(tablespace) ? string.Empty : " TABLESPACE " + q(tablespace.Trim());
}
