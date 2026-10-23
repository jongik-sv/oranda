using System.Security.Cryptography;
using System.Text;

namespace Oranda.Core.MyBatis;

/// <summary>
/// 매퍼 원문 해시(design.md 5.3·5.4). 연 때 해시·디스크 해시·자기 쓰기 무시가 모두 이 함수 하나를 쓴다(구현마다 다르게 만들지 않게).
/// 입력 = BOM 없는 원문 글자, UTF-8 로 바꿔 SHA-256, 소문자 16진.
/// </summary>
public static class MapperHash
{
    public static string Compute(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
