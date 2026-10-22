using System.Text;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 사용자의 원래 SELECT 문장을 인라인 뷰로 감싸 서버가 열 순서·정렬을 맡게 하는 보조 함수(TSK-13-17).
/// 원래 문장은 결과의 정본이므로 호출하는 쪽은 감싼 문장을 가져오기에만 쓰고, 이미 감싼 문장을 다시 감싸지 않는다(항상 원래 문장 기준).
/// </summary>
public static class ResultQueryWrapper
{
    /// <summary>끝의 공백·세미콜론을 뗀다. 감싼 괄호 안에 들어갈 본문이다.</summary>
    public static string Body(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var end = sql.Length;
        while (end > 0 && (char.IsWhiteSpace(sql[end - 1]) || sql[end - 1] == ';'))
        {
            end--;
        }

        return sql[..end];
    }

    /// <summary>
    /// 문자열·따옴표 이름·주석 밖에 <c>FOR UPDATE</c> 가 있으면 true. 이런 문장은 인라인 뷰 안에 둘 수 없다(ORA-02014).
    /// </summary>
    public static bool HasForUpdate(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var code = Blank(sql);
        for (var i = 0; i + 3 <= code.Length; i++)
        {
            if (!IsWordAt(code, i, "FOR"))
            {
                continue;
            }

            var j = i + 3;
            while (j < code.Length && char.IsWhiteSpace(code[j]))
            {
                j++;
            }

            if (j > i + 3 && IsWordAt(code, j, "UPDATE"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 열 순서만 <paramref name="order"/>(열 ordinal 의 나열, 나머지 열은 뒤에 원래 순서) 로 바꾼 SELECT 를 만든다.
    /// 순서가 그대로이거나 감쌀 수 없으면(열 이름 중복·빈 이름·ROWID 열·FOR UPDATE) null 이고 호출하는 쪽이 원래 문장을 쓴다.
    /// </summary>
    public static string? Reorder(string sql, IReadOnlyList<ResultColumn> columns, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(order);
        var full = order.Concat(Enumerable.Range(0, columns.Count).Where(o => !order.Contains(o))).ToList();
        if (full.SequenceEqual(Enumerable.Range(0, columns.Count)) || !CanProject(sql, columns))
        {
            return null;
        }

        return "SELECT " + string.Join(", ", full.Select(o => Quote(columns[o].Name))) + " FROM (" + Environment.NewLine + Body(sql) + Environment.NewLine + ")";
    }

    /// <summary>
    /// 서버 정렬의 <c>ORDER BY</c> 뒤 글: 열 위치(1부터, 데이터 열 위치)와 방향. 머리글 정렬 규칙대로 NULL 은 방향과 무관하게 항상 뒤다.
    /// 거르기 줄의 실행 경로(<c>GridFilterRunner</c>)가 이 글을 <c>ORDER BY</c> 뒤에 붙인다.
    /// </summary>
    public static string OrderByClause(int position, bool desc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        return position.ToString(System.Globalization.CultureInfo.InvariantCulture) + (desc ? " DESC" : string.Empty) + " NULLS LAST";
    }

    /// <summary>
    /// 원래 문장을 <c>SELECT * FROM (원문) ORDER BY n [DESC] NULLS LAST</c> 로 감싼 문장(TSK-13-17). 호출하는 쪽은 언제나 원래 문장을 넘긴다(중첩 금지).
    /// 안쪽 열 이름이 겹치면 서버가 ORA-00918 로 거절하므로 호출하는 쪽이 <see cref="CanProject"/> 로 미리 거른다.
    /// 줄바꿈은 거르기 실행 경로(<c>GridFilterRunner.BuildStatement</c>)가 만드는 문장과 같다.
    /// </summary>
    public static string OrderBy(string sql, int position, bool desc)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return "SELECT * FROM (\n" + Body(sql) + "\n)\nORDER BY " + OrderByClause(position, desc);
    }

    /// <summary>안쪽 열 이름으로 바깥에서 다시 고를 수 있는지: 이름이 모두 있고 겹치지 않으며 ROWID 가 아니고 FOR UPDATE 가 없다.</summary>
    internal static bool CanProject(string sql, IReadOnlyList<ResultColumn> columns) =>
        columns.Count > 0
        && columns.All(c => !string.IsNullOrEmpty(c.Name) && !string.Equals(c.Name, "ROWID", StringComparison.OrdinalIgnoreCase))
        && columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() == columns.Count
        && !HasForUpdate(sql);

    /// <summary>큰따옴표 이름. 이름 안의 큰따옴표는 두 번 쓴다.</summary>
    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsWordAt(string code, int i, string word) =>
        string.Compare(code, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (i == 0 || !IsIdentChar(code[i - 1]))
        && (i + word.Length >= code.Length || !IsIdentChar(code[i + word.Length]));

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>문자열 리터럴·따옴표 이름·주석 안쪽을 공백으로 바꾼 글자(길이 같음).</summary>
    private static string Blank(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = close < 0 ? sql.Length : close + 2;
                sb.Append(' ', stop - i);
                i = stop;
            }
            else if (c is '\'' or '"')
            {
                sb.Append(' ');
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c)
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }

                        sb.Append(' ');
                        i++;
                        break;
                    }

                    sb.Append(' ');
                    i++;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
