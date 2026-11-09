using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;

namespace Oranda.Tibero.Transfer;

/// <summary>
/// Tibero 의 내보내기·가져오기(TB5). 본문은 Core <see cref="DataTransferService"/> 에 있고 여기서는 Tibero 방언(<see cref="TiberoDataTransferDialect"/>)과
/// 값 변환기(<see cref="TiberoImportValueConverter"/>)만 넘긴다. 내보내기는 Tibero 실행 서비스(<see cref="IExecutionService"/> 디스패처 → Tibero 키)의 결과 집합
/// 페이지를 그대로 읽고, 가져오기는 <c>INSERT … SELECT … FROM DUAL UNION ALL …</c> 한 문장을 같은 실행 서비스로 보낸다(정책 관문·세이브포인트 포함).
/// DBMS 키 등록(<c>[OrandaService(typeof(IDataTransferService), Dbms = Dbms.Tibero)]</c>)은 통합 시험을 통과하는 TI1a 에서 한다 — 그 전에는 Tibero 세션에서
/// 서비스가 없어 화면이 꺼져 있다(DbmsFeature).
/// </summary>
internal sealed class TiberoDataTransferService(IExecutionService execution, IMetadataProvider metadata)
    : DataTransferService(execution, metadata, TiberoDataTransferDialect.Instance, new TiberoImportValueConverter());

/// <summary>
/// Tibero 가져오기 문장 조각. 6.2.15 로 확인: 한 행 SELECT 는 <c>FROM DUAL</c>(열 <c>DUMMY</c>)이고 <c>UNION ALL</c> 은 2001 갈래까지 한 문장으로 돈다.
/// 세이브포인트 문법은 Oracle 과 같다(<c>SAVEPOINT</c> / <c>ROLLBACK TO SAVEPOINT</c>, TiberoTransferProbeIt).
/// </summary>
internal sealed class TiberoDataTransferDialect : IDataTransferDialect
{
    internal static readonly TiberoDataTransferDialect Instance = new();

    public string SelectRow(IReadOnlyList<string> literals)
    {
        ArgumentNullException.ThrowIfNull(literals);
        return "SELECT " + string.Join(", ", literals) + " FROM DUAL";
    }

    public string SavepointStatement(string name) => "SAVEPOINT " + name;

    public string RollbackToSavepointStatement(string name) => "ROLLBACK TO SAVEPOINT " + name;
}
