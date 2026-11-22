using Oranda.Platform.Ai.Setup;

namespace Oranda.Platform.Tests.Ai.Setup;

/// <summary>claude 설치·로그인·상태 출력 읽기(실측 문서 ai-setup-claude-measure.md 「Linux」 절의 모양).</summary>
public sealed class ClaudeSetupOutputTests
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";

    [Fact]
    public void Login_url_is_taken_from_the_terminal_link_line()
    {
        var line = $"{Esc}]8;;https://claude.ai/oauth/authorize?abc-123{Bel}https://claude.ai/oauth/authorize?abc-123{Esc}]8;;{Bel}";

        Assert.Equal("https://claude.ai/oauth/authorize?abc-123", ClaudeSetupOutput.LoginUrl(line));
    }

    [Theory]
    [InlineData("If the browser didn't open, visit: https://platform.claude.com/oauth/authorize?code=true&state=x", "https://platform.claude.com/oauth/authorize?code=true&state=x")]
    [InlineData("Opening browser to sign in…", null)]
    [InlineData("see http://example.com/plain", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Login_url_needs_an_https_address(string? line, string? expected)
        => Assert.Equal(expected, ClaudeSetupOutput.LoginUrl(line));

    [Fact]
    public void Terminal_codes_are_stripped_from_log_lines()
    {
        var line = $"{Esc}[31mInstallation was killed{Esc}[0m {Esc}]8;;https://x.example{Bel}link{Esc}]8;;{Bel}";

        Assert.Equal("Installation was killed link", ClaudeSetupOutput.Plain(line));
    }

    [Theory]
    [InlineData("see https://a.example/cb?state=s3cr3t ok", "see https://a.example/cb?… ok")]
    [InlineData("see https://a.example/cb#code=s3cr3t", "see https://a.example/cb#…")]
    [InlineData("token: abcdef123456", "token=***")]
    [InlineData("libssl missing", "libssl missing")]
    public void Setup_text_hides_address_queries_fragments_and_secrets(string text, string expected)
        => Assert.Equal(expected, SetupText.Redact(text));

    [Fact]
    public void Logged_out_status_json_reads_as_not_logged_in()
    {
        const string json = """
            {"loggedIn": false, "authMethod": "none", "apiProvider": "firstParty", "loginExpiresAt": null, "consoleProfileSignInOffered": true}
            """;

        var state = ClaudeSetupOutput.AuthStatus(json);

        Assert.Equal(new ClaudeAuthState(false, "none", null), state);
    }

    [Fact]
    public void Logged_in_status_json_carries_the_account_email_and_ignores_unknown_fields()
    {
        const string json = """
            {
              "loggedIn": true,
              "authMethod": "claude.ai",
              "email": "user@example.com",
              "orgName": "Example",
              "subscriptionType": "team",
              "somethingNew": {"nested": [1, 2]}
            }
            """;

        Assert.Equal(new ClaudeAuthState(true, "claude.ai", "user@example.com"), ClaudeSetupOutput.AuthStatus(json));
    }

    [Theory]
    [InlineData("Not logged in. Run claude auth login to authenticate.")]
    [InlineData("""{"authMethod":"none"}""")]
    [InlineData("""{"loggedIn":"yes"}""")]
    [InlineData("[true]")]
    [InlineData("")]
    [InlineData(null)]
    public void Status_that_is_not_the_known_json_shape_is_unsupported(string? output)
        => Assert.Null(ClaudeSetupOutput.AuthStatus(output));

    [Fact]
    public void Status_json_can_follow_other_lines()
    {
        // 판에 따라 경고 줄이 앞에 붙어도 JSON 덩어리만 읽는다.
        const string output = "warning: something\n{\"loggedIn\": true, \"authMethod\": \"oauth_token\"}\n";

        Assert.Equal(new ClaudeAuthState(true, "oauth_token", null), ClaudeSetupOutput.AuthStatus(output));
    }
}
