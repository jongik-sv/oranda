namespace Oranda.Platform.Ai.Profiles;

/// <summary>
/// 오류 글에 API 키가 섞여 나가지 않게 가린다. 표준 오류 끝부분·서버 응답·예외 메시지처럼 외부에서 온 글을 사용자에게 보이기 전에 거친다.
/// </summary>
internal static class SecretRedactor
{
    // 너무 짧은 값은 일반 글자와 구분되지 않아 가리면 오히려 메시지를 망가뜨린다. 실제 API 키는 이보다 훨씬 길다.
    private const int MinSecretLength = 4;

    private const string Mask = "***";

    public static string Redact(string? text, string? secret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return string.IsNullOrEmpty(secret) || secret.Length < MinSecretLength
            ? text
            : text.Replace(secret, Mask, StringComparison.Ordinal);
    }
}
