namespace Oranda.Core.Connection;

/// <summary>
/// 접속 권한 모드(CONN-02). ODP.NET <c>DBA Privilege</c> 연결 속성 값과 1:1.
/// <see cref="SysBackup"/>·<see cref="SysDg"/>·<see cref="SysKm"/> 의 Managed 드라이버 지원 여부는 접속 Task 가 실측한다.
/// </summary>
public enum AuthMode
{
    Default,
    SysDba,
    SysOper,
    SysBackup,
    SysDg,
    SysKm,
}

/// <summary>환경 라벨(CONN-06, SAFE-01). <see cref="Production"/> 은 확인 대화상자·빨간 테두리 대상.</summary>
public enum ConnectionEnvironment
{
    Development,
    Test,
    Production,
}

/// <summary>관리 팩 보유 선언(SAFE-07). Tuning 은 Diagnostics 를 전제하므로 3값이며 값 순서가 포함 관계다.</summary>
public enum LicensePack
{
    None,
    Diagnostics,
    DiagnosticsAndTuning,
}

/// <summary>접속 직후 적용할 NLS 설정(CONN-08). null = 서버 기본값.</summary>
public sealed record NlsSettings
{
    public string? DateFormat { get; init; }

    public string? TimestampFormat { get; init; }

    public string? Language { get; init; }

    public string? Territory { get; init; }

    public string? Sort { get; init; }
}

/// <summary>
/// 접속 정보(<c>connections.json</c> 의 <c>profiles[]</c>, docs/design/data-model.md 2.2).
/// <see cref="DataSource"/> 는 Easy Connect Plus 문자열 또는 tnsnames.ora 별칭.
/// 비밀번호는 보관하지 않는다 — <see cref="CredentialRef"/> 로 <c>ICredentialStore</c> 에서 꺼낸다.
/// <see cref="Comment"/> 는 사람이 적는 여러 줄 설명(null = 없음, TSK-13-12).
/// D-004: 위치 매개변수 record 를 유지하고 새 매개변수는 끝에 기본값과 함께 덧붙인다.
/// </summary>
public sealed record ConnectionProfile(
    string Name,
    string User,
    string DataSource,
    AuthMode Auth = AuthMode.Default,
    string? TnsAdmin = null,
    string? WalletLocation = null,
    int ConnectTimeoutSeconds = 15,
    Guid Id = default,
    ConnectionEnvironment Environment = ConnectionEnvironment.Development,
    bool ReadOnly = false,
    LicensePack LicensePack = LicensePack.None,
    int KeepAliveSeconds = 0,
    NlsSettings? Nls = null,
    string? CredentialRef = null,
    string? ProxyUser = null,
    string? Color = null,
    string? Group = null,
    DateTimeOffset? LastConnectedAt = null,
    string? Comment = null);

/// <summary><c>connections.json</c> 루트.</summary>
public sealed record ConnectionsDocument
{
    public int SchemaVersion { get; init; } = Settings.SchemaVersions.Connections;

    public IReadOnlyList<ConnectionProfile> Profiles { get; init; } = [];

    /// <summary>
    /// 접속이 하나도 없어도 남기는 폴더 경로 목록(「부모/자식」). 접속의 <c>Group</c> 이 가리키는 폴더는 여기 없어도 있는 것으로 본다.
    /// 이전 형식 파일에는 없고, 없으면 빈 목록이다.
    /// </summary>
    public IReadOnlyList<string> Folders { get; init; } = [];
}
