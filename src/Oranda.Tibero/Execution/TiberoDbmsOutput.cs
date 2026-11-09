using System.Globalization;
using System.Text;

namespace Oranda.Tibero.Execution;

/// <summary>
/// DBMS_OUTPUT 거두기(E3, docs/design/tibero-driver-exec.md 5장). TDP.NET 은 <c>GET_LINES</c> 의 연관 배열 OUT 바인드를 받지 않아(TBR-15146)
/// 블록 안에서 <c>DBMS_OUTPUT.CHARARR</c> 로 받은 줄을 CLOB 하나로 묶어 돌려받는다: 줄마다 <c>&lt;길이&gt;:&lt;본문&gt;\n</c>, NULL 줄은 <c>-\n</c>.
/// 길이는 서버의 <c>LENGTH</c>(글자 수)다. 드라이버 형식을 쓰지 않는 부분만 둔다.
/// </summary>
internal static class TiberoDbmsOutput
{
    /// <summary>한 번에 거두는 최대 줄 수. 돌려받은 줄 수가 이보다 적으면 버퍼가 비었다.</summary>
    internal const int Batch = 1000;

    /// <summary>바인드: <c>:max</c>(IN 정수), <c>:n</c>(OUT 정수, 거둔 줄 수), <c>:r</c>(OUT CLOB, 묶은 줄).</summary>
    internal const string CollectBlock = """
        DECLARE l DBMS_OUTPUT.CHARARR; n INTEGER := :max; r CLOB;
        BEGIN
          DBMS_OUTPUT.GET_LINES(l, n);
          FOR i IN 1..n LOOP
            IF l(i) IS NULL THEN r := r || '-'; ELSE r := r || LENGTH(l(i)) || ':' || l(i); END IF;
            r := r || CHR(10);
          END LOOP;
          :n := n; :r := r;
        END;
        """;

    /// <summary>세션 첫 실행 때 한 번(<c>execution.dbmsOutput</c> 이 켜져 있을 때). 크기 제한 없음.</summary>
    internal const string EnableBlock = "BEGIN DBMS_OUTPUT.ENABLE(NULL); END;";

    /// <summary>
    /// 묶은 글을 줄로 푼다. NULL 줄은 빈 문자열(Oracle 경로와 같다). 길이는 글자(코드 포인트) 수로 먼저 읽고,
    /// 맞지 않으면 UTF-16 단위 수로 다시 읽는다. 형식이 깨진 나머지는 버리지 않고 한 줄로 붙인다(출력을 잃지 않게).
    /// </summary>
    internal static List<string> Decode(string? packed)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(packed))
        {
            return lines;
        }

        var pos = 0;
        while (pos < packed.Length)
        {
            if (packed[pos] == '-' && pos + 1 < packed.Length && packed[pos + 1] == '\n')
            {
                lines.Add(string.Empty);
                pos += 2;
                continue;
            }

            var colon = packed.IndexOf(':', pos);
            if (colon <= pos
                || !int.TryParse(packed.AsSpan(pos, colon - pos), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || !TryTake(packed, colon + 1, length, out var end))
            {
                lines.Add(packed[pos..].TrimEnd('\n'));
                break;
            }

            lines.Add(packed[(colon + 1)..end]);
            pos = end + 1;
        }

        return lines;
    }

    /// <summary><paramref name="start"/> 에서 <paramref name="length"/> 글자 뒤가 줄 끝(<c>\n</c>)이면 그 위치.</summary>
    private static bool TryTake(string text, int start, int length, out int end)
    {
        end = ByRunes(text, start, length);
        if (end >= 0 && end < text.Length && text[end] == '\n')
        {
            return true;
        }

        end = start + length;
        return end < text.Length && text[end] == '\n';
    }

    private static int ByRunes(string text, int start, int runes)
    {
        var i = start;
        for (var k = 0; k < runes; k++)
        {
            if (i >= text.Length || Rune.DecodeFromUtf16(text.AsSpan(i), out _, out var used) != System.Buffers.OperationStatus.Done)
            {
                return -1;
            }

            i += used;
        }

        return i;
    }
}
