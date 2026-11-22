using System;
using System.Collections.Generic;
using System.Text;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>
/// 편집기에서 직접 돌린 문장의 <c>DBMS_STATS.GATHER_TABLE_STATS(ownname, tabname, …)</c> 호출에서 대상 테이블을 읽는다(결함 2026-11-22: 다른 길로 통계를 바꿔도 테이블 창을 새로 읽게).
/// 이름은 문자열 상수(<c>'SCOTT'</c>)이나 <c>USER</c> 일 때만 안다 — 변수·식이면 그 호출은 건너뛴다. 주석·문자열 안의 글은 보지 않는다.
/// 이름 규칙은 DBMS_STATS 와 같다: 따옴표 없으면 대문자, <c>'"Mixed"'</c> 는 그대로.
/// </summary>
public static class GatherStatsSqlTargets
{
    private enum Kind
    {
        Word,
        Text,
        Symbol,
    }

    private readonly record struct Token(Kind Kind, string Value);

    /// <param name="sql">실행한 문장 글.</param>
    /// <param name="defaultOwner">접속 계정(<c>USER</c> 자리).</param>
    public static IReadOnlyList<TableStatsTarget> Find(string sql, string? defaultOwner)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var found = new List<TableStatsTarget>();
        if (sql.IndexOf("GATHER_TABLE_STATS", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return found;
        }

        var tokens = Tokenize(sql);
        for (var i = 2; i < tokens.Count; i++)
        {
            if (!IsWord(tokens[i], "GATHER_TABLE_STATS") || !IsSymbol(tokens[i - 1], ".") || !IsWord(tokens[i - 2], "DBMS_STATS")
                || i + 1 >= tokens.Count || !IsSymbol(tokens[i + 1], "("))
            {
                continue;
            }

            if (ReadCall(tokens, i + 2, defaultOwner) is { } target && !found.Contains(target))
            {
                found.Add(target);
            }
        }

        return found;
    }

    /// <summary>괄호 안 인자(위치·이름 => 값)에서 ownname·tabname 을 읽는다.</summary>
    private static TableStatsTarget? ReadCall(List<Token> tokens, int start, string? defaultOwner)
    {
        string? owner = null;
        string? table = null;
        var position = 0;
        var i = start;
        while (i < tokens.Count && !IsSymbol(tokens[i], ")"))
        {
            string? name = null;
            if (i + 1 < tokens.Count && tokens[i].Kind == Kind.Word && IsSymbol(tokens[i + 1], "=>"))
            {
                name = tokens[i].Value.ToUpperInvariant();
                i += 2;
            }

            // 값 한 칸: 문자열 상수·USER 만 안다. 그 밖(식·변수·중첩 괄호)은 쉼표까지 건너뛴다.
            string? value = null;
            var depth = 0;
            var first = true;
            var simple = true;
            while (i < tokens.Count && (depth > 0 || (!IsSymbol(tokens[i], ",") && !IsSymbol(tokens[i], ")"))))
            {
                var t = tokens[i];
                depth += IsSymbol(t, "(") ? 1 : IsSymbol(t, ")") ? -1 : 0;
                if (first)
                {
                    value = t.Kind == Kind.Text ? Name(t.Value) : IsWord(t, "USER") ? defaultOwner?.Trim().ToUpperInvariant() : null;
                }
                else
                {
                    simple = false;
                }

                first = false;
                i++;
            }

            if (!simple)
            {
                value = null;
            }

            var slot = name ?? (position == 0 ? "OWNNAME" : position == 1 ? "TABNAME" : null);
            if (slot == "OWNNAME")
            {
                owner = value;
            }
            else if (slot == "TABNAME")
            {
                table = value;
            }

            position++;
            if (i < tokens.Count && IsSymbol(tokens[i], ","))
            {
                i++;
            }
        }

        return string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(table) ? null : new TableStatsTarget(owner, table);
    }

    /// <summary>DBMS_STATS 이름 규칙: <c>"Mixed"</c> 는 그대로, 아니면 대문자.</summary>
    private static string? Name(string text)
    {
        var t = text.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
        {
            return t.Length == 2 ? null : t[1..^1];
        }

        return t.Length == 0 ? null : t.ToUpperInvariant();
    }

    private static bool IsWord(Token t, string word) => t.Kind == Kind.Word && string.Equals(t.Value, word, StringComparison.OrdinalIgnoreCase);

    private static bool IsSymbol(Token t, string symbol) => t.Kind == Kind.Symbol && t.Value == symbol;

    /// <summary>낱말·문자열 상수·기호로 나눈다. 주석은 버린다. 큰따옴표 이름은 낱말(따옴표 뺌)로.</summary>
    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        sb.Append('\'');
                        i += 2;
                    }
                    else if (sql[i] == '\'')
                    {
                        i++;
                        break;
                    }
                    else
                    {
                        sb.Append(sql[i++]);
                    }
                }

                tokens.Add(new Token(Kind.Text, sb.ToString()));
            }
            else if (c == '"')
            {
                var end = sql.IndexOf('"', i + 1);
                end = end < 0 ? sql.Length : end;
                tokens.Add(new Token(Kind.Word, sql[(i + 1)..end]));
                i = Math.Min(sql.Length, end + 1);
            }
            else if (TableStatsTarget.IsIdentifierChar(c))
            {
                var start = i;
                while (i < sql.Length && TableStatsTarget.IsIdentifierChar(sql[i]))
                {
                    i++;
                }

                tokens.Add(new Token(Kind.Word, sql[start..i]));
            }
            else if (c == '=' && i + 1 < sql.Length && sql[i + 1] == '>')
            {
                tokens.Add(new Token(Kind.Symbol, "=>"));
                i += 2;
            }
            else
            {
                tokens.Add(new Token(Kind.Symbol, c.ToString()));
                i++;
            }
        }

        return tokens;
    }
}
