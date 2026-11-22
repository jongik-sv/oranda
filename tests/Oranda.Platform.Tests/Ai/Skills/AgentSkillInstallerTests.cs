using Oranda.Platform.Ai.Skills;

namespace Oranda.Platform.Tests.Ai.Skills;

/// <summary>스킬 설치기: 가짜 홈 폴더에 세 경로로 쓰고, 표식 없는 남의 파일은 덮지 않으며, orandactl 경로가 바뀌면 다시 설치가 필요하다고 본다.</summary>
public sealed class AgentSkillInstallerTests : IDisposable
{
    private const string Ctl = "/opt/Oranda/orandactl";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "oranda-skill-" + Guid.NewGuid().ToString("N"));

    public AgentSkillInstallerTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private AgentSkillInstaller Installer(string ctl = Ctl) => new(_home, ctl);

    private string SkillFile(params string[] parts) => Path.Combine([_home, .. parts, "oranda", "SKILL.md"]);

    [Fact]
    public void Install_writes_the_skill_to_the_three_user_folders()
    {
        var results = Installer().Install(AgentSkillTargets.All);

        Assert.All(results, r => Assert.Equal(AgentSkillInstallOutcome.Installed, r.Outcome));
        Assert.True(File.Exists(SkillFile(".claude", "skills")));
        Assert.True(File.Exists(SkillFile(".agents", "skills")));
        Assert.True(File.Exists(SkillFile(".gemini", "config", "skills")));
        var text = File.ReadAllText(SkillFile(".agents", "skills"));
        Assert.Contains(OrandaSkillDocument.ManagedMarker, text, StringComparison.Ordinal);
        Assert.Contains("\"" + Ctl + "\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_reports_not_installed_then_current_after_install()
    {
        var installer = Installer();
        Assert.All(installer.Inspect(), s => Assert.Equal(AgentSkillState.NotInstalled, s.State));

        installer.Install([AgentSkillTarget.Claude]);

        var states = installer.Inspect().ToDictionary(s => s.Target, s => s.State);
        Assert.Equal(AgentSkillState.Current, states[AgentSkillTarget.Claude]);
        Assert.Equal(AgentSkillState.NotInstalled, states[AgentSkillTarget.Agents]);
        Assert.False(installer.NeedsReinstall());
    }

    [Fact]
    public void A_skill_file_without_the_marker_is_not_overwritten()
    {
        var path = SkillFile(".claude", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\nname: oranda\ndescription: mine\n---\nmy own skill\n");

        var result = Installer().Install([AgentSkillTarget.Claude]).Single();

        Assert.Equal(AgentSkillInstallOutcome.SkippedForeign, result.Outcome);
        Assert.Equal("---\nname: oranda\ndescription: mine\n---\nmy own skill\n", File.ReadAllText(path));
        Assert.Equal(AgentSkillState.Foreign, Installer().Inspect().Single(s => s.Target == AgentSkillTarget.Claude).State);
    }

    [Fact]
    public void A_foreign_file_is_overwritten_only_when_asked()
    {
        var path = SkillFile(".claude", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "my own skill\n");

        var result = Installer().Install([AgentSkillTarget.Claude], overwriteForeign: true).Single();

        Assert.Equal(AgentSkillInstallOutcome.Installed, result.Outcome);
        Assert.Contains(OrandaSkillDocument.ManagedMarker, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_moved_orandactl_makes_the_installed_skill_outdated_and_reinstall_fixes_it()
    {
        Installer().Install(AgentSkillTargets.All);

        var moved = Installer("/home/u/Oranda-new/orandactl");

        Assert.True(moved.NeedsReinstall());
        Assert.All(moved.Inspect(), s => Assert.Equal(AgentSkillState.Outdated, s.State));

        var results = moved.Install(AgentSkillTargets.All);

        Assert.All(results, r => Assert.Equal(AgentSkillInstallOutcome.Updated, r.Outcome));
        Assert.False(moved.NeedsReinstall());
    }

    [Fact]
    public void Reinstall_is_not_needed_when_nothing_is_installed_or_only_foreign_files_exist()
    {
        var path = SkillFile(".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "someone else's\n");

        Assert.False(Installer().NeedsReinstall());
    }

    [Fact]
    public void Crlf_line_endings_in_an_installed_file_still_count_as_current()
    {
        Installer().Install([AgentSkillTarget.Gemini]);
        var path = SkillFile(".gemini", "config", "skills");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(AgentSkillState.Current, Installer().Inspect().Single(s => s.Target == AgentSkillTarget.Gemini).State);
    }

    [Fact]
    public void Uninstall_removes_only_managed_files_and_the_empty_folder()
    {
        var installer = Installer();
        installer.Install([AgentSkillTarget.Claude]);
        var foreign = SkillFile(".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "keep me\n");

        var results = installer.Uninstall(AgentSkillTargets.All).ToDictionary(r => r.Target, r => r.Outcome);

        Assert.Equal(AgentSkillInstallOutcome.Removed, results[AgentSkillTarget.Claude]);
        Assert.Equal(AgentSkillInstallOutcome.SkippedForeign, results[AgentSkillTarget.Agents]);
        Assert.Equal(AgentSkillInstallOutcome.NotInstalled, results[AgentSkillTarget.Gemini]);
        Assert.False(Directory.Exists(Path.GetDirectoryName(SkillFile(".claude", "skills"))));
        Assert.Equal("keep me\n", File.ReadAllText(foreign));
    }

    [Fact]
    public void Uninstall_keeps_the_folder_when_the_user_added_other_files()
    {
        var installer = Installer();
        installer.Install([AgentSkillTarget.Claude]);
        var extra = Path.Combine(Path.GetDirectoryName(SkillFile(".claude", "skills"))!, "notes.md");
        File.WriteAllText(extra, "x");

        installer.Uninstall([AgentSkillTarget.Claude]);

        Assert.True(File.Exists(extra));
        Assert.False(File.Exists(SkillFile(".claude", "skills")));
    }

    [Fact]
    public void A_write_failure_is_reported_for_that_target_and_the_others_still_install()
    {
        // .gemini 를 파일로 만들어 그 아래 폴더를 만들 수 없게 한다.
        File.WriteAllText(Path.Combine(_home, ".gemini"), "not a folder");

        var results = Installer().Install(AgentSkillTargets.All).ToDictionary(r => r.Target);

        Assert.Equal(AgentSkillInstallOutcome.Failed, results[AgentSkillTarget.Gemini].Outcome);
        Assert.False(string.IsNullOrEmpty(results[AgentSkillTarget.Gemini].Message));
        Assert.Equal(AgentSkillInstallOutcome.Installed, results[AgentSkillTarget.Claude].Outcome);
        Assert.Equal(AgentSkillInstallOutcome.Installed, results[AgentSkillTarget.Agents].Outcome);
    }

    [Fact]
    public void A_client_is_present_when_its_config_folder_exists()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));

        var installer = Installer();

        Assert.True(installer.IsClientPresent(AgentSkillTarget.Agents));
        Assert.False(installer.IsClientPresent(AgentSkillTarget.Claude));
        Assert.False(installer.IsClientPresent(AgentSkillTarget.Gemini));
    }

    [Theory]
    [InlineData("/private/var/folders/x/AppTranslocation/1A2B/d/Oranda.app/Contents/MacOS/orandactl", true)]
    [InlineData("/Applications/Oranda.app/Contents/MacOS/orandactl", false)]
    [InlineData(@"C:\Program Files\Oranda\orandactl.exe", false)]
    [InlineData("/home/u/AppTranslocationNotes/orandactl", false)]
    public void Translocated_paths_are_recognised(string path, bool translocated)
    {
        Assert.Equal(translocated, AgentSkillInstaller.IsTranslocated(path));
    }

    [Fact]
    public void A_translocated_app_refuses_to_install_and_asks_no_reinstall()
    {
        Installer().Install(AgentSkillTargets.All);
        var moved = Installer("/private/var/folders/x/AppTranslocation/1A2B/d/Oranda.app/Contents/MacOS/orandactl");

        var results = moved.Install(AgentSkillTargets.All);

        Assert.All(results, r => Assert.Equal(AgentSkillInstallOutcome.Failed, r.Outcome));
        Assert.All(results, r => Assert.Contains("응용 프로그램 폴더", r.Message, StringComparison.Ordinal));
        Assert.DoesNotContain("AppTranslocation", File.ReadAllText(SkillFile(".claude", "skills")), StringComparison.Ordinal);
        Assert.False(moved.NeedsReinstall());
    }

    [Fact]
    public void Inspect_lists_the_full_path_of_each_target()
    {
        var paths = Installer().Inspect().ToDictionary(s => s.Target, s => s.FilePath);

        Assert.Equal(SkillFile(".claude", "skills"), paths[AgentSkillTarget.Claude]);
        Assert.Equal(SkillFile(".agents", "skills"), paths[AgentSkillTarget.Agents]);
        Assert.Equal(SkillFile(".gemini", "config", "skills"), paths[AgentSkillTarget.Gemini]);
    }
}
