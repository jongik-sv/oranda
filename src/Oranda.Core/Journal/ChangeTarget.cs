namespace Oranda.Core.Journal;

/// <summary>변경 일지가 보관할 DDL 의 종류(TSK-13-07). COMMENT·새 객체 CREATE 는 대상이 아니다. TRUNCATE 는 표 DDL 을 보관한다(되돌리기 지점 설계 9장).</summary>
public enum ChangeOperation
{
    Alter,
    Drop,
    CreateOrReplace,
    Rename,
    Truncate,
}

/// <summary>뒤에는 AS OF 로도 이전 데이터를 읽을 수 없는 DDL(되돌리기 지점 설계 9장). 백업 테이블(CTAS)을 권하는 대상이다.</summary>
public enum DataLossKind
{
    None,

    /// <summary><c>TRUNCATE TABLE</c>.</summary>
    Truncate,

    /// <summary><c>ALTER TABLE … DROP COLUMN c</c>·<c>DROP (c1, c2)</c>. 지울 열은 <see cref="ChangeTarget.DroppedColumns"/>.</summary>
    DropColumns,

    /// <summary><c>DROP TABLE … PURGE</c>(휴지통을 거치지 않는다).</summary>
    DropPurge,
}

/// <summary>
/// 실행 전에 이전 DDL 을 보관할 대상 객체. 분류기가 문장 앞부분에서 뽑는다.
/// <paramref name="Owner"/> 는 문장에 스키마 접두가 없으면 null(서버의 현재 스키마로 정한다).
/// <paramref name="ObjectType"/> 은 Oracle <c>OBJECT_TYPE</c> 표기(<c>TABLE</c>, <c>PACKAGE BODY</c>) 이며 RENAME 처럼 문장에 없으면 null.
/// 따옴표 없는 이름은 대문자, 따옴표 이름은 따옴표를 벗기고 대소문자를 유지한다.
/// </summary>
public sealed record ChangeTarget(string? Owner, string Name, string? ObjectType, ChangeOperation Operation)
{
    /// <summary>데이터가 사라지는 DDL 이면 그 종류(백업 테이블 권유, 설계 9장). 기본 <see cref="DataLossKind.None"/>.</summary>
    public DataLossKind DataLoss { get; init; }

    /// <summary><see cref="DataLossKind.DropColumns"/> 일 때 지울 열 이름(이름 규칙은 <see cref="Name"/> 과 같다). 그 밖은 빈 목록.</summary>
    public IReadOnlyList<string> DroppedColumns { get => _dropped; init => _dropped = value ?? []; }

    private readonly IReadOnlyList<string> _dropped = [];

    public bool Equals(ChangeTarget? other) =>
        other is not null
        && Owner == other.Owner && Name == other.Name && ObjectType == other.ObjectType && Operation == other.Operation
        && DataLoss == other.DataLoss && DroppedColumns.SequenceEqual(other.DroppedColumns);

    public override int GetHashCode() => HashCode.Combine(Owner, Name, ObjectType, Operation, DataLoss, DroppedColumns.Count);
}
