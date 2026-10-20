using System.Security.Cryptography;
using System.Text;

namespace Oranda.Core.Journal;

/// <summary>일지 중복 판정용 해시. 줄바꿈 차이(CRLF)와 앞뒤 공백은 같은 내용으로 본다.</summary>
public static class JournalHash
{
    public static string Compute(string ddl)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        var normalized = ddl.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
