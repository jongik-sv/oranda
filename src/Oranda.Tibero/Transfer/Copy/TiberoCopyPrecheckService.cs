using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Transfer.Copy;

/// <summary>
/// Tibero 의 복사 사전 점검 하위 형식(T5 TI1b). 본문은 <see cref="CopyPrecheckService"/> 에 있고 여기서는 Tibero 카탈로그(<see cref="TiberoSqlCatalog"/>)·
/// 분류기(<see cref="TiberoErrorClassifier"/>)·방언(<see cref="TiberoCopyPrecheckDialect"/>·<see cref="TiberoCopyDialect"/>)·
/// 값 해석(<see cref="TiberoBulkValueCodec"/>)만 넘긴다.
/// 키 서비스로 등록하지 않는다: 키 등록 = 두 번째 계정 복사 IT 뒤(TI1b 결정65). 지금은 시험·IT 가 직접 만든다.
/// </summary>
internal sealed class TiberoCopyPrecheckService(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy)
    : CopyPrecheckService(bulk, metadata, policy, new TiberoSqlCatalog(), new TiberoErrorClassifier(),
                          TiberoCopyPrecheckDialect.Instance, TiberoCopyDialect.Instance, TiberoBulkValueCodec.Instance);
