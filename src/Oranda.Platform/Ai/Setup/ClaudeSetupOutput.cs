using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oranda.Platform.Ai.Setup;

/// <summary><c>claude auth status --json</c> 에서 읽은 값. 모르는 칸은 버린다.</summary>
/// <param name="LoggedIn">로그인했는지.</param>
/// <param name="AuthMethod">로그인 방식(<c>none</c>·<c>claude.ai</c>·<c>oauth_token</c>·<c>api_key</c> 등). 없으면 null.</param>
/// <param name="Email">로그인한 계정 메일. 없으면 null(화면엔 「로그인됨」 만).</param>
internal sealed record ClaudeAuthState(bool LoggedIn, string? AuthMethod, string? Email);

/// <summary>
/// claude 설치·로그인·상태 출력 읽기. 모양은 실측 문서(<c>docs/oranda1/research/ai-setup-claude-measure.md</c>)를 따른다.
/// </summary>
internal static partial class ClaudeSetupOutput
{
    /// <summary>
    /// 로그인 주소 한 줄에서 주소를 꺼낸다. claude 는 주소를 터미널 링크(OSC 8)로 감싸 같은 주소를 두 번 찍으므로
    /// <c>https://</c> 부터 BEL·ESC·공백 전까지를 쓴다. https 주소가 없으면 null.
    /// </summary>
    public static string? LoginUrl(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = HttpsUrl().Match(line);
        return match.Success ? match.Value : null;
    }

    /// <summary>터미널 꾸밈 글자(색·링크)를 지운 글. 로그 창에 그대로 보인다.</summary>
    public static string Plain(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        // 링크(OSC 8)는 「ESC ] 8 ; ; 주소 BEL」 머리와 「ESC ] 8 ; ; BEL」 꼬리를 지워 보이는 글만 남긴다.
        var text = Osc().Replace(line, string.Empty);
        return Csi().Replace(text, string.Empty);
    }

    /// <summary>
    /// <c>claude auth status --json</c> 출력을 읽는다. <c>loggedIn</c>(bool)이 든 JSON 객체가 아니면 null
    /// (이 판은 상태 확인을 지원하지 않는다고 본다). 앞에 다른 줄이 있어도 첫 <c>{</c> 부터 읽는다.
    /// </summary>
    public static ClaudeAuthState? AuthStatus(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var start = output.IndexOf('{', StringComparison.Ordinal);
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output.AsMemory(start, end - start + 1));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            return new ClaudeAuthState(loggedIn.GetBoolean(), GetString(root, "authMethod"), GetString(root, "email"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    [GeneratedRegex(@"https://[^\s\u0007\u001b]+", RegexOptions.CultureInvariant)]
    private static partial Regex HttpsUrl();

    [GeneratedRegex(@"\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)", RegexOptions.CultureInvariant)]
    private static partial Regex Osc();

    [GeneratedRegex(@"\u001b\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex Csi();
}
