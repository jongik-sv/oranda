namespace Oranda.Core.Storage;

/// <summary>데이터 폴더 규칙을 고르는 OS(<see cref="AppDataLocation.Resolve(Func{string, string?}, AppOs)"/>).</summary>
public enum AppOs
{
    Windows,
    MacOS,

    /// <summary>Linux 와 그 밖 Unix(지원 대상 아님, CI·개발용으로 XDG 를 따른다).</summary>
    Linux,
}

/// <summary>
/// 데이터 폴더와 외부 연결 파일 경로(설계 agent-cli-control §3.4, 계약 A1). 앱(<c>AppDataPaths</c>)과 <c>orandactl</c>(Core 만 참조)이 같이 쓰는 순수 함수다.
/// 폴더를 만들거나 권한을 맞추지 않는다(Platform 의 <c>PrivateDirectory</c> 몫).
/// </summary>
public static class AppDataLocation
{
    /// <summary>데이터 폴더를 바꾸는 환경 변수.</summary>
    public const string DataDirEnvironmentVariable = "ORANDA_DATA_DIR";

    /// <summary>지금 OS.</summary>
    public static AppOs CurrentOs =>
        OperatingSystem.IsWindows() ? AppOs.Windows : OperatingSystem.IsMacOS() ? AppOs.MacOS : AppOs.Linux;

    /// <summary>지금 프로세스의 데이터 폴더.</summary>
    public static string Resolve() => Resolve(Environment.GetEnvironmentVariable, CurrentOs);

    /// <summary>
    /// 데이터 폴더: <c>ORANDA_DATA_DIR</c> 가 있으면 그 값, 없으면 Windows <c>%APPDATA%\Oranda</c>, macOS <c>$HOME/Library/Application Support/Oranda</c>,
    /// Linux <c>$XDG_DATA_HOME/Oranda</c> 또는 <c>$HOME/.local/share/Oranda</c>. 환경 변수가 비면 .NET 의 특수 폴더로 채운다.
    /// </summary>
    /// <param name="getEnvironmentVariable">환경 변수 조회(시험은 가짜를 넣는다).</param>
    /// <param name="os">규칙을 고를 OS.</param>
    public static string Resolve(Func<string, string?> getEnvironmentVariable, AppOs os)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var overridden = getEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        switch (os)
        {
            case AppOs.Windows:
                var appData = getEnvironmentVariable("APPDATA");
                if (string.IsNullOrEmpty(appData))
                {
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }

                return Path.Combine(appData, "Oranda");
            case AppOs.MacOS:
                return Path.Combine(Home(getEnvironmentVariable), "Library", "Application Support", "Oranda");
            default:
                var xdg = getEnvironmentVariable("XDG_DATA_HOME");
                var root = !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Home(getEnvironmentVariable), ".local", "share");
                return Path.Combine(root, "Oranda");
        }
    }

    /// <summary><c>&lt;데이터 폴더&gt;/run</c>: 소켓(macOS·Linux)과 외부 연결 파일 폴더의 부모.</summary>
    public static string RunDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, "run");
    }

    /// <summary><c>&lt;데이터 폴더&gt;/run/agents</c>: 외부 연결 파일 폴더(0700).</summary>
    public static string AgentsDirectory(string dataDirectory) => Path.Combine(RunDirectory(dataDirectory), "agents");

    /// <summary><c>&lt;데이터 폴더&gt;/run/agents/&lt;pid&gt;.json</c>: 외부 연결 파일(0600).</summary>
    public static string EndpointFile(string dataDirectory, int pid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        return Path.Combine(AgentsDirectory(dataDirectory), EndpointFileName(pid));
    }

    /// <summary>연결 파일 이름 <c>&lt;pid&gt;.json</c>.</summary>
    public static string EndpointFileName(int pid) => pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";

    /// <summary>연결 파일 이름에서 pid 를 읽는다(<c>12345.json</c> → 12345). 꼴이 다르면 null.</summary>
    public static int? PidFromEndpointFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stem = fileName[..^5];
        return int.TryParse(stem, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }

    private static string Home(Func<string, string?> getEnvironmentVariable)
    {
        var home = getEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
    }
}
