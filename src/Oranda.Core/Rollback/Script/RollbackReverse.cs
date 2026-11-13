namespace Oranda.Core.Rollback.Script;

/// <summary>
/// 되돌리기의 되돌리기(되돌리기 지점 설계 11.3, RD-8). 원복 실행·비교 반영이 1행을 바꾼 원복 문장마다 그 반대 문장을 만든다:
/// 원복 문장이 쓴 이전 값과 실행 직후 값을 맞바꾼다. 원복 문장 자체가 키와 값 조건으로 한 행만 바꾸므로 잠금 읽기 없이도 정확하다.
/// <list type="bullet">
/// <item><c>UPDATE t SET c = 이전 WHERE 키 AND c = 직후</c> → <c>UPDATE t SET c = 직후 WHERE 키 AND c = 이전</c></item>
/// <item><c>INSERT INTO t (열) VALUES (값)</c> → <c>DELETE FROM t WHERE 열 = 값 … AND ROWNUM = 1</c></item>
/// <item><c>DELETE FROM t WHERE 열 = 값 …</c> → <c>INSERT INTO t (열) VALUES (값)</c>(조건에 없던 큰 열은 빠진다, 문장 주석으로 알림)</item>
/// </list>
/// 엔진(<c>Capture/RollbackCaptureEngine</c>)이 만든 모양만 읽는다. 모양이 다르면(사용자가 고친 문장) null.
/// </summary>
public static class RollbackReverse
{
    /// <summary>이 주석이 붙은 원복 문장은 반대 문장을 만들지 않는다(<see cref="Capture.RollbackSql.NoReverseNote"/>).</summary>
    public const string NoReverseNote = Capture.RollbackSql.NoReverseNote;

    /// <summary>반대 문장. 만들 수 없으면 null.</summary>
    public static RollbackStep? Reverse(RollbackStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Note == NoReverseNote)
        {
            return null;
        }

        var sql = step.Sql.Trim().TrimEnd(';').Trim();
        if (sql.StartsWith("UPDATE ", StringComparison.Ordinal))
        {
            return ReverseUpdate(sql);
        }

        if (sql.StartsWith("INSERT INTO ", StringComparison.Ordinal))
        {
            return ReverseInsert(sql);
        }

        return sql.StartsWith("DELETE FROM ", StringComparison.Ordinal) ? ReverseDelete(sql) : null;
    }

    /// <summary>
    /// 실행한 원복 문장들(실행 차례)의 반대 문장 묶음. 반대 묶음은 실행 차례의 역순이다. 만들 수 없는 문장은 <paramref name="skipped"/> 로 센다.
    /// </summary>
    public static IReadOnlyList<RollbackStep> ReverseAll(IEnumerable<RollbackStep> executed, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(executed);
        var result = new List<RollbackStep>();
        skipped = 0;
        foreach (var step in executed.Reverse())
        {
            if (Reverse(step) is { } reverse)
            {
                result.Add(reverse);
            }
            else
            {
                skipped++;
            }
        }

        return result;
    }

    private static RollbackStep? ReverseUpdate(string sql)
    {
        var set = RollbackSqlText.IndexOfTop(sql, " SET ");
        var where = RollbackSqlText.IndexOfTop(sql, " WHERE ");
        if (set < 0 || where < set)
        {
            return null;
        }

        var table = sql[7..set];
        var sets = RollbackSqlText.SplitTop(sql[(set + 5)..where], ", ").Select(RollbackSqlText.ParsePredicate).ToList();
        var preds = RollbackSqlText.SplitTop(sql[(where + 7)..], " AND ");
        // 키 조건이 없으면(사용자가 고친 문장) 반대 문장이 여러 행을 바꿀 수 있다(RD-8 의 한 행 전제가 깨짐).
        if (sets.Any(s => s is null) || preds.Count <= sets.Count)
        {
            return null;
        }

        // 조건의 끝 sets.Count 개가 SET 열의 「직후 값」 조건이다(엔진이 키 조건 뒤에 붙인다).
        var key = preds.Take(preds.Count - sets.Count).ToList();
        var after = preds.Skip(preds.Count - sets.Count).Select(RollbackSqlText.ParsePredicate).ToList();
        var newSets = new List<string>();
        var newPreds = new List<string>(key);
        for (var i = 0; i < sets.Count; i++)
        {
            var (column, before) = sets[i]!.Value;
            if (after[i] is not { } a || a.Column != column)
            {
                return null;
            }

            newSets.Add(column + " = " + a.Literal);
            newPreds.Add(Literals.OracleFamilyLiterals.Predicate(column, before));
        }

        return new RollbackStep($"UPDATE {table} SET {string.Join(", ", newSets)} WHERE {string.Join(" AND ", newPreds)}");
    }

    private static RollbackStep? ReverseInsert(string sql)
    {
        var open = RollbackSqlText.IndexOfTop(sql, " (");
        var values = RollbackSqlText.IndexOfTop(sql, " VALUES (");
        if (open < 0 || values < open || !sql.EndsWith(')'))
        {
            return null;
        }

        var table = sql[12..open];
        var columns = RollbackSqlText.SplitTop(sql[(open + 2)..(values - 1)], ", ");
        var literals = RollbackSqlText.SplitTop(sql[(values + 9)..^1], ", ");
        if (columns.Count != literals.Count || columns.Count == 0)
        {
            return null;
        }

        var preds = columns.Select((c, i) => Literals.OracleFamilyLiterals.Predicate(c.Trim(), literals[i].Trim())).Append("ROWNUM = 1");
        return new RollbackStep($"DELETE FROM {table} WHERE {string.Join(" AND ", preds)}");
    }

    private static RollbackStep? ReverseDelete(string sql)
    {
        var where = RollbackSqlText.IndexOfTop(sql, " WHERE ");
        if (where < 0)
        {
            return null;
        }

        var table = sql[12..where];
        var columns = new List<string>();
        var literals = new List<string>();
        foreach (var p in RollbackSqlText.SplitTop(sql[(where + 7)..], " AND "))
        {
            if (p.Trim() == "ROWNUM = 1")
            {
                continue;
            }

            if (RollbackSqlText.ParsePredicate(p) is not { } parsed || parsed.Column.Contains(' ', StringComparison.Ordinal) && !parsed.Column.StartsWith('"'))
            {
                return null; // ROWID 조건 등 열이 아닌 조건
            }

            columns.Add(parsed.Column);
            literals.Add(parsed.Literal);
        }

        if (columns.Count == 0 || columns.Contains("ROWID", StringComparer.Ordinal))
        {
            return null;
        }

        return new RollbackStep($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", literals)})")
        {
            Note = "조건에 없던 열(큰 열 등)은 넣지 않습니다",
        };
    }
}
