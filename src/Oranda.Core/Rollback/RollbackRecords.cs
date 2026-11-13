using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;

namespace Oranda.Core.Rollback;

/// <summary>원복 문장 하나(리터럴로 쓴 SQL, 끝의 <c>;</c> 없음).</summary>
public sealed record RollbackStep(string Sql)
{
    /// <summary>문장 위에 붙일 주석(예: <c>불완전: ID 열</c>).</summary>
    public string? Note { get; init; }

    /// <summary>원복 실행이 영향 행 수 1 을 기대한다(0행 = 그 뒤 다른 사람이 바꾼 행).</summary>
    public bool ExpectOneRow { get; init; } = true;
}

/// <summary>끝 판정 질의 두 개(SELECT COUNT(*) …). 앞이 1 이면 커밋, 뒤가 1 이면 롤백.</summary>
public sealed record RollbackProbe(string CommittedIfOne, string RolledBackIfOne);

/// <summary>원장에 남기는 바인드 값(글자, 암호화 파일에만 저장).</summary>
public sealed record RollbackBind(string Name, BindDataType Type, string? Value);

/// <summary>문장 하나의 원장 기록(설계 7.3). 행 값·문장 글·바인드는 암호화 파일에만 저장한다(RD-10).</summary>
public sealed record RollbackStatementRecord
{
    public required Guid SessionId { get; init; }

    public required Guid ProfileId { get; init; }

    public required string ProfileName { get; init; }

    public required string DbKey { get; init; }

    public string? ServerName { get; init; }

    public ConnectionEnvironment Environment { get; init; }

    public Dbms Dbms { get; init; }

    public HistorySource Source { get; init; }

    public DateTimeOffset ExecutedAt { get; init; }

    /// <summary>사용자가 쓴 글(치환 뒤). 파일에만 저장.</summary>
    public required string Text { get; init; }

    public IReadOnlyList<RollbackBind> Binds { get; init; } = [];

    public DmlVerb Verb { get; init; }

    public DbObjectRef? Table { get; init; }

    public long? RowsAffected { get; init; }

    public long? LockedRows { get; init; }

    public ChangePoint? BeforePoint { get; init; }

    public string? TransactionId { get; init; }

    /// <summary>NotCreated | Open(완전) | Incomplete.</summary>
    public RollbackScriptState State { get; init; }

    public NotCreatedReason Reason { get; init; }

    public string? IncompleteReason { get; init; }

    /// <summary>이 문장 안 순서. 파일은 문장 역순으로 쓴다.</summary>
    public IReadOnlyList<RollbackStep> Steps { get; init; } = [];

    public IReadOnlyList<RollbackProbe> Probes { get; init; } = [];

    /// <summary>PK 없는 표의 겹침 검사용(RD-13). 파일에 저장하지 않음.</summary>
    public IReadOnlyList<string> TouchedRowIds { get; init; } = [];

    public bool ReturningAppended { get; init; }
}

/// <summary>묶음 안 문장 하나의 목록 정보(<c>rollback_statement</c> 행). <paramref name="Summary"/> 는 가린 문장 요약.</summary>
public sealed record RollbackStatementInfo(int Seq, DateTimeOffset ExecutedAt, DmlVerb Verb, DbObjectRef? Table,
                                           string Summary, long? RowsAffected, ChangePoint? BeforePoint,
                                           RollbackScriptState State, string? Reason, int StepCount, bool Invalidated)
{
    /// <summary>실행 출처(<c>rollback_statement.source</c>).</summary>
    public HistorySource Source { get; init; }

    /// <summary>잠금 읽기로 잠근 행 수(<c>rollback_statement.locked_rows</c>). 잠금 읽기를 하지 않았으면 null.</summary>
    public long? LockedRows { get; init; }
}

/// <summary>커밋 묶음 하나(<c>rollback_group</c> 행, 설계 12.1).</summary>
public sealed record RollbackGroup
{
    public long Id { get; init; }

    public required Guid ProfileId { get; init; }

    public required string ProfileName { get; init; }

    public required string DbKey { get; init; }

    public string? ServerName { get; init; }

    public ConnectionEnvironment Environment { get; init; }

    public Dbms Dbms { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public ChangePoint? BasePoint { get; init; }

    public ChangePoint? CommitPoint { get; init; }

    public string? TransactionId { get; init; }

    public TransactionEndKind? EndKind { get; init; }

    public TransactionEndSource? EndSource { get; init; }

    public RollbackScriptState State { get; init; }

    public NotCreatedReason Reason { get; init; }

    public string? StateReason { get; init; }

    public int StepCount { get; init; }

    public long RowsTotal { get; init; }

    public IReadOnlyList<DbObjectRef> Tables { get; init; } = [];

    public RollbackOrigin Origin { get; init; }

    public long? OriginGroupId { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool HasScriptFile { get; init; }

    public IReadOnlyList<RollbackStatementInfo> Statements { get; init; } = [];
}

/// <summary>암호화 파일의 평문(JSON, camelCase, <see cref="RollbackJsonContext"/>). SchemaVersion = 1.</summary>
public sealed record RollbackScriptDocument(int SchemaVersion, long GroupId, IReadOnlyList<RollbackScriptStatement> Statements)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>암호화 파일 안 문장 하나.</summary>
public sealed record RollbackScriptStatement(int Seq, DateTimeOffset ExecutedAt, string Text, IReadOnlyList<RollbackBind> Binds,
                                             IReadOnlyList<RollbackStep> Steps, IReadOnlyList<RollbackProbe> Probes,
                                             bool Invalidated, string? IncompleteReason, long? RowsAffected);

/// <summary>묶음 검색 조건(변경 일지 화면 1). 최근 순으로 <see cref="Limit"/> 개까지.</summary>
public sealed record RollbackQuery
{
    public string? Text { get; init; }

    public string? DbKey { get; init; }

    public DbObjectRef? Table { get; init; }

    public int Limit { get; init; } = 300;
}
