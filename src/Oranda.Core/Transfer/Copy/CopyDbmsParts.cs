using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 실행기(<see cref="CopyRunner"/>)·사전 점검(<see cref="CopyPrecheckService"/>)이 DBMS 마다 다르게 쓰는 부품 한 벌(xdb-copy XC2).
/// 실행기·점검은 원본 쪽 한 벌과 대상 쪽 한 벌을 따로 받는다: 원본 세션에 보내는 문장·원본 값 해석은 원본 벌, 나머지(대상 조회·DML·DDL·되살리기)는 대상 벌.
/// 같은 DBMS 복사(Oracle→Oracle 등)는 같은 벌을 두 번 넘긴다(동작이 예전 한 벌과 같다).
/// </summary>
/// <param name="Dbms">이 벌이 맡는 DBMS</param>
/// <param name="Catalog">카탈로그 조회 글(<c>copy.*</c>)</param>
/// <param name="Errors">서버 오류 분류(잡는 범위는 <see cref="CopyErrors"/>)</param>
/// <param name="Dialect">DBMS 마다 글이 다른 문장·오류 판정</param>
/// <param name="PrecheckDialect">사전 점검의 기능 차이·안내 글</param>
/// <param name="Codec">대량 값 해석</param>
/// <param name="Recreate">「테이블 다시 만들기」 전에 받아 두기(대상 DDL·원본 FK)</param>
public sealed record CopyDbmsParts(
    Dbms Dbms,
    ISqlCatalog Catalog,
    IDbErrorClassifier Errors,
    ICopyDialect Dialect,
    ICopyPrecheckDialect PrecheckDialect,
    IBulkValueCodec Codec,
    ICopyRecreateStep Recreate);

/// <summary>
/// DBMS 어셈블리가 내는 복사 부품 한 벌(<see cref="CopyDbmsParts"/>). 다른 DBMS 사이 복사는 원본·대상 DBMS 의 이것을 하나씩 꺼내
/// 실행기·점검을 만든다(등록은 뒤 항목).
/// </summary>
public interface ICopyDbmsParts
{
    /// <summary>이 DBMS 의 부품 한 벌.</summary>
    CopyDbmsParts Parts { get; }
}
