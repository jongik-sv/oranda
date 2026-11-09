using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema;

/// <summary>
/// 파라미터 변경 적용 범위(ALTER SYSTEM SET … SCOPE). 이름은 Oracle 용어다(Spfile = 서버 파라미터 파일).
/// DBMS 가 받지 않는 값은 <see cref="ISchemaStatements.ParameterChangeDdl"/> 이 <see cref="NotSupportedException"/> 으로 알린다.
/// </summary>
public enum ParameterScope
{
    Memory,
    Spfile,
    Both,
}

/// <summary>
/// 객체 관리 문장 만들기(INVALID 컴파일·휴지통 Purge/Flashback·파라미터 변경 DDL). 문장만 만들고 실행하지 않는다 —
/// 실행은 반드시 <c>IExecutionService</c>(정책 관문)로 보낸다. DBMS 마다 하나씩 구현한다(Oracle = Oranda.Oracle, Tibero = Oranda.Tibero).
/// 구현은 <c>[OrandaService(typeof(ISchemaStatements), Dbms = …)]</c> 로 등록하고, 쓰는 쪽은
/// DBMS 별 구현 고르기(<c>IDbmsServiceSelector</c>)로 세션의 DBMS 에 맞는 것을 꺼낸다.
/// </summary>
public interface ISchemaStatements
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>컴파일할 수 없는 유형이면 null.</summary>
    SqlStatement? Compile(DbObjectRef obj);

    /// <summary>휴지통 항목 하나를 영구 삭제한다. 되돌릴 수 없어 이름 재입력 확인 대상이다.</summary>
    SqlStatement PurgeItem(RecycleBinItem item);

    /// <summary>현재 스키마의 휴지통을 모두 비운다.</summary>
    SqlStatement PurgeAll();

    /// <summary>휴지통의 테이블을 되살린다. <paramref name="renameTo"/> 가 있으면 그 이름으로.</summary>
    SqlStatement Flashback(RecycleBinItem item, string? renameTo = null);

    /// <summary>
    /// 파라미터 변경 DDL 미리보기 문장(<c>ALTER SYSTEM SET</c>). 실행하지 않는다.
    /// 그 DBMS 에 없는 적용 범위(<paramref name="scope"/>)면 <see cref="NotSupportedException"/>.
    /// </summary>
    string ParameterChangeDdl(string name, string value, ParameterScope scope);
}
