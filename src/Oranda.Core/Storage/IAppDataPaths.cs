namespace Oranda.Core.Storage;

/// <summary>
/// 데이터 폴더 <c>{data}</c> 와 그 안의 경로(data-model 1). 환경 변수 <c>ORANDA_DATA_DIR</c> 가 있으면 그 폴더.
/// 구현: Oranda.Platform(TSK-03-01). 경로만 계산하고 폴더 생성은 각 저장소가 한다.
/// </summary>
public interface IAppDataPaths
{
    string DataDirectory { get; }

    string SettingsFile { get; }

    string ConnectionsFile { get; }

    string LayoutFile { get; }

    string FormatterDirectory { get; }

    string KeymapDirectory { get; }

    string WorkspacesDirectory { get; }

    string AutosaveDirectory { get; }

    string DatabaseFile { get; }

    string CredentialsDirectory { get; }

    string LogsDirectory { get; }

    string BackupDirectory { get; }

    /// <summary>접속 간 데이터 복사 작업 파일 폴더(<c>copy-jobs/</c>).</summary>
    string CopyJobsDirectory { get; }

    /// <summary>복사 실행 기록 폴더(<c>logs/copy-runs/</c>).</summary>
    string CopyRunsDirectory { get; }
}
