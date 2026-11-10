using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Transfer.Copy;

/// <summary>
/// Tibero 의 복사 부품 한 벌(<see cref="CopyDbmsParts"/>, xdb-copy XC2): 카탈로그(<see cref="TiberoSqlCatalog"/>)·분류기(<see cref="TiberoErrorClassifier"/>)·
/// 방언(<see cref="TiberoCopyDialect"/>·<see cref="TiberoCopyPrecheckDialect"/>)·값 해석(<see cref="TiberoBulkValueCodec"/>)·다시 만들기 전 받아 두기(<see cref="TiberoRecreateStep"/>).
/// <see cref="TiberoCopyRunner"/>·<see cref="TiberoCopyPrecheckService"/> 가 원본·대상에 같은 벌을 넘긴다. 키 서비스 등록은 뒤 항목(XC11)이 한다.
/// </summary>
internal sealed class TiberoCopyParts(IBulkExecutionService bulk) : ICopyDbmsParts
{
    public CopyDbmsParts Parts { get; } = new(Dbms.Tibero, new TiberoSqlCatalog(), new TiberoErrorClassifier(), TiberoCopyDialect.Instance,
                                              TiberoCopyPrecheckDialect.Instance, TiberoBulkValueCodec.Instance, new TiberoRecreateStep(bulk));
}
