using System;
using System.Security.Cryptography;
using System.Text;

namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 문장 글 → Oracle sql_id(13 글자). Oracle 과 같은 규칙: UTF-8 글 + NUL 의 MD5, 뒤 8 바이트를 두 개의 32 비트 정수(리틀 엔디언)로 읽어
/// 64 비트 수를 만들고 base32(<c>0123456789abcdfghjkmnpqrstuvwxyz</c>)로 적는다. 서버로 보낸 글과 한 글자라도 다르면 다른 값이 나온다.
/// 편집기 문장에서 보고서를 만들 때 마지막 실행 커서가 없으면 이것으로 sql_id 를 짐작한다(DB 조회 없음).
/// </summary>
public static class SqlIdHash
{
    private const string Alphabet = "0123456789abcdfghjkmnpqrstuvwxyz";

    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
#pragma warning disable CA5351 // Oracle sql_id 규칙이 MD5 다(보안 용도 아님).
        var hash = MD5.HashData(bytes);
#pragma warning restore CA5351
        ulong msb = BitConverter.ToUInt32(hash, 8);
        ulong lsb = BitConverter.ToUInt32(hash, 12);
        var value = (msb << 32) | lsb;
        var chars = new char[13];
        for (var i = 12; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }
}
