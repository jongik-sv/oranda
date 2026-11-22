using System.Runtime.CompilerServices;
using Oranda.Core.Composition;
using Oranda.Core.Storage;

[assembly: InternalsVisibleTo("Oranda.Platform.Tests")]
[assembly: InternalsVisibleTo("Oranda.App.Tests")]

namespace Oranda.Platform.Storage;

/// <summary>
/// 데이터 폴더 경로(data-model 1.1). 환경 변수 <c>ORANDA_DATA_DIR</c> 가 있으면 그 폴더,
/// 없으면 Windows <c>%APPDATA%\Oranda</c>, macOS <c>~/Library/Application Support/Oranda</c>(직접 조립).
/// 경로만 계산하고 폴더는 만들지 않는다. 소유: TSK-03-01.
/// </summary>
[OrandaService(typeof(IAppDataPaths))]
public sealed class AppDataPaths : IAppDataPaths
{
    /// <summary>데이터 폴더를 바꾸는 환경 변수.</summary>
    public const string DataDirEnvironmentVariable = AppDataLocation.DataDirEnvironmentVariable;

    public AppDataPaths()
        : this(ResolveDataDirectory(Environment.GetEnvironmentVariable))
    {
    }

    /// <summary>시험·휴대용: 지정한 폴더를 데이터 폴더로 쓴다.</summary>
    public AppDataPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string ConnectionsFile => Path.Combine(DataDirectory, "connections.json");

    public string LayoutFile => Path.Combine(DataDirectory, "layout.json");

    public string FormatterDirectory => Path.Combine(DataDirectory, "formatter");

    public string KeymapDirectory => Path.Combine(DataDirectory, "keymaps");

    public string WorkspacesDirectory => Path.Combine(DataDirectory, "workspaces");

    public string AutosaveDirectory => Path.Combine(DataDirectory, "autosave");

    public string DatabaseFile => Path.Combine(DataDirectory, "oranda.db");

    public string CredentialsDirectory => Path.Combine(DataDirectory, "credentials");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string BackupDirectory => Path.Combine(DataDirectory, "backup");

    public string CopyJobsDirectory => Path.Combine(DataDirectory, "copy-jobs");

    public string CopyRunsDirectory => Path.Combine(LogsDirectory, "copy-runs");

    /// <summary>환경 변수 조회 함수를 받아 데이터 폴더를 정한다(시험에서 가짜 환경을 넣는다). 규칙은 Core 의 <see cref="AppDataLocation"/> 하나다(<c>orandactl</c> 과 같이 쓴다).</summary>
    internal static string ResolveDataDirectory(Func<string, string?> getEnvironmentVariable) =>
        AppDataLocation.Resolve(getEnvironmentVariable, AppDataLocation.CurrentOs);
}
