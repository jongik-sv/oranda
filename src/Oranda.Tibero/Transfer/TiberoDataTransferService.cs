using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;

namespace Oranda.Tibero.Transfer;

/// <summary>
/// Tibero 의 내보내기·가져오기(TB5). 본문은 Core <see cref="DataTransferService"/> 에 있고 여기서는 Tibero 방언(<see cref="TiberoDataTransferDialect"/>)과
/// 값 변환기(<see cref="TiberoImportValueConverter"/>)만 넘긴다. 내보내기는 Tibero 실행 서비스(<see cref="IExecutionService"/> 디스패처 → Tibero 키)의 결과 집합
/// 페이지를 그대로 읽고, 가져오기는 <c>INSERT … SELECT … FROM DUAL UNION ALL …</c> 한 문장을 같은 실행 서비스로 보낸다(정책 관문·세이브포인트 포함).
/// DBMS 키로 등록한다. 화면은 DbmsServiceKeys 영역 규칙대로 같은 영역의 복사 서비스(ICopyRunner·ICopyPrecheckService)까지 있어야 켜지므로
/// 복사 키가 등록되기 전까지(두 번째 계정 복사 IT 뒤, T5 TI1b 결정65) 가져오기·내보내기 메뉴는 꺼진 채다(TU1 이 영역을 나눈다).
/// </summary>
[OrandaService(typeof(IDataTransferService), Dbms = Dbms.Tibero)]
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

    /// <summary>
    /// 오류 코드는 <c>TBR-10007</c> 인데 드라이버 메시지는 <c>ODP-10007:UNIQUE constraint violation …</c> 로 시작한다(6.2.15 실측, transfer.md 3장) — 접두(<c>TBR-</c>/<c>ODP-</c>)가 달라도
    /// 번호가 같으면 같은 오류이므로 코드를 또 붙이지 않는다(<c>행 2: TBR-10007: ODP-10007:…</c> 방지). 메시지 맨 앞 번호만 본다(중간의 다른 번호는 그대로 둔다).
    /// </summary>
    public bool MessageStartsWithCode(string code, string message)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        return message.StartsWith(code, StringComparison.OrdinalIgnoreCase)
               || (LeadingNumber(code) is { } wanted && LeadingNumber(message) == wanted);
    }

    /// <summary>글 맨 앞(공백 뒤)의 <c>TBR-nnnn</c>/<c>ODP-nnnn</c> 번호. 아니거나 뒤가 숫자로 이어지는 너무 긴 번호면 null.</summary>
    private static int? LeadingNumber(string text)
    {
        var span = text.AsSpan().TrimStart();
        if (span.Length < 5 || !(span.StartsWith("TBR-", StringComparison.OrdinalIgnoreCase) || span.StartsWith("ODP-", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var digits = 0;
        while (4 + digits < span.Length && span[4 + digits] is >= '0' and <= '9')
        {
            digits++;
        }

        return digits is >= 1 and <= 9 && int.TryParse(span.Slice(4, digits), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }
}
