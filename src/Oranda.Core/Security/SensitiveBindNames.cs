namespace Oranda.Core.Security;

/// <summary>
/// 비밀로 보는 바인드 이름 규칙(data-model 5.2·5.3) 한 곳. 이름에 pass·pwd·secret·token 이 들어가면 비밀이다.
/// 이력은 값을 <c>***</c> 로 가리고(<see cref="ISecretRedactor.IsSensitiveBindName"/>), 작업 공간·.sql 바인드 블록은 그 바인드를 통째로 뺀다.
/// </summary>
public static class SensitiveBindNames
{
    private static readonly string[] Parts = ["pass", "pwd", "secret", "token"];

    public static bool IsSensitive(string? bindName)
    {
        if (string.IsNullOrEmpty(bindName))
        {
            return false;
        }

        foreach (var part in Parts)
        {
            if (bindName.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
