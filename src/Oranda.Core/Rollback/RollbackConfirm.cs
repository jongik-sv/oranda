using Oranda.Core.Metadata;

namespace Oranda.Core.Rollback;

/// <summary>
/// 확인 창에 넘기는 원복 스크립트 제안(설계 6.1 의 2단계·10.1). <see cref="Applicable"/> 이 거짓이면 <see cref="Reason"/> 이 까닭이다.
/// </summary>
public sealed record RollbackOffer(bool Applicable, NotCreatedReason Reason)
{
    public static RollbackOffer NotApplicable(NotCreatedReason reason, string? message = null) => new(false, reason) { Message = message };

    public DmlShape? Shape { get; init; }

    public RollbackTable? Table { get; init; }

    /// <summary>같은 WHERE 의 <c>COUNT(*)</c>. null = 모름(RD-11).</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>예상 행 수가 문장당 상한을 넘었다(설계 6.5). 확인 창 체크가 꺼지고 흐려진다.</summary>
    public bool OverLimit { get; init; }

    /// <summary>확인 창 체크의 기본값(설정 <c>rollback.scriptOnProduction</c>).</summary>
    public bool DefaultOn { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Message { get; init; }
}

/// <summary>데이터가 사라지는 DDL 앞 백업 테이블 제안(설계 9장, RB-44).</summary>
public sealed record BackupOffer(DbObjectRef Table, string BackupName, string CreateSql, string Reason);

/// <summary>정책 관문이 확인 창에 덧붙여 넘기는 것(설계 6.1 의 3단계).</summary>
public sealed record ConfirmExtras(RollbackOffer? Rollback, BackupOffer? Backup)
{
    public static ConfirmExtras None { get; } = new(null, null);
}

/// <summary>
/// 확인 창의 답. <paramref name="CreateScript"/> = 원복 스크립트를 만든다, <paramref name="BackupSql"/> = 먼저 보낼 백업 테이블 CTAS(null = 만들지 않음).
/// </summary>
public sealed record ConfirmAnswer(bool Proceed, bool CreateScript, string? BackupSql)
{
    public static ConfirmAnswer Declined { get; } = new(false, false, null);

    /// <summary>묻지 않고 진행할 때: 원복은 제안의 기본값, 백업은 만들지 않는다.</summary>
    public static ConfirmAnswer Default(ConfirmExtras? extras) =>
        new(true, extras?.Rollback is { Applicable: true, OverLimit: false, DefaultOn: true }, null);
}
