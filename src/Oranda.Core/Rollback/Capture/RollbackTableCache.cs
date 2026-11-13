using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Oranda.Core.Rollback.Script;

namespace Oranda.Core.Rollback.Capture;

/// <summary>
/// 준비 단계의 사전 읽기 캐시(RB-90: 운영 접속 그리드 한 행 반영이 행마다 시노님 풀기·표 정보·트리거·CASCADE 찾기를 되풀이했다).
/// 키 = (세션, 문장에 적힌 소유자·이름, DELETE 여부 — CASCADE 안내는 DELETE 에만 붙는다). 항목은 <see cref="Ttl"/> 뒤 만료된다.
/// <see cref="InvalidateAll"/>(DDL·변경 감지, <c>MetadataInvalidated</c>)은 모두 버리고 세대를 올린다: 다른 계정 접속(DbKey 가 다름)의 DDL 도 이 계정의
/// 항목을 버리게 하고, 무효화 전에 읽기 시작한 채우기가 무효화 뒤에 낡은 값을 넣지 못하게 한다(<see cref="Generation"/>·<see cref="Put"/>).
/// <see cref="Forget"/>(세션 닫힘·끊김)은 그 세션 항목을 버린다. <see cref="Put"/> 은 만료 항목을 먼저 치우고, 그래도 <see cref="MaxEntries"/> 면 모두 비운다.
/// </summary>
internal sealed class RollbackTableCache<TValue>(TimeProvider time, TimeSpan ttl) where TValue : class
{
    public const int MaxEntries = 512;

    private readonly ConcurrentDictionary<RollbackTableKey, Entry> _entries = new();
    private long _generation;

    public TimeSpan Ttl { get; } = ttl;

    /// <summary>지금 세대. 사전 읽기를 시작할 때 잡아 두었다가 <see cref="Put"/> 에 넘긴다.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public bool TryGet(RollbackTableKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && time.GetUtcNow() - entry.At < Ttl)
        {
            value = entry.Value;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>읽기를 시작한 뒤 무효화가 있었으면(세대가 다르면) 넣지 않는다.</summary>
    public void Put(RollbackTableKey key, TValue value, long generation)
    {
        if (generation != Generation)
        {
            return;
        }

        if (_entries.Count >= MaxEntries)
        {
            var now = time.GetUtcNow();
            foreach (var (k, e) in _entries)
            {
                if (now - e.At >= Ttl)
                {
                    _entries.TryRemove(k, out _);
                }
            }

            if (_entries.Count >= MaxEntries)
            {
                _entries.Clear();
            }
        }

        _entries[key] = new Entry(value, time.GetUtcNow());
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _generation);
        _entries.Clear();
    }

    public void Forget(Guid sessionId)
    {
        foreach (var key in _entries.Keys)
        {
            if (key.SessionId == sessionId)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(TValue Value, DateTimeOffset At);
}

/// <summary><see cref="RollbackTableCache{TValue}"/> 의 키. 문장에 적힌 소유자·이름(소유자가 있는 직접 참조만 캐시한다).</summary>
internal readonly record struct RollbackTableKey(Guid SessionId, string Owner, string Name, bool Delete);

/// <summary>
/// WHERE 가 키로 한 행만 고르는지(RB-90: 이런 문장은 <c>COUNT(*)</c> 예상 행 수를 1 로 둔다). 애매하면 늘 거짓(COUNT 를 한다):
/// 오판하면 운영 확인 창이 「예상 1행」을 보이고 상한 넘음 안내를 놓친다(원복 스크립트 자체는 잠금 읽기가 정확히 만든다).
/// </summary>
internal static class SingleRowKey
{
    /// <summary>
    /// 참이 되는 조건: 주석·q-따옴표 글이 없고, 바깥 단계(따옴표·괄호 밖)에 OR 가 없고, AND 로 나눈 조각 가운데
    /// <c>열 = 상수</c>(왼쪽은 순수한 식별자 사슬, 오른쪽은 바인드·작은따옴표 글 하나·<c>CHARTOROWID(…)</c>·숫자 — 숫자는 숫자 형식 키 열에만)가
    /// <c>ROWID</c> 하나 또는 켜진 PK 열 전부를 덮는다. 다른 조각(그리드의 「원래 값」 조건 등)은 행을 더 좁힐 뿐이라 상관없다.
    /// </summary>
    /// <param name="whereText">문장의 WHERE 뒤 글</param>
    /// <param name="table">시노님을 푼 표(열 형식과 PK)</param>
    /// <param name="primaryKeyEnabled">PK 가 켜져 있는가(꺼진 PK 는 중복이 있을 수 있다)</param>
    public static bool Matches(string? whereText, RollbackTable table, bool primaryKeyEnabled)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (string.IsNullOrWhiteSpace(whereText) || HasCommentOrAlternativeQuote(whereText))
        {
            return false;
        }

        var text = Normalize(whereText);
        if (RollbackSqlText.IndexOfTop(text, " OR ") >= 0 || RollbackSqlText.IndexOfTop(text, "OR(") >= 0
            || RollbackSqlText.IndexOfTop(text, ")OR ") >= 0)
        {
            return false;
        }

        var numericKeys = table.Columns.Where(c => c.Kind == RollbackValueKind.Number).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in RollbackSqlText.SplitTop(text, " AND "))
        {
            if (RollbackSqlText.ParsePredicate(part) is not ({ } left, { } right) || ColumnName(left.Trim()) is not { } column)
            {
                continue;
            }

            var value = right.Trim();
            if (IsBind(value) || IsSingleQuoted(value) || IsRowIdLiteral(value)
                || (IsNumber(value) && (numericKeys.Contains(column) || column == "ROWID")))
            {
                covered.Add(column);
            }
        }

        if (covered.Contains("ROWID"))
        {
            return true;
        }

        return primaryKeyEnabled && table.KeyColumns.Count > 0 && table.KeyColumns.All(covered.Contains);
    }

    /// <summary>주석(<c>--</c>, <c>/*</c>)·q-따옴표 글(<c>q'…'</c>, <c>nq'…'</c>)은 나누는 도우미가 모른다: 판정하지 않는다.</summary>
    private static bool HasCommentOrAlternativeQuote(string text) =>
        text.Contains("--", StringComparison.Ordinal) || text.Contains("/*", StringComparison.Ordinal)
        || text.Contains("q'", StringComparison.OrdinalIgnoreCase);

    /// <summary>따옴표 밖의 공백 묶음을 빈칸 하나로, 글자를 대문자로(따옴표 안은 그대로).</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        char? quote = null;
        var space = false;
        foreach (var ch in text.Trim())
        {
            if (quote is { } q)
            {
                sb.Append(ch);
                if (ch == q)
                {
                    quote = null;
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                sb.Append(ch);
                continue;
            }

            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    private static bool IsBind(string value) =>
        value.Length > 1 && value[0] == ':' && value.Skip(1).All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '#');

    private static bool IsNumber(string value) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

    /// <summary>작은따옴표 글 하나(안의 작은따옴표는 두 번)로 끝까지 이루어졌는가.</summary>
    private static bool IsSingleQuoted(string value)
    {
        if (value.Length < 2 || value[0] != '\'')
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] != '\'')
            {
                continue;
            }

            if (i + 1 < value.Length && value[i + 1] == '\'')
            {
                i++;
                continue;
            }

            return i == value.Length - 1;
        }

        return false;
    }

    private static bool IsRowIdLiteral(string value) =>
        value.StartsWith("CHARTOROWID(", StringComparison.Ordinal) && value.EndsWith(')')
        && value["CHARTOROWID(".Length..^1].Trim() is var inner && (IsBind(inner) || IsSingleQuoted(inner));

    /// <summary>
    /// 왼쪽이 순수한 식별자 사슬(<c>열</c>, <c>별칭.열</c>, <c>소유자.표.열</c>, 따옴표 이름 포함)이면 마지막 이름(따옴표 이름은 벗김), 아니면 null.
    /// </summary>
    private static string? ColumnName(string left)
    {
        string? last = null;
        var i = 0;
        while (i < left.Length)
        {
            string part;
            if (left[i] == '"')
            {
                var end = i + 1;
                var sb = new StringBuilder();
                while (true)
                {
                    if (end >= left.Length)
                    {
                        return null;
                    }

                    if (left[end] == '"')
                    {
                        if (end + 1 < left.Length && left[end + 1] == '"')
                        {
                            sb.Append('"');
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    sb.Append(left[end]);
                    end++;
                }

                part = sb.ToString();
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < left.Length && (char.IsLetterOrDigit(left[i]) || left[i] is '_' or '$' or '#'))
                {
                    i++;
                }

                if (i == start || !char.IsLetter(left[start]))
                {
                    return null;
                }

                part = left[start..i];
            }

            if (part.Length == 0)
            {
                return null;
            }

            last = part;
            if (i == left.Length)
            {
                return last;
            }

            if (left[i] != '.')
            {
                return null;
            }

            i++;
        }

        return null; // 점으로 끝났다
    }
}
