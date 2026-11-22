using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;

namespace Oranda.Core.Tests.Ai.Setup;

/// <summary>진단 글·담당자 글(<see cref="AiSetupDiagnostics"/>)에 비밀·주소가 나가지 않는지, 필요한 칸이 있는지.</summary>
public class AiSetupDiagnosticsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 9, 30, 0, TimeSpan.FromHours(9));

    // 가짜 비밀·주소를 글 칸마다 끼워 넣는다.
    private static readonly AiSetupDiagnosticInfo Poisoned = new(
        "0.1.31 https://ai.example.com/v1 sk-ant-test-0000",
        "Microsoft Windows 10.0.26300 user@example.com http://10.1.2.3:8080/x token=abc",
        AiSetupPath.ClaudeAccount,
        ClaudeFound: true,
        "2.1.330 (Claude Code) https://ai.example.com/v1 sk-ant-test-0000",
        AiSetupErrorKind.NotLoggedIn,
        At);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Test_failure_text_has_the_facts_and_no_secrets(bool english)
    {
        var text = AiSetupDiagnostics.ForTestFailure(Poisoned, english);

        Assert.Contains("Oranda: 0.1.31", text, StringComparison.Ordinal);
        Assert.Contains("OS: Microsoft Windows 10.0.26300", text, StringComparison.Ordinal);
        Assert.Contains("2.1.330", text, StringComparison.Ordinal);
        Assert.Contains("NotLoggedIn", text, StringComparison.Ordinal);
        Assert.Contains("2026-11-23 09:30:00 +09:00", text, StringComparison.Ordinal);
        Assert.Contains(english ? "Company Claude account" : "회사 Claude 계정", text, StringComparison.Ordinal);
        AssertNoSecrets(text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Admin_text_says_what_to_send_and_whether_claude_is_installed(bool english)
    {
        var text = AiSetupDiagnostics.ForAdmin(Poisoned with { ClaudeFound = false, ClaudeVersion = null }, english);

        Assert.Contains(".oranda-ai", text, StringComparison.Ordinal);
        Assert.Contains("Oranda: 0.1.31", text, StringComparison.Ordinal);
        Assert.Contains(english ? "not installed" : "설치 안 됨", text, StringComparison.Ordinal);
        AssertNoSecrets(text);
    }

    [Fact]
    public void Missing_values_show_a_dash()
    {
        var text = AiSetupDiagnostics.ForTestFailure(new AiSetupDiagnosticInfo(null, null, null, false, null, null, At), english: false);

        Assert.Contains("Oranda: -", text, StringComparison.Ordinal);
        Assert.Contains("OS: -", text, StringComparison.Ordinal);
        Assert.Contains("방법: -", text, StringComparison.Ordinal);
        Assert.Contains("오류: -", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cli_not_found", AiSetupErrorKind.CliNotFound)]
    [InlineData("cli_not_logged_in", AiSetupErrorKind.NotLoggedIn)]
    [InlineData("profile_missing", AiSetupErrorKind.Unknown)]
    [InlineData(null, AiSetupErrorKind.Unknown)]
    public void Driver_codes_map_to_kinds(string? code, AiSetupErrorKind kind) => Assert.Equal(kind, AiSetupErrorCodes.KindFor(code));

    [Theory]
    [InlineData("서버가 제한 시간 안에 응답하지 않았습니다. 프로필 연결은 프록시를 쓰지 않습니다.", AiSetupErrorKind.Timeout)]
    [InlineData("서버에 연결하지 못했습니다: 이름을 찾을 수 없음 (프로필 연결은 프록시를 쓰지 않습니다)", AiSetupErrorKind.Network)]
    [InlineData("서버가 인증을 거부했습니다(401). API 키를 확인하세요.", AiSetupErrorKind.ServerRejected)]
    [InlineData("서버가 오류를 돌려줬습니다(500).", AiSetupErrorKind.ServerRejected)]
    [InlineData("모델 이름을 입력하세요.", AiSetupErrorKind.Unknown)]
    [InlineData(null, AiSetupErrorKind.Unknown)]
    public void Server_test_failures_map_to_kinds(string? message, AiSetupErrorKind kind)
    {
        Assert.Equal(kind, AiSetupErrorCodes.KindFor(new AiConnectionTestResult(false, TimeSpan.FromSeconds(1), message)));
        Assert.Null(AiSetupErrorCodes.KindFor(new AiConnectionTestResult(true, TimeSpan.FromSeconds(1), message)));
    }

    [Fact]
    public void Diagnostic_input_has_no_free_text_field_for_raw_details() =>
        Assert.Equal(
            ["AppVersion", "OsDescription", "Path", "ClaudeFound", "ClaudeVersion", "ErrorKind", "At"],
            typeof(AiSetupDiagnosticInfo).GetConstructors().Single().GetParameters().Select(p => p.Name!));

    [Fact]
    public void Every_kind_has_easy_text_in_both_languages_and_install_lines_follow_the_platform()
    {
        foreach (var kind in Enum.GetValues<AiSetupErrorKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AiSetupErrorText.For(kind, english: false)));
            Assert.False(string.IsNullOrWhiteSpace(AiSetupErrorText.For(kind, english: true)));
        }

        Assert.Equal("irm https://claude.ai/install.ps1 | iex", ClaudeInstallCommands.ManualFor(AiHostPlatform.Windows));
        Assert.Equal("curl -fsSL https://claude.ai/install.sh | bash", ClaudeInstallCommands.ManualFor(AiHostPlatform.MacOS));
        Assert.Equal("curl -fsSL https://claude.ai/install.sh | bash", ClaudeInstallCommands.ManualFor(AiHostPlatform.Linux));
    }

    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain("example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-ant", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
    }
}
