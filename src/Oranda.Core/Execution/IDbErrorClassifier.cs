using Oranda.Core.Connection;

namespace Oranda.Core.Execution;

/// <summary>
/// DBMS 공급자가 자기 오류 번호를 <see cref="DbErrorKind"/> 로 옮기는 규칙(D-046). DBMS 마다 하나씩 구현한다
/// (Oracle = Oranda.Oracle, Tibero = Oranda.Tibero). 앱은 드라이버 예외 형식(<c>OracleException</c> 등)을 보지 않고 이것만 쓴다.
/// 구현은 <c>[OrandaService(typeof(IDbErrorClassifier), Dbms = …)]</c> 로 등록하고, 쓰는 쪽은
/// DBMS 별 구현 고르기(<c>IDbmsServiceSelector</c>)로 프로필·세션의 DBMS 에 맞는 것을 꺼낸다.
/// 문장 위치(<see cref="DbError.Offset"/> 등)는 문장 원문이 있어야 하므로 실행 서비스가 따로 채우고, 분류는 <see cref="Classify"/> 를 부른다.
/// </summary>
public interface IDbErrorClassifier
{
    /// <summary>이 분류기가 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>서버 오류 번호(<see cref="DbError.Number"/>)의 분류. 모르는 번호·0 이면 <see cref="DbErrorKind.Other"/>.</summary>
    DbErrorKind Classify(int number);

    /// <summary>
    /// 드라이버 예외면 <see cref="DbError"/>(코드·번호·<see cref="DbError.Dbms"/>·<see cref="DbError.Kind"/> 채움)를 돌려준다.
    /// <see cref="Exception.InnerException"/>·<see cref="AggregateException"/> 안쪽까지 찾는다.
    /// 이 DBMS 드라이버의 예외가 아니면 null(호출자가 일반 오류로 다룬다).
    /// </summary>
    DbError? FromException(Exception exception);
}
