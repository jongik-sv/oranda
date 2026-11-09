using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 의 복사 사전 점검. 본문은 <see cref="CopyPrecheckService"/> 에 있고 여기서는 Oracle 카탈로그(<see cref="OracleSqlCatalog"/>)·
/// 분류기(<see cref="OracleErrorClassifier"/>)·방언(<see cref="OracleCopyPrecheckDialect"/>·<see cref="OracleCopyDialect"/>)·
/// 값 해석(<see cref="OracleBulkValueCodec"/>)만 넘긴다.
/// </summary>
[OrandaService(typeof(ICopyPrecheckService), Dbms = Dbms.Oracle)]
internal sealed class OracleCopyPrecheckService(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy)
    : CopyPrecheckService(bulk, metadata, policy, new OracleSqlCatalog(), new OracleErrorClassifier(),
                          OracleCopyPrecheckDialect.Instance, OracleCopyDialect.Instance, OracleBulkValueCodec.Instance);
