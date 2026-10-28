using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Oranda.Language.Ai.Tools;

/// <summary>
/// 문장 글에서 Oracle 이 붙이는 <c>sql_id</c> 를 계산한다: 글(UTF-8) 뒤에 0 바이트를 붙인 MD5 의 뒤 8바이트를 정수로 읽어
/// (8~11번째 바이트가 상위, 12~15번째가 하위 32비트, 각각 little-endian) 영숫자 32진수(<c>0-9a-z</c> 중 e·i·l·o 제외) 13글자로 쓴다. 글자 하나(공백·끝 세미콜론 포함)만 달라도 값이 다르므로
/// 실제로 서버에 보낸 글과 같을 때만 공유 풀의 커서와 맞는다. 계산한 값은 공유 풀에 있는지 확인한 뒤에 쓴다.
/// </summary>
public static class SqlIdCalculator
{
    private const string Alphabet = "0123456789abcdfghjkmnpqrstuvwxyz";
    private const int Length = 13;

    public static string Compute(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var bytes = new byte[Encoding.UTF8.GetByteCount(sql) + 1];
        Encoding.UTF8.GetBytes(sql, bytes);

        // sql_id 는 보안이 아니라 Oracle 이 정한 식별자 계산이라 MD5 를 쓴다.
#pragma warning disable CA5351
        var hash = MD5.HashData(bytes);
#pragma warning restore CA5351
        var value = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(8)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(12));
        Span<char> chars = stackalloc char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }
}
