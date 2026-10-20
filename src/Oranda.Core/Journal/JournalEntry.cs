using Oranda.Core.Events;

namespace Oranda.Core.Journal;

/// <summary>일지 행의 원인. 이력 <c>cause</c> 열 값.</summary>
public enum JournalCause
{
    /// <summary>DDL 실행 직전의 서버 DDL(DBMS_METADATA).</summary>
    Ddl,

    /// <summary>PL/SQL 편집기 컴파일 직전의 서버 소스.</summary>
    Compile,
}

/// <summary>
/// <c>oranda.db</c> <c>change_journal</c> 행(data-model 3.2.1). <see cref="PreviousDdl"/> 은 비밀 가림 후 본문이며,
/// 보관에 실패하면 null 이고 <see cref="FailureReason"/> 에 이유가 있다(실행은 막지 않는다).
/// </summary>
public sealed record JournalEntry
{
    public long Id { get; init; }

    public DateTimeOffset CapturedAt { get; init; }

    public Guid ProfileId { get; init; }

    public string ProfileName { get; init; } = string.Empty;

    public string DbKey { get; init; } = string.Empty;

    /// <summary>서버 표시 이름(호스트/서비스). 화면 표시용.</summary>
    public string? ServerName { get; init; }

    public string Owner { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Oracle <c>OBJECT_TYPE</c> 표기.</summary>
    public string ObjectType { get; init; } = string.Empty;

    public JournalCause Cause { get; init; }

    /// <summary>실행한 문장의 앞부분 한 줄 요약(비밀 가림 후).</summary>
    public string? StatementSummary { get; init; }

    public string? PreviousDdl { get; init; }

    /// <summary><see cref="PreviousDdl"/> 의 SHA-256(소문자 16진). 실패 행은 null.</summary>
    public string? ContentHash { get; init; }

    public string? FailureReason { get; init; }

    public bool IsFailure => FailureReason is not null;
}

/// <summary>변경 일지 검색 조건. 모두 선택.</summary>
public sealed record JournalQuery
{
    /// <summary>이름·소유자·요약·DDL 본문에서 부분 문자열(대소문자 무시).</summary>
    public string? Text { get; init; }

    public string? DbKey { get; init; }

    public string? Owner { get; init; }

    public string? Name { get; init; }

    public string? ObjectType { get; init; }

    /// <summary>true = 보관 실패 행을 뺀다.</summary>
    public bool OnlySucceeded { get; init; }

    public int Limit { get; init; } = 500;
}

/// <summary>보존 정책. 설정 화면에 노출하지 않는 상수(TSK-13-07): 기본 365일·객체당 50버전.</summary>
public sealed record JournalRetention
{
    public int MaxAgeDays { get; init; } = 365;

    public int MaxVersionsPerObject { get; init; } = 50;
}

/// <summary>실행 엔진·PL/SQL 편집기 → 일지 기록기. 보관 결과(성공·실패)를 행 한 줄로 실어 보낸다.</summary>
public sealed record ChangeSnapshotCaptured(JournalEntry Entry) : OrandaEvent;

/// <summary>일지 기록기 → 변경 일지 창. 행이 실제로 저장된 뒤 알린다(목록을 다시 읽을 때).</summary>
public sealed record ChangeJournalSaved(long Id) : OrandaEvent;

/// <summary><c>oranda.db</c> <c>change_journal</c>. 기록 전 비밀은 저장소가 <c>ISecretRedactor</c> 로 가린다. 구현: Platform.</summary>
public interface IChangeJournalStore
{
    /// <summary>
    /// 행을 더한다. 성공 행이 같은 객체(db_key·소유자·이름·종류)의 마지막 성공 행과 해시가 같으면 쌓지 않고 null.
    /// 읽기 전용 DB 면 null.
    /// </summary>
    Task<long?> AddAsync(JournalEntry entry, CancellationToken ct);

    /// <summary>최근 순.</summary>
    Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery query, CancellationToken ct);

    Task<JournalEntry?> GetAsync(long id, CancellationToken ct);

    Task PruneAsync(JournalRetention retention, CancellationToken ct);
}
