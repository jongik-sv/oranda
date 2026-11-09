namespace Oranda.Core.Transfer;

/// <summary>
/// <see cref="DataTransferService"/> 의 가져오기가 DBMS 마다 달리 쓰는 문장 조각(리터럴 변환은 <see cref="IImportValueConverter"/> 가 따로 맡는다).
/// 구현은 DBMS 어셈블리에 두고 그 DBMS 의 <see cref="DataTransferService"/> 하위 형식이 생성자에서 넘긴다. 키 서비스가 아니다(하위 형식이 직접 만든다).
/// </summary>
public interface IDataTransferDialect
{
    /// <summary>
    /// 값 리터럴 목록 하나를 「한 행을 만드는 SELECT 절」 로(<c>SELECT 1, 'a' FROM DUAL</c>). 여러 행 배치는 이것을 <c>UNION ALL</c> 로 이어
    /// <c>INSERT INTO … (열) SELECT … UNION ALL SELECT …</c> 한 문장으로 만든다.
    /// </summary>
    string SelectRow(IReadOnlyList<string> literals);

    /// <summary>세이브포인트를 만드는 문장(<c>SAVEPOINT name</c>).</summary>
    string SavepointStatement(string name);

    /// <summary>세이브포인트까지만 되돌리는 문장(<c>ROLLBACK TO SAVEPOINT name</c>).</summary>
    string RollbackToSavepointStatement(string name);

    /// <summary>
    /// 서버 오류 메시지가 이미 오류 코드(<see cref="Oranda.Core.Execution.DbError.Code"/>)로 시작하는가. 그러면 오류 행 문구에 코드를 또 붙이지 않는다.
    /// 기본 구현 = 메시지가 코드 글자로 시작함(Oracle: <c>ORA-00001: …</c>). 드라이버 메시지의 접두가 코드의 접두와 다른 DBMS 가 다시 구현한다.
    /// </summary>
    bool MessageStartsWithCode(string code, string message) => message.StartsWith(code, StringComparison.OrdinalIgnoreCase);
}
