using Oranda.Core.Composition;
using Oranda.Core.Storage;

namespace Oranda.Platform.Storage;

/// <summary>데이터 폴더 경로(ORANDA_DATA_DIR 우선). 소유: TSK-03-01. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(IAppDataPaths))]
internal sealed class AppDataPaths : IAppDataPaths
{
    public string DataDirectory
        => throw new NotImplementedException("TSK-03-01");

    public string SettingsFile
        => throw new NotImplementedException("TSK-03-01");

    public string ConnectionsFile
        => throw new NotImplementedException("TSK-03-01");

    public string LayoutFile
        => throw new NotImplementedException("TSK-03-01");

    public string FormatterDirectory
        => throw new NotImplementedException("TSK-03-01");

    public string KeymapDirectory
        => throw new NotImplementedException("TSK-03-01");

    public string WorkspacesDirectory
        => throw new NotImplementedException("TSK-03-01");

    public string AutosaveDirectory
        => throw new NotImplementedException("TSK-03-01");

    public string DatabaseFile
        => throw new NotImplementedException("TSK-03-01");

    public string CredentialsDirectory
        => throw new NotImplementedException("TSK-03-01");

    public string LogsDirectory
        => throw new NotImplementedException("TSK-03-01");

    public string BackupDirectory
        => throw new NotImplementedException("TSK-03-01");
}
