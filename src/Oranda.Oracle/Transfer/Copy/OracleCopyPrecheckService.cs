using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 의 복사 사전 점검. 본문은 <see cref="CopyPrecheckService"/> 에 있고 여기서는 Oracle 부품 한 벌(<see cref="OracleCopyParts"/>: 카탈로그·분류기·
/// 방언·값 해석)을 원본·대상에 같은 인스턴스로 넘긴다(같은 DBMS 복사).
/// </summary>
[OrandaService(typeof(ICopyPrecheckService), Dbms = Dbms.Oracle)]
internal sealed class OracleCopyPrecheckService(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy)
    : CopyPrecheckService(bulk, metadata, policy, new OracleCopyParts(bulk).Parts);
