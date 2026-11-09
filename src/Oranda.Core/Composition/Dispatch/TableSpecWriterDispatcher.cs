using Oranda.Core.Connection;
using Oranda.Core.Transfer;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ITableSpecWriter), Dbms = …)]</c>.
/// 정의서 쓰기는 이미 읽은 문서 모델을 파일로 옮길 뿐 세션을 받지 않아 두 멤버 모두 Oracle 구현에 맡긴다.
/// </summary>
[OrandaService(typeof(ITableSpecWriter))]
public sealed class TableSpecWriterDispatcher : ITableSpecWriter
{
    private readonly IDbmsServiceSelector _selector;

    public TableSpecWriterDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    // DBMS 무관: Oracle 구현에 맡긴다(T2 에서 재검토)
    public string Extension(TableSpecFormat format) =>
        _selector.Get<ITableSpecWriter>(Dbms.Oracle).Extension(format);

    // DBMS 무관: Oracle 구현에 맡긴다(T2 에서 재검토)
    public Task WriteAsync(TableSpecDocument document, TableSpecFormat format, Stream destination, CancellationToken ct) =>
        _selector.Get<ITableSpecWriter>(Dbms.Oracle).WriteAsync(document, format, destination, ct);
}
