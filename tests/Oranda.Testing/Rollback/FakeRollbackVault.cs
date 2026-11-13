using System.Security.Cryptography;
using Oranda.Core.Rollback;

namespace Oranda.Testing.Rollback;

/// <summary>
/// 암호화하지 않는 시험용 금고: 결과 = [AAD 길이 4바이트][AAD][평문 XOR 0x5A]. 풀 때 AAD 가 다르거나 모양이 틀리면
/// <see cref="CryptographicException"/>(실제 금고와 같은 실패 경로). <see cref="KeyMissing"/> 이면 늘 실패한다.
/// </summary>
public sealed class FakeRollbackVault : IRollbackVault
{
    public bool IsDurable { get; set; } = true;

    /// <summary>키를 잃은 상태(풀기가 늘 실패).</summary>
    public bool KeyMissing { get; set; }

    public Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plain, ReadOnlyMemory<byte> associated, CancellationToken ct)
    {
        var aad = associated.Span;
        var result = new byte[4 + aad.Length + plain.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), aad.Length);
        aad.CopyTo(result.AsSpan(4));
        var p = plain.Span;
        for (var i = 0; i < p.Length; i++)
        {
            result[4 + aad.Length + i] = (byte)(p[i] ^ 0x5A);
        }

        return Task.FromResult(result);
    }

    public Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> cipher, ReadOnlyMemory<byte> associated, CancellationToken ct)
    {
        if (KeyMissing)
        {
            throw new CryptographicException("키가 없습니다(시험)");
        }

        var c = cipher.Span;
        if (c.Length < 4)
        {
            throw new CryptographicException("파일 모양이 틀렸습니다(시험)");
        }

        var aadLength = BitConverter.ToInt32(c[..4]);
        if (aadLength < 0 || 4 + aadLength > c.Length || !c.Slice(4, aadLength).SequenceEqual(associated.Span))
        {
            throw new CryptographicException("덧붙인 데이터가 다릅니다(시험)");
        }

        var body = c[(4 + aadLength)..];
        var plain = new byte[body.Length];
        for (var i = 0; i < body.Length; i++)
        {
            plain[i] = (byte)(body[i] ^ 0x5A);
        }

        return Task.FromResult(plain);
    }
}
