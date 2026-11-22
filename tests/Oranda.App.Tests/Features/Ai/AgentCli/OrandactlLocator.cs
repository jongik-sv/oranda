namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// 시험이 띄울 빌드된 <c>orandactl</c> 찾기(AgentCli 시험 공용). 시험 어셈블리 출력 경로(<see cref="AppContext.BaseDirectory"/>)의
/// <c>bin/&lt;구성&gt;/&lt;tfm&gt;</c> 을 따라 <c>src/Oranda.Agent.Cli/bin/&lt;구성&gt;/&lt;tfm&gt;/</c> 를 본다 — 태그 CI 는 <c>-c Release --no-build</c> 로 돈다.
/// 없으면 앱이 출력에 복사한 <c>agent/</c> 폴더(시험 출력에 따라오는 경우)를 본다.
/// </summary>
internal static class OrandactlLocator
{
    /// <summary>이 OS 의 실행 파일 이름.</summary>
    public static string ExecutableName(bool windows) => windows ? "orandactl.exe" : "orandactl";

    /// <summary>지금 시험 출력에서 찾는다. 없으면 <see cref="FileNotFoundException"/>(본 경로들을 글에 담는다).</summary>
    public static string Find()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var root = FindRepositoryRoot(baseDirectory)
            ?? throw new DirectoryNotFoundException("Oranda.sln 이 있는 폴더를 찾지 못했습니다: " + baseDirectory);
        var candidates = Candidates(baseDirectory, root, OperatingSystem.IsWindows());
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("orandactl 을 먼저 빌드하세요. 본 곳: " + string.Join(", ", candidates), candidates[0]);
    }

    /// <summary>
    /// 볼 경로들(먼저 것부터). 출력 경로에 <c>bin/&lt;구성&gt;/&lt;tfm&gt;</c> 이 있으면 같은 구성·tfm 의 CLI 출력, 그다음 출력 아래 <c>agent/</c>.
    /// 출력 경로 꼴을 모르면 Debug/net10.0 을 본다(개발 기본).
    /// </summary>
    public static IReadOnlyList<string> Candidates(string baseDirectory, string repositoryRoot, bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var exe = ExecutableName(windows);
        var (configuration, framework) = OutputShape(baseDirectory) ?? ("Debug", "net10.0");
        return
        [
            Path.Combine(repositoryRoot, "src", "Oranda.Agent.Cli", "bin", configuration, framework, exe),
            Path.Combine(baseDirectory, "agent", exe),
        ];
    }

    /// <summary>출력 경로의 마지막 <c>bin</c> 다음 두 칸(구성, tfm). 꼴이 다르면 null. 구분자는 <c>/</c>·<c>\</c> 둘 다 받는다.</summary>
    public static (string Configuration, string Framework)? OutputShape(string baseDirectory)
    {
        var parts = baseDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var bin = Array.FindLastIndex(parts, p => string.Equals(p, "bin", StringComparison.OrdinalIgnoreCase));
        return bin >= 0 && bin + 2 < parts.Length ? (parts[bin + 1], parts[bin + 2]) : null;
    }

    /// <summary><c>Oranda.sln</c> 이 있는 위쪽 폴더. 없으면 null.</summary>
    public static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}
