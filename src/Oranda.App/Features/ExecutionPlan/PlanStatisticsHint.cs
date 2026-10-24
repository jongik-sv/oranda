using System;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 실제 계획에 A-Rows 가 없을 때 쓰는 사본 문장 만들기(TSK-06-04, TN-02). 편집기 글은 바꾸지 않는다.
/// <para>힌트는 첫 SELECT·INSERT·UPDATE·DELETE·MERGE 바로 뒤에 넣는다. WITH 로 시작하면 첫 SELECT(하위 질의 블록) 뒤인데,
/// GATHER_PLAN_STATISTICS 는 문장 전체에 걸리는 힌트라 어느 질의 블록에 있어도 된다. 바로 뒤에 힌트 주석(<c>/*+</c>)이 있으면 그 안에 더한다.</para>
/// </summary>
public static class PlanStatisticsHint
{
    public const string Hint = "GATHER_PLAN_STATISTICS";

    private static readonly string[] Verbs = ["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE"];

    /// <summary>힌트를 넣은 글. 이미 들어 있으면 그대로, 넣을 자리(동사)가 없으면 null.</summary>
    public static string? AddTo(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (FindVerbEnd(sql) is not { } at)
        {
            return null;
        }

        var rest = at;
        while (rest < sql.Length && char.IsWhiteSpace(sql[rest]))
        {
            rest++;
        }

        if (string.CompareOrdinal(sql, rest, "/*+", 0, 3) == 0)
        {
            var close = sql.IndexOf("*/", rest + 3, StringComparison.Ordinal);
            if (close > 0 && sql.AsSpan(rest, close - rest).Contains(Hint, StringComparison.OrdinalIgnoreCase))
            {
                return sql;
            }

            return sql.Insert(rest + 3, " " + Hint);
        }

        return sql.Insert(at, " /*+ " + Hint + " */");
    }

    /// <summary>
    /// 첫 DML·질의 동사의 끝 위치. 주석·문자열·따옴표 이름 안은 건너뛴다. 없으면 null.
    /// </summary>
    internal static int? FindVerbEnd(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i);
                i = nl < 0 ? sql.Length : nl + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = sql.IndexOf(c, i + 1);
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }

            if (char.IsLetter(c) && (i == 0 || !IsWordChar(sql[i - 1])))
            {
                var j = i;
                while (j < sql.Length && IsWordChar(sql[j]))
                {
                    j++;
                }

                var word = sql.AsSpan(i, j - i);
                foreach (var verb in Verbs)
                {
                    if (word.Equals(verb, StringComparison.OrdinalIgnoreCase))
                    {
                        return j;
                    }
                }

                i = j;
                continue;
            }

            i++;
        }

        return null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
