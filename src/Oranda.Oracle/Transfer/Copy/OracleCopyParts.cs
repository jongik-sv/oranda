using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 의 복사 부품 한 벌(<see cref="CopyDbmsParts"/>, xdb-copy XC2): 카탈로그(<see cref="OracleSqlCatalog"/>)·분류기(<see cref="OracleErrorClassifier"/>)·
/// 방언(<see cref="OracleCopyDialect"/>·<see cref="OracleCopyPrecheckDialect"/>)·값 해석(<see cref="OracleBulkValueCodec"/>)·다시 만들기 전 받아 두기(<see cref="RecreateStep"/>).
/// <see cref="OracleCopyRunner"/>·<see cref="OracleCopyPrecheckService"/> 가 원본·대상에 같은 벌을 넘긴다.
/// <c>Dbms.Oracle</c> 키로 등록한다(xdb-copy XC6): 교차 복사(Oracle↔Tibero)는 Core 디스패처가 이 벌과 상대 DBMS 의 벌로 실행기·점검을 만든다.
/// </summary>
[OrandaService(typeof(ICopyDbmsParts), Dbms = Dbms.Oracle)]
internal sealed class OracleCopyParts(IBulkExecutionService bulk) : ICopyDbmsParts
{
    public CopyDbmsParts Parts { get; } = new(Dbms.Oracle, new OracleSqlCatalog(), new OracleErrorClassifier(), OracleCopyDialect.Instance,
                                              OracleCopyPrecheckDialect.Instance, OracleBulkValueCodec.Instance, new RecreateStep(bulk));
}
