using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Tibero.Transfer.Copy;

/// <summary>
/// Tibero 의 복사 사전 점검 하위 형식(T5 TI1b). 본문은 <see cref="CopyPrecheckService"/> 에 있고 여기서는 Tibero 부품 한 벌(<see cref="TiberoCopyParts"/>: 카탈로그·
/// 분류기·방언·값 해석)을 원본·대상에 같은 인스턴스로 넘긴다(같은 DBMS 복사).
/// <c>Dbms.Tibero</c> 키로 등록한다(xdb-copy XC0, Tibero→Tibero 복사 켬 — 두 번째 계정 IT 는 아직 못 잼).
/// </summary>
[OrandaService(typeof(ICopyPrecheckService), Dbms = Dbms.Tibero)]
internal sealed class TiberoCopyPrecheckService(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy)
    : CopyPrecheckService(bulk, metadata, policy, new TiberoCopyParts(bulk).Parts);
