namespace Oranda.Core.Journal;

/// <summary>변경 일지가 보관할 DDL 의 종류(TSK-13-07). TRUNCATE·COMMENT·새 객체 CREATE 는 대상이 아니다.</summary>
public enum ChangeOperation
{
    Alter,
    Drop,
    CreateOrReplace,
    Rename,
}

/// <summary>
/// 실행 전에 이전 DDL 을 보관할 대상 객체. 분류기가 문장 앞부분에서 뽑는다.
/// <paramref name="Owner"/> 는 문장에 스키마 접두가 없으면 null(서버의 현재 스키마로 정한다).
/// <paramref name="ObjectType"/> 은 Oracle <c>OBJECT_TYPE</c> 표기(<c>TABLE</c>, <c>PACKAGE BODY</c>) 이며 RENAME 처럼 문장에 없으면 null.
/// 따옴표 없는 이름은 대문자, 따옴표 이름은 따옴표를 벗기고 대소문자를 유지한다.
/// </summary>
public sealed record ChangeTarget(string? Owner, string Name, string? ObjectType, ChangeOperation Operation);
