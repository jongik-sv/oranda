using System.Text.Json;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Setup;

/// <summary><c>.oranda-ai</c> 파일을 읽지 못한 까닭. 화면 글은 <see cref="AiSetupFile.ErrorText"/>.</summary>
public enum AiSetupFileError
{
    /// <summary>JSON 객체가 아니다(깨진 파일·너무 큰 파일 포함).</summary>
    NotReadable,

    /// <summary><c>format</c> 이 <c>oranda-ai</c> 가 아니다.</summary>
    WrongFormat,

    /// <summary><c>version</c> 이 없거나 이 Oranda 가 모르는 판이다.</summary>
    UnsupportedVersion,

    /// <summary>열쇠·비밀번호처럼 보이는 칸이 있다. 파일을 읽지 않는다.</summary>
    ContainsSecret,

    /// <summary><c>baseUrl</c> 이 없거나 쓸 수 없는 주소다.</summary>
    BadUrl,

    /// <summary><c>apiFormat</c> 이 <c>openai</c>·<c>anthropic</c> 이 아니다.</summary>
    BadApiFormat,
}

/// <summary><c>.oranda-ai</c> 읽기 결과. 성공이면 <see cref="Profile"/>, 실패면 <see cref="Error"/>.</summary>
/// <param name="Profile">프로필 초안(<see cref="AiLlmProfile.Id"/> 는 비어 있음 — 저장하는 쪽이 채운다). 실패면 null.</param>
/// <param name="Error">실패 까닭. 성공이면 null.</param>
public sealed record AiSetupFileResult(AiLlmProfile? Profile, AiSetupFileError? Error)
{
    /// <summary>읽었다.</summary>
    public bool Success => Profile is not null;
}

/// <summary>
/// 회사 AI 서버 설정 파일 <c>.oranda-ai</c>(JSON). 모양:
/// <c>{ "format": "oranda-ai", "version": 1, "name": "…", "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai|anthropic", "model": "…", "localAi": false }</c>.
/// 열쇠는 담지 않는다: 열쇠·토큰·비밀번호처럼 보이는 칸이 하나라도 있으면(깊이 상관없이) 거절한다. 모르는 칸은 무시한다.
/// </summary>
public static class AiSetupFile
{
    /// <summary>파일 확장자.</summary>
    public const string Extension = ".oranda-ai";

    /// <summary><c>format</c> 값.</summary>
    public const string FormatName = "oranda-ai";

    /// <summary>이 빌드가 읽는 판.</summary>
    public const int CurrentVersion = 1;

    /// <summary>받는 글 길이 상한(글자). 넘으면 읽지 않는다.</summary>
    public const int MaxLength = 64 * 1024;

    // 칸 이름에서 '_'·'-' 를 빼고 소문자로 바꾼 뒤 이 낱말이 들어 있으면 비밀로 본다.
    private static readonly string[] SecretWords = ["key", "token", "password", "passwd", "pwd", "secret", "credential", "authorization", "cookie", "bearer"];

    /// <summary>파일 글을 읽어 프로필 초안을 만든다. 예외를 내지 않는다.</summary>
    public static AiSetupFileResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return Fail(AiSetupFileError.NotReadable);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return Fail(AiSetupFileError.NotReadable);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail(AiSetupFileError.NotReadable);
            }

            // 비밀 칸 검사가 맨 먼저다: 다른 칸이 틀려도 열쇠가 든 파일이라는 것을 먼저 알린다.
            if (HasSecret(root))
            {
                return Fail(AiSetupFileError.ContainsSecret);
            }

            if (!string.Equals(Text(root, "format"), FormatName, StringComparison.Ordinal))
            {
                return Fail(AiSetupFileError.WrongFormat);
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != CurrentVersion)
            {
                return Fail(AiSetupFileError.UnsupportedVersion);
            }

            var format = Text(root, "apiFormat")?.Trim().ToLowerInvariant() switch
            {
                "openai" => AiApiFormat.OpenAi,
                "anthropic" => AiApiFormat.Anthropic,
                _ => AiApiFormat.Unknown,
            };
            if (format == AiApiFormat.Unknown)
            {
                return Fail(AiSetupFileError.BadApiFormat);
            }

            var baseUrl = Text(root, "baseUrl")?.Trim();
            if (AiProfileUrl.BaseFor(format, baseUrl) is null)
            {
                return Fail(AiSetupFileError.BadUrl);
            }

            var localAi = root.TryGetProperty("localAi", out var local) && local.ValueKind == JsonValueKind.True;
            var name = Text(root, "name")?.Trim();
            return new AiSetupFileResult(
                new AiLlmProfile
                {
                    Name = string.IsNullOrEmpty(name) ? new Uri(baseUrl!).Host : name,
                    Preset = AiProfilePreset.Custom,
                    Format = format,
                    BaseUrl = baseUrl!,
                    Model = Text(root, "model")?.Trim() ?? "",
                    LocalAiConfirmedUrl = localAi ? baseUrl : null,
                },
                null);
        }
    }

    /// <summary>실패 까닭 쉬운 글(한/영).</summary>
    public static string ErrorText(AiSetupFileError error, bool english) => error switch
    {
        AiSetupFileError.WrongFormat => english ? "This is not an Oranda AI settings file." : "Oranda AI 설정 파일이 아닙니다.",
        AiSetupFileError.UnsupportedVersion => english ? "This file is for a newer Oranda. Update Oranda or ask your admin." : "더 새 Oranda 용 파일입니다. Oranda 를 업데이트하거나 담당자에게 물어보세요.",
        AiSetupFileError.ContainsSecret => english ? "This file has a key or password in it, so it was not read. Ask your admin for a file without it." : "파일에 열쇠나 비밀번호가 들어 있어 읽지 않았습니다. 그것이 빠진 파일을 담당자에게 받아 주세요.",
        AiSetupFileError.BadUrl => english ? "The server address in the file is not valid." : "파일에 적힌 서버 주소가 올바르지 않습니다.",
        AiSetupFileError.BadApiFormat => english ? "The file does not say how to talk to the server." : "파일에 서버와 이야기하는 방식이 적혀 있지 않습니다.",
        _ => english ? "The file could not be read." : "파일을 읽을 수 없습니다.",
    };

    private static AiSetupFileResult Fail(AiSetupFileError error) => new(null, error);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasSecret(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSecretName(property.Name) || HasSecret(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasSecret(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool IsSecretName(string name)
    {
        // maxOutputTokens 처럼 토큰 「수」 칸은 비밀이 아니다.
        var normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()
            .Replace("tokens", "", StringComparison.Ordinal);
        return SecretWords.Any(word => normalized.Contains(word, StringComparison.Ordinal));
    }
}
