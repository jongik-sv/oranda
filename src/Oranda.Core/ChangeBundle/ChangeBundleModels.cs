using Oranda.Core.Connection;

namespace Oranda.Core.ChangeBundle;

/// <summary>
/// 반영 묶음 하나(테이블 편집기 설계 9.1 <c>change_bundle</c>). 개발 중 실행한 DDL 을 모아 환경별 실행 표시·의뢰용 스크립트 저장을 한다.
/// <see cref="Dbms"/> 는 빈 묶음이면 null 이고 첫 문장의 DBMS 를 따른다(9.3). <see cref="TargetOwners"/> 는 대문자 규칙 이름(TE-24)이며,
/// 비어 있으면 DBMS 처럼 첫 문장의 소유자를 따른다. 「지금 묶음」(<see cref="IsCurrent"/>)은 많아야 하나다.
/// </summary>
public sealed record ChangeBundleInfo(long Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Dbms? Dbms,
    IReadOnlyList<string> TargetOwners, bool IsCurrent)
{
    public bool Equals(ChangeBundleInfo? other) =>
        other is not null && Id == other.Id && Name == other.Name && CreatedAt == other.CreatedAt && UpdatedAt == other.UpdatedAt
        && Dbms == other.Dbms && IsCurrent == other.IsCurrent && TargetOwners.SequenceEqual(other.TargetOwners);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Dbms, IsCurrent, TargetOwners.Count);
}

/// <summary>묶음 문장이 어디서 들어왔나(설계 9.1 출처). 저장 값은 이름 문자열이다.</summary>
public enum ChangeBundleItemSource
{
    /// <summary>테이블 편집기 실행 또는 「묶음에 넣기」.</summary>
    TableEditor,

    /// <summary>SQL 편집기·스크립트 실행에서 자동으로 들어옴.</summary>
    SqlEditor,

    /// <summary>묶음 창에서 사용자가 직접 넣음.</summary>
    Manual,
}

/// <summary>
/// 묶음 안 문장 하나(설계 9.1 <c>change_bundle_item</c>). <see cref="Text"/> 는 소유자를 붙인 꼴(9.3)이고 끝의 <c>;</c>·<c>/</c> 는 없다.
/// 비밀 값은 가린 뒤 저장한다(변경 일지와 같은 수준, TE-2). <see cref="Hash"/> 는 가린 글의 <see cref="StatementHash.Compute"/> 값.
/// ⚠ 표시 = <see cref="Cautions"/>(앱이 문구로 바꿈) + <see cref="Warning"/>(만든 쪽이 준 문구, 서버 이름 제약 TE-8 등, 없으면 null).
/// </summary>
public sealed record ChangeBundleItem(long Id, long BundleId, int Position, string Text, ChangeBundleItemSource Source,
    string? TargetOwner, string? TargetName, bool Checked, DateTimeOffset AddedAt, string Hash, ChangeBundleCautions Cautions, string? Warning)
{
    /// <summary>⚠ 를 보일 것이 있나.</summary>
    public bool HasWarning => Cautions != ChangeBundleCautions.None || Warning is not null;
}

/// <summary>묶음 문장의 ⚠ 갈래(저장 값은 정수). 문구는 앱이 정한다.</summary>
[Flags]
public enum ChangeBundleCautions
{
    None = 0,

    /// <summary>문장 모양을 몰라 소유자를 붙이지 못하고 원문 그대로 넣었다(설계 9.3).</summary>
    OwnerNotQualified = 1,

    /// <summary>비밀 값(<c>IDENTIFIED BY</c> 등)을 가려 저장했다 — 실행·저장 전에 다시 적어야 한다.</summary>
    SecretRedacted = 2,
}

/// <summary>묶음 문장 한 번 실행한 기록(설계 9.1 <c>change_bundle_run</c>). 접속 이름만 두고 호스트는 두지 않는다.</summary>
public sealed record ChangeBundleRun(long ItemId, ConnectionEnvironment Environment, string ConnectionName, DateTimeOffset RanAt, bool Succeeded);

/// <summary>묶음 표의 개발·테스트·운영 칸: 환경별 마지막 성공 때. 성공한 적 없으면 null.</summary>
public sealed record ChangeBundleRunMarks(DateTimeOffset? Development, DateTimeOffset? Test, DateTimeOffset? Production)
{
    public static ChangeBundleRunMarks None { get; } = new(null, null, null);

    public DateTimeOffset? For(ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Development => Development,
        ConnectionEnvironment.Test => Test,
        _ => Production,
    };
}

/// <summary>
/// 새로 넣을 문장. <see cref="Text"/> 는 이미 소유자를 붙인 꼴이어야 한다(<see cref="IChangeBundleService"/> 가 붙여서 넘긴다).
/// 비밀 가림·해시는 저장소가 한다(가렸으면 <see cref="ChangeBundleCautions.SecretRedacted"/> 를 더한다).
/// </summary>
public sealed record NewChangeBundleItem(string Text, ChangeBundleItemSource Source, string? TargetOwner, string? TargetName,
    ChangeBundleCautions Cautions = ChangeBundleCautions.None, string? Warning = null);
