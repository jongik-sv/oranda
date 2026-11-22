using System.Text;
using Oranda.Core.Composition;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Ai.Skills;

/// <summary>설치된 스킬 파일의 상태.</summary>
public enum AgentSkillState
{
    /// <summary>파일이 없다.</summary>
    NotInstalled,

    /// <summary>Oranda 가 쓴 파일이고 지금 본문(지금 orandactl 경로)과 같다.</summary>
    Current,

    /// <summary>Oranda 가 쓴 파일인데 본문이 다르다(orandactl 경로가 바뀌었거나 판이 올라감). 「다시 설치」 대상.</summary>
    Outdated,

    /// <summary>머리 표식이 없는 남의 파일. 덮지 않는다.</summary>
    Foreign,
}

/// <summary>설치·지우기 한 건의 결과.</summary>
public enum AgentSkillInstallOutcome
{
    Installed,
    Updated,
    Removed,
    NotInstalled,
    SkippedForeign,
    Failed,
}

/// <summary>대상 하나의 상태.</summary>
public sealed record AgentSkillStatus(AgentSkillTarget Target, string FilePath, AgentSkillState State);

/// <summary>대상 하나의 설치·지우기 결과. 실패면 <see cref="Message"/> 에 까닭.</summary>
public sealed record AgentSkillInstallResult(AgentSkillTarget Target, string FilePath, AgentSkillInstallOutcome Outcome, string? Message = null);

/// <summary>밖의 LLM CLI 용 <c>oranda</c> 스킬 설치기(설계서 §7, 결정 E-13~E-15).</summary>
public interface IAgentSkillInstaller
{
    /// <summary>스킬 본문에 적는 orandactl 절대 경로.</summary>
    string OrandactlPath { get; }

    IReadOnlyList<AgentSkillStatus> Inspect();

    /// <summary>
    /// 쓴다. 표식 없는 남의 파일은 <paramref name="overwriteForeign"/> 가 아니면 건너뛴다(화면이 사용자에게 물은 뒤에만 true).
    /// 한 곳이 실패해도 나머지는 계속한다.
    /// </summary>
    IReadOnlyList<AgentSkillInstallResult> Install(IEnumerable<AgentSkillTarget> targets, bool overwriteForeign = false);

    /// <summary>Oranda 가 쓴 파일만 지운다. 폴더가 비면 폴더도 지운다.</summary>
    IReadOnlyList<AgentSkillInstallResult> Uninstall(IEnumerable<AgentSkillTarget> targets);

    /// <summary>Oranda 가 쓴 스킬이 하나라도 있고 그 본문이 지금과 다르면 true(「다시 설치」 안내).</summary>
    bool NeedsReinstall();
}

/// <summary>
/// <see cref="IAgentSkillInstaller"/> 구현. 홈 폴더 아래 세 곳(<see cref="AgentSkillTargets"/>)에 <c>oranda/SKILL.md</c> 를 쓴다.
/// 파일은 같은 폴더의 임시 파일에 쓴 뒤 바꿔 넣어, 도중에 끊겨도 반쯤 쓴 스킬이 남지 않게 한다.
/// 스킬에는 비밀이 없으므로 권한은 OS 기본값을 따른다.
/// </summary>
[OrandaService(typeof(IAgentSkillInstaller))]
public sealed class AgentSkillInstaller : IAgentSkillInstaller
{
    private readonly string _home;

    public AgentSkillInstaller()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), CliDriver.OrandactlPath(AppContext.BaseDirectory, File.Exists))
    {
    }

    /// <summary>시험용: 홈 폴더와 orandactl 경로를 바꿔 넣는다.</summary>
    internal AgentSkillInstaller(string home, string orandactlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(orandactlPath);
        _home = home;
        OrandactlPath = orandactlPath;
    }

    public string OrandactlPath { get; }

    public IReadOnlyList<AgentSkillStatus> Inspect()
    {
        var expected = OrandaSkillDocument.Render(OrandactlPath);
        return [.. AgentSkillTargets.All.Select(t => new AgentSkillStatus(t, FilePath(t), StateOf(FilePath(t), expected)))];
    }

    public bool NeedsReinstall() => Inspect().Any(s => s.State == AgentSkillState.Outdated);

    public IReadOnlyList<AgentSkillInstallResult> Install(IEnumerable<AgentSkillTarget> targets, bool overwriteForeign = false)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var expected = OrandaSkillDocument.Render(OrandactlPath);
        var results = new List<AgentSkillInstallResult>();
        foreach (var target in targets.Distinct())
        {
            var path = FilePath(target);
            try
            {
                var state = StateOf(path, expected);
                if (state == AgentSkillState.Foreign && !overwriteForeign)
                {
                    results.Add(new(target, path, AgentSkillInstallOutcome.SkippedForeign));
                    continue;
                }

                if (state != AgentSkillState.Current)
                {
                    WriteAtomically(path, expected);
                }

                var outcome = state == AgentSkillState.Outdated ? AgentSkillInstallOutcome.Updated : AgentSkillInstallOutcome.Installed;
                results.Add(new(target, path, outcome));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new(target, path, AgentSkillInstallOutcome.Failed, ex.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<AgentSkillInstallResult> Uninstall(IEnumerable<AgentSkillTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var results = new List<AgentSkillInstallResult>();
        foreach (var target in targets.Distinct())
        {
            var path = FilePath(target);
            try
            {
                if (!File.Exists(path))
                {
                    results.Add(new(target, path, AgentSkillInstallOutcome.NotInstalled));
                    continue;
                }

                if (!OrandaSkillDocument.IsManaged(File.ReadAllText(path)))
                {
                    results.Add(new(target, path, AgentSkillInstallOutcome.SkippedForeign));
                    continue;
                }

                File.Delete(path);
                var directory = Path.GetDirectoryName(path)!;
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }

                results.Add(new(target, path, AgentSkillInstallOutcome.Removed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new(target, path, AgentSkillInstallOutcome.Failed, ex.Message));
            }
        }

        return results;
    }

    private string FilePath(AgentSkillTarget target)
        => Path.Combine(AgentSkillTargets.SkillsDirectory(_home, target), OrandaSkillDocument.SkillName, OrandaSkillDocument.FileName);

    private static AgentSkillState StateOf(string path, string expected)
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return AgentSkillState.NotInstalled;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 읽지 못하는 파일은 남의 것으로 본다(덮지 않는다).
            return AgentSkillState.Foreign;
        }

        if (!OrandaSkillDocument.IsManaged(text))
        {
            return AgentSkillState.Foreign;
        }

        return string.Equals(text.Replace("\r\n", "\n", StringComparison.Ordinal), expected, StringComparison.Ordinal)
            ? AgentSkillState.Current
            : AgentSkillState.Outdated;
    }

    private static void WriteAtomically(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + OrandaSkillDocument.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
