using System.Globalization;

namespace Oranda.Core.Rollback.Compare;

/// <summary>
/// 트랜잭션 id 글(<c>DBMS_TRANSACTION.LOCAL_TRANSACTION_ID</c>, <c>usn.slot.sqn</c>) → <c>VERSIONS_XID</c> RAW(8) 16진 글(설계 8.2).
/// 바이트 순서는 리틀 엔디언(usn 2바이트, slot 2바이트, sqn 4바이트)으로 둔다. 실측 전이라(RB-12) 부르는 쪽은
/// VERSIONS 질의가 키를 하나도 내지 않으면 문장 WHERE 범위로 물러난다.
/// </summary>
public static class TransactionXid
{
    /// <summary>바꿀 수 없으면 null.</summary>
    public static string? ToRawHex(string? transactionId)
    {
        if (transactionId?.Split('.') is not [var a, var b, var c]
            || !ushort.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var usn)
            || !ushort.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
            || !uint.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var sqn))
        {
            return null;
        }

        Span<byte> raw = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(raw, usn);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(raw[2..], slot);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw[4..], sqn);
        return Convert.ToHexString(raw);
    }
}
