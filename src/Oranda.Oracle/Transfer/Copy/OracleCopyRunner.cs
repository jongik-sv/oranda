using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// Oracle 의 복사 실행기. 본문은 <see cref="CopyRunner"/> 에 있고 여기서는 Oracle 부품 한 벌(<see cref="OracleCopyParts"/>: 카탈로그·분류기·방언·값 해석·
/// 다시 만들기 전 받아 두기)을 원본·대상에 같은 인스턴스로 넘긴다(같은 DBMS 복사).
/// </summary>
[OrandaService(typeof(ICopyRunner), Dbms = Dbms.Oracle)]
internal sealed class OracleCopyRunner(IBulkExecutionService bulk, IExecutionService execution, ICopyRunLog runLog)
    : CopyRunner(bulk, execution, runLog, new OracleCopyParts(bulk).Parts);
