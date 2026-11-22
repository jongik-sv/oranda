namespace Oranda.Platform.Ai.Skills;

/// <summary>
/// 스킬을 쓰는 사용자 전역 폴더(설계서 §7.1, 결정 E-14). 세 곳이면 네 CLI 가 모두 읽는다.
/// opencode 는 <see cref="Claude"/>·<see cref="Agents"/> 두 곳을 함께 읽으므로 따로 두지 않는다.
/// </summary>
public enum AgentSkillTarget
{
    /// <summary><c>~/.claude/skills</c> — Claude Code, opencode.</summary>
    Claude,

    /// <summary><c>~/.agents/skills</c> — Codex, opencode.</summary>
    Agents,

    /// <summary><c>~/.gemini/config/skills</c> — Antigravity(<c>agy</c>, App·CLI 공유).</summary>
    Gemini,
}

/// <summary>대상 폴더 목록과 홈 기준 상대 경로.</summary>
public static class AgentSkillTargets
{
    public static IReadOnlyList<AgentSkillTarget> All { get; } = [AgentSkillTarget.Claude, AgentSkillTarget.Agents, AgentSkillTarget.Gemini];

    /// <summary>홈 폴더 아래 스킬 모음 폴더(그 안에 <c>oranda/SKILL.md</c> 를 둔다).</summary>
    public static string SkillsDirectory(string home, AgentSkillTarget target) => target switch
    {
        AgentSkillTarget.Claude => Path.Combine(home, ".claude", "skills"),
        AgentSkillTarget.Agents => Path.Combine(home, ".agents", "skills"),
        AgentSkillTarget.Gemini => Path.Combine(home, ".gemini", "config", "skills"),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>그 대상을 읽는 CLI 의 설정 폴더들(하나라도 있으면 그 CLI 를 쓰는 것으로 본다).</summary>
    public static IReadOnlyList<string> ClientFolders(string home, AgentSkillTarget target) => target switch
    {
        AgentSkillTarget.Claude => [Path.Combine(home, ".claude")],
        AgentSkillTarget.Agents => [Path.Combine(home, ".codex"), Path.Combine(home, ".agents"), Path.Combine(home, ".config", "opencode")],
        AgentSkillTarget.Gemini => [Path.Combine(home, ".gemini")],
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>화면에 보일 CLI 이름들.</summary>
    public static string ClientNames(AgentSkillTarget target) => target switch
    {
        AgentSkillTarget.Claude => "Claude Code · opencode",
        AgentSkillTarget.Agents => "Codex · opencode",
        AgentSkillTarget.Gemini => "Antigravity (agy)",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };
}
