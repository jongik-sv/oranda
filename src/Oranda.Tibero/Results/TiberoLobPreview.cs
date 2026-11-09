using Oranda.Core.Grid;

namespace Oranda.Tibero.Results;

/// <summary>
/// LOB 로케이터 → <see cref="LobValue"/>(앞부분 미리보기 + 로케이터). 드라이버 형식을 모르고 읽기 함수만 받아 어디서나 시험한다.
/// 근거 = docs/design/tibero-driver-values.md 4장: CLOB <c>Length</c> 는 UCS-2 바이트(문자 수 = Length/2), 임시 NCLOB 은 Length 가 0 으로 올 수 있고,
/// <c>InitialLOBFetchSize</c> 는 효과가 없어 미리보기는 로케이터에서 앞부분만 읽어 만든다.
/// </summary>
internal static class TiberoLobPreview
{
    /// <summary>길이를 알 수 없을 때 <see cref="LobValue.Length"/> (BFILE 과 같은 약속).</summary>
    public const long UnknownLength = -1;

    /// <summary>CLOB·NCLOB. <paramref name="readChars"/> 는 위치 0 에서 이어 읽는 함수(<c>TiberoLob.Read(char[],…)</c>).</summary>
    public static LobValue Clob(bool isNClob, long lengthBytes, Func<char[], int, int, int> readChars, object locator, int previewSize)
    {
        ArgumentNullException.ThrowIfNull(readChars);
        var limit = Math.Max(0, previewSize);
        var declared = lengthBytes > 0 ? lengthBytes / 2 : 0;
        var want = declared > 0 ? (int)Math.Min(declared, limit) : limit;
        var buffer = new char[want];
        var read = Fill(buffer, readChars);

        // 읽은 끝이 서로게이트 쌍의 앞 절반이면 쌍이 잘린 것이라 뺀다(미리보기가 깨진 글자로 끝나지 않게).
        var shown = read;
        var mayHaveMore = read == want && (declared == 0 || declared > read);
        if (shown > 0 && mayHaveMore && char.IsHighSurrogate(buffer[shown - 1]))
        {
            shown--;
        }

        long length;
        bool truncated;
        if (declared > 0)
        {
            length = declared;
            truncated = declared > shown;
        }
        else if (read < want)
        {
            // 길이는 0 으로 왔지만 끝까지 읽혔다 → 실제 길이를 안다.
            length = read;
            truncated = false;
        }
        else
        {
            // 길이를 모르고 요청한 만큼이 다 찼다 → 더 있을 수 있다(0 글자 요청이면 비었다고 단정하지 않는다).
            length = UnknownLength;
            truncated = want > 0;
        }

        return new LobValue(isNClob ? LobKind.NClob : LobKind.Clob, length)
        {
            TextPreview = new string(buffer, 0, shown),
            IsTruncated = truncated,
            Locator = locator,
        };
    }

    /// <summary>BLOB. <paramref name="readBytes"/> 는 위치 0 에서 이어 읽는 함수.</summary>
    public static LobValue Blob(long length, Func<byte[], int, int, int> readBytes, object locator, int previewSize)
    {
        ArgumentNullException.ThrowIfNull(readBytes);
        var limit = Math.Max(0, previewSize);
        var declared = Math.Max(0, length);
        var want = declared > 0 ? (int)Math.Min(declared, limit) : limit;
        var buffer = new byte[want];
        var read = Fill(buffer, readBytes);

        long resolved;
        bool truncated;
        if (declared > 0)
        {
            resolved = declared;
            truncated = declared > read;
        }
        else if (read < want)
        {
            resolved = read;
            truncated = false;
        }
        else
        {
            resolved = UnknownLength;
            truncated = want > 0;
        }

        return new LobValue(LobKind.Blob, resolved)
        {
            BinaryPreview = buffer.AsMemory(0, read),
            IsTruncated = truncated,
            Locator = locator,
        };
    }

    /// <summary>읽기 함수가 요청보다 적게 돌려줘도 0(끝)이 나올 때까지 이어 채운다.</summary>
    private static int Fill<T>(T[] buffer, Func<T[], int, int, int> read)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = read(buffer, total, buffer.Length - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
