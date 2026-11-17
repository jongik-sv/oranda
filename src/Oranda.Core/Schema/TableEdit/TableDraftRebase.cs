namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// 중간 실패 뒤 남은 편집 다시 계산(TE-5). 실행이 앞 문장만 반영하고 멈추면 화면은 테이블을 다시 읽어 새 원본(<see cref="TableDraftFactory.FromTable"/>, 줄 키 새로 붙음)을 만든다.
/// 편집 모델의 줄 키를 새 원본 줄 키로 옮기면 <see cref="TableEditScriptBuilder.Build"/>(새 원본, 옮긴 편집)가 아직 반영되지 않은 문장만 낸다.
/// 맞추는 방법: 실행한 문장 가운데 성공한 앞 <c>applied</c> 개의 효과(<see cref="TableEditStatement.Effect"/>: 지움·이름 바꿈·만듦)를 옛 원본에 차례로 적용해
/// 줄마다 지금 서버 이름을 구하고, 그 이름(테이블 안에서 유일)으로 새 원본 줄을 찾는다. 이름을 추측하지 않으므로 이름 바꾸기·지우기가 엇갈려도 다른 줄과 섞이지 않는다.
/// </summary>
public static class TableDraftRebase
{
    /// <param name="oldOriginal">실행 전 원본</param>
    /// <param name="edited">실행한 편집 모델(옛 원본 줄 키)</param>
    /// <param name="script">실행한 문장(<see cref="TableEditScriptBuilder.Build"/>(oldOriginal, edited) 결과)</param>
    /// <param name="applied">앞에서부터 성공한 문장 수</param>
    /// <param name="newOriginal">실행 뒤 다시 읽은 원본</param>
    /// <returns>새 원본 줄 키로 옮긴 편집 모델. 지우기가 반영된 줄은 빠진다.</returns>
    public static TableDraft Rebase(TableDraft oldOriginal, TableDraft edited, TableEditScript script, int applied, TableDraft newOriginal)
    {
        ArgumentNullException.ThrowIfNull(oldOriginal);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(newOriginal);
        ArgumentOutOfRangeException.ThrowIfNegative(applied);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(applied, script.Statements.Count);

        // 줄마다 지금 서버 이름(없으면 서버에 없음).
        var columns = oldOriginal.Columns.ToDictionary(c => c.RowKey, c => c.Name, StringComparer.Ordinal);
        var constraints = oldOriginal.Constraints.ToDictionary(c => c.RowKey, c => c.Name, StringComparer.Ordinal);
        var indexes = oldOriginal.Indexes.ToDictionary(i => i.RowKey, i => i.Name, StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);

        // 제약·인덱스 이름 바꾸기가 반영된 줄: 편집 줄 이름도 그 이름으로(규칙 이름 따라 바꾸기는 편집 줄 이름을 바꾸지 않으므로, 그대로 두면
        // 다음 문장 만들기가 사용자가 옛 이름으로 되돌린 것으로 본다). 열은 임시 이름($1)을 거치므로 하지 않는다.
        var constraintNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var indexNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in script.Statements.Take(applied))
        {
            if (s.Effect.Kind == TableEditEffectKind.CreateTable)
            {
                foreach (var c in edited.Columns.Where(c => !c.IsDropped))
                {
                    columns[c.RowKey] = c.Name;
                }

                foreach (var c in edited.Constraints.Where(c => !c.IsDropped && c.Kind != Metadata.ConstraintKind.ForeignKey))
                {
                    constraints[c.RowKey] = c.Name;
                }

                continue;
            }

            var names = s.Page switch
            {
                TableEditPage.Columns => columns,
                TableEditPage.Constraints => constraints,
                TableEditPage.Indexes => indexes,
                _ => null,
            };
            if (names is null)
            {
                continue;
            }

            var editedNames = s.Page switch
            {
                TableEditPage.Constraints => constraintNames,
                TableEditPage.Indexes => indexNames,
                _ => null,
            };
            switch (s.Effect.Kind)
            {
                case TableEditEffectKind.Drop:
                    names.Remove(s.RowKey);
                    removed.Add(s.RowKey);
                    editedNames?.Remove(s.RowKey);
                    break;
                case TableEditEffectKind.Rename or TableEditEffectKind.Create:
                    names[s.RowKey] = s.Effect.Name!;
                    removed.Remove(s.RowKey);
                    if (editedNames is not null)
                    {
                        editedNames[s.RowKey] = s.Effect.Name!;
                    }

                    break;
            }
        }

        // MOVE 가 반영됐으면 아직 안 한 REBUILD 를 넘긴다(새 원본과 차이에는 MOVE 가 없어 다시 나오지 않으므로).
        var moved = script.Statements.Take(applied).Any(s => s.Effect.Kind == TableEditEffectKind.Move);
        var rebuilt = script.Statements.Take(applied).Where(s => s.Effect.Kind == TableEditEffectKind.Rebuild).Select(s => s.Effect.Name!).ToHashSet(StringComparer.Ordinal);
        var rebuilds = moved
            ? script.Statements.Skip(applied).Where(s => s.Effect.Kind == TableEditEffectKind.Rebuild).Select(s => s.Effect.Name!)
            : [];
        edited = edited with
        {
            RebuildIndexes = [.. edited.RebuildIndexes.Where(n => !rebuilt.Contains(n)).Concat(rebuilds).Distinct(StringComparer.Ordinal)],
            Constraints = [.. edited.Constraints.Select(c => constraintNames.TryGetValue(c.RowKey, out var n) ? c with { Name = n } : c)],
            Indexes = [.. edited.Indexes.Select(i => indexNames.TryGetValue(i.RowKey, out var n) ? i with { Name = n } : i)],
        };

        var columnMap = Map(columns, newOriginal.Columns, c => c.Name, c => c.RowKey);
        var constraintMap = Map(constraints, newOriginal.Constraints, c => c.Name, c => c.RowKey);
        var indexMap = Map(indexes, newOriginal.Indexes, i => i.Name, i => i.RowKey);

        // 지울 열을 쓰는 제약·인덱스는 차이 계산이 같이 지우므로(TableDraftDiff) 지울 줄로 본다.
        var droppedColumns = oldOriginal.Columns.Select(c => c.RowKey)
            .Where(k => edited.Column(k) is null or { IsDropped: true })
            .ToHashSet(StringComparer.Ordinal);
        string ColumnKey(string key) => columnMap.TryGetValue(key, out var k) ? k
            : removed.Contains(key) ? TableDraftFactory.UnknownColumnKeyPrefix + (oldOriginal.Column(key)?.Name ?? key)
            : key;

        var rebasedColumns = Rows(edited.Columns, c => c.RowKey, c => c.IsDropped, oldOriginal.Column, columnMap, removed,
            (c, k) => c with { RowKey = k, OriginalPosition = newOriginal.Column(k)?.OriginalPosition ?? c.OriginalPosition });
        var rebasedConstraints = Rows(
                edited.Constraints, c => c.RowKey, c => c.IsDropped || c.ColumnKeys.Any(droppedColumns.Contains), oldOriginal.Constraint, constraintMap, removed,
                (c, k) => c with { RowKey = k })
            .Select(c => c with { ColumnKeys = [.. c.ColumnKeys.Select(ColumnKey)] })
            .ToList();
        var rebasedIndexes = Rows(
                edited.Indexes, i => i.RowKey, i => i.IsDropped || i.Columns.Any(c => c.ColumnKey is { } k && droppedColumns.Contains(k)), oldOriginal.Index,
                indexMap, removed, (i, k) => i with { RowKey = k, ConstraintName = newOriginal.Index(k)?.ConstraintName ?? i.ConstraintName })
            .Select(i => i with { Columns = [.. i.Columns.Select(c => c.ColumnKey is null ? c : c with { ColumnKey = ColumnKey(c.ColumnKey) })] })
            .ToList();

        return edited with
        {
            Columns = rebasedColumns,
            Constraints = rebasedConstraints,
            Indexes = rebasedIndexes,
            IsNewTable = newOriginal.IsNewTable,

            // CREATE 가 반영되면 익스텐트(새 테이블에서만 쓰는 값, TE-39)는 할 일이 끝났다 — 남기면 기존 테이블의 ⚠ existingStorage·변경 수 1 만 남는다.
            Storage = oldOriginal.IsNewTable && !newOriginal.IsNewTable ? edited.Storage with { InitialExtent = null, NextExtent = null } : edited.Storage,
        };
    }

    /// <summary>줄 키 → 새 원본 줄 키(지금 서버 이름으로 찾음).</summary>
    private static Dictionary<string, string> Map<T>(Dictionary<string, string> current, IReadOnlyList<T> newRows, Func<T, string> name, Func<T, string> key)
    {
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in newRows)
        {
            byName.TryAdd(name(row), key(row));
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rowKey, serverName) in current)
        {
            if (byName.TryGetValue(serverName, out var newKey))
            {
                map[rowKey] = newKey;
            }
        }

        return map;
    }

    /// <summary>
    /// 편집 줄 옮기기: 서버에 있는 줄 = 새 키. 지우기가 반영된 줄 = 지울 줄이면 뺌, 아니면(지우고 다시 만들기의 지우기만 반영) 새 키를 받아 다시 만들 줄로.
    /// 아직 만들지 않은 새 줄 = 그대로. 옛 원본 줄인데 서버에서 못 찾은 줄도 새 키(옛 키가 새 원본의 다른 줄 키와 겹칠 수 있어서).
    /// </summary>
    private static List<T> Rows<T>(
        IReadOnlyList<T> rows, Func<T, string> key, Func<T, bool> dropped, Func<string, T?> inOld, Dictionary<string, string> map, HashSet<string> removed,
        Func<T, string, T> rekey)
        where T : class
    {
        var result = new List<T>();
        foreach (var row in rows)
        {
            var k = key(row);
            if (map.TryGetValue(k, out var newKey))
            {
                result.Add(rekey(row, newKey));
            }
            else if (removed.Contains(k) || inOld(k) is not null)
            {
                if (!dropped(row))
                {
                    var fresh = DraftKeys.New();
                    map[k] = fresh; // 이 줄을 가리키는 제약·인덱스 열 키도 따라간다
                    result.Add(rekey(row, fresh));
                }
            }
            else
            {
                result.Add(row);
            }
        }

        return result;
    }
}
