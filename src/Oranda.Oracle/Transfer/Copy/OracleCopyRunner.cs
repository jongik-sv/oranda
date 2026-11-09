using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 의 복사 실행기. 본문은 <see cref="CopyRunner"/> 에 있고 여기서는 Oracle 카탈로그(<see cref="OracleSqlCatalog"/>)·
/// 분류기(<see cref="OracleErrorClassifier"/>)·방언(<see cref="OracleCopyDialect"/>)·값 해석(<see cref="OracleBulkValueCodec"/>)·
/// 다시 만들기 전 받아 두기(<see cref="RecreateStep"/>)만 넘긴다.
/// </summary>
[OrandaService(typeof(ICopyRunner), Dbms = Dbms.Oracle)]
internal sealed class OracleCopyRunner(IBulkExecutionService bulk, IExecutionService execution, ICopyRunLog runLog)
    : CopyRunner(bulk, execution, runLog, new OracleSqlCatalog(), new OracleErrorClassifier(),
                 OracleCopyDialect.Instance, OracleBulkValueCodec.Instance, new RecreateStep(bulk));
