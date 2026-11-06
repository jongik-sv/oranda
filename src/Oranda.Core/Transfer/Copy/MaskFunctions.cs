using System.Security.Cryptography;
using System.Text;

namespace Oranda.Core.Transfer.Copy;

/// <summary>복사 중 열 값 마스킹. 마스킹한 값만 대상에 넣고 원래 값은 어디에도 남기지 않는다.</summary>
public static class MaskFunctions
{
    private static readonly MaskKind[] TextKinds = [MaskKind.Fixed, MaskKind.KeepFirst, MaskKind.KeepLast, MaskKind.Hash, MaskKind.Null];
    private static readonly MaskKind[] OtherKinds = [MaskKind.Fixed, MaskKind.Null];

    /// <summary>
    /// 문자 열 전용. <c>Hash</c> 는 <c>HMACSHA256(key = Base64 디코드한 salt, data = UTF-8 값)</c> 의 대문자 16진수를
    /// <paramref name="maxLength"/> 로 자른다. <c>KeepFirst</c>·<c>KeepLast</c> 는 <c>Count</c> 글자를 남기고 나머지를 <c>*</c> 로 바꾼다.
    /// NULL 입력은 <c>Fixed</c> 외 규칙에서 NULL 그대로다.
    /// </summary>
    public static string? Apply(MaskRule rule, string? value, string salt, int maxLength)
    {
        switch (rule.Kind)
        {
            case MaskKind.Fixed:
                return rule.Value;
            case MaskKind.Null:
                return null;
        }
        if (value is null) return null;

        switch (rule.Kind)
        {
            case MaskKind.KeepFirst:
            {
                var keep = Math.Clamp(rule.Count, 0, value.Length);
                return value[..keep] + new string('*', value.Length - keep);
            }
            case MaskKind.KeepLast:
            {
                var keep = Math.Clamp(rule.Count, 0, value.Length);
                return new string('*', value.Length - keep) + value[^keep..];
            }
            case MaskKind.Hash:
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
                var hex = Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(salt), Encoding.UTF8.GetBytes(value)));
                return hex.Length > maxLength ? hex[..maxLength] : hex;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(rule));
        }
    }

    /// <summary>문자 타입이면 다섯 가지, 그 밖은 <c>Fixed</c>, <c>Null</c>.</summary>
    public static IReadOnlyList<MaskKind> AllowedKinds(string dataType)
    {
        var t = dataType.Trim().ToUpperInvariant();
        var text = t.StartsWith("VARCHAR2", StringComparison.Ordinal) || t.StartsWith("NVARCHAR2", StringComparison.Ordinal)
                   || t.StartsWith("CHAR", StringComparison.Ordinal) || t.StartsWith("NCHAR", StringComparison.Ordinal);
        return text ? TextKinds : OtherKinds;
    }

    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
