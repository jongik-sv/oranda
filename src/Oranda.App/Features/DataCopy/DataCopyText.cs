namespace Oranda.App.Features.DataCopy;

/// <summary>데이터 복사 창이 상태 줄·알림에 쓰는 문구 모음.</summary>
internal static class DataCopyText
{
    public const string StoreMissing = "복사 작업 저장소가 아직 준비되지 않았습니다. 앱을 다시 시작하거나 최신 버전으로 업데이트하세요.";
    public const string PrecheckMissing = "사전 점검 서비스가 아직 준비되지 않았습니다.";
    public const string RunnerMissing = "복사 실행기가 아직 준비되지 않았습니다.";
    public const string RunLogMissing = "실행 기록 서비스가 아직 준비되지 않았습니다.";
    public const string ExecutionMissing = "SQL 실행 서비스를 찾을 수 없습니다.";
    public const string MetadataMissing = "메타데이터 서비스를 찾을 수 없어 테이블 정보를 읽지 못했습니다.";
    public const string SessionsNotOpened = "원본·대상 세션을 열지 않았습니다(비밀번호 입력을 취소했거나 접속하지 못했습니다).";
    public const string SourceMissing = "원본 접속을 고르세요.";
    public const string TargetMissing = "대상 접속을 고르세요.";
    public const string NoTables = "복사할 테이블을 하나 이상 추가하세요.";
    public const string ProductionTarget = "운영 접속은 복사 대상으로 쓸 수 없습니다.";
    public const string ReadOnlyTarget = "대상 접속이 읽기 전용이라 복사할 수 없습니다. 쓰기가 가능한 접속을 고르세요.";
    public const string RunStopped = "사용자가 복사를 중지했습니다.";
    public const string UnfinishedHeader = "지난 실행이 끝나지 않았습니다. 대상 트리거·FK 상태를 확인하세요.";

    public const string SameSourceAndTarget = "원본과 대상이 같은 접속·같은 소유자입니다. 원본 행이 지워질 수 있어 시작하지 않습니다.";

    public static string RecreateNotAllowed(string table) =>
        $"테이블 {table} 의 「테이블 다시 만들기」는 대상이 개발 접속일 때만 쓸 수 있습니다. 대상 처리를 바꾸세요.";

    public static string TargetBlocked(string name) =>
        $"작업에 저장된 대상 접속 「{name}」 이(가) 이 PC 에서는 운영 접속이라 대상을 비웠습니다. 개발·테스트 접속을 다시 고르세요.";

    public static string SourceNotFound(string name) => $"원본 접속 「{name}」 을(를) 이 PC 에서 찾지 못했습니다. 접속을 다시 고르세요.";

    public static string TargetNotFound(string name) => $"대상 접속 「{name}」 을(를) 이 PC 에서 찾지 못했습니다. 접속을 다시 고르세요.";
}
