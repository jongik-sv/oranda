using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Transfer.Copy;

/// <summary>
/// Tibero 의 복사 실행기 하위 형식(T5 TI1b). 본문은 <see cref="CopyRunner"/> 에 있고 여기서는 Tibero 카탈로그(<see cref="TiberoSqlCatalog"/>)·
/// 분류기(<see cref="TiberoErrorClassifier"/>)·방언(<see cref="TiberoCopyDialect"/>)·값 해석(<see cref="TiberoBulkValueCodec"/>)·
/// 다시 만들기 전 받아 두기(<see cref="TiberoRecreateStep"/>)만 넘긴다.
/// 키 서비스로 등록하지 않는다: 키 등록 = 두 번째 계정 복사 IT 뒤(TI1b 결정65). 지금은 시험·IT 가 직접 만든다.
/// </summary>
internal sealed class TiberoCopyRunner(IBulkExecutionService bulk, IExecutionService execution, ICopyRunLog runLog)
    : CopyRunner(bulk, execution, runLog, new TiberoSqlCatalog(), new TiberoErrorClassifier(),
                 TiberoCopyDialect.Instance, TiberoBulkValueCodec.Instance, new TiberoRecreateStep(bulk));
