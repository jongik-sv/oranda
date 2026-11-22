using Oranda.Platform.Ai.Bridge;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Ai.Bridge;

/// <summary>소켓 경로 고르기(A4): 한도·XDG_RUNTIME_DIR·남이 먼저 만든 대체 폴더 피하기. 폴더 소유 판정은 가짜로 넣는다.</summary>
public class AgentSocketPathTests
{
    private const string Short = "/Users/short/Library/Application Support/Oranda/run/agent.sock";
    private static readonly string TooLong = "/Users/" + new string('n', 60) + "/Library/Application Support/Oranda/run/agent.sock";

    private static string Choose(string preferred, Func<string, bool> claim, string? xdg = null, int? instance = null, Func<string>? random = null) =>
        AgentSocketPath.Choose(preferred, "/tmp", "501", instance, AgentSocketPath.LimitFor(macOS: false), k => k == "XDG_RUNTIME_DIR" ? xdg : null, claim, random ?? (() => "abcd1234"));

    [Fact]
    public void Preferred_path_within_the_limit_is_kept_without_touching_folders()
    {
        var claimed = new List<string>();
        Assert.Equal(Short, Choose(Short, d => { claimed.Add(d); return true; }));
        Assert.Empty(claimed);
        Assert.Equal(103, AgentSocketPath.LimitFor(macOS: true));
        Assert.Equal(107, AgentSocketPath.LimitFor(macOS: false));
    }

    [Fact]
    public void Runtime_directory_comes_first_when_set()
    {
        Assert.Equal(Path.Combine("/run/user/501", "oranda", "agent.sock"), Choose(TooLong, _ => true, xdg: "/run/user/501"));
        Assert.Equal(Path.Combine("/tmp", "oranda-501", "agent.sock"), Choose(TooLong, _ => true, xdg: "relative/dir")); // 상대 경로는 쓰지 않는다
    }

    [Fact]
    public void Temp_folder_is_used_when_there_is_no_runtime_directory_and_keeps_the_instance_suffix()
    {
        Assert.Equal(Path.Combine("/tmp", "oranda-501", "agent.sock"), Choose(TooLong, _ => true));
        Assert.Equal(Path.Combine("/tmp", "oranda-501", "agent-42.sock"), Choose(TooLong, _ => true, instance: 42));
    }

    [Fact]
    public void Folder_made_by_another_user_is_skipped_for_a_random_one()
    {
        var hijacked = Path.Combine("/tmp", "oranda-501");
        var claimed = new List<string>();
        var chosen = Choose(TooLong, d => { claimed.Add(d); return d != hijacked; });

        Assert.Equal(Path.Combine("/tmp", "oranda-501-abcd1234", "agent.sock"), chosen);
        Assert.Equal([hijacked, Path.Combine("/tmp", "oranda-501-abcd1234")], claimed);
    }

    [Fact]
    public void Runtime_directory_that_cannot_be_claimed_falls_back_to_temp()
    {
        var runtime = Path.Combine("/run/user/501", "oranda");
        Assert.Equal(Path.Combine("/tmp", "oranda-501", "agent.sock"), Choose(TooLong, d => d != runtime, xdg: "/run/user/501"));
    }

    [Fact]
    public void Nothing_usable_returns_the_preferred_path_after_a_few_random_tries()
    {
        var tries = 0;
        Assert.Equal(TooLong, Choose(TooLong, _ => false, random: () => (++tries).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(3, tries);

        var huge = "/" + new string('t', 120);
        Assert.Equal(TooLong, AgentSocketPath.Choose(TooLong, huge, "501", null, 107, null, _ => true)); // 대체 경로도 넘으면 그대로
    }

    [Fact]
    public void Try_ensure_creates_an_owner_only_folder_and_reports_success()
    {
        using var root = new TempDirectory("oranda-sock-");
        var folder = Path.Combine(root, "oranda-test");
        Assert.True(PrivateDirectory.TryEnsure(folder));
        Assert.True(Directory.Exists(folder));
        Assert.True(PrivateDirectory.TryEnsure(folder)); // 이미 내 폴더면 그대로 참
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        }

        // 폴더 자리에 파일이 있으면 쓸 수 없다.
        var file = Path.Combine(root, "plain-file");
        File.WriteAllText(file, "x");
        Assert.False(PrivateDirectory.TryEnsure(file));
    }
}
