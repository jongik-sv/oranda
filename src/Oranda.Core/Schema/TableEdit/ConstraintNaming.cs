using System.Globalization;
using System.Text;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>자동으로 짓는 이름의 종류(설계 7.3·7.4).</summary>
public enum ConstraintNameKind
{
    PrimaryKey,
    Unique,
    ForeignKey,
    Check,
    Index,
}

/// <summary>
/// 제약·인덱스 이름 규칙(설계 7.3). <c>PK_&lt;T&gt;</c>, <c>UK_&lt;T&gt;_NN</c>, <c>FK_&lt;T&gt;_NN</c>, <c>CK_&lt;T&gt;_NN</c>, <c>IX_&lt;T&gt;_NN</c>.
/// <c>NN</c> 은 같은 테이블·같은 종류 이름 중 가장 큰 번호 + 1(두 자리, 99 를 넘으면 자리가 늘어남).
/// 서버 이름 길이 한도(Oracle 30 바이트·Tibero 128 바이트 등, UTF-8 바이트)를 넘으면 테이블 이름 쪽을 글자 경계에서 줄인다.
/// 서버가 지은 이름(<c>SYS_…</c>)은 번호 규칙에 맞지 않으므로 세지 않는다. 순수 문자열 로직(DBMS 접속 없음).
/// 소유: te-m1 (T2).
/// </summary>
public static class ConstraintNaming
{
    private const int NumberDigits = 2;

    /// <summary>다음 이름을 정한다. 따옴표는 붙이지 않는다(호출한 쪽이 <c>SqlIdentifier.Quote</c>).</summary>
    /// <param name="table">테이블 이름(소유자 제외)</param>
    /// <param name="existingNames">이 테이블에 이미 있는 제약·인덱스 이름(종류가 다른 것도 섞여도 됨). 대소문자 무시</param>
    /// <param name="maxBytes">서버가 받는 식별자 최대 UTF-8 바이트(Oracle 30, 12.2+·Tibero 128)</param>
    public static string Next(ConstraintNameKind kind, string table, IEnumerable<string> existingNames, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(existingNames);

        var taken = new HashSet<string>(existingNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
        var prefix = PrefixOf(kind);

        if (kind == ConstraintNameKind.PrimaryKey)
        {
            var pk = prefix + Fit(table, maxBytes - Utf8(prefix));
            if (!taken.Contains(pk))
            {
                return pk;
            }
            // PK_<T> 가 이미 쓰인 이름(다른 인덱스 등)이면 번호 꼴로 비켜 간다.
        }

        // 번호 자리가 늘어나도(100~) 한도를 지키도록, 번호 자리는 두 자리 + 필요한 만큼으로 매번 다시 맞춘다.
        var tableMax = maxBytes - Utf8(prefix) - Utf8("_") - NumberDigits;
        if (tableMax < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "이름 한도가 너무 작아 테이블 이름을 넣을 수 없다.");
        }

        var stem = prefix + Fit(table, tableMax) + "_";
        var max = 0;
        foreach (var name in taken)
        {
            if (TryNumberAfter(name, stem, out var n) && n > max)
            {
                max = n;
            }
        }

        var next = max + 1;
        while (true)
        {
            var digits = next.ToString("D" + NumberDigits, CultureInfo.InvariantCulture);
            var candidate = stem + digits;
            if (Utf8(candidate) > maxBytes)
            {
                // 세 자리 이상이 되어 한도를 넘으면 테이블 이름을 더 줄여서 다시 만든다.
                var room = maxBytes - Utf8(prefix) - 1 - digits.Length;
                if (room < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "이름 한도가 너무 작아 번호를 붙일 수 없다.");
                }

                candidate = prefix + Fit(table, room) + "_" + digits;
            }

            if (!taken.Contains(candidate))
            {
                return candidate;
            }

            next++;
        }
    }

    private static string PrefixOf(ConstraintNameKind kind) => kind switch
    {
        ConstraintNameKind.PrimaryKey => "PK_",
        ConstraintNameKind.Unique => "UK_",
        ConstraintNameKind.ForeignKey => "FK_",
        ConstraintNameKind.Check => "CK_",
        ConstraintNameKind.Index => "IX_",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary><paramref name="name"/> 이 <paramref name="stem"/> 바로 뒤에 숫자만 오는 꼴이면 그 숫자를 돌려준다.</summary>
    private static bool TryNumberAfter(string name, string stem, out int number)
    {
        number = 0;
        if (name.Length <= stem.Length || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tail = name.AsSpan(stem.Length);
        foreach (var ch in tail)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>UTF-8 바이트가 <paramref name="maxBytes"/> 이하가 되도록 뒤를 글자(서로게이트 쌍 포함) 경계에서 자른다.</summary>
    private static string Fit(string text, int maxBytes)
    {
        if (maxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "이름 한도가 너무 작아 테이블 이름을 넣을 수 없다.");
        }

        if (Utf8(text) <= maxBytes)
        {
            return text;
        }

        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes)
            {
                break;
            }

            sb.Append(rune.ToString());
            bytes += size;
        }

        return sb.ToString();
    }

    private static int Utf8(string text) => Encoding.UTF8.GetByteCount(text);
}
