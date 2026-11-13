namespace Oranda.Core.Rollback;

/// <summary>
/// 설정 「되돌리기」 탭(설계 10.4). <c>AppSettings.Rollback</c>, JSON 섹션 <c>"rollback"</c>. 새 섹션이라 스키마 버전은 그대로(키가 없는 옛 파일 = 모두 기본값).
/// </summary>
public sealed record RollbackSettings
{
    /// <summary>설정 섹션 이름(<c>SettingsChanged.Section</c>).</summary>
    public const string SectionName = "rollback";

    public const int DefaultMaxRows = 100_000, MinMaxRows = 1, MaxMaxRows = 1_000_000;
    public const int DefaultRetentionDays = 30, MinRetentionDays = 1, MaxRetentionDays = 365;

    /// <summary>쓰기 문장을 실행할 때 직전 시점(SCN) 기록. 끄면 층 B 를 하지 않는다(7.1).</summary>
    public bool RecordPoint { get; init; } = true;

    /// <summary>운영 접속에서 원복 스크립트 만들기. 개발·테스트 접속에는 켜는 항목이 없다.</summary>
    public bool ScriptOnProduction { get; init; } = true;

    /// <summary>문장당 상한(행, 1~1,000,000). 넘으면 원복 스크립트 없이 실행한다(6.5).</summary>
    public int MaxRowsPerStatement { get; init; } = DefaultMaxRows;

    /// <summary>보관 기간(일, 1~365, 12.3).</summary>
    public int RetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>잠근 행 수와 실행한 행 수가 다르면 커밋 전에 알리기(10.3).</summary>
    public bool WarnRowCountMismatch { get; init; } = true;

    /// <summary>TRUNCATE·DROP COLUMN·DROP … PURGE 앞에서 백업 테이블 만들기 권하기(9장).</summary>
    public bool SuggestBackupBeforeDataLossDdl { get; init; } = true;

    /// <summary>범위 밖 값을 범위 안으로 당긴 설정(손으로 고친 파일).</summary>
    public RollbackSettings Normalized() => this with
    {
        MaxRowsPerStatement = Math.Clamp(MaxRowsPerStatement, MinMaxRows, MaxMaxRows),
        RetentionDays = Math.Clamp(RetentionDays, MinRetentionDays, MaxRetentionDays),
    };
}
